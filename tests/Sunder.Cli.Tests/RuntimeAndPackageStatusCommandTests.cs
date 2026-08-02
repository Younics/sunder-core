using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Sunder.Host.Client;
using Sunder.Host.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli.Tests;

public sealed class RuntimeAndPackageStatusCommandTests
{
    [Fact]
    public async Task Cli_host_wrapper_validates_the_managed_protocol_once()
    {
        var paths = new List<string>();
        using var management = new HostManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5275/"), "host-token"),
            new RecordingHandler(request =>
            {
                paths.Add(request.RequestUri!.AbsolutePath);
                return request.RequestUri.AbsolutePath switch
                {
                    "/api/host/handshake" => Json(new HostHandshakeResponse(
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
                        new HostProductVersionDiagnostics("Sunder Host", "1.0.0", "1.0.0"))),
                    "/api/host/v1/status" => Json(new HostRuntimeStatus(
                        HostRuntimeDesiredState.Stopped,
                        HostRuntimeState.Stopped,
                        2,
                        null,
                        null,
                        null,
                        null)),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            }));
        using var client = new CliHostClient(management);

        await client.GetStatusAsync(CancellationToken.None);
        await client.GetStatusAsync(CancellationToken.None);

        Assert.Equal(1, paths.Count(path => path == "/api/host/handshake"));
        Assert.Equal(2, paths.Count(path => path == "/api/host/v1/status"));
    }

    [Fact]
    public async Task Runtime_status_reports_a_stopped_managed_runtime_without_contacting_the_worker()
    {
        var host = new FakeHostClient
        {
            Status = _ => Task.FromResult(new HostRuntimeStatus(
                HostRuntimeDesiredState.Stopped,
                HostRuntimeState.Stopped,
                7,
                null,
                null,
                null,
                null)),
        };
        var runtime = new FakeRuntimeClient
        {
            SystemStatus = _ => throw new InvalidOperationException("Stopped Runtime worker must not be queried."),
        };

        var result = await CliTestHost.RunAsync(["runtime", "status", "--json"], runtime, host: host);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("stopped", document.RootElement.GetProperty("data").GetProperty("state").GetString());
        Assert.Equal(7, document.RootElement.GetProperty("data").GetProperty("deploymentGeneration").GetInt64());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("data").GetProperty("runtime").ValueKind);
    }

    [Fact]
    public async Task Runtime_restart_submits_generation_bound_operation_and_waits_for_completion()
    {
        HostLifecycleRequest? captured = null;
        var waitCalled = false;
        var statusCalls = 0;
        var host = new FakeHostClient
        {
            Status = _ => Task.FromResult(++statusCalls == 1
                ? new HostRuntimeStatus(HostRuntimeDesiredState.Running, HostRuntimeState.Ready, 12, Guid.NewGuid(), null, null, null)
                : new HostRuntimeStatus(HostRuntimeDesiredState.Running, HostRuntimeState.Ready, 13, Guid.NewGuid(), null, null, null)),
            Restart = (request, _) =>
            {
                captured = request;
                return Task.FromResult(new HostLifecycleSubmission(new HostOperationDescriptor(
                    "restart-1",
                    request.MutationId,
                    HostOperationKinds.RuntimeRestart,
                    request.ExpectedDeploymentGeneration,
                    HostOperationState.Accepted,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    null,
                    null)));
            },
            Wait = (operation, _) =>
            {
                waitCalled = true;
                return Task.FromResult(operation with
                {
                    State = HostOperationState.Succeeded,
                    Message = "Runtime worker restarted.",
                });
            },
        };

        var result = await CliTestHost.RunAsync(
            ["runtime", "restart", "--json"],
            host: host,
            runtimeFactory: _ => throw new InvalidOperationException("Lifecycle commands must not create a Runtime client."));

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(12, captured?.ExpectedDeploymentGeneration);
        Assert.NotEqual(Guid.Empty, captured?.MutationId);
        Assert.True(waitCalled);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("succeeded", document.RootElement.GetProperty("data").GetProperty("operation").GetProperty("state").GetString());
        Assert.Equal(13, document.RootElement.GetProperty("data").GetProperty("runtime").GetProperty("deploymentGeneration").GetInt64());
    }

    [Fact]
    public async Task Package_status_combines_installed_provenance_and_atomic_session_snapshot()
    {
        var runtime = new FakeRuntimeClient
        {
            Installed = _ => Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>([
                new InstalledPackageDescriptor(
                    "demo",
                    "Demo",
                    "1.2.3",
                    PackageHostRoles.Runtime,
                    "Summary",
                    null,
                    true,
                    [],
                    DateTimeOffset.UnixEpoch,
                    null,
                    Provenance: new InstalledPackageProvenance(
                        InstalledPackageSourceKind.Registry,
                        InstalledPackageVersionPolicy.FollowTag,
                        "https://registry.test/",
                        RequestedTag: "latest")),
            ]),
            PackageSnapshot = _ => Task.FromResult(new RuntimePackageSnapshot(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                4,
                9,
                RuntimeBootstrapState.Ready,
                [new ActivePackageDescriptor("demo", "Demo", "1.2.3", PackageHostRoles.Runtime, null, true, PackageReadinessState.Ready, [])],
                [new SessionPackageDescriptor(
                    "demo",
                    "Demo",
                    "1.2.3",
                    PackageHostRoles.Runtime,
                    null,
                    true,
                    PackageReadinessState.Ready,
                    [],
                    null,
                    null,
                    null,
                    0)],
                [],
                [])),
        };

        var result = await CliTestHost.RunAsync(["package", "status", "demo", "--json"], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("registry", data.GetProperty("installed").GetProperty("source").GetString());
        Assert.Equal("latest", data.GetProperty("installed").GetProperty("tag").GetString());
        Assert.Equal("ready", data.GetProperty("session").GetProperty("readiness").GetString());
        Assert.True(data.GetProperty("session").GetProperty("active").GetBoolean());
        Assert.Equal(4, data.GetProperty("runtime").GetProperty("sessionGeneration").GetInt64());
    }

    private static HttpResponseMessage Json<T>(T value)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
