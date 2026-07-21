using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Host.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimeHealthProbeTests
{
    [Fact]
    public async Task RuntimeHealthProbe_WithStaleTokenStillDetectsOccupiedTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = Assert.IsType<IPEndPoint>(listener.LocalEndpoint);
            var runtimeUrl = new Uri($"http://127.0.0.1:{endpoint.Port}/");
            var connectionState = new RuntimeConnectionState(runtimeUrl);
            connectionState.SetConnection(new RuntimeConnectionInfo(runtimeUrl, "stale-token"));
            using var probe = new RuntimeHealthProbe(connectionState);

            Assert.True(await probe.IsRuntimeHealthyAsync(runtimeUrl, CancellationToken.None));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RuntimeHealthProbe_ShutdownRuntime_DoesNotRequireLifecycleFeatureNegotiation()
    {
        var runtimeUrl = new Uri("http://127.0.0.1:54321/");
        var connectionState = new RuntimeConnectionState(runtimeUrl);
        connectionState.SetConnection(new RuntimeConnectionInfo(runtimeUrl, "host-token"));
        var requests = new List<(HttpMethod Method, string Path)>();
        var handler = new RecordingHttpMessageHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new HostHandshakeResponse(
                    HostProtocol.Identity,
                    HostProtocol.CurrentRevision,
                    HostProtocol.MinimumSupportedRevision,
                    HostProtocol.MaximumSupportedRevision,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    [HostProtocolFeatures.RuntimeGatewayV1],
                    new HostProductVersionDiagnostics("Sunder.Host.Supervisor", "1.0.0", "test"))),
            };
        });
        using var probe = new RuntimeHealthProbe(connectionState, hostHandler: handler);

        await probe.ShutdownRuntimeAsync(runtimeUrl, CancellationToken.None);

        Assert.Equal(
            [
                (HttpMethod.Get, "/api/host/handshake"),
                (HttpMethod.Post, "/api/host/v1/shutdown"),
            ],
            requests);
    }

    [Fact]
    public async Task RuntimeHealthProbe_StartsAndPollsDurableHostOperation()
    {
        var runtimeUrl = new Uri("http://127.0.0.1:54321/");
        var connectionState = new RuntimeConnectionState(runtimeUrl);
        connectionState.SetConnection(new RuntimeConnectionInfo(runtimeUrl, "host-token"));
        var operationId = "11111111111111111111111111111111";
        var mutationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var now = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        var accepted = new HostOperationDescriptor(
            operationId,
            mutationId,
            HostOperationKinds.RuntimeStart,
            4,
            HostOperationState.Accepted,
            now,
            now,
            null,
            null);
        var stopped = new HostRuntimeStatus(
            HostRuntimeDesiredState.Stopped,
            HostRuntimeState.Stopped,
            4,
            null,
            null,
            null,
            null);
        var submittedOperation = accepted;
        var requests = new List<(HttpMethod Method, string Path)>();
        var handler = new RecordingHttpMessageHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            if (request.RequestUri.AbsolutePath == "/api/host/handshake")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new HostHandshakeResponse(
                        HostProtocol.Identity,
                        HostProtocol.CurrentRevision,
                        HostProtocol.MinimumSupportedRevision,
                        HostProtocol.MaximumSupportedRevision,
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        [
                            HostProtocolFeatures.RuntimeGatewayV1,
                            HostProtocolFeatures.RuntimeLifecycleV1,
                            HostProtocolFeatures.DurableOperationsV1,
                        ],
                        new HostProductVersionDiagnostics("Sunder.Host.Supervisor", "1.0.0", "test"))),
                };
            }
            if (request.RequestUri.AbsolutePath == "/api/host/v1/status")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(stopped) };
            }
            if (request.Method == HttpMethod.Post)
            {
                var lifecycleRequest = request.Content!.ReadFromJsonAsync<HostLifecycleRequest>()
                    .GetAwaiter()
                    .GetResult()!;
                submittedOperation = accepted with
                {
                    MutationId = lifecycleRequest.MutationId,
                    ExpectedDeploymentGeneration = lifecycleRequest.ExpectedDeploymentGeneration,
                };
                var response = new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = JsonContent.Create(new HostLifecycleSubmission(submittedOperation)),
                };
                response.Headers.Location = new Uri($"/api/host/v1/operations/{operationId}", UriKind.Relative);
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(submittedOperation with
                {
                    State = HostOperationState.Succeeded,
                    UpdatedAtUtc = now.AddSeconds(1),
                    Message = "Runtime worker is ready.",
                }),
            };
        });
        using var probe = new RuntimeHealthProbe(connectionState, hostHandler: handler);

        Assert.True(await probe.TryEnsureSupervisedRuntimeStartedAsync(runtimeUrl, CancellationToken.None));
        Assert.Equal(
            [
                (HttpMethod.Get, "/api/host/handshake"),
                (HttpMethod.Get, "/api/host/v1/status"),
                (HttpMethod.Post, "/api/host/v1/runtime/start"),
                (HttpMethod.Get, $"/api/host/v1/operations/{operationId}"),
            ],
            requests);
    }

    [Fact]
    public async Task RuntimeHealthProbe_AppliesRequestedIntentAfterPriorOperationFails()
    {
        var runtimeUrl = new Uri("http://127.0.0.1:54321/");
        var connectionState = new RuntimeConnectionState(runtimeUrl);
        connectionState.SetConnection(new RuntimeConnectionInfo(runtimeUrl, "host-token"));
        var priorOperationId = "11111111111111111111111111111111";
        var requestedOperationId = "22222222222222222222222222222222";
        var now = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        var priorOperation = new HostOperationDescriptor(
            priorOperationId,
            Guid.NewGuid(),
            HostOperationKinds.RuntimeStop,
            4,
            HostOperationState.Running,
            now,
            now,
            null,
            null);
        var activeStatus = new HostRuntimeStatus(
            HostRuntimeDesiredState.Stopped,
            HostRuntimeState.Stopping,
            5,
            null,
            null,
            null,
            priorOperationId);
        var failedStatus = activeStatus with
        {
            State = HostRuntimeState.Failed,
            FailureCode = "host.worker-stop-failed",
            FailureMessage = "stop failed",
            ActiveOperationId = null,
        };
        var statusRequestCount = 0;
        var requestedMutationId = Guid.Empty;
        var requests = new List<(HttpMethod Method, string Path)>();
        var handler = new RecordingHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add((request.Method, path));
            if (path == "/api/host/handshake")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new HostHandshakeResponse(
                        HostProtocol.Identity,
                        HostProtocol.CurrentRevision,
                        HostProtocol.MinimumSupportedRevision,
                        HostProtocol.MaximumSupportedRevision,
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        [
                            HostProtocolFeatures.RuntimeGatewayV1,
                            HostProtocolFeatures.RuntimeLifecycleV1,
                            HostProtocolFeatures.DurableOperationsV1,
                        ],
                        new HostProductVersionDiagnostics("Sunder.Host.Supervisor", "1.0.0", "test"))),
                };
            }
            if (path == "/api/host/v1/status")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        Interlocked.Increment(ref statusRequestCount) == 1 ? activeStatus : failedStatus),
                };
            }
            if (path.EndsWith(priorOperationId, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(priorOperation with
                    {
                        State = HostOperationState.Failed,
                        UpdatedAtUtc = now.AddSeconds(1),
                        Message = "stop failed",
                        FailureCode = "host.worker-stop-failed",
                    }),
                };
            }
            if (request.Method == HttpMethod.Post)
            {
                var lifecycleRequest = request.Content!.ReadFromJsonAsync<HostLifecycleRequest>()
                    .GetAwaiter()
                    .GetResult()!;
                requestedMutationId = lifecycleRequest.MutationId;
                var requestedOperation = new HostOperationDescriptor(
                    requestedOperationId,
                    lifecycleRequest.MutationId,
                    HostOperationKinds.RuntimeStart,
                    lifecycleRequest.ExpectedDeploymentGeneration,
                    HostOperationState.Accepted,
                    now,
                    now,
                    null,
                    null);
                var response = new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = JsonContent.Create(new HostLifecycleSubmission(requestedOperation)),
                };
                response.Headers.Location = new Uri(
                    $"/api/host/v1/operations/{requestedOperationId}",
                    UriKind.Relative);
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new HostOperationDescriptor(
                    requestedOperationId,
                    requestedMutationId,
                    HostOperationKinds.RuntimeStart,
                    5,
                    HostOperationState.Succeeded,
                    now,
                    now.AddSeconds(1),
                    "Runtime worker is ready.",
                    null)),
            };
        });
        using var probe = new RuntimeHealthProbe(connectionState, hostHandler: handler);

        Assert.True(await probe.TryEnsureSupervisedRuntimeStartedAsync(runtimeUrl, CancellationToken.None));
        Assert.Contains((HttpMethod.Post, "/api/host/v1/runtime/start"), requests);
        Assert.Contains((HttpMethod.Get, $"/api/host/v1/operations/{requestedOperationId}"), requests);
    }

    private sealed class RecordingHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
