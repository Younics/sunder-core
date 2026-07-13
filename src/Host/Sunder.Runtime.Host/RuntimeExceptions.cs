namespace Sunder.Runtime.Host;

internal abstract class RuntimeException(
    string code,
    string title,
    int statusCode,
    string detail,
    Exception? innerException = null) : Exception(detail, innerException)
{
    public string Code { get; } = code;

    public string Title { get; } = title;

    public int StatusCode { get; } = statusCode;
}

internal sealed class RuntimeValidationException(string detail)
    : RuntimeException(RuntimeErrorCodes.Validation, "Request validation failed", StatusCodes.Status400BadRequest, detail);

internal sealed class RuntimeAuthenticationException(string detail = "A valid Runtime bearer token is required.")
    : RuntimeException(RuntimeErrorCodes.Authentication, "Authentication required", StatusCodes.Status401Unauthorized, detail);

internal class RuntimeConflictException(string detail)
    : RuntimeException(RuntimeErrorCodes.Conflict, "Runtime state conflict", StatusCodes.Status409Conflict, detail);

internal sealed class RuntimeStaleGenerationException(string detail)
    : RuntimeException(RuntimeErrorCodes.StaleGeneration, "Stale Runtime generation", StatusCodes.Status409Conflict, detail);

internal sealed class RuntimeNotFoundException(string detail)
    : RuntimeException(RuntimeErrorCodes.NotFound, "Runtime resource not found", StatusCodes.Status404NotFound, detail);

internal sealed class RuntimeUnavailableException(string detail, Exception? innerException = null)
    : RuntimeException(RuntimeErrorCodes.Unavailable, "Runtime service unavailable", StatusCodes.Status503ServiceUnavailable, detail, innerException);

internal sealed class RuntimeCancellationException(string detail = "The Runtime operation was cancelled.")
    : RuntimeException(RuntimeErrorCodes.Cancellation, "Runtime operation cancelled", 499, detail);

internal sealed class RuntimeUploadLimitException(string detail)
    : RuntimeException(RuntimeErrorCodes.UploadLimit, "Upload limit exceeded", StatusCodes.Status413PayloadTooLarge, detail);

internal sealed class RuntimePackageValidationException(string detail)
    : RuntimeException(RuntimeErrorCodes.PackageValidation, "Package validation failed", StatusCodes.Status422UnprocessableEntity, detail);

internal static class RuntimeErrorCodes
{
    public const string Validation = "runtime.v1.validation";
    public const string Authentication = "runtime.v1.authentication";
    public const string Conflict = "runtime.v1.conflict";
    public const string StaleGeneration = "runtime.v1.stale-generation";
    public const string NotFound = "runtime.v1.not-found";
    public const string Unavailable = "runtime.v1.unavailable";
    public const string Cancellation = "runtime.v1.cancellation";
    public const string UploadLimit = "runtime.v1.upload-limit";
    public const string PackageValidation = "runtime.v1.package-validation";
    public const string Internal = "runtime.v1.internal";
}
