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
    public PackagesWindow Create(
        Func<IReadOnlyList<string>, CancellationToken, Task> applyPackageLifecycleChangesAsync,
        PackageOperationService packageOperationService,
        Action<double, double>? persistBackgroundProcessPopoverSize)
    {
        var window = new PackagesWindow(shellStateService, shellState);
        window.DataContext = new PackagesWindowViewModel(
            runtimeApiClientFactory.CreateClient(),
            new PackageArchivePicker(window),
            applyPackageLifecycleChangesAsync,
            packageOperationService,
            backgroundProcessQueue,
            notificationCenter: notificationCenter,
            backgroundProcessPopoverWidth: shellState.BackgroundProcessPopoverWidth,
            backgroundProcessPopoverHeight: shellState.BackgroundProcessPopoverHeight,
            persistBackgroundProcessPopoverSize: persistBackgroundProcessPopoverSize);
        return window;
    }
}
