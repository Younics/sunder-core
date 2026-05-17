using Sunder.App.Models;

namespace Sunder.App.ViewModels;

internal sealed class ShellSelectionPresenter
{
    private ShellItemViewModel? _selectedLeftTopItem;
    private ShellItemViewModel? _selectedMiddleItem;
    private ShellItemViewModel? _selectedRightTopItem;
    private ShellItemViewModel? _selectedLeftBottomItem;
    private ShellItemViewModel? _selectedRightBottomItem;

    public bool HasLeftTopPanelContent => _selectedLeftTopItem is not null;

    public bool HasMiddleSelection => _selectedMiddleItem is not null;

    public bool HasRightTopPanelContent => _selectedRightTopItem is not null;

    public bool HasLeftBottomPanelContent => _selectedLeftBottomItem is not null;

    public bool HasRightBottomPanelContent => _selectedRightBottomItem is not null;

    public ShellItemViewModel? GetSelectedItem(RailPlacement placement)
        => placement switch
        {
            RailPlacement.LeftTop => _selectedLeftTopItem,
            RailPlacement.Middle => _selectedMiddleItem,
            RailPlacement.RightTop => _selectedRightTopItem,
            RailPlacement.LeftBottom => _selectedLeftBottomItem,
            RailPlacement.RightBottom => _selectedRightBottomItem,
            _ => _selectedMiddleItem,
        };

    public void SetSelectedItem(RailPlacement placement, ShellItemViewModel? item)
    {
        switch (placement)
        {
            case RailPlacement.LeftTop:
                _selectedLeftTopItem = item;
                break;
            case RailPlacement.Middle:
                _selectedMiddleItem = item;
                break;
            case RailPlacement.RightTop:
                _selectedRightTopItem = item;
                break;
            case RailPlacement.LeftBottom:
                _selectedLeftBottomItem = item;
                break;
            case RailPlacement.RightBottom:
                _selectedRightBottomItem = item;
                break;
        }
    }

    public void Select(PackageIconBarViewModel bar, RailPlacement placement, ShellItemViewModel selected)
    {
        foreach (var item in bar.Items)
        {
            item.IsSelected = ReferenceEquals(item, selected);
        }

        SetSelectedItem(placement, selected);
    }

    public void Clear(PackageIconBarViewModel bar, RailPlacement placement)
    {
        foreach (var item in bar.Items)
        {
            item.IsSelected = false;
        }

        SetSelectedItem(placement, null);
    }
}
