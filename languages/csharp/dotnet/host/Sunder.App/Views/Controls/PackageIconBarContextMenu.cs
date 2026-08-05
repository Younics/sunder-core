using Avalonia.Controls;
using Sunder.App.ViewModels;

namespace Sunder.App.Views.Controls;

internal static class PackageIconBarContextMenu
{
    public static void OpenItemMenu(Control host, PackageIconBarViewModel viewModel, ShellItemViewModel item)
    {
        var reloadItem = new MenuItem
        {
            Header = "Reload",
            Classes = { "package-bar-context-menu-item" },
        };
        reloadItem.Click += async (_, _) => await viewModel.ReloadItemAsync(item.Id);

        var removeItem = new MenuItem
        {
            Header = "Remove",
            Classes = { "package-bar-context-menu-item" },
        };
        removeItem.Click += (_, _) => viewModel.RemoveItem(item.Id);

        OpenMenu(host, [reloadItem, removeItem], "package-bar-context-menu");
    }

    public static void OpenOverflowMenu(Control host, PackageIconBarViewModel viewModel)
    {
        var items = viewModel.OverflowItems.Select(item => new MenuItem
        {
            Header = item.MenuText,
            Command = item.SelectCommand,
        }).ToArray();

        OpenMenu(host, items);
    }

    private static void OpenMenu(Control host, IReadOnlyList<MenuItem> items, string? menuClass = null)
    {
        var menu = new ContextMenu
        {
            ItemsSource = items,
        };
        if (!string.IsNullOrWhiteSpace(menuClass))
        {
            menu.Classes.Add(menuClass);
        }

        host.ContextMenu = menu;
        menu.Open(host);
    }
}
