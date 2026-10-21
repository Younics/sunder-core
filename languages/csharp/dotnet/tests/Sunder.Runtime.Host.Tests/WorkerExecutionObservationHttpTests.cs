using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class WorkerExecutionObservationHttpTests
{
    [Fact]
    public async Task EndpointsRequireBearerAndRejectRetiredAppBeforeAnyStoreInitialization()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-execution-observation-http", Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.Services.AddSingleton(new RuntimeBearerTokenValidator("observation-token"));
        builder.Services.AddSingleton(new RuntimePackagePaths(root));
        builder.Services.AddSingleton<RuntimeRpcCatalog>();
        builder.Services.AddSingleton<RuntimeEventStreamService>();
        builder.Services.AddSingleton<RuntimeRpcPolicyOptions>();
        builder.Services.AddSingleton<RuntimeRpcPermissionStore>();
        builder.Services.AddSingleton(provider => new RuntimeSessionOwner(NullLogger<RuntimeSessionOwner>.Instance,
            provider.GetRequiredService<RuntimeEventStreamService>(), rpcCatalog: provider.GetRequiredService<RuntimeRpcCatalog>()));
        builder.Services.AddSingleton(provider => provider.GetRequiredService<RuntimeSessionOwner>().State);
        builder.Services.AddSingleton<RuntimeRpcAppSessionManager>();
        builder.Services.AddSingleton<RuntimeRpcBroker>();
        builder.Services.AddSingleton<ExecutionRuntimeStoreLayout>();
        builder.Services.AddSingleton(provider => new ExecutionRuntimeStore(provider.GetRequiredService<ExecutionRuntimeStoreLayout>(), new NeverGarbageCollection()));
        builder.Services.AddSingleton<PackageWorkerExecutionObservationService>();
        var app = builder.Build();
        app.UseMiddleware<RuntimeProblemDetailsMiddleware>();
        app.UseMiddleware<RuntimeBearerAuthenticationMiddleware>();
        app.MapGroup("/api/v1/rpc").MapWorkerExecutionObservationEndpoints();
        try
        {
            await app.StartAsync();
            var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            using var client = new HttpClient { BaseAddress = new(address) };
            var reference = new RuntimeWorkerExecutionReference(Guid.NewGuid(), "unknown-reference");
            using (var denied = await client.PostAsJsonAsync("api/v1/rpc/app/runtime-observations/observe-execution",
                       new RuntimeRpcAppExecutionObserveRequest("stale-session", reference)))
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "observation-token");
            using var capture = await client.PostAsJsonAsync("api/v1/rpc/app/runtime-observations/capture-execution",
                new RuntimeRpcAppExecutionCaptureRequest("stale-session", Provider()));
            var captureResult = await capture.Content.ReadFromJsonAsync<RuntimeRpcAppExecutionCaptureResponse>();
            Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
            Assert.Null(captureResult!.Reference);
            Assert.Equal(RuntimeRpcErrorKind.Unavailable, captureResult.Error?.Kind);
            using var observe = await client.PostAsJsonAsync("api/v1/rpc/app/runtime-observations/observe-execution",
                new RuntimeRpcAppExecutionObserveRequest("stale-session", reference));
            var observeResult = await observe.Content.ReadFromJsonAsync<RuntimeRpcAppExecutionObserveResponse>();
            Assert.Equal(HttpStatusCode.OK, observe.StatusCode);
            Assert.Null(observeResult!.Observation);
            Assert.Equal(RuntimeRpcErrorKind.Unavailable, observeResult.Error?.Kind);
            Assert.False(Directory.Exists(app.Services.GetRequiredService<ExecutionRuntimeStoreLayout>().ExecutionObservationRootPath));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClientPreservesStringSessionScopeExactProviderAndOriginalRuntimeReference()
    {
        var handler = new ObservationHandler();
        using var client = new RuntimeManagementClient(() => new(new Uri("http://runtime.test/"), "token"), handler);
        var provider = Provider();
        var capture = await client.CaptureAppWorkerExecutionAsync(new("app-string-session", provider, "scope-string"));
        Assert.Null(capture.Error);
        Assert.Equal("app-string-session", handler.Capture!.SessionId);
        Assert.Equal("scope-string", handler.Capture.CallScopeId);
        Assert.Equal(provider, handler.Capture.Provider);
        var result = await client.ObserveAppWorkerExecutionAsync(new("successor-session", capture.Reference!, "successor-scope"));
        Assert.Null(result.Error);
        Assert.Equal(capture.Reference, result.Observation!.Reference);
        Assert.Equal(RuntimeWorkerExecutionState.CleanupPending, result.Observation.State);
        Assert.Null(result.Observation.ConfirmedAtUtc);
        Assert.Equal("successor-session", handler.Observe!.SessionId);
        Assert.Equal("successor-scope", handler.Observe.CallScopeId);
        Assert.NotEqual(handler.Handshake.RuntimeInstanceId, result.Observation.Reference.RuntimeInstanceId);
    }

    [Fact]
    public async Task OldHostWithoutObservationFeatureCannotReceiveCaptureOrImplyCleanup()
    {
        var handler = new ObservationHandler { SupportsFeature = false };
        using var client = new RuntimeManagementClient(() => new(new Uri("http://runtime.test/"), "token"), handler);
        await Assert.ThrowsAsync<RuntimeProtocolException>(() => client.CaptureAppWorkerExecutionAsync(new("app-session", Provider())));
        Assert.Null(handler.Capture);
        Assert.Null(handler.Observe);
    }

    private static RuntimeRpcProviderDescriptor Provider()
        => new("observation.owner", "1.0.0", "observation.provider", "observation.contract", "1.0.0",
            new('a', 64), new('b', 64), Guid.NewGuid(), 2, 3, "rpc1_exact", 4, RuntimeRpcProviderState.Active, null);

    private sealed class NeverGarbageCollection : IExecutionRuntimeGarbageCollectionAuthority
    {
        public Task<TResult> ExecuteAsync<TResult>(Func<ExecutionRuntimeGarbageCollectionAuthoritySnapshot, CancellationToken, Task<TResult>> operation,
            CancellationToken token) => throw new InvalidOperationException("An observation must not initialize or collect the execution store.");
    }

    private sealed class ObservationHandler : HttpMessageHandler
    {
        internal bool SupportsFeature = true;
        internal RuntimeHandshakeResponse Handshake { get; } = new RuntimeProtocolDescriptor().Handshake;
        internal RuntimeRpcAppExecutionCaptureRequest? Capture;
        internal RuntimeRpcAppExecutionObserveRequest? Observe;
        private readonly RuntimeWorkerExecutionReference reference = new(Guid.NewGuid(), "opaque-original-execution");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/handshake":
                    return Json(Handshake with { SupportedFeatures = SupportsFeature ? Handshake.SupportedFeatures
                        : Handshake.SupportedFeatures.Where(feature => feature != RuntimeProtocolFeatures.WorkerExecutionObservationsV1).ToArray() });
                case "/api/v1/rpc/app/runtime-observations/capture-execution":
                    Capture = await request.Content!.ReadFromJsonAsync<RuntimeRpcAppExecutionCaptureRequest>(token);
                    return Json(new RuntimeRpcAppExecutionCaptureResponse(reference, null));
                case "/api/v1/rpc/app/runtime-observations/observe-execution":
                    Observe = await request.Content!.ReadFromJsonAsync<RuntimeRpcAppExecutionObserveRequest>(token);
                    return Json(new RuntimeRpcAppExecutionObserveResponse(new(reference, RuntimeWorkerExecutionState.CleanupPending), null));
                default: return new(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
