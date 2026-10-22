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
    public static void Apply(IPackageOperationStateViewModel target, BackgroundTaskSnapshot? operation)
    {
        target.HasActiveOperation = operation?.IsActive == true;
        target.OperationCanCancel = operation is { CanCancel: true, State: not BackgroundTaskState.Cancelling };
        target.OperationIsIndeterminate = operation?.ProgressPercent is null;
        target.OperationProgressPercent = operation?.ProgressPercent ?? 0;
        target.OperationStatusText = operation is null ? string.Empty : FormatStatus(operation);
    }

    public static string FormatStatus(BackgroundTaskSnapshot snapshot)
        => snapshot.State switch
        {
            BackgroundTaskState.Queued => string.IsNullOrWhiteSpace(snapshot.StatusText) || snapshot.StatusText == "Queued"
                ? $"Queued: {snapshot.Title}" : snapshot.StatusText,
            BackgroundTaskState.Running => snapshot.StatusText,
            BackgroundTaskState.Cancelling => "Cancelling...",
            _ => snapshot.StatusText,
        };
}
