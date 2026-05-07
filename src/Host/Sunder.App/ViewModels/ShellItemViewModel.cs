using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sunder.App.ViewModels;

public partial class ShellItemViewModel : ViewModelBase
{
    private readonly Action<ShellItemViewModel> _onSelect;

    public ShellItemViewModel(
        string id,
        string glyph,
        string title,
        string packageDisplayName,
        string toolTipText,
        Models.RailPlacement placement,
        Action<ShellItemViewModel> onSelect,
        bool isDragPreview = false)
    {
        Id = id;
        Glyph = glyph;
        Title = title;
        PackageDisplayName = packageDisplayName;
        ToolTipText = toolTipText;
        Placement = placement;
        _onSelect = onSelect;
        IsDragPreview = isDragPreview;
    }

    public string Id { get; }

    public string Glyph { get; }

    public string Title { get; }

    public string PackageDisplayName { get; }

    public string ToolTipText { get; }

    public Models.RailPlacement Placement { get; }

    public bool IsDragPreview { get; }

    public bool IsHorizontalBar => Placement == Models.RailPlacement.Middle;

    public bool IsVerticalBar => !IsHorizontalBar;

    public string MenuText => $"{PackageDisplayName} · {Title}";

    public void Activate() => _onSelect(this);

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void Select() => _onSelect(this);
}
