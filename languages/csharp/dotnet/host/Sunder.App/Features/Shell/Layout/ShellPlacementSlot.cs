using Sunder.App.Models;
using Sunder.App.ViewModels;

namespace Sunder.App.Features.Shell.Layout;

internal sealed record ShellPlacementSlot(
    RailPlacement Placement,
    PackageIconBarViewModel Bar,
    ShellPanelViewModel Panel,
    Action<ShellItemViewModel> OnSelect);
