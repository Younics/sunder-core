using Avalonia.Layout;
using Sunder.App.Models;

namespace Sunder.App.ViewModels;

internal sealed class ShellLayoutPresenter
{
    public ShellLayoutPresenter(
        Action<string, RailPlacement, int?> movePackageView,
        Func<string, ValueTask<bool>> reloadPackageViewAsync,
        Func<string, bool> removePackageViewFromHotbar,
        Action<ShellItemViewModel> toggleLeftTopView,
        Action<ShellItemViewModel> toggleMiddleView,
        Action<ShellItemViewModel> toggleRightTopView,
        Action<ShellItemViewModel> toggleLeftBottomView,
        Action<ShellItemViewModel> toggleRightBottomView)
    {
        LeftTopBar = new PackageIconBarViewModel(RailPlacement.LeftTop, Orientation.Vertical, movePackageView, reloadPackageViewAsync, removePackageViewFromHotbar);
        MiddleBar = new PackageIconBarViewModel(RailPlacement.Middle, Orientation.Horizontal, movePackageView, reloadPackageViewAsync, removePackageViewFromHotbar);
        RightTopBar = new PackageIconBarViewModel(RailPlacement.RightTop, Orientation.Vertical, movePackageView, reloadPackageViewAsync, removePackageViewFromHotbar);
        LeftBottomBar = new PackageIconBarViewModel(RailPlacement.LeftBottom, Orientation.Vertical, movePackageView, reloadPackageViewAsync, removePackageViewFromHotbar);
        RightBottomBar = new PackageIconBarViewModel(RailPlacement.RightBottom, Orientation.Vertical, movePackageView, reloadPackageViewAsync, removePackageViewFromHotbar);

        LeftTopPanel = new ShellPanelViewModel();
        MiddlePanel = new ShellPanelViewModel();
        RightTopPanel = new ShellPanelViewModel();
        LeftBottomPanel = new ShellPanelViewModel();
        RightBottomPanel = new ShellPanelViewModel();

        _toggleLeftTopView = toggleLeftTopView;
        _toggleMiddleView = toggleMiddleView;
        _toggleRightTopView = toggleRightTopView;
        _toggleLeftBottomView = toggleLeftBottomView;
        _toggleRightBottomView = toggleRightBottomView;
    }

    private readonly Action<ShellItemViewModel> _toggleLeftTopView;
    private readonly Action<ShellItemViewModel> _toggleMiddleView;
    private readonly Action<ShellItemViewModel> _toggleRightTopView;
    private readonly Action<ShellItemViewModel> _toggleLeftBottomView;
    private readonly Action<ShellItemViewModel> _toggleRightBottomView;

    public PackageIconBarViewModel LeftTopBar { get; }

    public PackageIconBarViewModel MiddleBar { get; }

    public PackageIconBarViewModel RightTopBar { get; }

    public PackageIconBarViewModel LeftBottomBar { get; }

    public PackageIconBarViewModel RightBottomBar { get; }

    public ShellPanelViewModel LeftTopPanel { get; }

    public ShellPanelViewModel MiddlePanel { get; }

    public ShellPanelViewModel RightTopPanel { get; }

    public ShellPanelViewModel LeftBottomPanel { get; }

    public ShellPanelViewModel RightBottomPanel { get; }

    public IReadOnlyList<ShellPlacementSlot> GetSlots()
        =>
        [
            new(RailPlacement.LeftTop, LeftTopBar, LeftTopPanel, _toggleLeftTopView),
            new(RailPlacement.Middle, MiddleBar, MiddlePanel, _toggleMiddleView),
            new(RailPlacement.RightTop, RightTopBar, RightTopPanel, _toggleRightTopView),
            new(RailPlacement.LeftBottom, LeftBottomBar, LeftBottomPanel, _toggleLeftBottomView),
            new(RailPlacement.RightBottom, RightBottomBar, RightBottomPanel, _toggleRightBottomView),
        ];

    public PackageIconBarViewModel GetBar(RailPlacement placement)
        => placement switch
        {
            RailPlacement.LeftTop => LeftTopBar,
            RailPlacement.Middle => MiddleBar,
            RailPlacement.RightTop => RightTopBar,
            RailPlacement.LeftBottom => LeftBottomBar,
            RailPlacement.RightBottom => RightBottomBar,
            _ => MiddleBar,
        };

    public ShellPanelViewModel GetPanel(RailPlacement placement)
        => placement switch
        {
            RailPlacement.LeftTop => LeftTopPanel,
            RailPlacement.Middle => MiddlePanel,
            RailPlacement.RightTop => RightTopPanel,
            RailPlacement.LeftBottom => LeftBottomPanel,
            RailPlacement.RightBottom => RightBottomPanel,
            _ => MiddlePanel,
        };
}
