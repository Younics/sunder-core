using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Composition;

public sealed class SettingsWindowFactory(
    IRuntimeApiClientFactory runtimeApiClientFactory,
    CliInstallationService cliInstallationService,
    SunderUpdateService updateService,
    BackgroundProcessQueueService backgroundProcessQueue,
    ShellStateService shellStateService,
    ShellState shellState,
    AppRuntimeResourcePolicySettingsService runtimeResourcePolicySettingsService,
    IRuntimeResourcePolicyStatusSource runtimeResourcePolicyStatusSource)
{
    internal SettingsWindow Create(
        PackageViewHostService packageViewHostService,
        Action<double, double>? persistBackgroundProcessPopoverSize,
        Func<RuntimePackageStamp, CancellationToken, Task<PackagePresentationResult>>? waitForPresentation = null,
        IRuntimeExecutionRuntimeStatusSource? runtimeStatusSource = null)
    {
        var window = new SettingsWindow(shellStateService, shellState);
        var runtimes = new SettingsPackageRuntimesViewModel(
            runtimeApiClientFactory.CreateClient<IRuntimeExecutionRuntimeManagementClient>(),
            (review, token) => RuntimeManagementDialogs.ShowAsync(window, review, token),
            backgroundProcessQueue,
            () => runtimeApiClientFactory.CreateClient<IRuntimeExecutionRuntimeManagementClient>(),
            waitForPresentation: waitForPresentation,
            changes: runtimeStatusSource);
        window.DataContext = new SettingsWindowViewModel(
                runtimeApiClientFactory.CreateClient<IRuntimePackageSettingsClient>(),
                packageViewHostService,
                cliInstallationService,
                updateService,
                backgroundProcessQueue,
                shellState.BackgroundProcessPopoverWidth,
                shellState.BackgroundProcessPopoverHeight,
                persistBackgroundProcessPopoverSize,
                runtimeResourcePolicySettingsService,
                runtimeResourcePolicyStatusSource,
                runtimes);
        return window;
    }
}
