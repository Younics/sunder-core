using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Sunder.Host.Client;
using Sunder.Host.Contracts;
using Sunder.Registry.Contracts;
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
                            HostProtocolFeatures.RuntimeStartupDiagnosticsV1,
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
        var binding = Binding();
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
                        RequestedTag: "latest"),
                    ExecutionRuntimeBindings: [binding]),
            ]),
            ExecutionRuntimes = _ => Task.FromResult<IReadOnlyList<InstalledExecutionRuntimeSummary>>([
                RuntimeSummary(binding),
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
        var executionRuntime = Assert.Single(data.GetProperty("executionRuntimes").EnumerateArray());
        Assert.Equal("sunder.runtime.dotnet", executionRuntime.GetProperty("binding").GetProperty("runtimeId").GetString());
        Assert.Equal("10.0.1", executionRuntime.GetProperty("binding").GetProperty("vendorVersion").GetString());
        Assert.Equal("dotnet", executionRuntime.GetProperty("binding").GetProperty("sourceId").GetString());
        Assert.Equal("win-x64", executionRuntime.GetProperty("binding").GetProperty("rid").GetString());
        Assert.Equal("sunder.dotnet-worker-module.v1", executionRuntime.GetProperty("binding").GetProperty("moduleAbi").GetString());
        Assert.Equal("ready", executionRuntime.GetProperty("health").GetProperty("status").GetString());
        Assert.Equal("Supported", executionRuntime.GetProperty("support").GetProperty("state").GetString());
        Assert.False(executionRuntime.GetProperty("actions").GetProperty("repairRequired").GetBoolean());
        Assert.False(executionRuntime.GetProperty("actions").GetProperty("rebindRequired").GetBoolean());
    }

    [Fact]
    public async Task Package_status_reports_missing_binding_ensure_rebind_and_shared_package_impact()
    {
        var binding = Binding();
        var runtime = new FakeRuntimeClient
        {
            Installed = _ => Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>([
                Installed("demo", binding),
                Installed("dependent", binding),
            ]),
            ExecutionRuntimes = _ => Task.FromResult<IReadOnlyList<InstalledExecutionRuntimeSummary>>([]),
        };

        var json = await CliTestHost.RunAsync(["package", "status", "demo", "--json"], runtime);
        var text = await CliTestHost.RunAsync(["package", "status", "demo"], runtime);

        Assert.Equal(CliExitCodes.Success, json.ExitCode);
        using var document = JsonDocument.Parse(json.Output);
        var status = Assert.Single(document.RootElement.GetProperty("data").GetProperty("executionRuntimes").EnumerateArray());
        var actions = status.GetProperty("actions");
        Assert.True(actions.GetProperty("ensureRequired").GetBoolean());
        Assert.True(actions.GetProperty("rebindRequired").GetBoolean());
        Assert.Equal(
            "sunder runtime ensure sunder.runtime.dotnet \">=10.0.0 <11.0.0\" sunder.dotnet-worker-module.v1",
            actions.GetProperty("ensureCommand").GetString());
        Assert.Equal(
            new[] { "demo", "dependent" },
            actions.GetProperty("impactedPackageIds").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("support").ValueKind);
        Assert.Contains("Package impact: demo, dependent", text.Output, StringComparison.Ordinal);
        Assert.Contains("sunder package update demo --dry-run", text.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Package_status_reports_repair_command_and_all_workers_affected_by_exact_runtime()
    {
        var binding = Binding();
        var unhealthy = RuntimeSummary(binding) with
        {
            Status = RuntimeExecutionRuntimeStatuses.Unhealthy,
            Health = new RuntimeExecutionRuntimeHealth(
                RuntimeExecutionRuntimeStatuses.Unhealthy,
                DateTimeOffset.UnixEpoch,
                RepairRecommended: true,
                "Integrity verification failed."),
        };
        var runtime = new FakeRuntimeClient
        {
            Installed = _ => Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>([
                Installed("demo", binding),
                Installed("dependent", binding),
            ]),
            ExecutionRuntimes = _ => Task.FromResult<IReadOnlyList<InstalledExecutionRuntimeSummary>>([unhealthy]),
        };

        var result = await CliTestHost.RunAsync(["package", "status", "demo", "--json"], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var actions = Assert.Single(document.RootElement.GetProperty("data").GetProperty("executionRuntimes").EnumerateArray())
            .GetProperty("actions");
        Assert.True(actions.GetProperty("repairRequired").GetBoolean());
        Assert.Equal(
            "sunder runtime repair dotnet sunder.runtime.dotnet 10.0.1 win-x64 sunder.dotnet-worker-module.v1 sha512 " + new string('b', 128) + " --yes",
            actions.GetProperty("repairCommand").GetString());
        Assert.Contains("2 package(s)", actions.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json<T>(T value)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private static InstalledPackageDescriptor Installed(
        string packageId,
        PackageTargetExecutionRuntimeBinding binding)
        => new(
            packageId,
            packageId,
            "1.2.3",
            PackageHostRoles.Runtime,
            null,
            null,
            true,
            [],
            DateTimeOffset.UnixEpoch,
            null,
            Provenance: new InstalledPackageProvenance(
                InstalledPackageSourceKind.Registry,
                InstalledPackageVersionPolicy.FollowTag,
                "https://registry.test/",
                RequestedTag: "latest"),
            ExecutionRuntimeBindings: [binding]);

    private static PackageTargetExecutionRuntimeBinding Binding()
    {
        var now = DateTimeOffset.UnixEpoch;
        return new PackageTargetExecutionRuntimeBinding(
            "runtime",
            "win-x64",
            new RuntimeExecutionRuntimeRequirement(
                "sunder.runtime.dotnet",
                ">=10.0.0 <11.0.0",
                "sunder.dotnet-worker-module.v1"),
            new RuntimeExecutionRuntimeIdentity(
                ConfiguredOfficialSourceId.DotNet,
                "sunder.runtime.dotnet",
                "10.0.1",
                "win-x64",
                "sunder.dotnet-worker-module.v1",
                RuntimeExecutionRuntimeChecksumAlgorithm.Sha512,
                new string('b', 128)),
            InstalledArtifact(),
            now);
    }

    private static InstalledExecutionRuntimeSummary RuntimeSummary(
        PackageTargetExecutionRuntimeBinding binding)
        => new(
            binding.Runtime,
            RuntimeExecutionRuntimeStatuses.Ready,
            32L * 1024L * 1024L,
            1,
            binding.InstalledArtifact,
            new RuntimeExecutionRuntimeHealth(
                RuntimeExecutionRuntimeStatuses.Ready,
                DateTimeOffset.UnixEpoch,
                false,
                null),
            new RuntimeExecutionRuntimeSupportStatus(
                RuntimeExecutionRuntimeSupportState.Supported,
                "10.0",
                DateTimeOffset.UnixEpoch,
                null,
                null),
            DateTimeOffset.UnixEpoch);

    private static RuntimeExecutionRuntimeInstalledArtifact InstalledArtifact()
        => new(
            new string('c', 64),
            32L * 1024L * 1024L,
            new string('d', 64),
            new RuntimeExecutionRuntimeProvenance(
                new RuntimeExecutionRuntimeDistribution(
                    "https://download.example/dotnet.tar.gz",
                    "https://metadata.example/dotnet.json",
                    new string('e', 64),
                    null,
                    null,
                    RuntimeExecutionRuntimeArchiveFormat.TarGzip,
                    "dotnet-runtime-v1",
                    32L * 1024L * 1024L),
                "https://download.example/dotnet.tar.gz",
                DateTimeOffset.UnixEpoch));

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
