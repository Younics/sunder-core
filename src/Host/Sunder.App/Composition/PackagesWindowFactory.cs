using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Composition;

public sealed class PackagesWindowFactory(
    IRuntimeApiClientFactory runtimeApiClientFactory,
    BackgroundProcessQueueService backgroundProcessQueue,
    ShellStateService shellStateService,
    ShellState shellState)
{
    internal PackagesWindow Create(
        PackageOperationService packageOperationService,
        Action<double, double>? persistBackgroundProcessPopoverSize)
    {
        var window = new PackagesWindow(shellStateService, shellState);
        window.DataContext = new PackagesWindowViewModel(
            runtimeApiClientFactory.CreateClient<IRuntimePackagesClient>(),
            new PackageArchivePicker(window),
            packageOperationService,
            backgroundProcessQueue,
            backgroundProcessPopoverWidth: shellState.BackgroundProcessPopoverWidth,
            backgroundProcessPopoverHeight: shellState.BackgroundProcessPopoverHeight,
            persistBackgroundProcessPopoverSize: persistBackgroundProcessPopoverSize);
        return window;
    }
}
