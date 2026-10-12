using Sunder.Runtime.Contracts;

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

    public virtual object? Details => null;
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

internal sealed class RuntimeAppPolicyException(string code)
    : RuntimeException(code, "App process policy prevents the operation", StatusCodes.Status409Conflict, code);

internal sealed class RuntimeUploadLimitException(string detail)
    : RuntimeException(RuntimeErrorCodes.UploadLimit, "Upload limit exceeded", StatusCodes.Status413PayloadTooLarge, detail);

internal sealed class RuntimePackageValidationException(string detail)
    : RuntimeException(RuntimeErrorCodes.PackageValidation, "Package validation failed", StatusCodes.Status422UnprocessableEntity, detail);

internal sealed class RuntimeExecutionRuntimeException : RuntimeException
{
    public RuntimeExecutionRuntimeException(string code, string? detail = null)
        : this(Find(code), detail)
    {
    }

    private RuntimeExecutionRuntimeException(ExecutionRuntimeErrorDescriptor descriptor, string? detail)
        : base(
            descriptor.Code,
            "Execution runtime request failed",
            descriptor.HttpStatus,
            string.IsNullOrWhiteSpace(detail) ? descriptor.SafeMessage : detail)
    {
    }

    private static ExecutionRuntimeErrorDescriptor Find(string code)
        => ExecutionRuntimeErrorCatalog.All.FirstOrDefault(descriptor =>
               string.Equals(descriptor.Code, code, StringComparison.Ordinal))
           ?? throw new ArgumentException("The execution-runtime error code is not registered.", nameof(code));
}

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
