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
    Func<MainWindowViewModel?> viewModelAccessor)
{
    public bool Show()
    {
        var viewModel = viewModelAccessor();
        if (viewModel is null)
        {
            return false;
        }

        toolbarMainMenu.ItemsSource = BuildToolbarMainMenuItems(viewModel);
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
        if (!toolbarMainMenu.IsVisible)
        {
            return;
        }

        if (e.Source is not Visual visual)
        {
            return;
        }

        if (!ReferenceEquals(TopLevel.GetTopLevel(visual), TopLevel.GetTopLevel(toolbarLeftMenuHost)))
        {
            return;
        }

        var ancestors = visual.GetSelfAndVisualAncestors().OfType<StyledElement>().ToArray();
        if (ancestors.Contains(toolbarLeftMenuHost) || ancestors.Any(x => x is Menu or MenuItem))
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

    private object[] BuildToolbarMainMenuItems(MainWindowViewModel viewModel)
    {
        var packageMenuGroups = viewModel.GetPackageViewGroups();
        var packagesMenu = CreateToolbarMenuItem("Packages");
        packagesMenu.ItemsSource = packageMenuGroups.Select(BuildPackageGroupMenuItem).ToArray();

        var viewMenu = new MenuItem { Header = "View", Classes = { "toolbar-menu-root-item" } };
        viewMenu.ItemsSource = new object[] { packagesMenu };
        viewMenu.PointerEntered += (_, _) => viewMenu.IsSubMenuOpen = true;

        return new object[] { viewMenu };
    }

    private MenuItem BuildPackageGroupMenuItem(PackageViewMenuGroup group)
    {
        var menuItem = CreateToolbarMenuItem(group.PackageDisplayName, group.PackageGlyph, group.PackageIconUri);
        menuItem.ItemsSource = group.Views.Select(BuildPackageViewMenuItem).ToArray();
        return menuItem;
    }

    private MenuItem BuildPackageViewMenuItem(PackageViewMenuItem item)
    {
        var menuItem = CreateToolbarMenuItem(item.Title, item.Glyph, item.IconUri);
        menuItem.IsEnabled = !item.IsInHotbar;
        if (!item.IsInHotbar)
        {
            menuItem.Click += async (_, _) =>
            {
                Hide();
                var viewModel = viewModelAccessor();
                if (viewModel is not null)
                {
                    await viewModel.OpenPackageViewPanelAsync(item.ViewId);
                }
            };
        }

        return menuItem;
    }

    private static MenuItem CreateToolbarMenuItem(string header, string? glyph = null, Uri? iconUri = null)
        => new() { Header = CreateToolbarMenuHeader(header, glyph, iconUri), Classes = { "toolbar-menu-item" } };

    private static object CreateToolbarMenuHeader(string header, string? glyph, Uri? iconUri)
    {
        if (string.IsNullOrWhiteSpace(glyph) && iconUri is null)
        {
            return header;
        }

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var iconImage = new Image
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            IsVisible = false,
        };
        var glyphText = new TextBlock
        {
            Text = glyph,
            Classes = { "toolbar-menu-icon-text" },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = !string.IsNullOrWhiteSpace(glyph),
        };

        var iconContent = new Grid();
        iconContent.Children.Add(iconImage);
        iconContent.Children.Add(glyphText);
        content.Children.Add(new Border
        {
            Classes = { "toolbar-menu-icon-badge" },
            Child = iconContent,
        });
        content.Children.Add(new TextBlock
        {
            Text = header,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (iconUri is not null)
        {
            _ = LoadToolbarMenuIconAsync(iconImage, glyphText, iconUri);
        }

        return content;
    }

    private static async Task LoadToolbarMenuIconAsync(Image iconImage, TextBlock glyphText, Uri iconUri)
    {
        var result = await PackageIconImageLoader.LoadAsync(iconUri);
        if (result.Image is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            iconImage.Source = result.Image;
            iconImage.IsVisible = true;
            glyphText.IsVisible = false;
        });
    }
}
