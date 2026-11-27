using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Composition;

public sealed class PackagesWindowFactory(
    IRuntimeApiClientFactory runtimeApiClientFactory,
    BackgroundTaskQueueService backgroundTaskQueue,
    ShellStateService shellStateService,
    ShellState shellState,
    RemoteImageService remoteImageService,
    PackageUpdateStartupCheckService updateChecks)
{
    internal PackagesWindow Create(
        PackageOperationService packageOperationService,
        Action<double, double>? persistBackgroundTaskPopoverSize)
    {
        var window = new PackagesWindow(shellStateService, shellState);
        window.DataContext = new PackagesWindowViewModel(
            runtimeApiClientFactory.CreateClient<IRuntimePackagesClient>(),
            new PackageArchivePicker(window),
            packageOperationService,
            backgroundTaskQueue,
            backgroundTaskPopoverWidth: shellState.BackgroundTaskPopoverWidth,
            backgroundTaskPopoverHeight: shellState.BackgroundTaskPopoverHeight,
            persistBackgroundTaskPopoverSize: persistBackgroundTaskPopoverSize,
            remoteImageService: remoteImageService,
            updateChecks: updateChecks,
            confirmSharedRuntimeRepair: async (impact, cancellationToken) =>
            {
                var dialog = new ExecutionRuntimeRepairConfirmationWindow(
                    new ExecutionRuntimeRepairConfirmationViewModel(impact));
                return await dialog.ShowDialog<bool>(window).WaitAsync(cancellationToken);
            });
        return window;
    }
}
