using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Compatibility;
using Xunit;
using static Sunder.Runtime.Host.Tests.ActivePolicyTestContext;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class AppProcessPolicySessionsTests
{
    [Theory]
    [InlineData(SunderSdkCapabilities.WorkerExecutionObservationsV1, false)]
    [InlineData(SunderSdkCapabilities.WorkerExecutionObservationsV1, true)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentRevisionsV1, false)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentRevisionsV1, true)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentReloadV1, false)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentReloadV1, true)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentLogsV1, false)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentLogsV1, true)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentLogQueriesV1, false)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentLogQueriesV1, true)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentViewsV1, false)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentViewsV1, true)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentFeedbackV1, false)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentFeedbackV1, true)]
    [InlineData(SunderSdkCapabilities.AppActivationServicesV1, false)]
    [InlineData(SunderSdkCapabilities.AppActivationServicesV1, true)]
    public async Task OlderAppCannotObtainAnObservationTargetPermitWithoutItsImplementedCapability(string capability, bool tracked)
    {
        using var fixture = new Fixture();
        await fixture.Policy.RefreshAsync(platforms: [Platform]);
        var target = SetObservationTarget(fixture, capability);
        var session = await fixture.OpenAsync();
        // Exact old wire shape: no implemented-capability metadata. Authentication,
        // process birth, policy, snapshot and target otherwise match this live App.
        var request = fixture.Request(session) with
        {
            AppActivationId = tracked ? Guid.NewGuid() : null,
            AppTarget = tracked ? target : null,
        };
        var denied = await Assert.ThrowsAsync<RuntimeValidationException>(
            () => fixture.Sessions.PermitAsync(request, fixture.Peer, default));
        Assert.Contains(capability, denied.Message, StringComparison.Ordinal);
        if (capability is SunderSdkCapabilities.PackageDevelopmentLogQueriesV1 or SunderSdkCapabilities.PackageDevelopmentFeedbackV1)
        {
            var legacyOnly = request with { AppImplementedCapabilities = [SunderSdkCapabilities.PackageDevelopmentLogsV1, SunderSdkCapabilities.PackageDevelopmentViewsV1] };
            var legacyDenied = await Assert.ThrowsAsync<RuntimeValidationException>(
                () => fixture.Sessions.PermitAsync(legacyOnly, fixture.Peer, default));
            Assert.Contains(capability, legacyDenied.Message, StringComparison.Ordinal);
        }
        Assert.Empty((await fixture.Sessions.HeartbeatAsync(request.Session, fixture.Peer, default)).LoadedPackages);
        Assert.Equal(0, fixture.Process.Terminations);
    }

    [Theory]
    [InlineData(SunderSdkCapabilities.WorkerExecutionObservationsV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentRevisionsV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentReloadV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentLogsV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentLogQueriesV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentViewsV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentFeedbackV1)]
    [InlineData(SunderSdkCapabilities.AppActivationServicesV1)]
    [InlineData("both")]
    public async Task CurrentAppCanObtainExactTrackedPermitWithItsImplementedCapabilities(string selection)
    {
        using var fixture = new Fixture();
        await fixture.Policy.RefreshAsync(platforms: [Platform]);
        string[] capabilities = selection == "both"
            ? [SunderSdkCapabilities.WorkerExecutionObservationsV1, SunderSdkCapabilities.PackageDevelopmentRevisionsV1] : [selection];
        var target = SetObservationTarget(fixture, capabilities);
        var session = await fixture.OpenAsync();
        var request = fixture.Request(session) with
        {
            AppActivationId = Guid.NewGuid(), AppTarget = target, AppImplementedCapabilities = capabilities,
        };
        var permit = await fixture.Sessions.PermitAsync(request, fixture.Peer, default);
        Assert.True(permit.Allowed);
        Assert.Equal(request.AppActivationId, permit.AppActivationId);
        Assert.NotNull(permit.PermitId);
        Assert.True(fixture.Sessions.RequireCallerPermit(fixture.Peer, fixture.Snapshots.Source!,
            request.AppActivationId!.Value, permit.PermitId, target)!.IsActive);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("case")]
    [InlineData("duplicate")]
    [InlineData("blank")]
    [InlineData("length")]
    [InlineData("count")]
    public async Task UnmatchedOrMalformedImplementedCapabilitiesNeverRecordLoadedSubjects(string mutation)
    {
        using var fixture = new Fixture();
        await fixture.Policy.RefreshAsync(platforms: [Platform]);
        const string capability = SunderSdkCapabilities.WorkerExecutionObservationsV1;
        var target = SetObservationTarget(fixture, capability);
        var session = await fixture.OpenAsync();
        string[] claims = mutation switch
        {
            "wrong" => [SunderSdkCapabilities.PackageDevelopmentRevisionsV1], "case" => [capability.ToUpperInvariant()],
            "duplicate" => [capability, capability], "blank" => [capability, " "], "length" => [capability, new('a', 129)],
            _ => Enumerable.Repeat(capability, 129).ToArray(),
        };
        var request = fixture.Request(session) with
        { AppActivationId = Guid.NewGuid(), AppTarget = target, AppImplementedCapabilities = claims };
        await Assert.ThrowsAsync<RuntimeValidationException>(() => fixture.Sessions.PermitAsync(request, fixture.Peer, default));
        Assert.Empty((await fixture.Sessions.HeartbeatAsync(request.Session, fixture.Peer, default)).LoadedPackages);
    }

    private static PackageTargetDescriptor SetObservationTarget(Fixture fixture, params string[] capabilities)
    {
        _ = SetTrackedTarget(fixture, development: true, web: false);
        var source = fixture.Snapshots.Source!;
        var original = Assert.IsType<SunderPackageTargetManifest>(Assert.Single(source.Manifest!.Targets!));
        var target = new SunderPackageTargetManifest
        {
            Role = original.Role, Rid = original.Rid, Kind = original.Kind, EntryPoint = original.EntryPoint,
            TargetFramework = original.TargetFramework, SdkVersion = original.SdkVersion,
            RequiredHostCapabilities = [SunderSdkCapabilities.Baseline11V1, .. capabilities],
        };
        fixture.Snapshots.Source = source with
        {
            Manifest = new() { Id = source.Manifest.Id, Version = source.Manifest.Version, Targets = [target],
                PackageArtifactImports = source.Manifest.PackageArtifactImports },
        };
        return PackageTargetSelection.ToDescriptor(target);
    }
}
