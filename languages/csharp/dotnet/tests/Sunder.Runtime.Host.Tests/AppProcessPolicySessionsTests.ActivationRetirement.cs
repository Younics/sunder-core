using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;
using static Sunder.Runtime.Host.Tests.ActivePolicyTestContext;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class AppProcessPolicySessionsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetiredManagedAndWebActivationsRetainLoadedSecurityAndOriginalDenialDeadline(bool development, bool web)
    {
        using var fixture = new Fixture();
        await fixture.Policy.RefreshAsync(platforms: [Platform]);
        var target = SetTrackedTarget(fixture, development, web);
        var session = await fixture.OpenAsync();
        var request = fixture.Request(session) with { AppActivationId = Guid.NewGuid(), AppTarget = target };
        var permit = await fixture.Sessions.PermitAsync(request, fixture.Peer, default);
        Assert.Equal(permit.PermitId, (await fixture.Sessions.PermitAsync(request, fixture.Peer, default)).PermitId);
        var retired = new RuntimeAppActivationRetirementRequest(request.Session, request.AppActivationId.Value, permit.PermitId!);
        Assert.Equal("retired", (await fixture.Sessions.RetireActivationAsync(retired, fixture.Peer, default)).State);
        Assert.Equal("retired", (await fixture.Sessions.RetireActivationAsync(retired, fixture.Peer, default)).State);
        await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => fixture.Sessions.PermitAsync(request, fixture.Peer, default));
        Assert.Throws<PackagePolicyDeniedException>(() => fixture.Sessions.RequireCallerPermit(fixture.Peer, fixture.Snapshots.Source!, request.AppActivationId.Value, permit.PermitId, target));
        fixture.Snapshots.Source = null;
        var subjects = Assert.Single(fixture.Sessions.CapturePolicySubjects());
        Assert.Equal(Platform, Assert.Single(subjects.Platforms));
        Assert.Equal(development ? 0 : 1, subjects.Packages.Count);
        fixture.Policy.Response = sync => Allowed(sync, 2) with
        {
            Packages = sync.Packages.Select(package => new RegistryPackagePolicyDecision(package, "SecurityBlocked", "Listed", "Denied", PackagePolicyErrorCodes.SecurityBlocked, "blocked", [Platform])).ToArray(),
            PlatformCatalogs = sync.PlatformCatalogs.Select(platform => new RegistryPlatformPolicyDecision(platform, "Revoked", "Denied", PackagePolicyErrorCodes.ContractAuthorityRevoked, ObservedAt)).ToArray(),
        };
        await fixture.Policy.RefreshAsync(packages: subjects.Packages, platforms: subjects.Platforms);
        Assert.True((await fixture.Sessions.HeartbeatAsync(request.Session, fixture.Peer, default)).ShutdownRequired);
        await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => fixture.Sessions.RetireActivationAsync(retired, fixture.Peer, default));
        fixture.Policy.Clock.Advance(TimeSpan.FromMilliseconds(4999)); await fixture.Sessions.EnforceAsync(default); Assert.Equal(0, fixture.Process.Terminations);
        fixture.Policy.Clock.Advance(TimeSpan.FromMilliseconds(1)); await fixture.Sessions.EnforceAsync(default); Assert.Equal(1, fixture.Process.Terminations);
        Assert.Single((await fixture.Sessions.HeartbeatAsync(request.Session, fixture.Peer, default)).LoadedPackages);
    }

    [Theory]
    [InlineData("peer")]
    [InlineData("session")]
    [InlineData("process-generation")]
    [InlineData("runtime")]
    [InlineData("activation")]
    [InlineData("permit")]
    public async Task RetirementRejectsForgedAuthorityWithoutRevokingTheOwnedActivation(string mismatch)
    {
        using var fixture = new Fixture();
        await fixture.Policy.RefreshAsync(platforms: [Platform]);
        var target = SetTrackedTarget(fixture, development: true, web: true);
        var session = await fixture.OpenAsync();
        var request = fixture.Request(session) with { AppActivationId = Guid.NewGuid(), AppTarget = target };
        var permit = await fixture.Sessions.PermitAsync(request, fixture.Peer, default);
        var retire = new RuntimeAppActivationRetirementRequest(request.Session, request.AppActivationId.Value, permit.PermitId!);
        var peer = fixture.Peer;
        switch (mismatch)
        {
            case "peer": peer = peer with { ProcessId = 456 }; break;
            case "session": retire = retire with { Session = retire.Session with { SessionId = new('f', 32) } }; break;
            case "process-generation": retire = retire with { Session = retire.Session with { AppGenerationId = Guid.NewGuid() } }; break;
            case "runtime": retire = retire with { Session = retire.Session with { RuntimeInstanceId = Guid.NewGuid() } }; break;
            case "activation": retire = retire with { AppActivationId = Guid.NewGuid() }; break;
            case "permit": retire = retire with { LoadPermitId = new('f', 32) }; break;
        }
        await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => fixture.Sessions.RetireActivationAsync(retire, peer, default));
        Assert.True(fixture.Sessions.RequireCallerPermit(fixture.Peer, fixture.Snapshots.Source!, request.AppActivationId.Value, permit.PermitId, target)!.IsActive);
        Assert.Throws<PackagePolicyDeniedException>(() => fixture.Sessions.RequireCallerPermit(fixture.Peer, fixture.Snapshots.Source!, request.AppActivationId.Value, null, target));
        Assert.Throws<PackagePolicyDeniedException>(() => fixture.Sessions.RequireCallerPermit(fixture.Peer, fixture.Snapshots.Source!, request.AppActivationId.Value, permit.PermitId, target with { EntryPoint = "other.html" }));
    }

    [Fact]
    public async Task CancelledRetirementKeepsTerminalAdmissionFenceAndWaitsForOpeningWork()
    {
        using var fixture = new Fixture();
        await fixture.Policy.RefreshAsync(platforms: [Platform]);
        var target = SetTrackedTarget(fixture, development: true, web: false);
        var session = await fixture.OpenAsync();
        var request = fixture.Request(session) with { AppActivationId = Guid.NewGuid(), AppTarget = target };
        var permit = await fixture.Sessions.PermitAsync(request, fixture.Peer, default);
        var authority = fixture.Sessions.RequireCallerPermit(fixture.Peer, fixture.Snapshots.Source!, request.AppActivationId.Value, permit.PermitId, target)!;
        using var opening = authority.Acquire();
        using var cancel = new CancellationTokenSource();
        var retire = new RuntimeAppActivationRetirementRequest(request.Session, request.AppActivationId.Value, permit.PermitId!);
        var pending = fixture.Sessions.RetireActivationAsync(retire, fixture.Peer, cancel.Token);
        await WaitUntilAsync(() => authority.RetirementToken.IsCancellationRequested);
        Assert.False(pending.IsCompleted);
        Assert.Throws<SunderRpcException>(() => authority.Acquire());
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => fixture.Sessions.PermitAsync(request, fixture.Peer, default));
        Assert.False((await fixture.Sessions.HeartbeatAsync(request.Session, fixture.Peer, default)).ShutdownRequired);
        opening.Dispose();
        Assert.Equal("retired", (await fixture.Sessions.RetireActivationAsync(retire, fixture.Peer, default)).State);
        var replacement = await fixture.Sessions.PermitAsync(request with { AppActivationId = Guid.NewGuid() }, fixture.Peer, default);
        Assert.NotEqual(permit.PermitId, replacement.PermitId);
    }

    [Fact]
    public async Task OriginalPermitAnchorRetiresEverySourcePermitOfTheSameActualActivation()
    {
        using var fixture = new Fixture();
        await fixture.Policy.RefreshAsync(platforms: [Platform]);
        var target = SetTrackedTarget(fixture, development: true, web: false);
        var session = await fixture.OpenAsync();
        var request = fixture.Request(session) with { AppActivationId = Guid.NewGuid(), AppTarget = target };
        var original = await fixture.Sessions.PermitAsync(request, fixture.Peer, default);
        var source = fixture.Snapshots.Source!;
        fixture.Snapshots.Source = source with { ContentIdentity = Hash("replacement-source") };
        var replacementRequest = request with { Package = request.Package with { PackageContentIdentity = fixture.Snapshots.Source.ContentIdentity! } };
        var replacement = await fixture.Sessions.PermitAsync(replacementRequest, fixture.Peer, default);
        Assert.NotEqual(original.PermitId, replacement.PermitId);
        var first = fixture.Sessions.RequireCallerPermit(fixture.Peer, source, request.AppActivationId!.Value, original.PermitId, target)!;
        var second = fixture.Sessions.RequireCallerPermit(fixture.Peer, fixture.Snapshots.Source, request.AppActivationId.Value, replacement.PermitId, target)!;
        Assert.Same(first, second);
        await fixture.Sessions.RetireActivationAsync(new(request.Session, request.AppActivationId.Value, original.PermitId!), fixture.Peer, default);
        Assert.False(second.IsActive);
        Assert.Throws<PackagePolicyDeniedException>(() => fixture.Sessions.RequireCallerPermit(fixture.Peer, fixture.Snapshots.Source, request.AppActivationId.Value, replacement.PermitId, target));
        Assert.Equal(2, (await fixture.Sessions.HeartbeatAsync(request.Session, fixture.Peer, default)).LoadedPackages.Count);
        Assert.Equal("retired", (await fixture.Sessions.RetireActivationAsync(new(request.Session, request.AppActivationId.Value, replacement.PermitId!), fixture.Peer, default)).State);
    }

    [Fact]
    public async Task PublicationFenceBlocksNewPermitsWhilePolicyEnforcementContinues()
    {
        using var fixture = new Fixture();
        await fixture.Policy.RefreshAsync(platforms: [Platform]);
        var target = SetTrackedTarget(fixture, development: true, web: true);
        var session = await fixture.OpenAsync();
        var request = fixture.Request(session) with { AppActivationId = Guid.NewGuid(), AppTarget = target };
        await fixture.Sessions.PermitAsync(request, fixture.Peer, default);
        using var fence = await fixture.Sessions.FencePublicationAsync(ActivePackageSession.CreateEmpty(), default);
        await Assert.ThrowsAsync<RuntimeUnavailableException>(() => fixture.Sessions.PermitAsync(request with { AppActivationId = Guid.NewGuid() }, fixture.Peer, default));
        fixture.Policy.Clock.Advance(TimeSpan.FromHours(24));
        Assert.True((await fixture.Sessions.HeartbeatAsync(request.Session, fixture.Peer, default)).ShutdownRequired);
        fixture.Policy.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Sessions.EnforceAsync(default);
        Assert.Equal(1, fixture.Process.Terminations);
    }

    private static PackageTargetDescriptor SetTrackedTarget(Fixture fixture, bool development, bool web)
    {
        var source = fixture.Snapshots.Source!;
        var target = new SunderPackageTargetManifest { Role = "app", Rid = "osx-arm64", Kind = web ? "web" : "avalonia", EntryPoint = web ? "index.html" : "lib/App.dll", TargetFramework = web ? null : "net10.0", SdkVersion = "1.1.0", RequiredHostCapabilities = [] };
        fixture.Snapshots.Source = source with
        {
            Kind = development ? PackageSourceKind.Dev : PackageSourceKind.Installed,
            Manifest = new() { Id = source.PackageId, Version = source.Manifest!.Version, Targets = [target],
                PackageArtifactImports = development ? [new() { AuthorityConstraints = [new() { AuthorityKind = "platform", AuthorityId = Platform.AuthorityId, SelectedAuthorityRelease = Platform.ReleaseVersion, SelectedAuthorityContentIdentity = Platform.CatalogSha256 }] }] : [] },
            DevelopmentImports = development ? new(1, "registry", source.PackageId, "1.0.0", Hash("manifest"), Origin, [], []) : null,
        };
        return PackageTargetSelection.ToDescriptor(target);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }
}
