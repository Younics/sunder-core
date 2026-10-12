using System.Net;
using System.Security.Cryptography;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

// Dedicated credential-free transport. Tests replace the handler, never the source policy.
internal sealed class OfficialRuntimeHttpClient(IHttpClientFactory clients) : IDisposable
{
    private readonly HttpClient _client = clients.CreateClient(ClientName);
    internal const string ClientName = "official-runtimes";
    internal static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null,
        PreAuthenticate = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15), MaxResponseHeadersLength = 32,
    };

    internal async Task<HttpResponseMessage> OpenAsync(Uri uri, ConfiguredOfficialSourceId source, CancellationToken token,
        string? version = null, string? rid = null, bool checksum = false)
    {
        for (var hop = 0; hop <= 3; hop++)
        {
            OfficialRuntimeSourcePolicy.ValidateUri(uri, source, version, rid, checksum);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.AcceptEncoding.Clear();
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            try
            {
                // A handler following redirects behind this boundary is a composition error.
                if (response.RequestMessage?.RequestUri is { } final && final != uri)
                    throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.SourceNotAllowed);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location ?? throw new InvalidDataException("Official redirect has no location.");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    response.Dispose();
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentRange is not null)
                    throw new InvalidDataException("Official source returned an unexpected partial response.");
                if (response.Content.Headers.ContentEncoding.Count != 0)
                    throw new InvalidDataException("Unexpected official response content encoding.");
                response.RequestMessage ??= new HttpRequestMessage(HttpMethod.Get, uri);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.SourceNotAllowed, "Official redirect limit exceeded.");
    }

    internal async Task<(byte[] Bytes, string Sha256)> ReadAsync(Uri uri, ConfiguredOfficialSourceId source,
        int limit, CancellationToken token, string? version = null, bool checksum = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await OpenAsync(uri, source, deadline.Token, version, checksum: checksum).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Official metadata exceeds its byte limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > limit) throw new InvalidDataException("Official metadata exceeds its byte limit.");
                output.Write(buffer, 0, count);
            }
            if (response.Content.Headers.ContentLength is { } expected && output.Length != expected)
                throw new InvalidDataException("Truncated official metadata.");
            var bytes = output.ToArray();
            return (bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Official metadata retrieval exceeded its timeout."); }
    }

    public void Dispose() => _client.Dispose();
}
