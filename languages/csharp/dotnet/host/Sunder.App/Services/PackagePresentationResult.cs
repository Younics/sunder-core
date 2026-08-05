namespace Sunder.App.Services;

internal enum PackagePresentationOutcome
{
    Applied,
    Unavailable,
    Failed,
}

internal sealed record PackagePresentationResult(
    PackagePresentationOutcome Outcome,
    string? Message = null,
    Exception? Exception = null)
{
    public static PackagePresentationResult Applied { get; } = new(PackagePresentationOutcome.Applied);

    public static PackagePresentationResult Unavailable(string message)
        => new(PackagePresentationOutcome.Unavailable, message);

    public static PackagePresentationResult Failed(string message, Exception? exception = null)
        => new(PackagePresentationOutcome.Failed, message, exception);

    public Exception CreateException()
        => Exception is null
            ? new InvalidOperationException(Message ?? "The running shell did not apply the package presentation.")
            : new InvalidOperationException(
                Message ?? "The running shell did not apply the package presentation.",
                Exception);
}
