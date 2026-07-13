namespace Sunder.App.ViewModels;

internal enum PresentationOperationStatus
{
    Idle,
    Running,
    Succeeded,
    Failed,
}

internal readonly record struct PresentationOperationState(
    PresentationOperationStatus Status,
    string ErrorMessage)
{
    public static PresentationOperationState Idle => new(PresentationOperationStatus.Idle, string.Empty);

    public static PresentationOperationState Running => new(PresentationOperationStatus.Running, string.Empty);

    public static PresentationOperationState Succeeded => new(PresentationOperationStatus.Succeeded, string.Empty);

    public static PresentationOperationState Failed(string errorMessage)
        => new(PresentationOperationStatus.Failed, errorMessage);

    public bool IsRunning => Status == PresentationOperationStatus.Running;

    public bool IsSucceeded => Status == PresentationOperationStatus.Succeeded;

    public bool HasError => Status == PresentationOperationStatus.Failed;
}
