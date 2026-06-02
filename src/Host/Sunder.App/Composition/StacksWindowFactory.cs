using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;

namespace Sunder.App.Composition;

public sealed class StacksWindowFactory(
    LocalStackLibraryService localStackLibrary,
    IRuntimeApiClientFactory runtimeApiClientFactory,
    RegistryPackageInstallService registryPackageInstallService,
    ShellStateService shellStateService,
    ShellState shellState)
{
    internal StacksWindow Create(Func<IReadOnlyList<string>, CancellationToken, Task> applyPackageLifecycleChangesAsync)
    {
        var window = new StacksWindow(shellStateService, shellState);
        window.DataContext = new StacksWindowViewModel(
            localStackLibrary,
            new StackArchivePicker(window),
            runtimeApiClientFactory.CreateClient(),
            registryPackageInstallService,
            applyPackageLifecycleChangesAsync);
        return window;
    }
}
