using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sunder.App.Features.Shell.Menus;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

internal sealed class ToolbarMainMenuController(
    Menu toolbarMainMenu,
    Control toolbarDefaultActions,
    Control middlePackageIconBar,
    Control toolbarLeftMenuHost,
    Func<MainWindowViewModel?> viewModelAccessor) : IDisposable
{
    private readonly OwnedTaskObserver _tasks = new("toolbar main menu");

    public bool Show()
    {
        var items = viewModelAccessor()?.GetMainMenuItems();
        if (items is null)
        {
            return false;
        }

        toolbarMainMenu.ItemsSource = items.Select(BuildMenuItem).ToArray();
        toolbarDefaultActions.IsVisible = false;
        middlePackageIconBar.IsVisible = false;
        toolbarMainMenu.IsVisible = true;
        toolbarMainMenu.Focus();
        return true;
    }

    public void Hide()
    {
        if (!toolbarMainMenu.IsVisible)
        {
            return;
        }

        toolbarMainMenu.IsVisible = false;
        toolbarMainMenu.ItemsSource = null;
        middlePackageIconBar.IsVisible = true;
        toolbarDefaultActions.IsVisible = true;
    }

    public void HideIfPointerOutside(PointerPressedEventArgs e)
    {
        if (!toolbarMainMenu.IsVisible || e.Source is not Visual visual)
        {
            return;
        }

        if (!ReferenceEquals(TopLevel.GetTopLevel(visual), TopLevel.GetTopLevel(toolbarLeftMenuHost)))
        {
            return;
        }

        var ancestors = visual.GetSelfAndVisualAncestors().OfType<StyledElement>().ToArray();
        if (ancestors.Contains(toolbarLeftMenuHost) || ancestors.Any(element => element is Menu or MenuItem))
        {
            return;
        }

        Dispatcher.UIThread.Post(Hide);
    }

    public bool HideOnEscape(Key key)
    {
        if (!toolbarMainMenu.IsVisible || key != Key.Escape)
        {
            return false;
        }

        Hide();
        return true;
    }

    public void Dispose() => _tasks.Dispose();

    private MenuItem BuildMenuItem(ShellMenuItem item)
    {
        var menuItem = new MenuItem
        {
            Header = CreateMenuHeader(item),
            IsEnabled = item.IsEnabled,
            Classes = { item.Id is "view" or "developer" ? "toolbar-menu-root-item" : "toolbar-menu-item" },
        };
        if (item.Children.Count > 0)
        {
            menuItem.ItemsSource = item.Children.Select(BuildMenuItem).ToArray();
            if (item.Id is "view" or "developer")
            {
                menuItem.PointerEntered += (_, _) => menuItem.IsSubMenuOpen = true;
            }
        }
        else if (item.ExecuteAsync is not null)
        {
            menuItem.Click += (_, _) =>
            {
                Hide();
                _tasks.Run(item.ExecuteAsync, $"executing menu command '{item.Id}'");
            };
        }

        return menuItem;
    }

    private static object CreateMenuHeader(ShellMenuItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Glyph) && item.IconImage is null)
        {
            return item.Title;
        }

        var iconImage = new Image
        {
            Source = item.IconImage,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            IsVisible = item.IconImage is not null,
        };
        var glyphText = new TextBlock
        {
            Text = item.Glyph,
            Classes = { "toolbar-menu-icon-text" },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = item.IconImage is null && !string.IsNullOrWhiteSpace(item.Glyph),
        };
        var iconContent = new Grid();
        iconContent.Children.Add(iconImage);
        iconContent.Children.Add(glyphText);

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border { Classes = { "toolbar-menu-icon-badge" }, Child = iconContent },
                new TextBlock { Text = item.Title, VerticalAlignment = VerticalAlignment.Center },
            },
        };
    }
}
