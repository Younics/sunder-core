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
        Func<RuntimePackageStamp, CancellationToken, Task> waitUntilPresentationAppliedAsync)
    {
        var window = new StacksWindow(shellStateService, shellState, stackWizardWindowFactory);
        window.DataContext = new StacksWindowViewModel(
            localStackLibrary,
            new StackArchivePicker(window),
            runtimeApiClientFactory.CreateClient<IRuntimeStacksClient>(),
            registryPackageInstallService,
            waitUntilPresentationAppliedAsync);
        return window;
    }
}
