using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
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
        var connection = new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5275/"), "test-runtime-token");
        using var runtimeApiClient = new RuntimeApiClient(() => connection, handler);

        var status = await runtimeApiClient.GetSystemStatusAsync();

        Assert.NotNull(status);
        Assert.Equal("Runtime", status.Name);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri("http://127.0.0.1:5275/api/v1/system"), request.RequestUri);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.True(request.HasAuthorizationParameter);
    }

    [Fact]
    public async Task Request_WhenConnectionIsMissing_FailsBeforeSending()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var runtimeApiClient = new RuntimeApiClient(() => null, handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtimeApiClient.GetSystemStatusAsync());

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StreamRuntimeEventsAsync_ParsesSseAndSendsReconnectSequence()
    {
        var runtimeEvent = new RuntimeEventDescriptor(
            43,
            DateTimeOffset.UtcNow,
            RuntimeEventKind.SessionGenerationChanged,
            8,
            RuntimeOperationPhase.Idle,
            ["test.package"]);
        var content = $"id: 43\nevent: runtime\ndata: {JsonSerializer.Serialize(runtimeEvent, new JsonSerializerOptions(JsonSerializerDefaults.Web))}\n\n";
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content),
        });
        var connection = new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5275/"), "test-runtime-token");
        using var runtimeApiClient = new RuntimeApiClient(() => connection, handler);

        var received = new List<RuntimeEventDescriptor>();
        await foreach (var streamedEvent in runtimeApiClient.StreamRuntimeEventsAsync(42))
        {
            received.Add(streamedEvent);
        }

        var receivedEvent = Assert.Single(received);
        Assert.Equal(43, receivedEvent.SequenceId);
        Assert.Equal(8, receivedEvent.SessionGeneration);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("42", request.LastEventId);
        Assert.Equal("Bearer", request.AuthorizationScheme);
    }

    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        private readonly List<RecordedRequest> _requests = [];

        public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization?.Scheme,
                !string.IsNullOrWhiteSpace(request.Headers.Authorization?.Parameter),
                request.Headers.TryGetValues("Last-Event-ID", out var values) ? values.SingleOrDefault() : null));
            return Task.FromResult(send(request));
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        string? AuthorizationScheme,
        bool HasAuthorizationParameter,
        string? LastEventId);
}
