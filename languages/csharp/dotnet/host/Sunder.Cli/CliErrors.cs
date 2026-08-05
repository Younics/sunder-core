using System.Net;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal static class CliExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int NotFound = 3;
    public const int Authentication = 4;
    public const int Forbidden = 5;
    public const int Conflict = Forbidden;
    public const int Unavailable = 6;
    public const int Timeout = 124;
    public const int Cancelled = 130;
}

internal sealed class CliUsageException(string message) : Exception(message);
internal sealed class CliConfigurationException(string message, Exception? innerException = null) : Exception(message, innerException);
internal sealed class CliConflictException(string message, Exception? innerException = null) : Exception(message, innerException);
internal sealed class CliAuthenticationException(string message) : Exception(message);
internal sealed class CliUnavailableException(string message) : Exception(message);

internal sealed class CliHttpException(
    HttpStatusCode statusCode,
    string title,
    string? detail = null,
    string? code = null,
    string? correlationId = null) : Exception(string.IsNullOrWhiteSpace(detail) ? title : $"{title}: {detail}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public string? Code { get; } = code;
    public string? CorrelationId { get; } = CliText.SanitizeToken(correlationId);
}

internal sealed record CliErrorDescriptor(string Code, string Message, string? CorrelationId, int ExitCode);

internal static class CliErrorMapper
{
    public static int FromException(Exception exception) => Describe(exception).ExitCode;

    public static CliErrorDescriptor Describe(Exception exception)
    {
        if (IsTimeout(exception))
            return new("cli.timeout", "Operation timed out. Use --timeout <duration> to increase the request timeout.", CorrelationId(exception), CliExitCodes.Timeout);
        return exception switch
        {
            CliUsageException => new("cli.usage", exception.Message, null, CliExitCodes.Usage),
            CliConfigurationException => new("cli.configuration.invalid", exception.Message, null, CliExitCodes.Usage),
            CliAuthenticationException => new("cli.authentication.required", exception.Message, null, CliExitCodes.Authentication),
            CliUnavailableException => new("cli.service.unavailable", exception.Message, null, CliExitCodes.Unavailable),
            ArgumentException => new("cli.usage", exception.Message, null, CliExitCodes.Usage),
            FileNotFoundException or DirectoryNotFoundException => new("cli.resource.not_found", exception.Message, null, CliExitCodes.NotFound),
            UnauthorizedAccessException => new("cli.resource.forbidden", exception.Message, null, CliExitCodes.Forbidden),
            CliConflictException => new("cli.resource.conflict", exception.Message, null, CliExitCodes.Conflict),
            RuntimeClientException runtime => new(
                string.IsNullOrWhiteSpace(runtime.ErrorCode) ? "runtime.request.failed" : runtime.ErrorCode,
                runtime.Message,
                runtime.CorrelationId,
                FromStatus(runtime.StatusCode)),
            RuntimeProtocolException => new(
                "runtime.protocol.incompatible",
                exception.Message,
                null,
                CliExitCodes.Unavailable),
            CliHttpException http => new(
                string.IsNullOrWhiteSpace(http.Code) ? "registry.request.failed" : http.Code,
                http.Message,
                http.CorrelationId,
                FromStatus(http.StatusCode)),
            HttpRequestException { StatusCode: { } status } => new(
                "cli.service.request_failed",
                exception.Message,
                CorrelationId(exception),
                FromStatus(status)),
            HttpRequestException => new("cli.service.unavailable", exception.Message, CorrelationId(exception), CliExitCodes.Unavailable),
            _ => new("cli.operation.failed", exception.Message, CorrelationId(exception), CliExitCodes.Failure),
        };
    }

    public static bool IsTimeout(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException or TimeoutException
                || current is RuntimeClientException runtime && IsTimeoutStatus(runtime.StatusCode)
                || current is CliHttpException http && IsTimeoutStatus(http.StatusCode))
            {
                return true;
            }
        }
        return false;
    }

    public static int FromRegistryCode(RuntimeRegistryErrorCode code) => code switch
    {
        RuntimeRegistryErrorCode.AuthenticationRequired => CliExitCodes.Authentication,
        RuntimeRegistryErrorCode.Forbidden => CliExitCodes.Forbidden,
        RuntimeRegistryErrorCode.NotFound => CliExitCodes.NotFound,
        RuntimeRegistryErrorCode.Conflict => CliExitCodes.Conflict,
        RuntimeRegistryErrorCode.RegistryUnavailable => CliExitCodes.Unavailable,
        RuntimeRegistryErrorCode.Cancelled => CliExitCodes.Cancelled,
        _ => CliExitCodes.Failure,
    };

    private static int FromStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => CliExitCodes.Authentication,
        HttpStatusCode.Forbidden => CliExitCodes.Forbidden,
        HttpStatusCode.NotFound => CliExitCodes.NotFound,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => CliExitCodes.Timeout,
        HttpStatusCode.Conflict => CliExitCodes.Conflict,
        HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable => CliExitCodes.Unavailable,
        HttpStatusCode.RequestEntityTooLarge => CliExitCodes.Failure,
        _ => CliExitCodes.Failure,
    };

    private static bool IsTimeoutStatus(HttpStatusCode status)
        => status is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout;

    private static string? CorrelationId(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is RuntimeClientException { CorrelationId: { Length: > 0 } runtimeCorrelation })
                return runtimeCorrelation;
            if (current is CliHttpException { CorrelationId: { Length: > 0 } registryCorrelation })
                return registryCorrelation;
        }
        return null;
    }
}
