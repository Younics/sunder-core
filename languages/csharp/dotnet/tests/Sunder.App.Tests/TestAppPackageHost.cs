using Sunder.App.Services;
using Sunder.Package.Hosting;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Tests;

internal sealed class TestAppPackagePolicy : IAppPackagePolicy
{
    internal static TestAppPackagePolicy Instance { get; } = new();
    public Task RequireLoadAsync(PackageUiSnapshotDescriptor snapshot, CancellationToken token) => Task.CompletedTask;
}

internal static class TestAppPackageHost
{
    internal static PackageViewHostService Create(AppPackageViewRegistry viewRegistry, HashSet<string> disabledPackageIds,
        IReadOnlyList<object> ownedDisposables, IReadOnlyList<AppPackageLoadContext> loadContexts, string? sessionFolder,
        AppSharedAssemblyRegistry? sharedAssemblyRegistry = null, IPackageShellViewService? shellViewService = null,
        IPackageSettingsNavigationService? settingsNavigationService = null, NotificationCenterService? notificationCenter = null,
        BackgroundProcessQueueService? backgroundProcessQueue = null, AppPackageResourceAssemblyRegistry? resourceAssemblyRegistry = null,
        Func<RuntimeConnectionInfo?>? getRuntimeConnectionInfo = null,
        Func<PackageUiSnapshotDescriptor, Stream, CancellationToken, Task>? downloadPackageUiSnapshotAsync = null,
        Func<string, CancellationToken, Task<PackageIconImageLoadResult>>? loadPackageIconImageAsync = null,
        IUiDispatcher? uiDispatcher = null, Func<AppPackageGeneration, ValueTask>? retireGenerationAsync = null,
        TimeSpan? retirementBudget = null, TimeSpan? retirementDrainBudget = null, string? packageContentCacheRoot = null,
        Action<Exception>? fatalCleanupFaultHandler = null)
        => new(viewRegistry, disabledPackageIds, ownedDisposables, loadContexts, sessionFolder, sharedAssemblyRegistry, shellViewService,
            settingsNavigationService, notificationCenter, backgroundProcessQueue, resourceAssemblyRegistry, getRuntimeConnectionInfo,
            downloadPackageUiSnapshotAsync, loadPackageIconImageAsync, uiDispatcher, retireGenerationAsync, retirementBudget, retirementDrainBudget,
            packageContentCacheRoot, fatalCleanupFaultHandler, TestAppPackagePolicy.Instance);
}
