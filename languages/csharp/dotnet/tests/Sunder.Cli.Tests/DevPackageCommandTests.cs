using System.Net;
using System.Net.Http.Json;
using Sunder.Cli;
using Sunder.Host.Client;
using Sunder.Host.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.Cli.Tests;

public sealed class DevPackageCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareImportsDelegatesLocalOrRegistryInputsWithoutLoadingOrPublishingCode(bool local)
    {
        var folder = CreateFolder(); var calls = new List<string>();
        var runtime = new FakeRuntimeClient
        {
            PrepareDevImports = (request, _) =>
            {
                calls.Add("prepare-imports"); Assert.Equal(Path.GetFullPath(folder), request.Folder);
                Assert.Equal(local ? null : "https://registry.example/", request.RegistryOrigin);
                Assert.Equal(local ? new[] { Path.GetFullPath("owner.sunderpkg"), Path.GetFullPath("provider.sunderpkg") } : [], request.CandidateArchives);
                Assert.Equal(local ? Path.GetFullPath("core-catalog.json") : null, request.PlatformCatalogPath);
                return Task.FromResult(new RuntimeDevPackageImportsPrepared("example.local", "1.0.0", new string('a', 64), 1, 0));
            },
        };
        var host = new FakeHostClient { EnsureReady = _ => { calls.Add("host-ready"); return Task.FromResult(ReadyHost()); } };
        try
        {
            string[] options = local ? ["--candidate", "owner.sunderpkg", "--candidate", "provider.sunderpkg", "--platform-catalog", "core-catalog.json"]
                : ["--registry", "https://registry.example/"];
            var result = await CliTestHost.RunAsync(["dev", "package", "prepare-imports", folder, .. options], runtime, host: host);
            Assert.Equal(CliExitCodes.Success, result.ExitCode); Assert.Equal(["host-ready", "prepare-imports"], calls);
            Assert.Contains("remains unpublished", result.Output, StringComparison.Ordinal);
            Assert.Null(Assert.IsType<PrepareDevPackageImportsCommand>(new CliCommandParser().Parse(["dev", "package", "prepare-imports", folder]).Invocation!.Command).RegistryOrigin);
            foreach (var arguments in new[] { new[] { "dev", "package", "prepare-imports", folder, "--candidate", "" },
                         new[] { "dev", "package", "prepare-imports", folder, "--registry", "" },
                         new[] { "dev", "package", "prepare-imports", folder, "--registry", "https://registry.example/", "--candidate", "local.sunderpkg" } })
                Assert.Null(new CliCommandParser().Parse(arguments).Invocation);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ManagedHostBridge_StartsStoppedRuntimeAndWaitsForReady()
    {
        HostLifecycleRequest? startRequest = null;
        var started = false;
        var paths = new List<string>();
        var operation = new HostOperationDescriptor(
            "operation-1",
            Guid.Empty,
            HostOperationKinds.RuntimeStart,
            7,
            HostOperationState.Accepted,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            null);
        var handler = new DelegateHandler(async request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            switch (request.RequestUri.AbsolutePath)
            {
                case "/api/host/handshake":
                    return Json(new HostHandshakeResponse(
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
                        new HostProductVersionDiagnostics("Sunder.Host.Supervisor", "1.0.0", "test")));
                case "/api/host/v1/status":
                    return Json(started
                        ? ReadyHost() with { DeploymentGeneration = 8 }
                        : new HostRuntimeStatus(
                            HostRuntimeDesiredState.Stopped,
                            HostRuntimeState.Stopped,
                            7,
                            null,
                            null,
                            null,
                            null));
                case "/api/host/v1/runtime/start":
                    startRequest = await request.Content!.ReadFromJsonAsync<HostLifecycleRequest>();
                    operation = operation with { MutationId = startRequest!.MutationId };
                    started = true;
                    var submission = Json(new HostLifecycleSubmission(operation));
                    submission.Headers.Location = new Uri("/api/host/v1/operations/operation-1", UriKind.Relative);
                    return submission;
                case "/api/host/v1/operations/operation-1":
                    return Json(operation with
                    {
                        State = HostOperationState.Succeeded,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Message = "Started.",
                    });
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        });
        var connection = new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5275/"), "host-token");
        using var client = new CliHostClient(new HostManagementClient(() => connection, handler));

        var status = await client.EnsureRuntimeReadyAsync(CancellationToken.None);

        Assert.Equal(HostRuntimeState.Ready, status.State);
        Assert.Equal(7, startRequest?.ExpectedDeploymentGeneration);
        Assert.Equal(
            [
                "/api/host/handshake",
                "/api/host/v1/status",
                "/api/host/v1/runtime/start",
                "/api/host/v1/operations/operation-1",
                "/api/host/v1/status",
            ],
            paths);
    }

    [Fact]
    public async Task Smoke_UsesManagedHostThenRuntimeOwnerAndDoesNotAcquireExecutionRuntime()
    {
        var folder = CreateFolder();
        var calls = new List<string>();
        var executionRuntimeAcquisitions = 0;
        var session = new FakeOwnerSession
        {
            Acquire = (folders, watch, _) =>
            {
                calls.Add("owner-acquire");
                Assert.Equal(Path.GetFullPath(folder), Assert.Single(folders));
                Assert.False(watch);
                return Task.FromResult(Lease(watch));
            },
            Release = _ =>
            {
                calls.Add("owner-release");
                return Task.CompletedTask;
            },
            Dispose = () =>
            {
                calls.Add("owner-dispose");
                return ValueTask.CompletedTask;
            },
        };
        var host = new FakeHostClient
        {
            EnsureReady = _ =>
            {
                calls.Add("host-ready");
                return Task.FromResult(ReadyHost());
            },
        };
        var runtime = new FakeRuntimeClient
        {
            DevPackageOwnerSession = _ => session,
            EnsureExecutionRuntime = (_, _) =>
            {
                executionRuntimeAcquisitions++;
                throw new InvalidOperationException("Smoke must not acquire an execution runtime.");
            },
        };
        try
        {
            var result = await CliTestHost.RunAsync(
                ["dev", "package", "smoke", folder],
                runtime,
                host: host);

            Assert.Equal(CliExitCodes.Success, result.ExitCode);
            Assert.Equal(["host-ready", "owner-acquire", "owner-release", "owner-dispose"], calls);
            Assert.Equal(0, executionRuntimeAcquisitions);
            Assert.Contains("smoke completed", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Run_IsUnboundedAndCancellationDisposesOwnerSession()
    {
        var folder = CreateFolder();
        using var cancellation = new CancellationTokenSource();
        var disposed = false;
        var session = new FakeOwnerSession
        {
            Acquire = (_, watch, _) =>
            {
                Assert.True(watch);
                cancellation.Cancel();
                return Task.FromResult(Lease(watch));
            },
            Dispose = () =>
            {
                disposed = true;
                return ValueTask.CompletedTask;
            },
        };
        var runtime = new FakeRuntimeClient { DevPackageOwnerSession = _ => session };
        try
        {
            var command = Assert.IsType<RunDevPackageCommand>(
                new CliCommandParser().Parse(["dev", "package", "run", folder, "--watch"]).Invocation?.Command);
            Assert.False(CliCommandPlan.For(command).UseRequestTimeout);

            var result = await CliTestHost.RunAsync(
                ["dev", "package", "run", folder, "--watch"],
                runtime,
                token: cancellation.Token);

            Assert.Equal(CliExitCodes.Cancelled, result.ExitCode);
            Assert.True(disposed);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task FailedAcquireStillDisposesUncertainOwnerSession()
    {
        var folder = CreateFolder();
        var disposed = false;
        var session = new FakeOwnerSession
        {
            Acquire = (_, _, _) => throw new HttpRequestException("response lost"),
            Dispose = () =>
            {
                disposed = true;
                return ValueTask.CompletedTask;
            },
        };
        var runtime = new FakeRuntimeClient { DevPackageOwnerSession = _ => session };
        try
        {
            var result = await CliTestHost.RunAsync(
                ["dev", "package", "smoke", folder],
                runtime);

            Assert.Equal(CliExitCodes.Unavailable, result.ExitCode);
            Assert.True(disposed);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task MissingFolderFailsBeforeStartingManagedRuntime()
    {
        var hostCalled = false;
        var host = new FakeHostClient
        {
            EnsureReady = _ =>
            {
                hostCalled = true;
                return Task.FromResult(ReadyHost());
            },
        };

        var result = await CliTestHost.RunAsync(
            ["dev", "package", "smoke", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}")],
            host: host);

        Assert.Equal(CliExitCodes.NotFound, result.ExitCode);
        Assert.False(hostCalled);
    }

    private static string CreateFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sunder-cli-dev-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static HostRuntimeStatus ReadyHost()
        => new(
            HostRuntimeDesiredState.Running,
            HostRuntimeState.Ready,
            1,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            null,
            null,
            null);

    private static DevPackageOwnerLeaseResponse Lease(bool watch)
        => new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "cli-owner",
            "mutation-1",
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            2,
            [new DevPackageOwnerPackage("demo", watch)]);

    private static HttpResponseMessage Json<T>(T value)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => send(request);
    }

    private sealed class FakeOwnerSession : IDevPackageOwnerSession
    {
        public Func<IReadOnlyList<string>, bool, CancellationToken, Task<DevPackageOwnerLeaseResponse>> Acquire { get; init; }
            = (_, _, _) => throw new NotSupportedException();
        public Func<CancellationToken, Task> Release { get; init; } = _ => Task.CompletedTask;
        public Func<ValueTask> Dispose { get; init; } = () => ValueTask.CompletedTask;

        public Task<DevPackageOwnerLeaseResponse> AcquireAsync(
            IReadOnlyList<string> folders,
            bool watch,
            CancellationToken cancellationToken = default)
            => Acquire(folders, watch, cancellationToken);

        public Task ReleaseAsync(CancellationToken cancellationToken = default) => Release(cancellationToken);

        public ValueTask DisposeAsync() => Dispose();
    }
}
