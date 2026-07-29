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

    [Fact]
    public async Task InvokeAsync_TranslatesRuntimeProblemIntoSdkInvocationException()
    {
        var handler = new RecordingHandler
        {
            OperationResponse = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    """
                    {
                      "title":"Runtime unavailable",
                      "detail":"Host-only diagnostic detail.",
                      "code":"runtime.v1.unavailable",
                      "correlationId":"safe-correlation-42"
                    }
                    """,
                    Encoding.UTF8,
                    "application/problem+json"),
            },
        };
        using var transport = new RuntimePackageOperationClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5199"), "test-token"),
            handler);
        var client = new AppPackageRuntimeClient("test.package", transport);
        var operation = new PackageRuntimeOperation<EchoRequest, EchoResponse>("echo.run");

        var exception = await Assert.ThrowsAsync<PackageRuntimeInvocationException>(async () =>
            await client.InvokeAsync(operation, new EchoRequest("hello")));

        Assert.Equal("runtime.v1.unavailable", exception.Code);
        Assert.True(exception.IsTransient);
        Assert.Equal(503, exception.StatusCode);
        Assert.Equal("safe-correlation-42", exception.CorrelationId);
        Assert.DoesNotContain("Host-only diagnostic detail", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task InvokeAsync_SanitizesUntrustedRuntimeProblemMetadata()
    {
        var handler = new RecordingHandler
        {
            OperationResponse = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """
                    {
                      "detail":"Host-only diagnostic detail.",
                      "code":"Runtime V1 BAD!",
                      "correlationId":"unsafe correlation"
                    }
                    """,
                    Encoding.UTF8,
                    "application/problem+json"),
            },
        };
        using var transport = new RuntimePackageOperationClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5199"), "test-token"),
            handler);
        var client = new AppPackageRuntimeClient("test.package", transport);
        var operation = new PackageRuntimeOperation<EchoRequest, EchoResponse>("echo.run");

        var exception = await Assert.ThrowsAsync<PackageRuntimeInvocationException>(async () =>
            await client.InvokeAsync(operation, new EchoRequest("hello")));

        Assert.Equal("runtime.v1.request-failed", exception.Code);
        Assert.False(exception.IsTransient);
        Assert.Equal(400, exception.StatusCode);
        Assert.Null(exception.CorrelationId);
        Assert.DoesNotContain("Host-only diagnostic detail", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Runtime V1 BAD!", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_WhenConnectionIsMissing_ThrowsSanitizedUnavailableFailure()
    {
        using var transport = new RuntimePackageOperationClient(
            () => (RuntimeConnectionInfo?)null,
            new RecordingHandler());
        var client = new AppPackageRuntimeClient("test.package", transport);

        var exception = await Assert.ThrowsAsync<PackageRuntimeInvocationException>(async () =>
            await client.InvokeAsync(
                new PackageRuntimeOperation<EchoRequest, EchoResponse>("echo.run"),
                new EchoRequest("hello")));

        Assert.Equal("runtime.v1.unavailable", exception.Code);
        Assert.True(exception.IsTransient);
        Assert.Equal(503, exception.StatusCode);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task InvokeAsync_WhenTransportFails_DoesNotExposeTransportException()
    {
        var handler = new RecordingHandler
        {
            OperationFailure = new HttpRequestException("internal socket and endpoint detail"),
        };
        using var transport = new RuntimePackageOperationClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5199"), "test-token"),
            handler);
        var client = new AppPackageRuntimeClient("test.package", transport);

        var exception = await Assert.ThrowsAsync<PackageRuntimeInvocationException>(async () =>
            await client.InvokeAsync(
                new PackageRuntimeOperation<EchoRequest, EchoResponse>("echo.run"),
                new EchoRequest("hello")));

        Assert.Equal("runtime.v1.transport-error", exception.Code);
        Assert.True(exception.IsTransient);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("internal socket", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_WhenHostDeadlineExpires_ThrowsSanitizedTimeoutFailure()
    {
        var handler = new RecordingHandler
        {
            OperationFailure = new TaskCanceledException("internal deadline detail"),
        };
        using var transport = new RuntimePackageOperationClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5199"), "test-token"),
            handler);
        var client = new AppPackageRuntimeClient("test.package", transport);

        var exception = await Assert.ThrowsAsync<PackageRuntimeInvocationException>(async () =>
            await client.InvokeAsync(
                new PackageRuntimeOperation<EchoRequest, EchoResponse>("echo.run"),
                new EchoRequest("hello")));

        Assert.Equal("runtime.v1.timeout", exception.Code);
        Assert.True(exception.IsTransient);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task InvokeAsync_WhenCallerCancels_PreservesOperationCanceledException()
    {
        var handler = new RecordingHandler { WaitForCancellation = true };
        using var transport = new RuntimePackageOperationClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5199"), "test-token"),
            handler);
        var client = new AppPackageRuntimeClient("test.package", transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.InvokeAsync(
                new PackageRuntimeOperation<EchoRequest, EchoResponse>("echo.run"),
                new EchoRequest("hello"),
                cancellation.Token));
    }

    [Fact]
    public async Task SubscribeAsync_WhenRuntimeReturnsTerminalFailure_ThrowsSanitizedSdkFailure()
    {
        var handler = new RecordingHandler
        {
            OperationResponse = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    "{\"detail\":\"host stream detail\",\"code\":\"runtime.v1.stream-unavailable\"}",
                    Encoding.UTF8,
                    "application/problem+json"),
            },
        };
        using var transport = new RuntimePackageOperationClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5199"), "test-token"),
            handler);
        var client = new AppPackageRuntimeClient("test.package", transport);

        var exception = await Assert.ThrowsAsync<PackageRuntimeInvocationException>(async () =>
        {
            await foreach (var _ in client.SubscribeAsync(
                               new PackageRuntimeStream<CountRequest, CountEvent>("count.events"),
                               new CountRequest(1)))
            {
            }
        });

        Assert.Equal("runtime.v1.stream-unavailable", exception.Code);
        Assert.True(exception.IsTransient);
        Assert.Equal(503, exception.StatusCode);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("host stream detail", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubscribeAsync_WhenStreamReturnsErrorFrame_ThrowsSanitizedSdkFailure()
    {
        var handler = new RecordingHandler
        {
            StreamResponseBody =
                "{\"type\":\"error\",\"error\":{\"code\":\"runtime.v1.stream-failed\",\"message\":\"host-only stream detail\"}}\n",
        };
        using var transport = new RuntimePackageOperationClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5199"), "test-token"),
            handler);
        var client = new AppPackageRuntimeClient("test.package", transport);

        var exception = await Assert.ThrowsAsync<PackageRuntimeInvocationException>(async () =>
        {
            await foreach (var _ in client.SubscribeAsync(
                               new PackageRuntimeStream<CountRequest, CountEvent>("count.events"),
                               new CountRequest(1)))
            {
            }
        });

        Assert.Equal("runtime.v1.stream-failed", exception.Code);
        Assert.False(exception.IsTransient);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("host-only stream detail", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingGeneration_RuntimeAccessIsLimitedToItsPreparationScope()
    {
        var handler = new RecordingHandler();
        using var transport = new RuntimePackageOperationClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5199"), "test-token"),
            handler);
        var publication = new AppPackageGenerationPublication();
        var client = new AppPackageRuntimeClient("test.package", transport, publication);
        var operation = new PackageRuntimeOperation<EchoRequest, EchoResponse>("echo.run");

        Assert.False(client.IsAvailable);
        var pending = await Assert.ThrowsAsync<PackageRuntimeInvocationException>(async () =>
            await client.InvokeAsync(operation, new EchoRequest("pending")));
        Assert.Equal("runtime.v1.unavailable", pending.Code);

        using (publication.BeginRuntimePreparation())
        {
            Assert.True(client.IsAvailable);
            Assert.Equal("HELLO", (await client.InvokeAsync(operation, new EchoRequest("candidate"))).Value);
        }

        Assert.False(client.IsAvailable);
        publication.Publish();
        Assert.True(client.IsAvailable);
        Assert.Equal("HELLO", (await client.InvokeAsync(operation, new EchoRequest("published"))).Value);
        publication.Revoke();
        Assert.False(client.IsAvailable);
        var retired = await Assert.ThrowsAsync<PackageRuntimeInvocationException>(async () =>
            await client.InvokeAsync(operation, new EchoRequest("retired")));
        Assert.Equal("runtime.v1.unavailable", retired.Code);
        Assert.Equal(2, handler.OperationRequestCount);
    }

    [Fact]
    public async Task RuntimePreparationAuthority_DoesNotAuthorizeAnotherCandidateOrEscapedWork()
    {
        var handler = new RecordingHandler();
        using var transport = new RuntimePackageOperationClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5199"), "test-token"),
            handler);
        var firstPublication = new AppPackageGenerationPublication();
        var secondPublication = new AppPackageGenerationPublication();
        var first = new AppPackageRuntimeClient("test.package", transport, firstPublication);
        var second = new AppPackageRuntimeClient("test.package", transport, secondPublication);
        var operation = new PackageRuntimeOperation<EchoRequest, EchoResponse>("echo.run");
        var releaseEscapedCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<EchoResponse> escapedCall;

        using (firstPublication.BeginRuntimePreparation())
        {
            Assert.True(first.IsAvailable);
            Assert.False(second.IsAvailable);
            var unrelated = await Assert.ThrowsAsync<PackageRuntimeInvocationException>(async () =>
                await second.InvokeAsync(operation, new EchoRequest("unrelated")));
            Assert.Equal("runtime.v1.unavailable", unrelated.Code);
            escapedCall = Task.Run(async () =>
            {
                await releaseEscapedCall.Task;
                return await first.InvokeAsync(operation, new EchoRequest("escaped"));
            });
        }

        releaseEscapedCall.TrySetResult();
        var escaped = await Assert.ThrowsAsync<PackageRuntimeInvocationException>(() => escapedCall);
        Assert.Equal("runtime.v1.unavailable", escaped.Code);
        Assert.Equal(0, handler.OperationRequestCount);
    }

    [Fact]
    public async Task Revoke_CancelsRuntimeWorkOwnedByThatGeneration()
    {
        var handler = new RecordingHandler { WaitForCancellation = true };
        using var transport = new RuntimePackageOperationClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5199"), "test-token"),
            handler);
        var publication = new AppPackageGenerationPublication();
        var client = new AppPackageRuntimeClient("test.package", transport, publication);
        var operation = new PackageRuntimeOperation<EchoRequest, EchoResponse>("echo.run");

        Task<EchoResponse> invocation;
        using (publication.BeginRuntimePreparation())
        {
            invocation = client.InvokeAsync(operation, new EchoRequest("candidate")).AsTask();
            await handler.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            publication.Revoke();
        }

        var exception = await Assert.ThrowsAsync<PackageRuntimeInvocationException>(() => invocation);
        Assert.Equal("runtime.v1.unavailable", exception.Code);
        Assert.Equal(1, handler.OperationRequestCount);
    }

    private sealed record EchoRequest(string Value);

    private sealed record EchoResponse(string Value);

    private sealed record CountRequest(int Count);

    private sealed record CountEvent(int Value);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private int _operationRequestCount;

        public HttpResponseMessage? OperationResponse { get; init; }

        public Exception? OperationFailure { get; init; }

        public bool WaitForCancellation { get; init; }

        public string? StreamResponseBody { get; init; }

        public Uri? RequestUri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        public int OperationRequestCount => Volatile.Read(ref _operationRequestCount);

        public TaskCompletionSource OperationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                        "{\"protocolIdentity\":\"dev.sunder.runtime\",\"protocolRevision\":3,\"minimumSupportedRevision\":3,\"maximumSupportedRevision\":3,\"runtimeInstanceId\":\"11111111-1111-1111-1111-111111111111\",\"supportedFeatures\":[\"api.v1\",\"package-runtime-operations.v1\",\"package-runtime-stream-envelopes.v1\"],\"product\":{\"productName\":\"Sunder.Runtime.Host\",\"productVersion\":\"Development\",\"informationalVersion\":\"Development\"}}",
                        Encoding.UTF8,
                        "application/json"),
                };
            }
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            Interlocked.Increment(ref _operationRequestCount);
            OperationStarted.TrySetResult();
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (OperationFailure is not null)
            {
                throw OperationFailure;
            }
            if (OperationResponse is not null)
            {
                return OperationResponse;
            }
            var responseBody = request.RequestUri?.AbsolutePath.Contains("/streams/", StringComparison.Ordinal) == true
                ? StreamResponseBody
                  ?? "{\"type\":\"event\",\"event\":{\"value\":1}}\n{\"type\":\"event\",\"event\":{\"value\":2}}\n{\"type\":\"completed\"}\n"
                : "{\"value\":\"HELLO\"}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
