using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeProtocolHttpIntegrationTests
{
    private static readonly PackageRuntimeStream<StreamRequest, StreamEvent> LifecycleStream = new("lifecycle.events");

    [Fact]
    public async Task Handshake_IsAuthenticatedUnversionedAndStableForRuntimeInstance()
    {
        await using var server = await RuntimeHttpServer.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = server.BaseUri };

        using var missing = await anonymous.GetAsync("api/handshake");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        using var wrong = await anonymous.GetAsync("api/handshake");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RuntimeHttpServer.Token);
        var first = await anonymous.GetFromJsonAsync<RuntimeHandshakeResponse>("api/handshake");
        var second = await anonymous.GetFromJsonAsync<RuntimeHandshakeResponse>("api/handshake");

        Assert.NotNull(first);
        Assert.Equal(RuntimeProtocol.Identity, first.ProtocolIdentity);
        Assert.Equal(RuntimeProtocol.CurrentRevision, first.ProtocolRevision);
        Assert.Equal(first.RuntimeInstanceId, second?.RuntimeInstanceId);
        Assert.Contains(RuntimeProtocolFeatures.PackageRuntimeStreamEnvelopesV1, first.SupportedFeatures);
        Assert.False(string.IsNullOrWhiteSpace(first.Product.ProductVersion));
    }

    [Fact]
    public async Task StreamClient_ReadsChunkedEnvelopeFramesAndTerminalCompletion()
    {
        const string frames = "{\"type\":\"event\",\"event\":{\"value\":1}}\n"
                              + "{\"type\":\"event\",\"event\":{\"value\":2}}\n"
                              + "{\"type\":\"completed\"}\n";
        await using var server = await RuntimeHttpServer.StartAsync(app => MapChunkedStream(app, frames, 3));
        using var client = server.CreatePackageClient();
        var values = new List<int>();

        await foreach (var payload in client.SubscribeAsync("test.package", "test.stream", "{}"u8.ToArray()))
        {
            values.Add(JsonDocument.Parse(payload).RootElement.GetProperty("value").GetInt32());
        }

        Assert.Equal([1, 2], values);
    }

    [Fact]
    public async Task RuntimeEndpoint_FinitePackageStreamWritesEventsAndTerminalCompletion()
    {
        var state = CreateState(TimeSpan.FromSeconds(2));
        await state.PublishSessionAsync(CreatePackageSession(new FiniteLifecycleHandler()));
        await using var server = await StartOperationServerAsync(state);
        using var client = server.CreatePackageClient();
        var values = new List<int>();

        await foreach (var payload in client.SubscribeAsync("test.package", LifecycleStream.StreamId, "{}"u8.ToArray()))
        {
            values.Add(JsonDocument.Parse(payload).RootElement.GetProperty("value").GetInt32());
        }

        Assert.Equal([1, 2], values);
        await state.ClearActiveSessionAsync();
    }

    [Theory]
    [InlineData("{\"type\":\"event\",\"event\":{\"value\":1}}\n", "terminal frame")]
    [InlineData("{\"type\":\"event\",\"event\":{\"value\":1}}", "partial JSON frame")]
    public async Task StreamClient_RejectsMissingOrPartialTerminalRecord(string frames, string expectedMessage)
    {
        await using var server = await RuntimeHttpServer.StartAsync(app => MapChunkedStream(app, frames, 5));
        using var client = server.CreatePackageClient();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in client.SubscribeAsync("test.package", "test.stream", "{}"u8.ToArray()))
            {
            }
        });

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StreamClient_RejectsChunkedRecordPastDedicatedLimit()
    {
        var frames = $"{{\"type\":\"event\",\"event\":{{\"value\":\"{new string('x', 256)}\"}}}}\n";
        await using var server = await RuntimeHttpServer.StartAsync(app => MapChunkedStream(app, frames, 7));
        using var client = server.CreatePackageClient(new Sunder.Runtime.Client.RuntimePackageOperationPolicyOptions
        {
            MaxStreamRecordBytes = 128,
        });

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in client.SubscribeAsync("test.package", "test.stream", "{}"u8.ToArray()))
            {
            }
        });
    }

    [Fact]
    public async Task StreamClient_PreservesEventsThenSurfacesHandlerErrorEnvelope()
    {
        const string frames = "{\"type\":\"event\",\"event\":{\"value\":1}}\n"
                              + "{\"type\":\"error\",\"error\":{\"code\":\"handler.failed\",\"message\":\"handler failed\"}}\n";
        await using var server = await RuntimeHttpServer.StartAsync(app => MapChunkedStream(app, frames, 11));
        using var client = server.CreatePackageClient();
        var values = new List<int>();

        var exception = await Assert.ThrowsAsync<RuntimePackageStreamException>(async () =>
        {
            await foreach (var payload in client.SubscribeAsync("test.package", "test.stream", "{}"u8.ToArray()))
            {
                values.Add(JsonDocument.Parse(payload).RootElement.GetProperty("value").GetInt32());
            }
        });

        Assert.Equal([1], values);
        Assert.Equal("handler.failed", exception.Code);
    }

    [Fact]
    public async Task StreamClient_ReceivesHttpErrorWhenHandlerFailsBeforeFirstEvent()
    {
        await using var server = await RuntimeHttpServer.StartAsync(app =>
            app.MapPost(
                "/api/v1/packages/{packageId}/streams/{streamId}",
                static (HttpContext _) => Task.FromException(
                    new InvalidOperationException("handler failed before first event"))));
        using var client = server.CreatePackageClient();

        var exception = await Assert.ThrowsAsync<RuntimeClientException>(async () =>
        {
            await foreach (var _ in client.SubscribeAsync("test.package", "test.stream", "{}"u8.ToArray()))
            {
            }
        });

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
    }

    [Fact]
    public async Task StreamCancellation_DisconnectsHttpRequestLifetime()
    {
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await RuntimeHttpServer.StartAsync(app =>
            app.MapPost("/api/v1/packages/{packageId}/streams/{streamId}", async (HttpContext context) =>
            {
                context.RequestAborted.Register(() => disconnected.TrySetResult());
                try
                {
                    await context.Response.WriteAsync("{\"type\":\"event\",\"event\":{\"value\":1}}\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    var heartbeat = new byte[64 * 1024];
                    while (true)
                    {
                        await context.Response.Body.WriteAsync(heartbeat, context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                    }
                }
                finally
                {
                    disconnected.TrySetResult();
                }
            }));
        using var client = server.CreatePackageClient();
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = client
            .SubscribeAsync("test.package", "test.stream", "{}"u8.ToArray(), cancellation.Token)
            .GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        await enumerator.DisposeAsync();
        client.Dispose();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task InfinitePackageStream_ReloadCancelsHttpHandlerAndDrainsLease()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = CreateState(TimeSpan.FromSeconds(2));
        await state.PublishSessionAsync(CreatePackageSession(new CancellableLifecycleHandler(entered)));
        await using var server = await StartOperationServerAsync(state);
        using var client = server.CreatePackageClient();
        await using var enumerator = client
            .SubscribeAsync("test.package", LifecycleStream.StreamId, "{}"u8.ToArray())
            .GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        var pending = enumerator.MoveNextAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var replacement = CreateEmptySession();
        var publication = state.PublishSessionAsync(replacement);

        await Assert.ThrowsAsync<RuntimePackageStreamException>(() => pending);
        Assert.Equal(2, (await publication.WaitAsync(TimeSpan.FromSeconds(2))).Generation);
        await state.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task IgnoredRetirement_RejectsHttpLeaseAdmissionAndFailsReloadWithoutDisposal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = CreateState(TimeSpan.FromMilliseconds(150));
        await state.PublishSessionAsync(CreatePackageSession(
            new IgnoringLifecycleHandler(entered, retirement, release)));
        await using var server = await StartOperationServerAsync(state);
        using var client = server.CreatePackageClient();
        await using var enumerator = client
            .SubscribeAsync("test.package", LifecycleStream.StreamId, "{}"u8.ToArray())
            .GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        var pending = enumerator.MoveNextAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var replacement = CreateEmptySession();
        var publication = state.PublishSessionAsync(replacement);
        await retirement.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var admission = await Assert.ThrowsAsync<RuntimeClientException>(async () =>
        {
            await foreach (var _ in client.SubscribeAsync("test.package", LifecycleStream.StreamId, "{}"u8.ToArray()))
            {
            }
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, admission.StatusCode);
        await Assert.ThrowsAsync<RuntimeUnavailableException>(() => publication);

        using (var admitted = state.AcquireLease())
        {
            Assert.Equal(1, admitted.Generation);
            Assert.NotNull(state.GetLoadedPackage(admitted, "test.package"));
        }
        release.TrySetResult();
        await Assert.ThrowsAsync<RuntimePackageStreamException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        await replacement.DisposeAsync();
        await state.ClearActiveSessionAsync();
    }

    private static void MapChunkedStream(WebApplication app, string frames, int chunkSize)
    {
        app.MapPost("/api/v1/packages/{packageId}/streams/{streamId}", async (HttpResponse response, CancellationToken token) =>
        {
            response.ContentType = "application/x-ndjson";
            var payload = Encoding.UTF8.GetBytes(frames);
            for (var offset = 0; offset < payload.Length; offset += chunkSize)
            {
                await response.Body.WriteAsync(payload.AsMemory(offset, Math.Min(chunkSize, payload.Length - offset)), token);
                await response.Body.FlushAsync(token);
            }
        });
    }

    private static async Task<RuntimeHttpServer> StartOperationServerAsync(PackageSessionState state)
    {
        var policy = new Sunder.Runtime.Host.Services.RuntimePackageOperationPolicyOptions();
        return await RuntimeHttpServer.StartAsync(
            app => app.MapGroup("/api/v1").MapPackageRuntimeOperationEndpoints(),
            services =>
            {
                services.AddSingleton(state);
                services.AddSingleton(policy);
                services.AddSingleton(new RuntimePackageOperationService(state, policy));
            });
    }

    private static PackageSessionState CreateState(TimeSpan drainTimeout)
        => new(
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            static () => { },
            static _ => { },
            drainTimeout);

    private static ActivePackageSession CreatePackageSession(
        IPackageRuntimeStreamHandler<StreamRequest, StreamEvent> handler)
    {
        const string packageId = "test.package";
        var services = new ServiceCollection().BuildServiceProvider();
        var registry = new RuntimePackageContributionRegistry(
            services,
            new RuntimePackageExtensionCatalog(),
            packageId);
        registry.RegisterRuntimeStream(LifecycleStream, handler);
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-http-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var assemblyPath = typeof(RuntimeProtocolHttpIntegrationTests).Assembly.Location;
        var loadedPackage = new ActiveLoadedPackage(
            new ActivePackageDescriptor(packageId, packageId, "1.0.0", null, true, PackageReadinessState.Ready, []),
            new RuntimePackageSource(packageId, PackageSourceKind.Dev, root),
            null,
            new JsonPackageKeyValueStore(Path.Combine(root, "state.json")),
            new JsonPackageSecretsStore(
                Path.Combine(root, "secrets.json"),
                null,
                null,
                new RestrictedFileMasterKeyProtection()),
            null,
            new Dictionary<string, IPackageCallbackHandler>(),
            [],
            services,
            new RuntimePackageLoadContext(
                packageId,
                assemblyPath,
                new RuntimeSharedAssemblyRegistry([Path.GetDirectoryName(assemblyPath)!])),
            EmptyTestPackageSettings.Instance)
        {
            RuntimeStreams = registry.RuntimeStreams,
        };
        return new ActivePackageSession(
            root,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase) { [packageId] = loadedPackage },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = new(
                    packageId,
                    packageId,
                    "1.0.0",
                    null,
                    true,
                    PackageReadinessState.Ready,
                    [],
                    null,
                    null,
                    null,
                    0),
            });
    }

    private static ActivePackageSession CreateEmptySession()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-http-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new ActivePackageSession(
            root,
            new Dictionary<string, ActiveLoadedPackage>(),
            new Dictionary<string, SessionPackageDescriptor>());
    }

    private sealed record StreamRequest;

    private sealed record StreamEvent(int Value);

    private sealed class FiniteLifecycleHandler : IPackageRuntimeStreamHandler<StreamRequest, StreamEvent>
    {
        public async IAsyncEnumerable<StreamEvent> SubscribeAsync(
            StreamRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new StreamEvent(1);
            await Task.Yield();
            yield return new StreamEvent(2);
        }
    }

    private sealed class CancellableLifecycleHandler(TaskCompletionSource entered)
        : IPackageRuntimeStreamHandler<StreamRequest, StreamEvent>
    {
        public async IAsyncEnumerable<StreamEvent> SubscribeAsync(
            StreamRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new StreamEvent(1);
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class IgnoringLifecycleHandler(
        TaskCompletionSource entered,
        TaskCompletionSource retirement,
        TaskCompletionSource release) : IPackageRuntimeStreamHandler<StreamRequest, StreamEvent>
    {
        public async IAsyncEnumerable<StreamEvent> SubscribeAsync(
            StreamRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new StreamEvent(1);
            cancellationToken.Register(() => retirement.TrySetResult());
            entered.TrySetResult();
            await release.Task;
        }
    }

    private sealed class RuntimeHttpServer(WebApplication app, Uri baseUri) : IAsyncDisposable
    {
        public const string Token = "integration-runtime-token";

        public Uri BaseUri { get; } = baseUri;

        public static async Task<RuntimeHttpServer> StartAsync(
            Action<WebApplication>? map = null,
            Action<IServiceCollection>? configureServices = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(new RuntimeBearerTokenValidator(Token));
            builder.Services.AddSingleton<RuntimeProtocolDescriptor>();
            configureServices?.Invoke(builder.Services);
            var app = builder.Build();
            app.UseMiddleware<RuntimeProblemDetailsMiddleware>();
            app.UseMiddleware<RuntimeBearerAuthenticationMiddleware>();
            app.MapRuntimeHandshakeEndpoint();
            map?.Invoke(app);
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = Assert.Single(addresses ?? []);
            return new RuntimeHttpServer(app, new Uri(address));
        }

        public RuntimePackageOperationClient CreatePackageClient(
            Sunder.Runtime.Client.RuntimePackageOperationPolicyOptions? policy = null)
            => new(
                () => new RuntimeConnectionInfo(BaseUri, Token),
                policy: policy);

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
