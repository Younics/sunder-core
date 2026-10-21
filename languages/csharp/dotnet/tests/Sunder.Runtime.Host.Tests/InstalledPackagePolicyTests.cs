using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;
using static Sunder.Runtime.Host.Tests.ActivePolicyTestContext;

namespace Sunder.Runtime.Host.Tests;

public sealed class InstalledPackagePolicyTests
{
    [Fact]
    public async Task MetadataTransactionPersistsExactPolicyAndIsIdempotentWhileIdle()
    {
        using var fixture = new Fixture();
        var package = fixture.Package();
        await fixture.Store.WriteAsync([package]);
        await fixture.AllowAsync(package, [Platform]);
        await fixture.Metadata.ReconcileAsync(default);
        var installed = Assert.Single(await fixture.Store.ListAsync());
        var policy = Assert.IsType<InstalledPackagePolicyRecord>(installed.Policy);
        Assert.Equal("Allowed", policy.State);
        Assert.Equal(InstalledPackagePolicyRecord.Subject(package), policy.Package);
        Assert.Equal(Platform, Assert.Single(Assert.Single(policy.Snapshots).Platforms.Values).Decision.Catalog);
        var before = await File.ReadAllBytesAsync(fixture.Context.Paths.StateFilePath);
        fixture.Context.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Metadata.ReconcileAsync(default);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Context.Paths.StateFilePath));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Context.Paths.TransactionRootPath));
    }

    [Theory]
    [InlineData("package")]
    [InlineData("platform")]
    [InlineData("expiry")]
    public async Task LearnedDenialIsPersistedAsPolicyBlockedWithoutRemovingContent(string kind)
    {
        using var fixture = new Fixture();
        var package = fixture.Package();
        await fixture.Store.WriteAsync([package]);
        await fixture.AllowAsync(package, [Platform]);
        await fixture.Metadata.ReconcileAsync(default);
        var manifest = await File.ReadAllBytesAsync(package.ManifestPath);
        if (kind == "expiry") fixture.Context.Clock.Advance(TimeSpan.FromHours(24));
        else
        {
            fixture.Context.Response = request =>
            {
                var value = Allowed(request, 2);
                return kind switch
                {
                    "package" => value with { Packages = value.Packages.Select(item => item with { PackageState = "SecurityBlocked", Disposition = "Denied", ErrorCode = PackagePolicyErrorCodes.SecurityBlocked }).ToArray() },
                    _ => value with
                    {
                        Packages = value.Packages.Select(item => item with { Disposition = "Denied", ErrorCode = PackagePolicyErrorCodes.ContractAuthorityRevoked }).ToArray(),
                        PlatformCatalogs = [new(Platform, "Revoked", "Denied", PackagePolicyErrorCodes.ContractAuthorityRevoked, ObservedAt)],
                    },
                };
            };
            await fixture.AllowAsync(package, [Platform]);
        }
        await fixture.Metadata.ReconcileAsync(default);
        var blocked = Assert.Single(await fixture.Store.ListAsync());
        Assert.Equal("PolicyBlocked", blocked.Policy!.State);
        Assert.True(blocked.IsEnabled);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(package.ManifestPath));
        var descriptor = fixture.Store.ToDescriptor(blocked);
        Assert.Equal("PolicyBlocked", descriptor.StatusMessage);
        Assert.Equal(blocked.Policy.ErrorCode, descriptor.PolicyErrorCode);
    }

    [Theory]
    [InlineData(PackageStoreFaultPoint.CutoverPrepared)]
    [InlineData(PackageStoreFaultPoint.JournalPrepared)]
    [InlineData(PackageStoreFaultPoint.DurableCommitStarted)]
    [InlineData(PackageStoreFaultPoint.PayloadsCommitting)]
    [InlineData(PackageStoreFaultPoint.PayloadsCommitted)]
    [InlineData(PackageStoreFaultPoint.CatalogReplacing)]
    [InlineData(PackageStoreFaultPoint.CatalogReplaced)]
    [InlineData(PackageStoreFaultPoint.PolicyReconciling)]
    [InlineData(PackageStoreFaultPoint.PolicyReconciled)]
    [InlineData(PackageStoreFaultPoint.CatalogCommitRecording)]
    [InlineData(PackageStoreFaultPoint.CatalogCommitted)]
    public async Task CrashRecoveryRetainsDenialEvenIfPolicyCacheAndPrimaryCatalogLag(object pointValue)
    {
        var point = (PackageStoreFaultPoint)pointValue;
        var fault = new CrashFault();
        using var fixture = new Fixture(fault);
        var package = fixture.Package() with
        {
            AcquiredTargets = [new("app", PackageTargetSelection.GetCurrentRuntimeIdentifier()), new("runtime", PackageTargetSelection.GetCurrentRuntimeIdentifier())],
        };
        await fixture.Store.WriteAsync([package]);
        await fixture.AllowAsync(package);
        await fixture.Metadata.ReconcileAsync(default);
        fixture.Block();
        await fixture.AllowAsync(package);
        fault.Point = point;
        await Assert.ThrowsAsync<PackageStoreSimulatedCrashException>(() => fixture.Metadata.ReconcileAsync(default));
        // The package journal itself must retain accepted policy even when its
        // catalog mutation rolls back and both separate cache copies are missing.
        Directory.Delete(Path.Combine(fixture.Context.Paths.RootPath, RuntimeV1StateDescriptor.PackagePolicyDirectory), true);
        var restarted = new PackageLifecyclePolicyCache(fixture.Context.Paths, fixture.Context.Clock);
        var store = new PackageStoreCoordinator(fixture.Context.Paths, fixture.Store, new(fixture.Context.Paths), packagePolicies: restarted);
        await store.InitializeAsync();
        await new InstalledPackagePolicyCoordinator(fixture.Store, store, new(), restarted).ReconcileAsync(default);
        var recovered = Assert.Single(await fixture.Store.ListAsync());
        Assert.Equal(package.AcquiredTargets, recovered.AcquiredTargets);
        Assert.Equal("PolicyBlocked", recovered.Policy!.State);
        Assert.Equal(PackagePolicyErrorCodes.SecurityBlocked, recovered.Policy.ErrorCode);
        Assert.True(File.Exists(package.ManifestPath));
        Assert.Equal(PackagePolicyErrorCodes.SecurityBlocked, Assert.Throws<PackagePolicyDeniedException>(() => restarted.RequireActivation(Origin, recovered.Policy.Package)).Code);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Context.Paths.TransactionRootPath));
    }

    [Theory]
    [InlineData(PackagePolicyPersistencePoint.IntentDurable)]
    [InlineData(PackagePolicyPersistencePoint.SnapshotDurable)]
    public async Task PolicyWriteCrashRecoversFromDurableIntentBeforeAnyCatalogUpdate(object pointValue)
    {
        var point = (PackagePolicyPersistencePoint)pointValue;
        using var fixture = new Fixture();
        var package = fixture.Package();
        await fixture.Store.WriteAsync([package]);
        await fixture.AllowAsync(package);
        await fixture.Metadata.ReconcileAsync(default);
        fixture.Block();
        var observation = await fixture.Context.FetchAsync([InstalledPackagePolicyRecord.Subject(package)], []);
        var failing = new PackageLifecyclePolicyCache(fixture.Context.Paths, fixture.Context.Clock, new PolicyCrash(point));
        await Assert.ThrowsAsync<IOException>(() => failing.ObserveAsync(observation));
        Assert.Equal("Allowed", Assert.Single(await fixture.Store.ListAsync()).Policy!.State);
        var restarted = new PackageLifecyclePolicyCache(fixture.Context.Paths, fixture.Context.Clock);
        var store = new PackageStoreCoordinator(fixture.Context.Paths, fixture.Store, new(fixture.Context.Paths), packagePolicies: restarted);
        await new InstalledPackagePolicyCoordinator(fixture.Store, store, new(), restarted).ReconcileAsync(default);
        Assert.Equal("PolicyBlocked", Assert.Single(await fixture.Store.ListAsync()).Policy!.State);
    }

    [Fact]
    public async Task InstalledCopyRestoresDeletedCacheWithoutRenewingLease()
    {
        using var fixture = new Fixture();
        var package = fixture.Package();
        await fixture.Store.WriteAsync([package]);
        await fixture.AllowAsync(package);
        await fixture.Metadata.ReconcileAsync(default);
        var retained = Assert.Single(await fixture.Store.ListAsync());
        var original = retained.Policy!.Snapshots.Single().Packages.Values.Single().Lease;
        Directory.Delete(Path.Combine(fixture.Context.Paths.RootPath, RuntimeV1StateDescriptor.PackagePolicyDirectory), true);
        var cache = new PackageLifecyclePolicyCache(fixture.Context.Paths, fixture.Context.Clock);
        await cache.RestoreRetainedAsync(retained.Policy.Snapshots, default);
        cache.RequireActivation(Origin, retained.Policy.Package);
        var roundtrip = Assert.Single(await cache.CaptureInstalledAsync([retained], default)).Policy!;
        Assert.Equal(original, roundtrip.Snapshots.Single().Packages.Values.Single().Lease);
        fixture.Context.Clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(PackagePolicyErrorCodes.PolicyExpired, Assert.Throws<PackagePolicyDeniedException>(() => cache.RequireActivation(Origin, retained.Policy.Package)).Code);
    }

    [Fact]
    public async Task RestoringOldInstalledPolicyCannotEraseNewerClockInvalidation()
    {
        using var fixture = new Fixture();
        var package = fixture.Package();
        await fixture.AllowAsync(package);
        var old = Assert.Single(await fixture.Context.Cache.CaptureInstalledAsync([package], default));
        await fixture.Context.Cache.InvalidateClockAsync(Origin);
        var cache = new PackageLifecyclePolicyCache(fixture.Context.Paths, fixture.Context.Clock);
        await cache.RestoreRetainedAsync(old.Policy!.Snapshots, default);
        Assert.Equal(PackagePolicyErrorCodes.PolicyClockInvalid, Assert.Throws<PackagePolicyDeniedException>(() => cache.RequireActivation(Origin, old.Policy.Package)).Code);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("clock")]
    [InlineData("expiry")]
    public async Task CapturedTransactionAuthorityRejectsChangeEvenAfterARefresh(string change)
    {
        using var fixture = new Fixture();
        var package = fixture.Package();
        await fixture.AllowAsync(package);
        var expected = Assert.Single(await fixture.Context.Cache.CaptureInstalledAsync([package], default)).Policy!;
        Assert.True(await fixture.Context.Cache.ExecuteUnderAuthorityFenceAsync([expected], _ => Task.FromResult(true), default));
        if (change == "revision") fixture.Context.Response = request => Allowed(request, 2);
        if (change == "clock") await fixture.Context.Cache.InvalidateClockAsync(Origin);
        if (change == "expiry") fixture.Context.Clock.Advance(TimeSpan.FromHours(24));
        await fixture.AllowAsync(package);
        fixture.Context.Cache.RequireActivation(Origin, expected.Package);
        var invoked = false;
        await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => fixture.Context.Cache.ExecuteUnderAuthorityFenceAsync([expected], _ => { invoked = true; return Task.FromResult(true); }, default));
        Assert.False(invoked);
    }

    [Fact]
    public async Task AuthorityLockSerializesConcurrentRefreshThroughProtectedOperation()
    {
        using var fixture = new Fixture();
        var package = fixture.Package();
        await fixture.AllowAsync(package);
        var expected = Assert.Single(await fixture.Context.Cache.CaptureInstalledAsync([package], default)).Policy!;
        var observation = await fixture.Context.FetchAsync([expected.Package], []);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = fixture.Context.Cache.ExecuteUnderAuthorityFenceAsync([expected], async _ => { entered.SetResult(); await release.Task; return 42; }, default);
        await entered.Task;
        var refresh = fixture.Context.Cache.ObserveAsync(observation);
        try { Assert.NotSame(refresh, await Task.WhenAny(refresh, Task.Delay(100))); }
        finally { release.SetResult(); }
        Assert.Equal(42, await operation);
        await refresh;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalArchiveUsesRetainedFullSourceIdentityAndNeverFetchesPolicy(bool cached)
    {
        using var fixture = new Fixture();
        using var authoring = TemporaryDirectoryScope.Create("sunder-policy-authoring");
        var source = authoring.GetPath("source");
        CanonicalPackageTestBuilder.WriteExplodedPackage(source, "example.local", "1.0.0", typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            runtimeIdentifiers: SunderPackageFormat.SupportedRuntimeIdentifiers);
        var original = CanonicalPackageTestBuilder.CreateInstalledRecord(source, "example.local", "1.0.0");
        original = original with { ExecutionRuntimeBindings = original.ExecutionRuntimeBindings.Where(binding => binding.Rid == PackageTargetSelection.GetCurrentRuntimeIdentifier()).ToArray() };
        var subject = new RegistryPackageVersionIdentity(original.PackageId, original.Version, original.ContentIdentity);
        if (cached) await fixture.Context.RefreshAsync([subject], []);
        fixture.Context.Send = (_, _) => throw new InvalidOperationException("Local installation must not fetch policy.");
        var archive = authoring.GetPath("package.sunderpkg");
        ZipFile.CreateFromDirectory(source, archive);
        var prepared = await fixture.Coordinator.PrepareStageAsync([new PackageStoreMutation(PackageStoreMutationKind.Install,
            ArchiveFilePath: archive, ExecutionRuntimeBindings: original.ExecutionRuntimeBindings)]);
        Assert.True(cached == prepared.Success, prepared.Failure?.Message);
        if (!cached)
        {
            Assert.Empty(await fixture.Store.ListAsync());
            Assert.Contains(PackagePolicyErrorCodes.PolicyRequired, prepared.Failure!.Message!);
            return;
        }
        Assert.True((await fixture.Coordinator.CommitStageAsync(prepared.Stage!.StageId)).Success);
        var installed = Assert.Single(await fixture.Store.ListAsync());
        Assert.Equal(subject, installed.Policy!.Package);
        Assert.Equal(original.ContentIdentity, installed.SourcePackageContentIdentity);
        Assert.NotEqual(installed.ContentIdentity, installed.SourcePackageContentIdentity);
    }

    [Fact]
    public async Task PolicyUpdateWaitsForPreparedStageButItsDenialAlreadyFencesCommit()
    {
        using var fixture = new Fixture();
        var package = fixture.Package() with { IsEnabled = false };
        await fixture.Store.WriteAsync([package]);
        await fixture.AllowAsync(package);
        await fixture.Metadata.ReconcileAsync(default);
        var prepared = await fixture.Coordinator.PrepareStageAsync([new PackageStoreMutation(PackageStoreMutationKind.Enable, package.PackageId)]);
        Assert.True(prepared.Success, prepared.Failure?.Message);
        var stageId = prepared.Stage!.StageId;
        Assert.True(await fixture.Coordinator.ExecuteUnderPackagePolicyFenceAsync(stageId, _ => Task.FromResult(true), default));
        var before = await File.ReadAllBytesAsync(fixture.Context.Paths.StateFilePath);
        fixture.Block();
        await fixture.AllowAsync(package);
        await fixture.Metadata.ReconcileAsync(default);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Context.Paths.StateFilePath));
        Assert.Equal("PolicyBlocked", fixture.Metadata.ProjectCurrent(Assert.Single(await fixture.Store.ListAsync())).Policy!.State);
        var failure = await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => fixture.Coordinator.CommitStageAsync(stageId));
        Assert.Equal(PackagePolicyErrorCodes.SecurityBlocked, failure.Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Context.Paths.StateFilePath));
        await fixture.Coordinator.DiscardStageAsync(stageId);
        await fixture.Metadata.ReconcileAsync(default);
        Assert.Equal("PolicyBlocked", Assert.Single(await fixture.Store.ListAsync()).Policy!.State);
        Assert.False(Assert.Single(await fixture.Store.ListAsync()).IsEnabled);
    }

    [Fact]
    public async Task PartialInstalledCopiesMergeAcrossDistinctObservationAndFileGenerations()
    {
        using var fixture = new Fixture();
        var retained = new List<InstalledPackageRecord>();
        foreach (var id in new[] { "example.first", "example.second", "example.third" })
        {
            var package = fixture.Package(id);
            await fixture.AllowAsync(package);
            retained.Add(Assert.Single(await fixture.Context.Cache.CaptureInstalledAsync([package], default)));
        }
        Directory.Delete(Path.Combine(fixture.Context.Paths.RootPath, RuntimeV1StateDescriptor.PackagePolicyDirectory), true);
        var cache = new PackageLifecyclePolicyCache(fixture.Context.Paths, fixture.Context.Clock);
        await cache.RestoreRetainedAsync(retained.SelectMany(package => package.Policy!.Snapshots), default);
        foreach (var package in retained) cache.RequireActivation(Origin, package.Policy!.Package);
    }

    [Fact]
    public async Task InstalledClockInvalidationAloneSurvivesCacheLossAndTickCatchup()
    {
        using var fixture = new Fixture();
        var package = fixture.Package();
        await fixture.Store.WriteAsync([package]);
        await fixture.AllowAsync(package);
        await fixture.Metadata.ReconcileAsync(default);
        fixture.Context.Clock.Advance(TimeSpan.FromHours(1));
        fixture.Context.Cache.RequireActivation(Origin, InstalledPackagePolicyRecord.Subject(package));
        fixture.Context.Clock.Advance(TimeSpan.FromMinutes(-30));
        await fixture.Metadata.ReconcileAsync(default);
        var retained = Assert.Single(await fixture.Store.ListAsync());
        Assert.Equal(PackagePolicyErrorCodes.PolicyClockInvalid, retained.Policy!.ErrorCode);
        Directory.Delete(Path.Combine(fixture.Context.Paths.RootPath, RuntimeV1StateDescriptor.PackagePolicyDirectory), true);
        fixture.Context.Clock.Advance(TimeSpan.FromHours(2));
        var cache = new PackageLifecyclePolicyCache(fixture.Context.Paths, fixture.Context.Clock);
        await cache.RestoreRetainedAsync(retained.Policy.Snapshots, default);
        Assert.Equal(PackagePolicyErrorCodes.PolicyClockInvalid, Assert.Throws<PackagePolicyDeniedException>(() => cache.RequireActivation(Origin, retained.Policy.Package)).Code);
    }

    [Fact]
    public async Task IndeterminateCatalogWriteRequestsShutdownAndPreservesRecoveryJournal()
    {
        using var fixture = new Fixture();
        var package = fixture.Package();
        await fixture.Store.WriteAsync([package]);
        await fixture.AllowAsync(package);
        var fault = new IndeterminateFault(fixture.Context.Paths.StateFilePath);
        var store = new PackageStoreCoordinator(fixture.Context.Paths, fixture.Store, new(fixture.Context.Paths), fault, packagePolicies: fixture.Context.Cache);
        var lifetime = new PolicyLifetime();
        var metadata = new InstalledPackagePolicyCoordinator(fixture.Store, store, new(), fixture.Context.Cache, lifetime);
        await Assert.ThrowsAsync<InvalidDataException>(() => metadata.ReconcileAsync(default));
        Assert.True(lifetime.StopRequested);
        Assert.Single(Directory.EnumerateDirectories(fixture.Context.Paths.TransactionRootPath));
        await Assert.ThrowsAsync<InvalidDataException>(() => metadata.ReconcileAsync(default));
        var recovery = new PackageStoreCoordinator(fixture.Context.Paths, fixture.Store, new(fixture.Context.Paths), packagePolicies: fixture.Context.Cache);
        await recovery.InitializeAsync();
        Assert.Equal("Allowed", Assert.Single(await fixture.Store.ListAsync()).Policy!.State);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Context.Paths.TransactionRootPath));
    }

    [Fact]
    public async Task BackgroundPersistenceQuiescenceJoinsAcceptedWriteBeforeGateRetirementAndRetainsDenial()
    {
        using var fault = new BlockingPolicyWrite();
        using var fixture = new Fixture(fault);
        var package = fixture.Package();
        await fixture.Store.WriteAsync([package]);
        fixture.Block();
        await fixture.AllowAsync(package);
        var retained = Assert.Single(await fixture.Context.Cache.CaptureInstalledAsync([package], default)).Policy!;
        // Keep network refresh independent and in flight; draining the background
        // writer must neither wait for it nor disable expiry enforcement.
        var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Context.Send = async (_, token) =>
        {
            refreshEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable after refresh cancellation.");
        };
        var logs = new RecordingLogSink();
        var owner = new RuntimeSessionOwner(NullLogger<RuntimeSessionOwner>.Instance, new());
        using var refresh = new RegistryPackagePolicySynchronizer(fixture.Context.Client, fixture.Context.Cache);
        using var service = new ActivePackagePolicySynchronizer(fixture.Store, owner.State, owner, refresh,
            new(fixture.Context.Cache), fixture.Context.Cache, fixture.Context.Clock, fixture.Context.Time,
            logs.CreateLogger<ActivePackagePolicySynchronizer>(), installedPolicies: fixture.Metadata);
        try
        {
            await service.StartAsync(default);
            await fault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var draining = service.StopPersistenceAsync(default);
            Assert.False(draining.IsCompleted); // An accepted, cancellation-ignoring write still owns the drain.
            using (var deadline = new CancellationTokenSource())
            {
                deadline.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StopPersistenceAsync(deadline.Token));
            }
            Assert.False(draining.IsCompleted); // A cancelled wait is not a write/cleanup acknowledgement.
            fault.Release.Set();
            await draining.WaitAsync(TimeSpan.FromSeconds(5));
            await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Operations.ShutdownAsync(() => Task.CompletedTask);
            // Periodic refresh/enforcement remain live, but the quiesced writer
            // cannot attempt a new operation against the retired gate.
            fixture.Context.Time.Advance(TimeSpan.FromSeconds(2));
            Assert.False(service.ExecuteTask!.IsCompleted);
            await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);

            var restarted = new PackageLifecyclePolicyCache(fixture.Context.Paths, fixture.Context.Clock);
            var coordinator = new PackageStoreCoordinator(fixture.Context.Paths, fixture.Store, new(fixture.Context.Paths), packagePolicies: restarted);
            await coordinator.InitializeAsync();
            await new InstalledPackagePolicyCoordinator(fixture.Store, coordinator, new(), restarted).ReconcileAsync(default);
            var durable = Assert.Single(await fixture.Store.ListAsync());
            Assert.Equal("PolicyBlocked", durable.Policy!.State);
            Assert.Equal(PackagePolicyErrorCodes.SecurityBlocked, durable.Policy.ErrorCode);
            Assert.Equal(retained.Snapshots.Single().Packages.Values.Single().Lease,
                durable.Policy.Snapshots.Single().Packages.Values.Single().Lease);
            Assert.Equal(PackagePolicyErrorCodes.SecurityBlocked,
                Assert.Throws<PackagePolicyDeniedException>(() => restarted.RequireActivation(Origin, retained.Package)).Code);
        }
        finally
        {
            fault.Release.Set();
            await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task BackgroundPersistenceQuiescedBeforeStartNeverEntersRetiredGate()
    {
        using var fixture = new Fixture();
        var logs = new RecordingLogSink();
        var owner = new RuntimeSessionOwner(NullLogger<RuntimeSessionOwner>.Instance, new());
        using var refresh = new RegistryPackagePolicySynchronizer(fixture.Context.Client, fixture.Context.Cache);
        using var service = new ActivePackagePolicySynchronizer(fixture.Store, owner.State, owner, refresh,
            new(fixture.Context.Cache), fixture.Context.Cache, fixture.Context.Clock, fixture.Context.Time,
            logs.CreateLogger<ActivePackagePolicySynchronizer>(), installedPolicies: fixture.Metadata);
        await service.StopPersistenceAsync(default);
        await fixture.Operations.ShutdownAsync(() => Task.CompletedTask);
        try
        {
            await service.StartAsync(default);
            await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task IntegrityFenceWithoutNormalQuiescenceRemainsAnError()
    {
        using var fixture = new Fixture();
        fixture.Operations.FenceForIntegrityFailure();
        var logs = new RecordingLogSink();
        var owner = new RuntimeSessionOwner(NullLogger<RuntimeSessionOwner>.Instance, new());
        using var refresh = new RegistryPackagePolicySynchronizer(fixture.Context.Client, fixture.Context.Cache);
        using var service = new ActivePackagePolicySynchronizer(fixture.Store, owner.State, owner, refresh,
            new(fixture.Context.Cache), fixture.Context.Cache, fixture.Context.Clock, fixture.Context.Time,
            logs.CreateLogger<ActivePackagePolicySynchronizer>(), installedPolicies: fixture.Metadata);
        try
        {
            await service.StartAsync(default);
            await WaitForPersistenceFailureAsync();
            var error = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Error);
            Assert.Equal(nameof(RuntimeOperationRejectedException), error.Properties["FailureType"]);
            await service.StopPersistenceAsync(default);
        }
        finally { await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }

        async Task WaitForPersistenceFailureAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!logs.Entries.Any(entry => entry.Level == LogLevel.Error)) await Task.Delay(10, deadline.Token);
        }
    }

    [Fact]
    public async Task MissingPolicyFieldCannotBeSilentlyReadAsAnOlderSchema()
    {
        using var fixture = new Fixture();
        await fixture.Store.WriteAsync([fixture.Package()]);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Context.Paths.StateFilePath))!;
        node["packages"]![0]!.AsObject().Remove("policy");
        await File.WriteAllTextAsync(fixture.Context.Paths.StateFilePath, node.ToJsonString());
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.ListAsync());
        Assert.Contains("sunder runtime reset --yes", failure.Message, StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        internal ActivePolicyTestContext Context { get; } = new();
        internal InstalledPackageStore Store { get; }
        internal PackageStoreCoordinator Coordinator { get; }
        internal InstalledPackagePolicyCoordinator Metadata { get; }
        internal RuntimeOperationGate Operations { get; } = new();
        internal Fixture(IPackageStoreFaultInjector? fault = null)
        {
            RuntimeLocalState.EnsureInitialized(Context.Paths.RootPath);
            Store = new(Context.Paths);
            Coordinator = new(Context.Paths, Store, new(Context.Paths), fault, packagePolicies: Context.Cache);
            Metadata = new(Store, Coordinator, Operations, Context.Cache);
        }
        internal InstalledPackageRecord Package(string id = "example.package")
        {
            var path = Context.Paths.GetInstalledPackagePath(id, "1.0.0");
            CanonicalPackageTestBuilder.WriteExplodedPackage(path, id, "1.0.0", typeof(PackageSessionOverlayTestPackageModule).Assembly.Location);
            var value = CanonicalPackageTestBuilder.CreateInstalledRecord(path, id, "1.0.0");
            return value with { Provenance = new(InstalledPackageSourceKind.Registry, InstalledPackageVersionPolicy.ExplicitVersion, Origin + "/", id,
                RequestedVersion: "1.0.0", SourceIdentity: value.ContentIdentity) };
        }
        internal Task AllowAsync(InstalledPackageRecord package, IReadOnlyList<RegistryPlatformCatalogIdentity>? platforms = null)
            => Context.RefreshAsync([InstalledPackagePolicyRecord.Subject(package)], platforms ?? []);
        internal void Block() => Context.Response = request => Allowed(request, 2) with
        { Packages = request.Packages.Select(subject => new RegistryPackagePolicyDecision(subject, "SecurityBlocked", "Listed", "Denied", PackagePolicyErrorCodes.SecurityBlocked, "Blocked.", [])).ToArray() };
        public void Dispose() => Context.Dispose();
    }

    private sealed class CrashFault : IPackageStoreFaultInjector
    {
        internal PackageStoreFaultPoint? Point { get; set; }
        public void Hit(PackageStoreFaultPoint point) { if (Point == point) throw new PackageStoreSimulatedCrashException(point); }
    }
    private sealed class PolicyCrash(PackagePolicyPersistencePoint at) : IPackagePolicyFaultInjector
    { public void Hit(PackagePolicyPersistencePoint point) { if (point == at) throw new IOException("Simulated policy write crash."); } }
    private sealed class BlockingPolicyWrite : IPackageStoreFaultInjector, IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        public void Hit(PackageStoreFaultPoint point)
        {
            if (point != PackageStoreFaultPoint.CatalogReplaced) return;
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Policy-write test barrier did not release.");
        }
        public void Dispose() => Release.Dispose();
    }
    private sealed class IndeterminateFault(string path) : IPackageStoreFaultInjector
    { public void Hit(PackageStoreFaultPoint point) { if (point == PackageStoreFaultPoint.CatalogReplaced) throw new DurableWriteIndeterminateException(path, new IOException("Flush could not be proven.")); } }
    private sealed class PolicyLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        internal bool StopRequested { get; private set; }
        public void StopApplication() => StopRequested = true;
    }
}
