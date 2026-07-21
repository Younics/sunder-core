using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeLifecycleSnapshotTests
{
    [Fact]
    public async Task BootstrapState_IsExplicitAndUsesOneRuntimeIdentityAcrossSnapshotsAndEvents()
    {
        var protocol = new RuntimeProtocolDescriptor();
        var events = new RuntimeEventStreamService(protocol);
        var owner = new RuntimeSessionOwner(NullLogger<RuntimeSessionOwner>.Instance, events);

        var starting = owner.GetSnapshot();
        var bootstrapCompletion = owner.WaitForBootstrapAsync();
        Assert.False(bootstrapCompletion.IsCompleted);
        owner.MarkReady(["bootstrap warning"]);
        await bootstrapCompletion;
        var ready = owner.GetSnapshot();
        owner.MarkShuttingDown();
        var stopping = owner.GetSnapshot();

        Assert.Equal(RuntimeBootstrapState.Starting, starting.BootstrapState);
        Assert.Equal(RuntimeBootstrapState.Ready, ready.BootstrapState);
        Assert.Equal(RuntimeBootstrapState.ShuttingDown, stopping.BootstrapState);
        Assert.Contains("bootstrap warning", ready.Warnings);
        Assert.Equal(starting.RuntimeInstanceId, ready.RuntimeInstanceId);
        Assert.Equal(protocol.Handshake.RuntimeInstanceId, starting.RuntimeInstanceId);
        Assert.Equal(ready.RuntimeInstanceId, stopping.RuntimeInstanceId);
        Assert.All(events.GetSnapshot().Events, item => Assert.Equal(starting.RuntimeInstanceId, item.RuntimeInstanceId));
        Assert.Equal(stopping.EventSequence, events.GetSnapshot().SequenceId);
    }

    [Fact]
    public void BootstrapFailure_RemainsQueryableWithDiagnostics()
    {
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService());

        owner.MarkFailed("bootstrap failed", ["warning"], ["package failed"]);

        var snapshot = owner.GetSnapshot();
        Assert.Equal(RuntimeBootstrapState.Failed, snapshot.BootstrapState);
        Assert.Contains("warning", snapshot.Warnings);
        Assert.Contains("package failed", snapshot.Errors);
    }

    [Fact]
    public async Task Publication_CommitsSourcesPackagesAndEventAsOneGeneration_AndRejectsStaleCandidate()
    {
        var events = new RuntimeEventStreamService();
        var owner = new RuntimeSessionOwner(NullLogger<RuntimeSessionOwner>.Instance, events);
        var sources = owner.Sources.Snapshot();
        sources.SetDevOverlay(new PackageSessionDevOverlay(
            "first.package",
            "/first",
            Watch: false,
            PackageSessionOverlayOwner.Startup));
        var first = CreateSession("first.package");

        await owner.PublishAsync(first, sources, [], ["first warning"], [], expectedGeneration: 0);

        var committed = owner.GetSnapshot();
        var generationEvent = Assert.Single(events.GetSnapshot().Events, item => item.Kind == RuntimeEventKind.SessionGenerationChanged);
        Assert.Equal(1, committed.SessionGeneration);
        Assert.Equal(generationEvent.SequenceId, committed.EventSequence);
        Assert.Equal(committed.RuntimeInstanceId, generationEvent.RuntimeInstanceId);
        Assert.Equal("first.package", Assert.Single(committed.ActivePackages).PackageId);
        Assert.Equal("first.package", Assert.Single(committed.SessionPackages).PackageId);
        Assert.Equal("first.package", Assert.Single(owner.Sources.Snapshot().ActiveDevOverlays).PackageId);

        var staleSources = owner.Sources.Snapshot();
        staleSources.SetDevOverlay(new PackageSessionDevOverlay(
            "stale.package",
            "/stale",
            Watch: false,
            PackageSessionOverlayOwner.Startup));
        var stale = CreateSession("stale.package");
        await Assert.ThrowsAsync<RuntimeStaleGenerationException>(() => owner.PublishAsync(
            stale,
            staleSources,
            [],
            [],
            [],
            expectedGeneration: 0));

        var unchanged = owner.GetSnapshot();
        Assert.Equal(committed, unchanged);
        Assert.Equal("first.package", Assert.Single(owner.Sources.Snapshot().ActiveDevOverlays).PackageId);
        await stale.DisposeAsync();
        await owner.State.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task Publication_DefensivelyFreezesCommittedPackagePayloadAndDiagnostics()
    {
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService());
        var views = new List<PackageViewDescriptor>
        {
            new("first.view", "test.package", "First", Icon: null, DefaultPlacement: null),
        };
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = new SessionPackageDescriptor(
                    "test.package",
                    "Test Package",
                    "1.0.0",
                    PackageHostRoles.App | PackageHostRoles.Runtime,
                    Icon: null,
                    IsEnabled: true,
                    PackageReadinessState.Ready,
                    views,
                    FailureOrigin: null,
                    LastError: null,
                    LastFailureAtUtc: null,
                    FailureCount: 0),
            });
        var uiSnapshots = new List<PackageUiSnapshotDescriptor>
        {
            new("test.package", PackageSourceKind.Dev, 1, "hash", "snapshot", "packages/ui-snapshots/snapshot"),
        };
        var warnings = new List<string> { "original warning" };
        var errors = new List<string> { "original error" };

        await owner.PublishAsync(
            session,
            owner.Sources.Snapshot(),
            uiSnapshots,
            warnings,
            errors,
            expectedGeneration: 0);
        views[0] = views[0] with { Title = "mutated view" };
        uiSnapshots[0] = uiSnapshots[0] with { SnapshotId = "mutated-snapshot" };
        warnings[0] = "mutated warning";
        errors[0] = "mutated error";

        var committed = owner.GetSnapshot();
        Assert.Equal("First", Assert.Single(Assert.Single(committed.ActivePackages).Views).Title);
        Assert.Equal("First", Assert.Single(Assert.Single(committed.SessionPackages).Views).Title);
        Assert.Equal("snapshot", Assert.Single(committed.PackageUiSnapshots).SnapshotId);
        Assert.Equal("original warning", Assert.Single(committed.Warnings));
        Assert.Equal("original error", Assert.Single(committed.Errors));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ActivePackageDescriptor>)committed.ActivePackages)[0] = committed.ActivePackages[0]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<PackageViewDescriptor>)committed.ActivePackages[0].Views)[0] = committed.ActivePackages[0].Views[0]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)committed.Warnings)[0] = "mutated warning");

        await owner.State.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task RuntimePackageFault_AdvancesGenerationAndPublishesFailedSessionSnapshot()
    {
        var events = new RuntimeEventStreamService();
        var owner = new RuntimeSessionOwner(NullLogger<RuntimeSessionOwner>.Instance, events);
        await owner.PublishAsync(
            CreateSession("test.package"),
            owner.Sources.Snapshot(),
            [],
            [],
            [],
            expectedGeneration: 0);

        var reported = owner.ReportPackageFault(
            "test.package",
            new ReportPackageFaultRequest(
                PackageFailureOrigin.RuntimeActivation,
                "runtime activation failed",
                GenerationId: 1));

        Assert.True(reported);
        var snapshot = owner.GetSnapshot();
        Assert.Equal(2, snapshot.SessionGeneration);
        Assert.Empty(snapshot.ActivePackages);
        var package = Assert.Single(snapshot.SessionPackages);
        Assert.False(package.IsEnabled);
        Assert.Equal(PackageReadinessState.Failed, package.Readiness);
        Assert.Contains(snapshot.Errors, error => error.Contains("runtime activation failed", StringComparison.Ordinal));
        var generationEvent = events.GetSnapshot().Events.Last(item => item.Kind == RuntimeEventKind.SessionGenerationChanged);
        Assert.Equal(2, generationEvent.SessionGeneration);
        Assert.Equal(snapshot.RuntimeInstanceId, generationEvent.RuntimeInstanceId);
        await owner.State.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task AppPackageFault_DoesNotChangeSharedRuntimeSession()
    {
        var events = new RuntimeEventStreamService();
        var owner = new RuntimeSessionOwner(NullLogger<RuntimeSessionOwner>.Instance, events);
        await owner.PublishAsync(
            CreateSession("test.package"),
            owner.Sources.Snapshot(),
            [],
            [],
            [],
            expectedGeneration: 0);

        var reported = owner.ReportPackageFault(
            "test.package",
            new ReportPackageFaultRequest(
                PackageFailureOrigin.AppHostedView,
                "one client view failed",
                GenerationId: 1));

        Assert.False(reported);
        var snapshot = owner.GetSnapshot();
        Assert.Equal(1, snapshot.SessionGeneration);
        Assert.Single(snapshot.ActivePackages);
        Assert.True(Assert.Single(snapshot.SessionPackages).IsEnabled);
        await owner.State.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task CleanupWarning_ChangesSnapshotOnlyWithANewEventSequence()
    {
        var events = new RuntimeEventStreamService();
        var owner = new RuntimeSessionOwner(NullLogger<RuntimeSessionOwner>.Instance, events);
        await owner.PublishAsync(
            CreateCleanupFaultSession(),
            owner.Sources.Snapshot(),
            [],
            [],
            [],
            expectedGeneration: 0);

        await owner.PublishAsync(
            CreateSession("replacement.package"),
            owner.Sources.Snapshot(),
            [],
            [],
            [],
            expectedGeneration: 1);

        var snapshot = owner.GetSnapshot();
        var generationEvent = events.GetSnapshot().Events.Last(item => item.Kind == RuntimeEventKind.SessionGenerationChanged);
        var diagnosticsEvent = events.GetSnapshot().Events.Last(item => item.Kind == RuntimeEventKind.SnapshotDiagnosticsChanged);
        Assert.Contains(snapshot.Warnings, warning => warning.Contains("cleanup failed", StringComparison.OrdinalIgnoreCase));
        Assert.True(diagnosticsEvent.SequenceId > generationEvent.SequenceId);
        Assert.Equal(diagnosticsEvent.SequenceId, snapshot.EventSequence);
        await owner.State.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task GenerationCommit_EagerlyInvalidatesPendingStageStatusAndGeneration()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            timeProvider: clock);
        owner.RegisterStage(
            "stale-stage",
            generation: 1,
            baseGeneration: 0,
            RuntimePackageStageKind.PackageSession,
            clock.GetUtcNow(),
            clock.GetUtcNow() + TimeSpan.FromMinutes(5));

        await owner.PublishAsync(
            CreateSession("replacement.package"),
            owner.Sources.Snapshot(),
            [],
            [],
            [],
            expectedGeneration: 0);

        var status = owner.GetStageStatus("stale-stage");
        Assert.Equal(RuntimePackageStageState.Failed, status?.State);
        Assert.Contains("stale", status?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(owner.TryGetStageGeneration("stale-stage", out _));
        await owner.State.ClearActiveSessionAsync();
    }

    [Fact]
    public void TerminalStageStatusRetention_IsCountAndTimeBounded()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var policy = new RuntimeLifecyclePolicyOptions
        {
            MaximumTerminalStageStatuses = 2,
            TerminalStageRetention = TimeSpan.FromMinutes(5),
        };
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            timeProvider: clock,
            lifecyclePolicy: policy);
        for (var index = 0; index < 3; index++)
        {
            var stageId = $"stage-{index}";
            owner.RegisterStage(
                stageId,
                generation: 1,
                baseGeneration: 0,
                RuntimePackageStageKind.PackageSession,
                clock.GetUtcNow(),
                clock.GetUtcNow() + TimeSpan.FromMinutes(1));
            owner.MarkStageFailed(stageId, "failed");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Null(owner.GetStageStatus("stage-0"));
        Assert.NotNull(owner.GetStageStatus("stage-2"));

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(owner.GetStageStatus("stage-2"));
    }

    [Fact]
    public void RuntimeEventAndLifecycleContracts_DefensivelyFreezeInputCollections()
    {
        var packageIds = new List<string> { "original.package" };
        var runtimeEvent = new RuntimeEventDescriptor(
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            RuntimeEventKind.SessionGenerationChanged,
            1,
            RuntimeOperationPhase.Idle,
            RuntimeBootstrapState.Ready,
            packageIds);
        var warnings = new List<string> { "original warning" };
        var impacted = new List<string> { "original.package" };
        var result = new PackageOperationResult(true, "ok", true, false, warnings, [])
        {
            ImpactedPackageIds = impacted,
        };
        var callbackParameters = new Dictionary<string, string> { ["state"] = "original" };
        var callback = new PackageCallbackSessionStartRequest(callbackParameters);

        packageIds[0] = "mutated.package";
        warnings[0] = "mutated warning";
        impacted[0] = "mutated.package";
        callbackParameters["state"] = "mutated";

        Assert.Equal("original.package", Assert.Single(runtimeEvent.PackageIds));
        Assert.Equal("original warning", Assert.Single(result.Warnings));
        Assert.Equal("original.package", Assert.Single(result.ImpactedPackageIds));
        Assert.Equal("original", callback.Parameters?["state"]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)runtimeEvent.PackageIds)[0] = "mutated.package");
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)callback.Parameters!)["state"] = "mutated");
    }

    private static ActivePackageSession CreateSession(string packageId)
        => new(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = new SessionPackageDescriptor(
                    packageId,
                    packageId,
                    "1.0.0",
                    PackageHostRoles.Runtime,
                    Icon: null,
                    IsEnabled: true,
                    PackageReadinessState.Ready,
                    Views: [],
                    FailureOrigin: null,
                    LastError: null,
                    LastFailureAtUtc: null,
                    FailureCount: 0),
            });

    private static ActivePackageSession CreateCleanupFaultSession()
    {
        const string packageId = "cleanup.package";
        var root = Path.Combine(Path.GetTempPath(), "sunder-cleanup-warning-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var descriptor = new ActivePackageDescriptor(
            packageId,
            packageId,
            "1.0.0",
            PackageHostRoles.Runtime,
            null,
            true,
            PackageReadinessState.Ready,
            []);
        var sessionDescriptor = new SessionPackageDescriptor(
            packageId,
            packageId,
            "1.0.0",
            PackageHostRoles.Runtime,
            null,
            true,
            PackageReadinessState.Ready,
            [],
            null,
            null,
            null,
            0);
        var assemblyPath = typeof(RuntimeLifecycleSnapshotTests).Assembly.Location;
        var loaded = new ActiveLoadedPackage(
            descriptor,
            new RuntimePackageSource(packageId, PackageSourceKind.Dev, root),
            null,
            new JsonPackageKeyValueStore(Path.Combine(root, "state.json")),
            new JsonPackageSecretsStore(Path.Combine(root, "secrets.json")),
            null,
            new Dictionary<string, IPackageCallbackHandler>(),
            [],
            new ThrowingServiceProvider(),
            new RuntimePackageLoadContext(
                packageId,
                assemblyPath,
                new RuntimeSharedAssemblyRegistry([Path.GetDirectoryName(assemblyPath)!])),
            EmptyTestPackageSettings.Instance);
        return new ActivePackageSession(
            root,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase) { [packageId] = loaded },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase) { [packageId] = sessionDescriptor });
    }

    private sealed class ThrowingServiceProvider : IServiceProvider, IDisposable
    {
        public object? GetService(Type serviceType) => null;

        public void Dispose() => throw new IOException("Injected cleanup failure.");
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;
    }
}
