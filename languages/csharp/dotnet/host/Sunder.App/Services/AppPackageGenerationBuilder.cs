using System.Diagnostics;
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
    BackgroundTaskQueueService? backgroundTaskQueue,
    Func<RuntimeConnectionInfo?>? getRuntimeConnectionInfo,
    IUiDispatcher uiDispatcher,
    IAppPackagePolicy? packagePolicy = null,
    IAppPackageDevelopmentBackend? developerServices = null,
    Action<AppPackageGeneration, Task>? registerActivation = null)
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
        var viewRegistry = new AppPackageViewRegistry(generationId, uiDispatcher);
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
        var preparedPackages = new List<AppPreparedPackageActivation>();

        try
        {
            registerPendingCandidate(candidate, hasCommittedGeneration);
            var activeByPackageId = activePackages.ToDictionary(
                static package => package.PackageId,
                StringComparer.OrdinalIgnoreCase);
            foreach (var group in currentGeneration.ActivationGroups)
            {
                var ownedIds = group.OwnedActivationIds;
                if (ownedIds.Count == 0 || !group.Composition.CanRetainActivations || !ownedIds.All(id =>
                        !disabledPackageIds.Contains(id) && !retryPackageIds.Contains(id)
                        && !currentGeneration.State.IsPackageDisabled(id)
                        && activeByPackageId.TryGetValue(id, out var active)
                        && sourcesByPackageId.TryGetValue(id, out var source)
                        && currentGeneration.State.GetLoadedPackage(id) is { } handle && CanRetain(handle, active, source))) continue;
                foreach (var id in ownedIds)
                {
                    var source = sourcesByPackageId[id];
                    var handle = group.State.GetLoadedPackage(id)
                        ?? throw new InvalidOperationException($"Retained App activation '{id}' retired during preparation.");
                    if (handle.TargetLifetime is AppPackageManagedRpcClient rpc)
                        candidate.AddRpcReplacement(await rpc.PrepareAsync(source, cancellationToken).ConfigureAwait(false));
                    else
                        await (packagePolicy ?? UnavailableAppPackagePolicy.Instance).RequireLoadAsync(source, cancellationToken).ConfigureAwait(false);
                    candidate.State.StageRetainedSource(activeByPackageId[id], source);
                }
                // Every exact new source has a permit and a prepared caller session.
                // Retain the original publication/owner, never a reconstructed provider.
                candidate.RetainGroup(group);
            }
            await candidate.PrepareDataHandoffsAsync(currentGeneration, cancellationToken).ConfigureAwait(false);
            var materializations = packageSources
                .Where(source => activeByPackageId.ContainsKey(source.PackageId) && !candidate.Composition.IsRetainedPackage(source.PackageId))
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
                            Exception: null,
                            ReadyTimestamp: Stopwatch.GetTimestamp());
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
                            Exception: exception,
                            ReadyTimestamp: Stopwatch.GetTimestamp());
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
                    materialization.PreparedSource!,
                    materialization.ReadyTimestamp));
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

                var startupTrace = AppStartupTrace.BeginPackageModuleLoad(
                    preparedPackage.Package,
                    preparedPackage.Source,
                    generationId,
                    preparedPackage.ReadyTimestamp);
                var activationStartedTimestamp = Stopwatch.GetTimestamp();
                AppSessionLog.WriteInfo(
                    $"App package module load started for '{preparedPackage.Package.PackageId}' in generation '{generationId:N}'.",
                    visibleInDeveloperLog: false);
                try
                {
                    var activation = CreateGeneration(generationId, folder: null, [preparedPackage.Package],
                        new AppPackageViewRegistry(generationId, uiDispatcher), new AppPackageHostState([], [], []),
                        sharedAssemblyRegistry: null, activationOwnerId: Guid.NewGuid());
                    activation.ShareDirectory(candidate);
                    activation.State.SetPackageContentHash(package.PackageId, preparedPackage.Source.ContentHash);
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    registerActivation?.Invoke(activation, ready.Task);
                    try
                    {
                        await activation.Composition.ActivatePackageAsync(
                            preparedPackage.Package, preparedPackage.Source, preparedPackage.PreparedSource,
                            cancellationToken).ConfigureAwait(false);
                        activation.SealActivations();
                        candidate.RetainGroup(activation, existingActivation: false);
                    }
                    finally
                    {
                        // On success the candidate now owns this same live activation.
                        // On failure this releases only its partially prepared resources.
                        try { await activation.DisposeAsync().ConfigureAwait(false); }
                        finally { ready.TrySetResult(); }
                    }
                    startupTrace?.Complete();
                    AppSessionLog.WriteInfo(
                        $"App package module load completed for '{preparedPackage.Package.PackageId}' in {Stopwatch.GetElapsedTime(activationStartedTimestamp).TotalMilliseconds:0.0} ms.",
                        visibleInDeveloperLog: false);
                }
                catch (OperationCanceledException exception)
                {
                    startupTrace?.Fail(exception);
                    throw;
                }
                catch (Exception ex) when (AppPackageUiFaultPolicy.IsRecoverable(ex))
                {
                    startupTrace?.Fail(ex);
                    HandlePackageFailure(
                        candidate,
                        preparedPackage.Package.PackageId,
                        $"Failed to activate package views: {ex.Message}",
                        ex,
                        hasCommittedGeneration);
                }
                catch (Exception exception)
                {
                    startupTrace?.Fail(exception);
                    throw;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            candidate.SealActivations();
            return candidate;
        }
        catch
        {
            discardGeneration(candidate);
            throw;
        }
        finally
        {
            foreach (var preparedPackage in preparedPackages)
            {
                if (preparedPackage.PreparedSource.ProjectionLease is { } lease)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
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

    // A Host-issued development fingerprint can exclude this package's Worker
    // payload, but the App projection still matches byte-for-byte. This is only
    // reuse eligibility: BuildAsync requires the exact fresh policy/snapshot permit
    // and stages a new caller session before retaining the activation.
    internal static bool CanRetain(AppLoadedPackageHandle handle, ActivePackageDescriptor active, PackageUiSnapshotDescriptor source)
        => handle.Source.ActivationFingerprint is { Length: 64 } fingerprint
            && fingerprint == source.ActivationFingerprint
            && handle.Source.SourceKind == source.SourceKind
            && handle.Source.ContentHash == source.ContentHash
            && (handle.Source.PolicySubject == source.PolicySubject
                || source.SourceKind == PackageSourceKind.Dev
                    && handle.Source.PolicySubject is { } previous && source.PolicySubject is { } next
                    && previous.PackageId == next.PackageId && next.PackageId == active.PackageId
                    && previous.Version == next.Version && next.Version == active.Version)
            && handle.Source.Target.Kind == Sunder.Package.Format.SunderPackageFormat.AvaloniaTargetKind
            && SameTarget(handle.Source.Target, source.Target)
            && handle.Package.Version == active.Version && handle.Package.HostRoles == active.HostRoles
            && handle.Package.IsEnabled == active.IsEnabled && handle.Package.Readiness == active.Readiness
            && (handle.TargetLifetime is AppPackageManagedRpcClient
                || handle.TargetLifetime is null && AppProcessPolicySupervisor.GetImplementedObservationCapabilities(source.Target) is null);

    private static bool SameTarget(PackageTargetDescriptor left, PackageTargetDescriptor right)
        => left.Role == right.Role && left.Rid == right.Rid && left.Kind == right.Kind && left.EntryPoint == right.EntryPoint
            && left.TargetFramework == right.TargetFramework && left.SdkVersion == right.SdkVersion
            && left.RequiredHostCapabilities.SequenceEqual(right.RequiredHostCapabilities, StringComparer.Ordinal)
            && left.Views.SequenceEqual(right.Views);

    private AppPackageGeneration CreateGeneration(
        Guid generationId,
        string? folder,
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        AppPackageViewRegistry viewRegistry,
        AppPackageHostState state,
        AppSharedAssemblyRegistry? sharedAssemblyRegistry,
        Guid? activationOwnerId = null)
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
            backgroundTaskQueue,
            getRuntimeConnectionInfo,
            uiDispatcher,
            webRpcClientFactory: getRuntimeConnectionInfo is null
                ? null
                : new RuntimeAppWebRpcClientFactory(getRuntimeConnectionInfo),
            packagePolicy: packagePolicy,
            developerServices: developerServices,
            activationOwnerId: activationOwnerId);
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
        Exception? Exception,
        long ReadyTimestamp);
}
