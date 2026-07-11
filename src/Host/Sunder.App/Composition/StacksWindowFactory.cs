using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Composition;

public sealed class StacksWindowFactory(
    LocalStackLibraryService localStackLibrary,
    IRuntimeApiClientFactory runtimeApiClientFactory,
    RegistryPackageInstallService registryPackageInstallService,
    StackWizardWindowFactory stackWizardWindowFactory,
    ShellStateService shellStateService,
    ShellState shellState)
{
    internal StacksWindow Create(
        Func<IReadOnlyList<string>, CancellationToken, Task> applyPackageLifecycleChangesAsync,
        Func<IReadOnlyList<RuntimeStackImportAppliedContributionDescriptor>, CancellationToken, Task<IReadOnlyList<string>>> notifyStackImportAppliedAsync)
    {
        var window = new StacksWindow(shellStateService, shellState, stackWizardWindowFactory);
        window.DataContext = new StacksWindowViewModel(
            localStackLibrary,
            new StackArchivePicker(window),
            runtimeApiClientFactory.CreateClient(),
            registryPackageInstallService,
            applyPackageLifecycleChangesAsync,
            notifyStackImportAppliedAsync);
        return window;
    }
}
