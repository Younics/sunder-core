using System.Net;
using System.Text;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Sdk.Runtime;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageRuntimeClientTests
{
    [Fact]
    public async Task InvokeAsync_SendsAuthenticatedPackageScopedJsonAndReadsTypedResponse()
    {
        var handler = new RecordingHandler();
        using var transport = new RuntimePackageOperationClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5199"), "test-token"),
            handler);
        var client = new AppPackageRuntimeClient("test.package", transport);
        var operation = new PackageRuntimeOperation<EchoRequest, EchoResponse>("echo.run");

        var response = await client.InvokeAsync(operation, new EchoRequest("hello"));

        Assert.True(client.IsAvailable);
        Assert.Equal("HELLO", response.Value);
        Assert.Equal(
            "http://127.0.0.1:5199/api/v1/packages/test.package/operations/echo.run",
            handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-token", handler.AuthorizationParameter);
        Assert.Contains("\"value\":\"hello\"", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscribeAsync_ReadsTypedEnvelopeEventsAndTerminalFrame()
    {
        var handler = new RecordingHandler();
        using var transport = new RuntimePackageOperationClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5199"), "test-token"),
            handler);
        var client = new AppPackageRuntimeClient("test.package", transport);
        var stream = new PackageRuntimeStream<CountRequest, CountEvent>("count.events");
        var events = new List<CountEvent>();

        await foreach (var value in client.SubscribeAsync(stream, new CountRequest(2)))
        {
            events.Add(value);
        }

        Assert.Equal([1, 2], events.Select(value => value.Value));
        Assert.Equal(
            "http://127.0.0.1:5199/api/v1/packages/test.package/streams/count.events",
            handler.RequestUri?.AbsoluteUri);
        Assert.Equal("test-token", handler.AuthorizationParameter);
        Assert.Contains("\"count\":2", handler.RequestBody, StringComparison.Ordinal);
    }

    private sealed record EchoRequest(string Value);

    private sealed record EchoResponse(string Value);

    private sealed record CountRequest(int Count);

    private sealed record CountEvent(int Value);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            if (request.RequestUri?.AbsolutePath == "/api/handshake")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"protocolIdentity\":\"dev.sunder.runtime\",\"protocolRevision\":1,\"minimumSupportedRevision\":1,\"maximumSupportedRevision\":1,\"runtimeInstanceId\":\"11111111-1111-1111-1111-111111111111\",\"supportedFeatures\":[\"api.v1\"],\"product\":{\"productName\":\"Sunder.Runtime.Host\",\"productVersion\":\"Development\",\"informationalVersion\":\"Development\"}}",
                        Encoding.UTF8,
                        "application/json"),
                };
            }
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var responseBody = request.RequestUri?.AbsolutePath.Contains("/streams/", StringComparison.Ordinal) == true
                ? "{\"type\":\"event\",\"event\":{\"value\":1}}\n{\"type\":\"event\",\"event\":{\"value\":2}}\n{\"type\":\"completed\"}\n"
                : "{\"value\":\"HELLO\"}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
