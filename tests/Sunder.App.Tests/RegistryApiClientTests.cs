using System.Net;
using System.Net.Http.Json;
using Sunder.App.Services;
using Sunder.Registry.Shared;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RegistryApiClientTests
{
    [Fact]
    public async Task SearchAsync_UsesInjectedHttpClientAndRegistryUrl()
    {
        var package = new RegistryPackageSummary(
            "sunder.package.agent",
            "Sunder Agent",
            "Adds local agents.",
            "1.0.0",
            IconUrl: null,
            IsYanked: false,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create<IReadOnlyList<RegistryPackageSummary>>([package]),
        });
        using var httpClient = new HttpClient(handler);
        using var registryClient = new RegistryApiClient(new Uri("https://registry.example/"), httpClient);

        var results = await registryClient.SearchAsync(" agent package ", skip: 10, take: 20);

        Assert.Collection(results, result => Assert.Equal("sunder.package.agent", result.PackageId));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri("https://registry.example/api/packages?skip=10&take=20&query=agent%20package"), request.RequestUri);
    }

    [Fact]
    public async Task Dispose_DoesNotDisposeInjectedHttpClient()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        var registryClient = new RegistryApiClient(new Uri("https://registry.example/"), httpClient);

        registryClient.Dispose();

        using var response = await httpClient.GetAsync(new Uri("https://registry.example/health"));
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
