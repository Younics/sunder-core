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
    public const int Unavailable = 6;
    public const int Timeout = 124;
    public const int Cancelled = 130;
}

internal sealed class CliUsageException(string message) : Exception(message);

internal sealed class CliHttpException(
    HttpStatusCode statusCode,
    string title,
    string? detail = null,
    string? code = null) : Exception(string.IsNullOrWhiteSpace(detail) ? title : $"{title}: {detail}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
}

internal static class CliErrorMapper
{
    public static int FromException(Exception exception) => exception switch
    {
        CliUsageException => CliExitCodes.Usage,
        RuntimeClientException runtime => FromStatus(runtime.StatusCode),
        CliHttpException http => FromStatus(http.StatusCode),
        HttpRequestException => CliExitCodes.Unavailable,
        _ => CliExitCodes.Failure,
    };

    public static int FromRegistryCode(RuntimeRegistryErrorCode code) => code switch
    {
        RuntimeRegistryErrorCode.AuthenticationRequired => CliExitCodes.Authentication,
        RuntimeRegistryErrorCode.Forbidden => CliExitCodes.Forbidden,
        RuntimeRegistryErrorCode.NotFound => CliExitCodes.NotFound,
        RuntimeRegistryErrorCode.RegistryUnavailable => CliExitCodes.Unavailable,
        RuntimeRegistryErrorCode.Cancelled => CliExitCodes.Cancelled,
        _ => CliExitCodes.Failure,
    };

    private static int FromStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => CliExitCodes.Authentication,
        HttpStatusCode.Forbidden or HttpStatusCode.Conflict => CliExitCodes.Forbidden,
        HttpStatusCode.NotFound => CliExitCodes.NotFound,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => CliExitCodes.Timeout,
        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable => CliExitCodes.Unavailable,
        _ => CliExitCodes.Failure,
    };
}
