using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Sunder.App.Views;

internal static class WindowDragHost
{
    public static void BeginWindowDragOrToggleMaximize(Window window, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Visual visual)
        {
            if (!ReferenceEquals(TopLevel.GetTopLevel(visual), window))
            {
                return;
            }

            var ancestors = visual.GetSelfAndVisualAncestors().OfType<StyledElement>();
            if (ancestors.Any(static element => element is Button or TextBox or ComboBox or CheckBox or Menu or MenuItem))
            {
                return;
            }
        }

        if (e.ClickCount == 2)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        window.BeginMoveDrag(e);
    }
}
