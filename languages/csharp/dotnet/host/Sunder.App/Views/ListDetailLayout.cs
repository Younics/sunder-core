using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Sunder.App.Views;

/// <summary>Keeps the same controls and selection when a resizable sidebar becomes a separate page.</summary>
internal sealed class ListDetailLayout
{
    private readonly Grid _grid;
    private readonly Control _list;
    private readonly Control _details;
    private readonly GridSplitter _splitter;
    private readonly Button _back;
    private readonly Func<bool> _hasSelection;
    private readonly double _sidebarMinimum;
    private readonly double _detailMinimum;
    private GridLength _wideSidebarWidth;
    private bool _compact;
    private bool _detailPage = true;

    public ListDetailLayout(Grid grid, Control list, Control details, GridSplitter splitter, Button back, Func<bool> hasSelection)
    {
        _grid = grid;
        _list = list;
        _details = details;
        _splitter = splitter;
        _back = back;
        _hasSelection = hasSelection;
        _sidebarMinimum = grid.ColumnDefinitions[0].MinWidth;
        _detailMinimum = grid.ColumnDefinitions[2].MinWidth;
        grid.SizeChanged += (_, _) => Refresh();
        back.Click += (_, _) =>
        {
            _detailPage = false;
            Refresh();
            _list.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible)?.Focus();
        };
    }

    public void OpenDetails()
    {
        _detailPage = true;
        Refresh();
    }

    public void OpenSelectedRow(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && sender is ListBox { SelectedItem: not null }
            && e.Source is Control source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
            OpenDetails();
    }

    public void OpenSelectedRow(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is ListBox { SelectedItem: not null })
        {
            OpenDetails();
            if (_compact) _back.Focus();
        }
    }

    public void Refresh()
    {
        var width = _grid.Bounds.Width;
        if (width <= 0) return;
        var columns = _grid.ColumnDefinitions;
        var sidebar = columns[0];
        var compact = width < 850;
        if (compact && sidebar.Width.IsAbsolute) _wideSidebarWidth = sidebar.Width;
        else if (!compact && _compact) sidebar.Width = _wideSidebarWidth;

        _compact = compact;
        var open = _detailPage && _hasSelection();
        _list.IsVisible = !compact || !open;
        _details.IsVisible = !compact || open;
        _back.IsVisible = compact && open;
        _splitter.IsVisible = !compact;
        sidebar.MaxWidth = compact ? double.PositiveInfinity : Math.Min(900, Math.Max(_sidebarMinimum, width - _detailMinimum - 4));
        columns[2].MinWidth = compact ? 0 : _detailMinimum;
        if (compact) sidebar.Width = GridLength.Star;
        columns[1].Width = new GridLength(compact ? 0 : 4);
        columns[2].Width = compact ? new GridLength(0) : GridLength.Star;
        Grid.SetColumn(_details, compact ? 0 : 2);
    }
}
