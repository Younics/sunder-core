using System.Net.Http.Headers;

namespace Sunder.Runtime.Client;

public sealed class RuntimeAuthenticatedHttpMessageHandler : DelegatingHandler
{
    private readonly Func<RuntimeConnectionInfo?> _getConnection;

    public RuntimeAuthenticatedHttpMessageHandler(
        Func<RuntimeConnectionInfo?> getConnection,
        HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        _getConnection = getConnection ?? throw new ArgumentNullException(nameof(getConnection));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var connection = _getConnection()
            ?? throw new InvalidOperationException("Authenticated Runtime connection information is not available.");
        if (request.RequestUri is null || !connection.CanSendTo(request.RequestUri))
        {
            throw new InvalidOperationException("Refusing to send Runtime credentials to a URL other than the authenticated Runtime URL.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.BearerToken);
        return base.SendAsync(request, cancellationToken);
    }
}
