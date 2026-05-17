using Sunder.App.Models;

namespace Sunder.App.ViewModels;

internal sealed record ShellPlacementSlot(
    RailPlacement Placement,
    PackageIconBarViewModel Bar,
    ShellPanelViewModel Panel,
    Action<ShellItemViewModel> OnSelect);
