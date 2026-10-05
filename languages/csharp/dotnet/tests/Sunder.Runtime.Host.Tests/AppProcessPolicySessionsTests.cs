using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.LocalState;
using Xunit;
using static Sunder.Runtime.Host.Tests.ActivePolicyTestContext;

namespace Sunder.Runtime.Host.Tests;

public sealed class AppProcessPolicySessionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnpublishedAppRetainsOnlyItsSelectedRegistryObligations(bool local)
    {
        using var fixture = new Fixture();
        if (!local) await fixture.Policy.RefreshAsync(packages: [], platforms: [Platform]);
        var source = fixture.Snapshots.Source!;
        var manifest = new Sunder.Package.Format.SunderPackageManifest
        {
            Id = source.PackageId, Version = source.Manifest!.Version,
            PackageArtifactImports = [new() { AuthorityConstraints = [new() { AuthorityKind = "platform", AuthorityId = Platform.AuthorityId,
                SelectedAuthorityRelease = Platform.ReleaseVersion, SelectedAuthorityContentIdentity = Platform.CatalogSha256 }] }],
        };
        fixture.Snapshots.Source = source with { Kind = PackageSourceKind.Dev, Manifest = manifest,
            DevelopmentImports = new(1, local ? "local" : "registry", source.PackageId, manifest.Version!, Hash("manifest"), local ? null : Origin, [], []) };
        var session = await fixture.OpenAsync();
        var permit = await fixture.Sessions.PermitAsync(fixture.Request(session), fixture.Peer, default);
        Assert.True(permit.Development);
        if (local)
        {
            Assert.Null(permit.Lease); Assert.Empty(fixture.Sessions.CapturePolicySubjects());
            fixture.Policy.Clock.Advance(TimeSpan.FromHours(24));
            Assert.False((await fixture.Sessions.HeartbeatAsync(fixture.Session(session), fixture.Peer, default)).ShutdownRequired);
            Assert.Equal(0, fixture.Process.Terminations); return;
        }
        Assert.Equal("valid", permit.Lease!.State);
        var subjects = Assert.Single(fixture.Sessions.CapturePolicySubjects()); Assert.Empty(subjects.Packages); Assert.Equal(Platform, Assert.Single(subjects.Platforms));
        fixture.Snapshots.Source = null;
        fixture.Policy.Clock.Advance(TimeSpan.FromHours(24));
        Assert.True((await fixture.Sessions.HeartbeatAsync(fixture.Session(session), fixture.Peer, default)).ShutdownRequired);
        fixture.Policy.Clock.Advance(TimeSpan.FromSeconds(5)); await fixture.Sessions.EnforceAsync(default); Assert.Equal(1, fixture.Process.Terminations);
    }

    [Fact]
    public async Task PermitIsRecordedDurablyBeforeReturningAndSurvivesHostRestart()
    {
        using var fixture = new Fixture();
        await fixture.Policy.RefreshAsync();
        var session = await fixture.OpenAsync();
        var permit = await fixture.Sessions.PermitAsync(fixture.Request(session), fixture.Peer, default);
        Assert.True(permit.Allowed);
        var status = await fixture.Sessions.HeartbeatAsync(fixture.Session(session), fixture.Peer, default);
        Assert.Single(status.LoadedPackages);
        using var restarted = fixture.CreateSessions(new RuntimeProtocolDescriptor());
        await restarted.InitializeAsync(default);
        fixture.Policy.Clock.Advance(TimeSpan.FromSeconds(5));
        await restarted.EnforceAsync(default);
        Assert.Equal(1, fixture.Process.Terminations);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("generation")]
    [InlineData("runtime")]
    [InlineData("snapshot")]
    [InlineData("content")]
    public async Task ForgedPermitAuthorityIsRejectedBeforeRecording(string mismatch)
    {
        using var fixture = new Fixture();
        await fixture.Policy.RefreshAsync();
        var session = await fixture.OpenAsync();
        var request = fixture.Request(session);
        var peer = fixture.Peer;
        switch (mismatch)
        {
            case "pid": peer = peer with { ProcessId = 999 }; break;
            case "generation": request = request with { Session = request.Session with { AppGenerationId = Guid.NewGuid() } }; break;
            case "runtime": request = request with { Session = request.Session with { RuntimeInstanceId = Guid.NewGuid() } }; break;
            case "snapshot": request = request with { SnapshotId = new('f', 32) }; break;
            case "content": request = request with { Package = request.Package with { PackageContentIdentity = Hash("other") } }; break;
        }
        await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => fixture.Sessions.PermitAsync(request, peer, default));
        Assert.Empty((await fixture.Sessions.HeartbeatAsync(fixture.Session(session), fixture.Peer, default)).LoadedPackages);
    }

    [Fact]
    public async Task OpenBindsTheClaimedPidToTheKernelPeer()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => fixture.Sessions.OpenAsync(new(fixture.Identity with { ProcessId = 456 }), fixture.Peer, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DenialOrExpiryBlocksNewPermitsAndForcesTheExactProcessAfterGrace(bool expired)
    {
        using var fixture = new Fixture();
        await fixture.Policy.RefreshAsync();
        var session = await fixture.OpenAsync();
        await fixture.Sessions.PermitAsync(fixture.Request(session), fixture.Peer, default);
        if (expired) fixture.Policy.Clock.Advance(TimeSpan.FromHours(24));
        else
        {
            fixture.Policy.Response = request => Allowed(request, 2) with
            { Packages = [new(ActivePolicyTestContext.Package, "SecurityBlocked", "Listed", "Denied", PackagePolicyErrorCodes.SecurityBlocked, "Blocked", [])] };
            await fixture.Policy.RefreshAsync();
        }
        await fixture.Sessions.EnforceAsync(default);
        var status = await fixture.Sessions.HeartbeatAsync(fixture.Session(session), fixture.Peer, default);
        Assert.True(status.ShutdownRequired);
        Assert.Equal(PackagePolicyErrorCodes.AppProcessTerminationRequired, status.ErrorCode);
        Assert.Equal(0, fixture.Process.Terminations);
        fixture.Policy.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Sessions.EnforceAsync(default);
        Assert.Equal(1, fixture.Process.Terminations);
        var replacement = fixture.Identity with { ProcessId = 457, AppGenerationId = Guid.NewGuid() };
        Assert.Equal(PackagePolicyErrorCodes.AppProcessTerminationRequired,
            (await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => fixture.Sessions.OpenAsync(new(replacement), new(457, null), default))).Code);
        fixture.Process.State = AppPolicyProcessState.ExitProven;
        await fixture.Sessions.EnforceAsync(default);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Policy.Paths.RootPath, RuntimeV1StateDescriptor.AppPolicySessionsDirectory), "*.json"));
    }

    [Fact]
    public async Task OwnerYankDoesNotTerminateAnExistingAllowedBinding()
    {
        using var fixture = new Fixture();
        fixture.Policy.Response = request => Allowed(request) with { Packages = [new(ActivePolicyTestContext.Package, "Active", "Yanked", "Allowed", null, null, [])] };
        await fixture.Policy.RefreshAsync();
        var session = await fixture.OpenAsync();
        await fixture.Sessions.PermitAsync(fixture.Request(session), fixture.Peer, default);
        await fixture.Sessions.EnforceAsync(default);
        Assert.False((await fixture.Sessions.HeartbeatAsync(fixture.Session(session), fixture.Peer, default)).ShutdownRequired);
    }

    [Fact]
    public async Task LoadedDenialBlocksAllPermitsAndRpcBeforePeriodicEnforcement()
    {
        using var fixture = new Fixture();
        var second = new RegistryPackageVersionIdentity("example.second", "1.0.0", Hash("second"));
        await fixture.Policy.RefreshAsync(packages: [ActivePolicyTestContext.Package, second]);
        var session = await fixture.OpenAsync();
        await fixture.Sessions.PermitAsync(fixture.Request(session), fixture.Peer, default);
        var loadedSource = fixture.Snapshots.Source!;
        fixture.Snapshots.Source = loadedSource with { PackageId = second.PackageId, ContentIdentity = second.PackageContentIdentity,
            Manifest = new() { Id = second.PackageId, Version = second.Version } };
        fixture.Policy.Response = request => Allowed(request, 2) with
        {
            Packages = request.Packages.Select(package => package == ActivePolicyTestContext.Package
                ? new RegistryPackagePolicyDecision(package, "SecurityBlocked", "Listed", "Denied", PackagePolicyErrorCodes.SecurityBlocked, "Blocked", [])
                : new(package, "Active", "Listed", "Allowed", null, null, [])).ToArray(),
        };
        await fixture.Policy.RefreshAsync(packages: [ActivePolicyTestContext.Package, second]);

        Assert.Equal(PackagePolicyErrorCodes.AppProcessTerminationRequired,
            Assert.Throws<PackagePolicyDeniedException>(() => fixture.Sessions.RequireLoaded(fixture.Peer, loadedSource)).Code);
        var next = fixture.Request(session) with { Package = new(second.PackageId, second.Version, second.PackageContentIdentity) };
        Assert.Equal(PackagePolicyErrorCodes.AppProcessTerminationRequired,
            (await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => fixture.Sessions.PermitAsync(next, fixture.Peer, default))).Code);
        var status = await fixture.Sessions.HeartbeatAsync(fixture.Session(session), fixture.Peer, default);
        Assert.True(status.ShutdownRequired);
        Assert.Equal(ActivePolicyTestContext.Package.PackageId, Assert.Single(status.LoadedPackages).PackageId);
        Assert.Equal(0, fixture.Process.Terminations);
    }

    [Fact]
    public async Task HeartbeatObservesExpiryWithoutWaitingForTheEnforcementLoop()
    {
        using var fixture = new Fixture();
        await fixture.Policy.RefreshAsync();
        var session = await fixture.OpenAsync();
        await fixture.Sessions.PermitAsync(fixture.Request(session), fixture.Peer, default);
        fixture.Policy.Clock.Advance(TimeSpan.FromHours(24));
        Assert.True((await fixture.Sessions.HeartbeatAsync(fixture.Session(session), fixture.Peer, default)).ShutdownRequired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameContentCannotReuseAPermitFromAnotherDevelopmentOrOriginContext(bool firstDevelopment)
    {
        using var fixture = new Fixture();
        await fixture.Policy.RefreshAsync();
        fixture.Snapshots.Source = fixture.Snapshots.Source! with { Kind = firstDevelopment ? PackageSourceKind.Dev : PackageSourceKind.Installed };
        var session = await fixture.OpenAsync();
        await fixture.Sessions.PermitAsync(fixture.Request(session), fixture.Peer, default);
        fixture.Snapshots.Source = fixture.Snapshots.Source with { Kind = firstDevelopment ? PackageSourceKind.Installed : PackageSourceKind.Dev };
        await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => fixture.Sessions.PermitAsync(fixture.Request(session), fixture.Peer, default));
        Assert.Throws<PackagePolicyDeniedException>(() => fixture.Sessions.RequireLoaded(fixture.Peer, fixture.Snapshots.Source));
    }

    [Fact]
    public async Task LoadedAppSubjectsAndPlatformsRemainInRefreshAfterCatalogAndSnapshotRemoval()
    {
        using var fixture = new Fixture();
        await fixture.Policy.RefreshAsync(platforms: [Platform]);
        var session = await fixture.OpenAsync();
        await fixture.Sessions.PermitAsync(fixture.Request(session), fixture.Peer, default);
        fixture.Snapshots.Source = null;
        var services = new ServiceCollection();
        services.AddRuntimeHostServices(fixture.Policy.Paths, new RuntimeBearerTokenValidator("test-token"));
        services.AddSingleton(fixture.Sessions);
        await using var provider = services.BuildServiceProvider();
        Assert.Empty(await provider.GetRequiredService<InstalledPackageStore>().ListAsync());
        var subjects = await provider.GetRequiredService<ActivePackagePolicySynchronizer>().CaptureSubjectsAsync(default);
        var group = Assert.Single(subjects);
        Assert.Equal(ActivePolicyTestContext.Package, Assert.Single(group.Packages));
        Assert.Equal(Platform, Assert.Single(group.Platforms));
        using var recovered = fixture.CreateSessions(new RuntimeProtocolDescriptor());
        await recovered.InitializeAsync(default);
        Assert.Equal(Platform, Assert.Single(Assert.Single(recovered.CapturePolicySubjects()).Platforms));
    }

    [Fact]
    public async Task UnprovenExitNeverCompletesBlockAndRetriesAfterVerificationWindow()
    {
        using var fixture = new Fixture();
        await fixture.Policy.RefreshAsync();
        var session = await fixture.OpenAsync();
        await fixture.Sessions.PermitAsync(fixture.Request(session), fixture.Peer, default);
        await fixture.Sessions.ShutdownAsync(fixture.Session(session), fixture.Peer, default);
        fixture.Policy.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Sessions.EnforceAsync(default);
        fixture.Policy.Clock.Advance(TimeSpan.FromSeconds(14));
        await fixture.Sessions.EnforceAsync(default);
        Assert.Equal(1, fixture.Process.Terminations);
        fixture.Policy.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Sessions.EnforceAsync(default);
        Assert.Equal(2, fixture.Process.Terminations);
        Assert.True((await fixture.Sessions.HeartbeatAsync(fixture.Session(session), fixture.Peer, default)).ShutdownRequired);
    }

    [Fact]
    public void TcpConnectionCannotClaimLocalProcessAuthority()
    {
        Assert.Throws<PackagePolicyDeniedException>(() => AppPolicyPeerIdentity.Read(new DefaultHttpContext()));
    }

    [Fact]
    public async Task KestrelPreservesKernelPeerIdentityThroughLocalHttp()
    {
        if (OperatingSystem.IsWindows()) return; // The named-pipe feature is covered by the native child lane.
        var address = "/tmp/sap-http-" + Guid.NewGuid().ToString("N");
        using var fixture = new Fixture(new(Environment.ProcessId, OperatingSystem.IsMacOS() ? RuntimeProcessIdentity.CurrentAuditToken() : null));
        await fixture.Policy.RefreshAsync();
        var session = await fixture.OpenAsync();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(fixture.Sessions);
        builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(address));
        await using var app = builder.Build();
        app.MapGet("/peer", (HttpContext context) => AppPolicyPeerIdentity.Read(context).ProcessId);
        app.MapGroup("/api/v1").MapAppPolicyEndpoints();
        await app.StartAsync();
        try
        {
            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, token) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(address), token); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                },
            };
            using var client = new HttpClient(handler);
            Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), await client.GetStringAsync("http://localhost/peer"));
            using var statusResponse = await client.GetAsync($"http://localhost/api/v1/app-policy-sessions/{session.SessionId}");
            Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
            Assert.True(statusResponse.Headers.CacheControl?.NoStore);
            var status = await statusResponse.Content.ReadFromJsonAsync<RuntimeAppPolicySessionStatus>();
            Assert.Equal(RuntimeAppPolicySessionStates.Active, status!.State);
            var forgedExit = JsonSerializer.SerializeToNode(fixture.Session(session))!.AsObject();
            forgedExit["state"] = RuntimeAppPolicySessionStates.ExitProven;
            using var rejected = await client.PostAsJsonAsync($"http://localhost/api/v1/app-policy-sessions/{session.SessionId}/shutdown", forgedExit);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.False((await fixture.Sessions.GetStatusAsync(session.SessionId, fixture.Peer, default)).ShutdownRequired);
            await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => fixture.Sessions.GetStatusAsync(session.SessionId, new(999, null), default));
        }
        finally { await app.StopAsync(); File.Delete(address); }
    }

    [Fact]
    public async Task NativePeerIdentityAndStableTerminationTargetAnActualChildProcess()
    {
        using var scope = Sunder.Core.TestSupport.TemporaryDirectoryScope.Create("sunder-app-peer");
        var address = OperatingSystem.IsWindows() ? "sunder-app-peer-" + Guid.NewGuid().ToString("N") : "/tmp/sap-" + Guid.NewGuid().ToString("N");
        using var pipe = OperatingSystem.IsWindows() ? new NamedPipeServerStream(address, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly) : null;
        using var listener = OperatingSystem.IsWindows() ? null : new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener?.Bind(new UnixDomainSocketEndPoint(address)); listener?.Listen(1);
        var start = TestWorkerFixture.CreateProcessStartInfo(scope.RootPath);
        start.ArgumentList.Add("--sunder-test-policy-peer"); start.ArgumentList.Add(address);
        using var child = Process.Start(start)!;
        try
        {
            var context = new DefaultHttpContext();
            using var accepted = listener is null ? null : await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (pipe is not null)
            {
                await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
                context.Features.Set<IConnectionNamedPipeFeature>(new PipeFeature(pipe));
            }
            else context.Features.Set<IConnectionSocketFeature>(new SocketFeature(accepted!));
            var peer = AppPolicyPeerIdentity.Read(context);
            Assert.Equal(child.Id, peer.ProcessId);
            var identity = new RuntimeAppProcessIdentity(child.Id, RuntimeProcessIdentity.Birth(child, peer.AuditToken), RuntimeProcessIdentity.ExecutableHash(child), "native-fixture", Guid.NewGuid(), Guid.NewGuid());
            using var process = new AppPolicyNativeProcess(identity, peer);
            Assert.Equal(AppPolicyProcessState.Alive, process.Probe());
            using var wrongBirth = new AppPolicyNativeProcess(identity with { ProcessBirthIdentity = identity.ProcessBirthIdentity + "-different" }, peer);
            wrongBirth.Terminate();
            Assert.False(child.HasExited);
            process.Terminate();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(AppPolicyProcessState.ExitProven, process.Probe());
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
            if (!OperatingSystem.IsWindows()) File.Delete(address);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal ActivePolicyTestContext Policy { get; } = new();
        internal RuntimeProtocolDescriptor Protocol { get; } = new();
        internal AppPolicyPeer Peer { get; }
        internal RuntimeAppProcessIdentity Identity { get; }
        internal FakeProcess Process { get; } = new();
        internal FakeSnapshots Snapshots { get; }
        internal AppProcessPolicySessions Sessions { get; }
        internal Fixture(AppPolicyPeer? peer = null)
        {
            Peer = peer ?? new(123, null);
            Identity = new(Peer.ProcessId, "test-birth", Hash("app"), "test-build", Guid.NewGuid(), Protocol.RuntimeInstanceId);
            Snapshots = new(new(ActivePolicyTestContext.Package.PackageId, PackageSourceKind.Installed, Policy.Paths.RootPath,
                ContentIdentity: ActivePolicyTestContext.Package.PackageContentIdentity,
                Manifest: new() { Id = ActivePolicyTestContext.Package.PackageId, Version = "1.0.0" }) { RegistryPolicyOrigin = Origin });
            Sessions = CreateSessions(Protocol);
        }
        internal AppProcessPolicySessions CreateSessions(RuntimeProtocolDescriptor protocol) => new(Policy.Paths, protocol, Snapshots, new(Policy.Cache), Policy.Cache,
            Policy.Clock, Policy.Time, new FakeController(Process), NullLogger<AppProcessPolicySessions>.Instance);
        internal Task<RuntimeAppPolicySessionDescriptor> OpenAsync() => Sessions.OpenAsync(new(Identity), Peer, default);
        internal RuntimeAppPolicySessionRequest Session(RuntimeAppPolicySessionDescriptor value) => new(value.SessionId, value.Process.AppGenerationId, value.Process.RuntimeInstanceId);
        internal RuntimeAppPackageLoadPermitRequest Request(RuntimeAppPolicySessionDescriptor value) => new(Session(value), new(ActivePolicyTestContext.Package.PackageId, "1.0.0", ActivePolicyTestContext.Package.PackageContentIdentity)) { SnapshotId = new('a', 32) };
        public void Dispose() { Sessions.Dispose(); Policy.Dispose(); }
    }
    private sealed class FakeSnapshots(RuntimePackageSource source) : IAppPolicySnapshotSource
    {
        internal RuntimePackageSource? Source { get; set; } = source;
        public RuntimePackageSource? PolicySource(string id) => id == new string('a', 32) ? Source : null;
    }
    private sealed class FakeController(FakeProcess process) : IAppPolicyProcessController
    { public IAppPolicyProcessHandle Attach(RuntimeAppProcessIdentity identity, AppPolicyPeer peer, bool recovery) => process; }
    private sealed class FakeProcess : IAppPolicyProcessHandle
    {
        internal AppPolicyProcessState State = AppPolicyProcessState.Alive;
        internal int Terminations;
        public AppPolicyProcessState Probe() => State;
        public void Terminate() => Terminations++;
        public void Dispose() { }
    }
    private sealed class SocketFeature(Socket socket) : IConnectionSocketFeature { public Socket Socket => socket; }
    private sealed class PipeFeature(NamedPipeServerStream pipe) : IConnectionNamedPipeFeature { public NamedPipeServerStream NamedPipe => pipe; }
}
