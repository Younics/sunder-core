using System.Reflection;
using Avalonia.Controls;
using Sunder.Protocol;
using Sunder.Sdk.Abstractions;

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
        new AppPackageBackgroundServiceCoordinator(),
        [],
        [],
        [],
        faultReporter: null,
        sessionFolder: null,
        backgroundProcessQueue: null);

    private AppPackageBackgroundServiceCoordinator _backgroundServices;
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
        AppPackageBackgroundServiceCoordinator backgroundServices,
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
        BackgroundProcessQueueService? backgroundProcessQueue = null)
    {
        _faultReporter = faultReporter;
        _sessionFolder = sessionFolder;
        _shellViewService = shellViewService;
        _settingsNavigationService = settingsNavigationService;
        _packageSessionService = packageSessionService;
        _notificationCenter = notificationCenter;
        _backgroundProcessQueue = backgroundProcessQueue;
        _backgroundServices = backgroundServices;
        _state = new AppPackageHostState(disabledPackageIds, ownedDisposables, loadContexts);
        _composition = new AppPackageHostComposition(
            this,
            viewRegistry,
            _backgroundServices,
            _state,
            faultReporter,
            sessionFolder,
            sharedAssemblyRegistry,
            extensionCatalog,
            shellViewService,
            settingsNavigationService,
            packageSessionService,
            notificationCenter,
            backgroundProcessQueue);
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
        IReadOnlyList<PackageSourceDescriptor> packageSources,
        PackageRuntimeFaultReporter? faultReporter = null,
        IPackageShellViewService? shellViewService = null,
        IPackageSettingsNavigationService? settingsNavigationService = null,
        IPackageSessionService? packageSessionService = null,
        NotificationCenterService? notificationCenter = null,
        BackgroundProcessQueueService? backgroundProcessQueue = null,
        CancellationToken cancellationToken = default)
    {
        AppPackageSessionDirectories.CleanupStaleSessions();
        var sessionFolder = activePackages.Count > 0 ? AppPackageSessionDirectories.CreateSessionFolder() : null;
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            new AppPackageBackgroundServiceCoordinator(),
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
            backgroundProcessQueue);

        await hostService.ApplyPackageDeltaAsync(activePackages, packageSources, cancellationToken: cancellationToken);
        return hostService;
    }

    public async Task ApplyPackageDeltaAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageSourceDescriptor> packageSources,
        IReadOnlyCollection<string>? forceReloadPackageIds = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var lifecycle = await _lifecycleGate.EnterAsync(cancellationToken);
        await _composition.ApplyPackageDeltaAsync(activePackages, packageSources, forceReloadPackageIds, cancellationToken);
    }

    internal async Task<AppPackagePreflightResult> PreflightPackageDeltaAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageSourceDescriptor> packageSources,
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
        return _state.FilterEnabledPackages(activePackages);
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

    public async ValueTask DisposeAsync()
    {
        using var lifecycle = await _lifecycleGate.TryEnterDisposeAsync();
        if (lifecycle is null)
        {
            return;
        }

        DetachFaultForwarder(_composition);
        await DisposeGenerationAsync(_backgroundServices, _composition, _state);

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

    private void ThrowIfDisposed()
        => _lifecycleGate.ThrowIfDisposed();

    private void AttachFaultForwarder(AppPackageHostComposition composition)
        => composition.FaultNotifier.PackageFaulted += Composition_OnPackageFaulted;

    private void DetachFaultForwarder(AppPackageHostComposition composition)
        => composition.FaultNotifier.PackageFaulted -= Composition_OnPackageFaulted;

    private void Composition_OnPackageFaulted(object? sender, PackageViewHostFaultEventArgs e)
        => PackageFaultedHandlers?.Invoke(this, e);

    private static async Task DisposeGenerationAsync(
        AppPackageBackgroundServiceCoordinator backgroundServices,
        AppPackageHostComposition composition,
        AppPackageHostState state)
    {
        var packageIds = state.SnapshotLoadedPackageIds();

        foreach (var packageId in packageIds)
        {
            await composition.UnloadPackageAsync(packageId);
        }

        await backgroundServices.StopAllAsync();
        await composition.DisposeLegacyOwnedInstancesAsync();
        composition.DisposeSharedAssemblies();
    }
}
