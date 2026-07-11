using System.Reflection;
using Avalonia.Controls;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;
using Sunder.App.Views.Controls;

namespace Sunder.App.Services;

public sealed class PackageViewHostFaultEventArgs(string packageId, string message, PackageFailureOrigin origin) : EventArgs
{
    public string PackageId { get; } = packageId;

    public string Message { get; } = message;

    public PackageFailureOrigin Origin { get; } = origin;
}

public sealed record PackageSettingsViewDescriptor(
    string PackageId,
    string DisplayName,
    string? Summary);

public sealed class PackageViewHostService : IAsyncDisposable
{
    public static PackageViewHostService Empty { get; } = new(
        new AppPackageViewRegistry(),
        [],
        [],
        [],
        faultReporter: null,
        sessionFolder: null,
        backgroundProcessQueue: null);

    private AppPackageHostComposition _composition;
    private AppPackageHostState _state;
    private readonly AppPackageLifecycleGate _lifecycleGate = new(nameof(PackageViewHostService));
    private readonly PackageRuntimeFaultReporter? _faultReporter;
    private readonly IPackageShellViewService? _shellViewService;
    private readonly IPackageSettingsNavigationService? _settingsNavigationService;
    private readonly IPackageSessionService? _packageSessionService;
    private readonly NotificationCenterService? _notificationCenter;
    private readonly BackgroundProcessQueueService? _backgroundProcessQueue;
    private string? _sessionFolder;
    private event EventHandler<PackageViewHostFaultEventArgs>? PackageFaultedHandlers;

    internal PackageViewHostService(
        AppPackageViewRegistry viewRegistry,
        HashSet<string> disabledPackageIds,
        IReadOnlyList<object> ownedDisposables,
        IReadOnlyList<AppPackageLoadContext> loadContexts,
        PackageRuntimeFaultReporter? faultReporter,
        string? sessionFolder,
        AppSharedAssemblyRegistry? sharedAssemblyRegistry = null,
        AppPackageExtensionCatalog? extensionCatalog = null,
        IPackageShellViewService? shellViewService = null,
        IPackageSettingsNavigationService? settingsNavigationService = null,
        IPackageSessionService? packageSessionService = null,
        NotificationCenterService? notificationCenter = null,
        BackgroundProcessQueueService? backgroundProcessQueue = null,
        AppPackageResourceAssemblyRegistry? resourceAssemblyRegistry = null,
        Func<RuntimeConnectionInfo?>? getRuntimeConnectionInfo = null,
        Func<PackageUiSnapshotDescriptor, Stream, CancellationToken, Task>? downloadPackageUiSnapshotAsync = null)
    {
        _faultReporter = faultReporter;
        _sessionFolder = sessionFolder;
        _shellViewService = shellViewService;
        _settingsNavigationService = settingsNavigationService;
        _packageSessionService = packageSessionService;
        _notificationCenter = notificationCenter;
        _backgroundProcessQueue = backgroundProcessQueue;
        _state = new AppPackageHostState(disabledPackageIds, ownedDisposables, loadContexts);
        _composition = new AppPackageHostComposition(
            this,
            viewRegistry,
            _state,
            faultReporter,
            sessionFolder,
            sharedAssemblyRegistry,
            extensionCatalog,
            shellViewService,
            settingsNavigationService,
            packageSessionService,
            notificationCenter,
            backgroundProcessQueue,
            resourceAssemblyRegistry,
            getRuntimeConnectionInfo,
            downloadPackageUiSnapshotAsync);
        AttachFaultForwarder(_composition);
    }

    public event EventHandler<PackageViewHostFaultEventArgs>? PackageFaulted
    {
        add => PackageFaultedHandlers += value;
        remove => PackageFaultedHandlers -= value;
    }

    internal int LoadedPackageCount => _state.LoadedPackageCount;

    internal int OwnedDisposableCount => _state.OwnedDisposableCount;

    internal int LoadContextCount => _state.LoadContextCount;

