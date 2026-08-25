namespace Sunder.Runtime.Host.Services;

internal enum RuntimeStackFailureCategory
{
    Validation,
    PackageValidation,
    StaleGeneration,
    NotFound,
    Unavailable,
}

internal sealed record RuntimeStackFailure(
    RuntimeStackFailureCategory Category,
    string? Detail);

internal sealed record RuntimeStackOperationResult<TResponse>(
    TResponse Response,
    RuntimeStackFailure? Failure)
{
    public static RuntimeStackOperationResult<TResponse> Succeeded(TResponse response)
        => new(response, null);

    public static RuntimeStackOperationResult<TResponse> Failed(
        TResponse response,
        RuntimeStackFailureCategory category,
        string? detail)
        => new(response, new RuntimeStackFailure(category, detail));
}
