using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;

namespace Sunder.App.Composition;

public sealed class SettingsWindowFactory(
    IRuntimeApiClientFactory runtimeApiClientFactory,
    CliInstallationService cliInstallationService,
    SunderUpdateService updateService,
    BackgroundProcessQueueService backgroundProcessQueue,
    ShellStateService shellStateService,
    ShellState shellState)
{
    public SettingsWindow Create(
        PackageViewHostService packageViewHostService,
        Action<double, double>? persistBackgroundProcessPopoverSize)
        => new(shellStateService, shellState)
        {
            DataContext = new SettingsWindowViewModel(
                runtimeApiClientFactory.CreateClient(),
                packageViewHostService,
                cliInstallationService,
                updateService,
                backgroundProcessQueue,
                shellState.BackgroundProcessPopoverWidth,
                shellState.BackgroundProcessPopoverHeight,
                persistBackgroundProcessPopoverSize),
        };
}
