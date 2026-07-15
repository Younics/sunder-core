using System.Net;

namespace Sunder.Runtime.Client;

public sealed class RuntimeClientException : Exception
{
    public RuntimeClientException(
        HttpStatusCode statusCode,
        string title,
        string? detail = null,
        string? errorCode = null,
        Exception? innerException = null)
        : this(statusCode, title, detail, errorCode, correlationId: null, innerException)
    {
    }

    public RuntimeClientException(
        HttpStatusCode statusCode,
        string title,
        string? detail,
        string? errorCode,
        string? correlationId,
        Exception? innerException)
        : base(string.IsNullOrWhiteSpace(detail) ? title : $"{title}: {detail}", innerException)
    {
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
        ErrorCode = errorCode;
        CorrelationId = correlationId;
    }

    public HttpStatusCode StatusCode { get; }

    public string Title { get; }

    public string? Detail { get; }

    public string? ErrorCode { get; }

    public string? CorrelationId { get; }
}
