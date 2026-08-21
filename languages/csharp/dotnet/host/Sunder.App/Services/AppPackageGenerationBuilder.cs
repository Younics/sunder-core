using System.Runtime.ExceptionServices;
using System.Reflection;
using Sunder.Package.Hosting;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class StalePackageUiSnapshotException(
    string packageId,
    Exception innerException)
    : Exception($"Runtime package UI snapshot for '{packageId}' became stale.", innerException);

internal sealed class AppPackageGenerationBuilder(
    object eventSender,
    AppPackageSnapshotCache snapshotCache,
    Func<string> ensureSessionFolder,
    Func<Guid, string, string, string?, Exception, bool> quarantineView,
    Func<Guid, string, Task<AppPackageUiCleanupOutcome>> quarantinePackageBoundariesAsync,
    Action<Guid, string, string?, Assembly> registerFaultAttribution,
    Action<AppPackageGeneration, bool> registerPendingCandidate,
    IPackageShellViewService? shellViewService,
    IPackageSettingsNavigationService? settingsNavigationService,
    AppPackageFaultNoticeCoordinator faultNoticeCoordinator,
    NotificationCenterService? notificationCenter,
    BackgroundProcessQueueService? backgroundProcessQueue,
    Func<RuntimeConnectionInfo?>? getRuntimeConnectionInfo,
    IUiDispatcher uiDispatcher)
{
    public AppPackageGeneration CreateInitialGeneration(
        AppPackageViewRegistry viewRegistry,
        HashSet<string> disabledPackageIds,
        IReadOnlyList<object> ownedDisposables,
        IReadOnlyList<AppPackageLoadContext> loadContexts,
        AppSharedAssemblyRegistry? sharedAssemblyRegistry)
        => CreateGeneration(
            Guid.NewGuid(),
            folder: null,
            activePackages: [],
            viewRegistry,
            new AppPackageHostState(disabledPackageIds, ownedDisposables, loadContexts),
            sharedAssemblyRegistry);

    public async Task<AppPackageGeneration> BuildAsync(
        AppPackageGeneration currentGeneration,
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? retryDisabledPackageIds,
        IReadOnlyCollection<string>? forceDisabledPackageIds,
        bool hasCommittedGeneration,
        Action<AppPackageGeneration> discardGeneration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generationId = Guid.NewGuid();
        var generationFolder = activePackages.Count == 0
            ? null
            : Path.Combine(ensureSessionFolder(), "generations", generationId.ToString("N"));
        if (generationFolder is not null)
        {
            Directory.CreateDirectory(generationFolder);
        }

        var retryPackageIds = retryDisabledPackageIds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(retryDisabledPackageIds, StringComparer.OrdinalIgnoreCase);
        var sourcesByPackageId = packageSources
            .GroupBy(source => source.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var disabledPackageIds = currentGeneration.State.SnapshotDisabledPackageIds();
        disabledPackageIds.IntersectWith(activePackages.Select(package => package.PackageId));
        disabledPackageIds.RemoveWhere(packageId =>
            sourcesByPackageId.TryGetValue(packageId, out var source)
            && currentGeneration.State.GetPackageContentHash(packageId) is { } currentContentHash
            && !string.Equals(
                source.ContentHash,
                currentContentHash,
                StringComparison.OrdinalIgnoreCase));
        disabledPackageIds.ExceptWith(retryPackageIds);
        if (forceDisabledPackageIds is not null)
        {
            disabledPackageIds.UnionWith(forceDisabledPackageIds);
        }
        disabledPackageIds.IntersectWith(activePackages.Select(package => package.PackageId));
        var viewRegistry = new AppPackageViewRegistry(uiDispatcher: uiDispatcher);
        var state = new AppPackageHostState(disabledPackageIds, [], []);
        foreach (var source in packageSources)
        {
            state.SetPackageContentHash(source.PackageId, source.ContentHash);
        }
        var candidate = CreateGeneration(
            generationId,
            generationFolder,
            activePackages,
            viewRegistry,
            state,
            sharedAssemblyRegistry: null);

        try
        {
            registerPendingCandidate(candidate, hasCommittedGeneration);
            var activeByPackageId = activePackages.ToDictionary(
                static package => package.PackageId,
                StringComparer.OrdinalIgnoreCase);
            var preparedPackages = new List<AppPreparedPackageActivation>();
            var materializations = packageSources
                .Where(source => activeByPackageId.ContainsKey(source.PackageId))
                .Select(async source =>
                {
                    try
                    {
                        var preparedSource = await snapshotCache.MaterializeAsync(
                            source,
                            generationFolder!,
                            cancellationToken).ConfigureAwait(false);
                        return new AppPackageMaterialization(
                            activeByPackageId[source.PackageId],
                            source,
                            preparedSource,
                            Exception: null);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (RuntimeClientException exception) when (
                        !hasCommittedGeneration
                        && AppPackageUiFaultPolicy.IsRecoverable(exception)
                        && exception.ErrorCode is "runtime.v1.not-found" or "runtime.v1.stale-generation")
                    {
                        throw new StalePackageUiSnapshotException(source.PackageId, exception);
                    }
                    catch (Exception exception) when (
                        AppPackageUiFaultPolicy.IsRecoverable(exception))
                    {
                        return new AppPackageMaterialization(
                            activeByPackageId[source.PackageId],
                            source,
                            PreparedSource: null,
                            Exception: exception);
                    }
                })
                .ToArray();
            foreach (var materialization in await Task.WhenAll(materializations).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (materialization.Exception is not null)
                {
                    HandlePackageFailure(
                        candidate,
                        materialization.Package.PackageId,
                        materialization.Exception.Message,
                        materialization.Exception,
                        hasCommittedGeneration);
                    continue;
                }
                preparedPackages.Add(new AppPreparedPackageActivation(
                    materialization.Package,
                    materialization.Source,
                    materialization.PreparedSource!));
            }

            foreach (var package in activePackages)
            {
                if ((package.HostRoles & PackageHostRoles.App) == 0
                    || state.IsPackageDisabled(package.PackageId)
                    || sourcesByPackageId.ContainsKey(package.PackageId))
                {
                    continue;
                }
                HandlePackageFailure(
                    candidate,
                    package.PackageId,
                    "Runtime did not provide a loadable app-side package source.",
                    exception: null,
                    hasCommittedGeneration);
            }

            candidate.Composition.AddSharedAssemblyProbeDirectories(
                preparedPackages.Select(package => package.LibraryFolder));
            var preparedByPackageId = preparedPackages.ToDictionary(
                static package => package.Package.PackageId,
                StringComparer.OrdinalIgnoreCase);
            foreach (var package in activePackages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((package.HostRoles & PackageHostRoles.App) == 0
                    || state.IsPackageDisabled(package.PackageId)
                    || !preparedByPackageId.TryGetValue(package.PackageId, out var preparedPackage))
                {
                    continue;
                }

                try
                {
                    await candidate.Composition.ActivatePackageAsync(
                        preparedPackage.Package,
                        preparedPackage.Source,
                        preparedPackage.PreparedSource,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (AppPackageUiFaultPolicy.IsRecoverable(ex))
                {
                    HandlePackageFailure(
                        candidate,
                        preparedPackage.Package.PackageId,
                        $"Failed to activate package views: {ex.Message}",
                        ex,
                        hasCommittedGeneration);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return candidate;
        }
        catch
        {
            discardGeneration(candidate);
            throw;
        }
    }

    public static IReadOnlyList<ActivePackageDescriptor> FilterEnabledPackages(
        AppPackageGeneration generation,
        IReadOnlyList<ActivePackageDescriptor> activePackages)
        => generation.State.FilterEnabledPackages(activePackages)
            .Select(package => package with
            {
                Views = generation.Composition.ViewFacade.GetPackageViewDescriptors(package.PackageId),
            })
            .ToArray();

    private AppPackageGeneration CreateGeneration(
        Guid generationId,
        string? folder,
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        AppPackageViewRegistry viewRegistry,
        AppPackageHostState state,
        AppSharedAssemblyRegistry? sharedAssemblyRegistry)
    {
        var composition = new AppPackageHostComposition(
            eventSender,
            generationId,
            viewRegistry,
            state,
            activePackages,
            faultNoticeCoordinator,
            quarantineView,
            quarantinePackageBoundariesAsync,
            registerFaultAttribution,
            sharedAssemblyRegistry,
            shellViewService,
            settingsNavigationService,
            notificationCenter,
            backgroundProcessQueue,
            getRuntimeConnectionInfo,
            uiDispatcher,
            webRpcClientFactory: getRuntimeConnectionInfo is null
                ? null
                : new RuntimeAppWebRpcClientFactory(getRuntimeConnectionInfo));
        return new AppPackageGeneration(
            generationId,
            folder,
            activePackages,
            viewRegistry,
            state,
            composition,
            PackageIconGeneration.Empty());
    }

    private static void HandlePackageFailure(
        AppPackageGeneration candidate,
        string packageId,
        string message,
        Exception? exception,
        bool hasCommittedGeneration)
    {
        if (exception is not null && !AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        candidate.Composition.FaultNotifier.QueueActivationFailure(
            packageId,
            hasCommittedGeneration,
            exception);
        if (hasCommittedGeneration)
        {
            throw new InvalidOperationException($"App package candidate failed for '{packageId}': {message}", exception);
        }

        candidate.State.TryMarkPackageDisabled(packageId);
        AppSessionLog.WriteError($"App package candidate skipped '{packageId}': {message}", exception);
    }

    private sealed record AppPackageMaterialization(
        ActivePackageDescriptor Package,
        PackageUiSnapshotDescriptor Source,
        AppPreparedPackageSource? PreparedSource,
        Exception? Exception);
}
