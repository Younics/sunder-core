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
        : base(string.IsNullOrWhiteSpace(detail) ? title : $"{title}: {detail}", innerException)
    {
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
        ErrorCode = errorCode;
    }

    public HttpStatusCode StatusCode { get; }

    public string Title { get; }

    public string? Detail { get; }

    public string? ErrorCode { get; }
}
