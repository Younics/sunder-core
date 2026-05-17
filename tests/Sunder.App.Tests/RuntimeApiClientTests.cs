using System.Net;
using System.Net.Http.Json;
using Sunder.App.Services;
using Sunder.Protocol;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimeApiClientTests
{
    [Fact]
    public async Task GetSystemStatusAsync_UsesInjectedHttpClientAndRuntimeBaseUri()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new SystemStatusResponse("Runtime", "1.0.0", true, DateTimeOffset.UtcNow)),
        });
        using var httpClient = new HttpClient(handler);
        using var runtimeApiClient = new RuntimeApiClient(() => new Uri("http://127.0.0.1:5275/"), httpClient);

        var status = await runtimeApiClient.GetSystemStatusAsync();

        Assert.NotNull(status);
        Assert.Equal("Runtime", status.Name);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri("http://127.0.0.1:5275/api/system"), request.RequestUri);
    }

    [Fact]
    public async Task Dispose_DoesNotDisposeInjectedHttpClient()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        var runtimeApiClient = new RuntimeApiClient(() => new Uri("http://127.0.0.1:5275/"), httpClient);

        runtimeApiClient.Dispose();

        using var response = await httpClient.GetAsync(new Uri("http://127.0.0.1:5275/health"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        private readonly List<RecordedRequest> _requests = [];

        public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri));
            return Task.FromResult(send(request));
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? RequestUri);
}
