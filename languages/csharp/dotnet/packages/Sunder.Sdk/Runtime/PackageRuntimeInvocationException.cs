using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Runtime;

/// <summary>Represents a Runtime-reported failure of a package-scoped operation invocation.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RuntimeOperationsV1)]
[SunderSdkCapability(SunderSdkCapabilities.RuntimeInvocationErrorsV1)]
public sealed class PackageRuntimeInvocationException : Exception
{
    /// <summary>Creates a package-visible Runtime invocation failure.</summary>
    /// <exception cref="ArgumentException"><paramref name="code"/> is not a safe lowercase ASCII error code.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> is outside the HTTP status range.</exception>
    public PackageRuntimeInvocationException(
        string code,
        bool isTransient,
        int? statusCode = null,
        string? correlationId = null)
        : base(CreateMessage(ValidateCode(code), correlationId))
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode), "Runtime invocation status codes must be valid HTTP status codes.");
        }
        if (correlationId is not null && !IsSafeToken(correlationId))
        {
            throw new ArgumentException("Runtime invocation correlation ids must be bounded portable ASCII tokens.", nameof(correlationId));
        }

        Code = code;
        IsTransient = isTransient;
        StatusCode = statusCode;
        CorrelationId = correlationId;
    }

    /// <summary>Gets the bounded safe Runtime error code.</summary>
    public string Code { get; }

    /// <summary>Gets whether retrying after the current transient condition may succeed.</summary>
    public bool IsTransient { get; }

    /// <summary>Gets the HTTP status reported by Runtime, when available.</summary>
    public int? StatusCode { get; }

    /// <summary>Gets the safe Runtime diagnostic correlation id, when available.</summary>
    public string? CorrelationId { get; }

    private static string ValidateCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (!IsSafeToken(code)
            || code.Any(static character => char.IsAsciiLetter(character) && !char.IsAsciiLetterLower(character)))
        {
            throw new ArgumentException("Runtime invocation error codes must be lowercase portable ASCII tokens.", nameof(code));
        }

        return code;
    }

    private static bool IsSafeToken(string value)
        => value.Length is > 0 and <= 128
           && value.All(static character => char.IsAsciiLetterOrDigit(character)
                                           || character is '.' or '-' or '_');

    private static string CreateMessage(string code, string? correlationId)
        => string.IsNullOrEmpty(correlationId)
            ? $"Package Runtime invocation failed with code '{code}'."
            : $"Package Runtime invocation failed with code '{code}' (correlation '{correlationId}').";
}
