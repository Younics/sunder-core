using Sunder.Sdk.Abstractions;

namespace Sunder.App.ViewModels;

internal interface IPackageOperationStateViewModel
{
    bool HasActiveOperation { get; set; }

    bool OperationCanCancel { get; set; }

    bool OperationIsIndeterminate { get; set; }

    double OperationProgressPercent { get; set; }

    string OperationStatusText { get; set; }
}

internal static class PackageOperationStateProjector
{
    public static void Apply(IPackageOperationStateViewModel target, BackgroundProcessSnapshot? operation)
    {
        target.HasActiveOperation = operation?.IsActive == true;
        target.OperationCanCancel = operation is { CanCancel: true, State: not BackgroundProcessState.Cancelling };
        target.OperationIsIndeterminate = operation?.ProgressPercent is null;
        target.OperationProgressPercent = operation?.ProgressPercent ?? 0;
        target.OperationStatusText = operation is null ? string.Empty : FormatStatus(operation);
    }

    public static string FormatStatus(BackgroundProcessSnapshot snapshot)
        => snapshot.State switch
        {
            BackgroundProcessState.Queued => $"Queued: {snapshot.Title}",
            BackgroundProcessState.Running => snapshot.StatusText,
            BackgroundProcessState.Cancelling => "Cancelling...",
            _ => snapshot.StatusText,
        };
}
