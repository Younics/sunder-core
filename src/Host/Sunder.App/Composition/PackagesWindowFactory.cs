using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;

namespace Sunder.App.Composition;

public sealed class PackagesWindowFactory(
    IRuntimeApiClientFactory runtimeApiClientFactory,
    NotificationCenterService notificationCenter,
    BackgroundProcessQueueService backgroundProcessQueue,
    ShellStateService shellStateService,
    ShellState shellState)
{
    internal PackagesWindow Create(
        Func<IReadOnlyList<string>, CancellationToken, Task> applyPackageLifecycleChangesAsync,
        Func<IReadOnlyList<Sunder.Runtime.Contracts.ActivePackageDescriptor>, IReadOnlyList<Sunder.Runtime.Contracts.PackageUiSnapshotDescriptor>, IReadOnlyList<string>, CancellationToken, Task> preflightPackageLifecycleChangesAsync,
        PackageOperationService packageOperationService,
        Action<double, double>? persistBackgroundProcessPopoverSize)
    {
        var window = new PackagesWindow(shellStateService, shellState);
        window.DataContext = new PackagesWindowViewModel(
            runtimeApiClientFactory.CreateClient<IRuntimePackagesClient>(),
            new PackageArchivePicker(window),
            applyPackageLifecycleChangesAsync,
            packageOperationService,
            backgroundProcessQueue,
            notificationCenter: notificationCenter,
            preflightPackageLifecycleChangesAsync: preflightPackageLifecycleChangesAsync,
            backgroundProcessPopoverWidth: shellState.BackgroundProcessPopoverWidth,
            backgroundProcessPopoverHeight: shellState.BackgroundProcessPopoverHeight,
            persistBackgroundProcessPopoverSize: persistBackgroundProcessPopoverSize);
        return window;
    }
}
