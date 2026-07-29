using Avalonia.Controls;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;

namespace Sunder.App.Composition;

public sealed class StackWizardWindowFactory(ShellStateService shellStateService, ShellState shellState)
{
    public CreateStackWizardWindow Create(CreateStackWizardViewModel viewModel)
    {
        var window = new CreateStackWizardWindow { DataContext = viewModel };
        OwnPlacement(
            window,
            state => state.CreateStackWizardWindowPlacement,
            (state, placement) => state.CreateStackWizardWindowPlacement = placement);
        return window;
    }

    public UseStackWizardWindow Create(UseStackWizardViewModel viewModel)
    {
        var window = new UseStackWizardWindow { DataContext = viewModel };
        OwnPlacement(
            window,
            state => state.UseStackWizardWindowPlacement,
            (state, placement) => state.UseStackWizardWindowPlacement = placement);
        return window;
    }

    private void OwnPlacement(
        Window window,
        Func<ShellState, ShellWindowPlacement?> getPlacement,
        Action<ShellState, ShellWindowPlacement?> setPlacement)
    {
        ShellWindowPlacementService.Apply(window, getPlacement(shellState));
        var placementTracker = new ShellWindowPlacementTracker(window, getPlacement(shellState));
        window.Closing += (_, _) =>
        {
            var placement = placementTracker.Capture();
            setPlacement(shellState, placement);
            shellStateService.Update(shellState, state => setPlacement(state, placement));
        };
    }
}
