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
    ShellState shellState,
    RemoteImageService remoteImageService)
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
            persistBackgroundProcessPopoverSize: persistBackgroundProcessPopoverSize,
            remoteImageService: remoteImageService,
            confirmSharedRuntimeRepair: async (impact, cancellationToken) =>
            {
                var dialog = new ExecutionRuntimeRepairConfirmationWindow(
                    new ExecutionRuntimeRepairConfirmationViewModel(impact));
                return await dialog.ShowDialog<bool>(window).WaitAsync(cancellationToken);
            });
        return window;
    }
}
