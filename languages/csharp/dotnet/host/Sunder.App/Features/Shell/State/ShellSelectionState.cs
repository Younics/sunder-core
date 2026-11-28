using Sunder.App.Models;

namespace Sunder.App.Features.Shell.State;

internal static class ShellSelectionState
{
    public static IReadOnlyList<(RailPlacement Placement, string? ViewId)> GetDeferredActivationSelections(ShellState shellState)
        =>
        [
            (RailPlacement.Middle, shellState.SelectedMiddleViewId),
            (RailPlacement.LeftTop, shellState.SelectedLeftTopViewId),
            (RailPlacement.RightTop, shellState.SelectedRightTopViewId),
            (RailPlacement.LeftBottom, shellState.SelectedLeftBottomViewId),
            (RailPlacement.RightBottom, shellState.SelectedRightBottomViewId),
        ];

    public static string? GetSelectedViewId(ShellState shellState, RailPlacement placement)
        => placement switch
        {
            RailPlacement.LeftTop => shellState.SelectedLeftTopViewId,
            RailPlacement.Middle => shellState.SelectedMiddleViewId,
            RailPlacement.RightTop => shellState.SelectedRightTopViewId,
            RailPlacement.LeftBottom => shellState.SelectedLeftBottomViewId,
            RailPlacement.RightBottom => shellState.SelectedRightBottomViewId,
            _ => shellState.SelectedMiddleViewId,
        };

    public static string? GetRememberedViewId(ShellState shellState, RailPlacement placement)
        => placement == RailPlacement.Middle
            ? shellState.SelectedMiddleViewId ?? shellState.ClosedMiddleViewId
            : GetSelectedViewId(shellState, placement);

    public static void SetSelectedViewId(ShellState shellState, RailPlacement placement, string? viewId, bool explicitlyClosed = false)
    {
        switch (placement)
        {
            case RailPlacement.LeftTop:
                shellState.SelectedLeftTopViewId = viewId;
                break;
            case RailPlacement.Middle:
                shellState.ClosedMiddleViewId = explicitlyClosed ? shellState.SelectedMiddleViewId : null;
                shellState.SelectedMiddleViewId = viewId;
                break;
            case RailPlacement.RightTop:
                shellState.SelectedRightTopViewId = viewId;
                break;
            case RailPlacement.LeftBottom:
                shellState.SelectedLeftBottomViewId = viewId;
                break;
            case RailPlacement.RightBottom:
                shellState.SelectedRightBottomViewId = viewId;
                break;
        }
    }

    public static void ForgetView(ShellState shellState, RailPlacement placement, string viewId)
    {
        if (string.Equals(GetRememberedViewId(shellState, placement), viewId, StringComparison.OrdinalIgnoreCase))
        {
            SetSelectedViewId(shellState, placement, null);
        }
    }
}