    public static async Task<PackageViewHostService> CreateForPackagesAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        PackageRuntimeFaultReporter? faultReporter = null,
        IPackageShellViewService? shellViewService = null,
        IPackageSettingsNavigationService? settingsNavigationService = null,
        IPackageSessionService? packageSessionService = null,
        NotificationCenterService? notificationCenter = null,
        BackgroundProcessQueueService? backgroundProcessQueue = null,
        CancellationToken cancellationToken = default)
        => await CreateForPackagesCoreAsync(
            activePackages,
            packageSources,
            faultReporter,
            shellViewService,
            settingsNavigationService,
            packageSessionService,
            notificationCenter,
            backgroundProcessQueue,
            resourceAssemblyRegistry: null,
            getRuntimeConnectionInfo: null,
            cancellationToken).ConfigureAwait(false);

    internal static async Task<PackageViewHostService> CreateForPackagesWithResourceRegistryAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        PackageRuntimeFaultReporter? faultReporter,
        IPackageShellViewService? shellViewService,
        IPackageSettingsNavigationService? settingsNavigationService,
        IPackageSessionService? packageSessionService,
        NotificationCenterService? notificationCenter,
        BackgroundProcessQueueService? backgroundProcessQueue,
        AppPackageResourceAssemblyRegistry resourceAssemblyRegistry,
        Func<RuntimeConnectionInfo?> getRuntimeConnectionInfo,
        CancellationToken cancellationToken = default)
        => await CreateForPackagesCoreAsync(
            activePackages,
            packageSources,
            faultReporter,
            shellViewService,
            settingsNavigationService,
            packageSessionService,
            notificationCenter,
            backgroundProcessQueue,
            resourceAssemblyRegistry,
            getRuntimeConnectionInfo,
            cancellationToken).ConfigureAwait(false);

    private static async Task<PackageViewHostService> CreateForPackagesCoreAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        PackageRuntimeFaultReporter? faultReporter,
        IPackageShellViewService? shellViewService,
        IPackageSettingsNavigationService? settingsNavigationService,
        IPackageSessionService? packageSessionService,
        NotificationCenterService? notificationCenter,
        BackgroundProcessQueueService? backgroundProcessQueue,
        AppPackageResourceAssemblyRegistry? resourceAssemblyRegistry,
        Func<RuntimeConnectionInfo?>? getRuntimeConnectionInfo,
        CancellationToken cancellationToken)
    {
        AppPackageSessionDirectories.CleanupStaleSessions();
        var sessionFolder = activePackages.Count > 0 ? AppPackageSessionDirectories.CreateSessionFolder() : null;
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            [],
            [],
            [],
            faultReporter,
            sessionFolder,
            new AppSharedAssemblyRegistry([]),
            new AppPackageExtensionCatalog(),
            shellViewService,
            settingsNavigationService,
            packageSessionService,
            notificationCenter,
            backgroundProcessQueue,
            resourceAssemblyRegistry,
            getRuntimeConnectionInfo);

        await hostService.ApplyPackageDeltaAsync(activePackages, packageSources, cancellationToken: cancellationToken);
        return hostService;
    }

    public async Task ApplyPackageDeltaAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? forceReloadPackageIds = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var lifecycle = await _lifecycleGate.EnterAsync(cancellationToken);
        await _composition.ApplyPackageDeltaAsync(activePackages, packageSources, forceReloadPackageIds, cancellationToken);
    }

    internal async Task<AppPackagePreflightResult> PreflightPackageDeltaAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? forceReloadPackageIds = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var lifecycle = await _lifecycleGate.EnterAsync(cancellationToken);
        return await _composition.PreflightPackageDeltaAsync(activePackages, packageSources, forceReloadPackageIds, cancellationToken);
    }

    public IReadOnlyList<ActivePackageDescriptor> FilterEnabledPackages(IReadOnlyList<ActivePackageDescriptor> activePackages)
    {
        ThrowIfDisposed();
        return _state.FilterEnabledPackages(activePackages)
            .Select(package => package with
            {
                Views = _composition.ViewFacade.GetPackageViewDescriptors(package.PackageId),
            })
            .ToArray();
    }

    public bool TryHandleUnhandledException(Exception exception)
    {
        ThrowIfDisposed();
        var packageId = _composition.AssemblyTracker.ResolvePackageId(exception);
        if (packageId is null)
        {
            return false;
        }

        DisablePackage(
            packageId,
            $"Unhandled package UI exception: {exception.Message}",
            PackageFailureOrigin.AppUnhandledUi,
            exception);
        return true;
    }

    public Control? GetOrCreateView(string viewId)
    {
        ThrowIfDisposed();
        return _composition.ViewFacade.GetOrCreateView(viewId);
    }

    public Control? ReloadView(string viewId)
    {
        ThrowIfDisposed();
        return _composition.ViewFacade.ReloadView(viewId);
    }

    public bool InvalidateView(string viewId)
    {
        ThrowIfDisposed();
        return _composition.ViewFacade.InvalidateView(viewId);
    }

    public async ValueTask NotifyViewNavigatedAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        await _composition.ViewFacade.NotifyViewNavigatedAsync(viewId, parameters, cancellationToken);
    }

    public bool HasSettingsView(string packageId)
    {
        ThrowIfDisposed();
        return _composition.ViewFacade.HasSettingsView(packageId);
    }

    public IReadOnlyList<PackageSettingsViewDescriptor> ListSettingsViewPackages()
    {
        ThrowIfDisposed();
        return _composition.ViewFacade.ListSettingsViewPackages();
    }

    public Control? GetOrCreateSettingsView(string packageId)
    {
        ThrowIfDisposed();
        return _composition.ViewFacade.GetOrCreateSettingsView(packageId);
    }

    public async Task<IReadOnlyList<string>> NotifyStackImportAppliedAsync(
        IReadOnlyList<RuntimeStackImportAppliedContributionDescriptor> appliedContributions,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (appliedContributions.Count == 0)
        {
            return [];
        }

        var warnings = new List<string>();
        var stackContributions = _composition.ExtensionCatalog.GetExtensionContributions(SunderStackExtensionPoints.StackContributors);
        foreach (var applied in appliedContributions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handlers = stackContributions
                .Where(contribution => string.Equals(contribution.PackageId, applied.OwnerPackageId, StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(contribution.Contribution.ContributorId, applied.ContributorId, StringComparison.OrdinalIgnoreCase))
                .Select(contribution => contribution.Contribution)
                .OfType<IPackageStackImportAppliedHandler>()
                .ToArray();
            if (handlers.Length == 0)
            {
                continue;
            }

            var context = new StackImportAppliedContext(
                applied.OwnerPackageId,
                applied.ContributorId,
                applied.FragmentIds,
                applied.ImportedItems
                    .Select(item => new StackImportedItem(item.ItemId, item.DisplayName, item.Kind))
                    .ToArray());
            foreach (var handler in handlers)
            {
                try
                {
                    await handler.OnStackImportAppliedAsync(context, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var message = $"Package '{applied.OwnerPackageId}' did not refresh imported Stack data: {ex.Message}";
                    warnings.Add(message);
                    AppSessionLog.WriteError(message, ex);
                }
            }
        }

        return warnings;
    }

    internal Control? CreateHostedViewBoundary(string packageId, string viewId, Control? hostedView)
    {
        ThrowIfDisposed();
        return hostedView is null
            ? null
            : new HostedPackageViewBoundary(packageId, viewId, hostedView, ReportHostedViewFailure);
    }

    public async ValueTask DisposeAsync()
    {
        using var lifecycle = await _lifecycleGate.TryEnterDisposeAsync();
        if (lifecycle is null)
        {
            return;
        }

        DetachFaultForwarder(_composition);
        await DisposeGenerationAsync(_composition, _state);

        // Keep package shadows for the rest of the process; native library finalizers can run after package unload.
        GC.SuppressFinalize(this);
    }

    internal static async Task DisposeOwnedInstanceAsync(object ownedInstance)
        => await AppPackageResourceDisposer.DisposeOwnedInstanceAsync(ownedInstance);

    internal void RegisterPackageAssembly(string packageId, Assembly assembly)
        => _composition.RegisterPackageAssembly(packageId, assembly);

    internal void DisablePackage(
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception = null)
        => _composition.DisablePackage(packageId, message, origin, exception);

    internal async Task DisablePackageAsync(
        string packageId,
        string message,
        PackageFailureOrigin origin,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var lifecycle = await _lifecycleGate.EnterAsync(cancellationToken);
        await _composition.DisablePackageAsync(packageId, message, origin, exception, cancellationToken);
    }

    private void ReportHostedViewFailure(string packageId, string viewId, Exception exception)
        => DisablePackage(
            packageId,
            $"Hosted package view '{viewId}' failed: {exception.Message}",
            PackageFailureOrigin.AppHostedView,
            exception);

    private void ThrowIfDisposed()
        => _lifecycleGate.ThrowIfDisposed();

    private void AttachFaultForwarder(AppPackageHostComposition composition)
        => composition.FaultNotifier.PackageFaulted += Composition_OnPackageFaulted;

    private void DetachFaultForwarder(AppPackageHostComposition composition)
        => composition.FaultNotifier.PackageFaulted -= Composition_OnPackageFaulted;

    private void Composition_OnPackageFaulted(object? sender, PackageViewHostFaultEventArgs e)
        => PackageFaultedHandlers?.Invoke(this, e);

    private static async Task DisposeGenerationAsync(
        AppPackageHostComposition composition,
        AppPackageHostState state)
    {
        var packageIds = state.SnapshotLoadedPackageIds();

        foreach (var packageId in packageIds)
        {
            await composition.UnloadPackageAsync(packageId);
        }

        await composition.DisposeLegacyOwnedInstancesAsync();
        composition.DisposeSharedAssemblies();
        composition.Dispose();
    }
}
