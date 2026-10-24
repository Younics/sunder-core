using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed partial class DevPackageOwnerLeaseTests
{
    [Fact]
    public async Task ExplicitSameByteReloadRetiresTheExactWorkerInsteadOfPreservingItsActivationState()
    {
        var fixture = CreateFixture();
        var maker = CreatePackageLayout(fixture.Paths.RootPath, "maker", "startup.counted.package", "1.0.0").InstallPath;
        const string token = "owner-token-reload-reload-reload-reload";
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync("maker", Mutation(fixture.Host.RuntimeInstanceId, token, "load", 1, maker, false));
            var original = fixture.Host.ActiveSession.LoadedPackageMap["startup.counted.package"];
            var stage = await fixture.Host.StagePackageLifecycleAsync(new(["startup.counted.package"]));
            Assert.NotNull(stage.StageId);
            var committed = await fixture.Host.CommitPackageLifecycleStageAsync(stage.StageId);
            Assert.True(committed.Success, committed.Message);
            var current = fixture.Host.ActiveSession.LoadedPackageMap["startup.counted.package"];
            Assert.NotEqual(original.RuntimeActivationId, current.RuntimeActivationId);
            Assert.NotSame(original.Worker, current.Worker);
            Assert.True(original.Worker.GenerationCompletion.IsCompleted);
            Assert.Throws<RuntimeUnavailableException>(() => fixture.Host.SessionOwner.State.AcquirePackageActivationLease("startup.counted.package",
                new(original.RuntimeActivationId, original.ActivationSessionGeneration!.Value)));
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task UnrelatedOwnerAddRemoveRetainsLiveWorkerAndBirthAuthorityUntilItsActualOwnerReleases()
    {
        var fixture = CreateFixture();
        var maker = CreateRolePackage(fixture.Paths.RootPath, "startup.counted.package", workerMode: "v1-publication-discovery");
        var target = CreatePackageLayout(fixture.Paths.RootPath, "target", "target.package", "1.0.0").InstallPath;
        const string makerToken = "owner-token-maker-maker-maker-maker";
        const string targetToken = "owner-token-target-target-target-target";
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync("maker", Mutation(fixture.Host.RuntimeInstanceId, makerToken, "load", 1, maker, false));
            var original = fixture.Host.ActiveSession.LoadedPackageMap["startup.counted.package"];
            Assert.IsType<ProcessRuntimeWorker>(original.Worker);
            var birth = new RuntimePackageGeneration(original.RuntimeActivationId, original.ActivationSessionGeneration!.Value);
            var trigger = Assert.Single(fixture.Host.RpcCatalog.GetSnapshot().Providers);
            var snapshotFolder = original.Source.EffectiveSnapshotFolder;
            var counters = Path.Combine(original.Worker.WorkspaceRootPath!, "activation-counters.txt");
            var before = File.ReadAllText(counters);
            await AssertWorkerDiscoveryAsync("initial");
            await fixture.Host.ReplaceDevPackageOwnerAsync("target", Mutation(fixture.Host.RuntimeInstanceId, targetToken, "load", 1, target, true));
            Assert.Same(original.Worker, fixture.Host.ActiveSession.LoadedPackageMap["startup.counted.package"].Worker);
            Assert.False(original.Worker.GenerationCompletion.IsCompleted);
            Assert.True(Directory.Exists(snapshotFolder));
            using (var lease = fixture.Host.SessionOwner.State.AcquirePackageActivationLease("startup.counted.package", birth))
                Assert.True(lease.Generation > birth.SessionGeneration);
            await ReleaseWithDiscoveryProbeAsync("target", targetToken, "continuing");
            Assert.Same(original.Worker, fixture.Host.ActiveSession.LoadedPackageMap["startup.counted.package"].Worker);
            Assert.Equal(before, File.ReadAllText(counters));
            await AssertWorkerDiscoveryAsync("continued");
            await ReleaseWithDiscoveryProbeAsync("maker", makerToken, "retiring", "stale-endpoint", "rpc.caller.generation-stale");
            Assert.True(original.Worker.GenerationCompletion.IsCompleted);
            Assert.False(Directory.Exists(snapshotFolder));
            Assert.Throws<RuntimeUnavailableException>(() => fixture.Host.SessionOwner.State.AcquirePackageActivationLease("startup.counted.package", birth));

            async Task ReleaseWithDiscoveryProbeAsync(string owner, string ownerToken, string probe, string? kind = null, string? code = null)
            {
                // Hold an actual lease so the previous generation remains in its
                // drain boundary before the candidate catalog is installed.
                using var lease = fixture.Host.AcquireSessionLease();
                var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = lease.RetirementToken.Register(() => draining.TrySetResult());
                var release = fixture.Host.ReleaseDevPackageOwnerAsync(owner, new(fixture.Host.RuntimeInstanceId, ownerToken));
                try
                {
                    await draining.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.False(release.IsCompleted);
                    await AssertWorkerDiscoveryAsync(probe, kind, code);
                    Assert.False(original.Worker.GenerationCompletion.IsCompleted);
                }
                finally
                {
                    lease.Dispose();
                    await release.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }

            async Task AssertWorkerDiscoveryAsync(string probe, string? kind = null, string? code = null)
            {
                var provider = trigger;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                // The direct host-side invocation only triggers autonomous Worker
                // discovery while ordinary session lease acquisition is fenced.
                await ((ProcessRuntimeWorker)original.Worker).InvokeUnaryAsync(provider.ProviderId,
                    new(provider.PackageId, provider.PackageVersion, provider, DateTimeOffset.UtcNow.AddSeconds(5), 1, deadline.Token),
                    "messages", "send", JsonSerializer.SerializeToElement(new { message = probe }), deadline.Token);
                var path = Path.Combine(((ProcessRuntimeWorker)original.Worker).WorkerTemporaryPath, "publication-discovery-" + probe + ".json");
                using var response = JsonDocument.Parse(await File.ReadAllBytesAsync(path, deadline.Token));
                var dataPath = Path.Combine(((ProcessRuntimeWorker)original.Worker).WorkerTemporaryPath, "publication-data-" + probe + ".json");
                using var data = JsonDocument.Parse(await File.ReadAllBytesAsync(dataPath, deadline.Token));
                if (kind is null)
                {
                    Assert.Equal("host.result", response.RootElement.GetProperty("type").GetString());
                    Assert.Single(response.RootElement.GetProperty("value").GetProperty("providers").EnumerateArray());
                    Assert.Equal("host.result", data.RootElement.GetProperty("type").GetString());
                }
                else
                {
                    Assert.Equal("host.error", response.RootElement.GetProperty("type").GetString());
                    var error = response.RootElement.GetProperty("error");
                    Assert.Equal(kind, error.GetProperty("kind").GetString());
                    Assert.Equal(code, error.GetProperty("code").GetString());
                    Assert.Equal("not-dispatched", error.GetProperty("dispatchState").GetString());
                    Assert.Equal("safe", error.GetProperty("retrySafety").GetString());
                    Assert.Equal("host.error", data.RootElement.GetProperty("type").GetString());
                    Assert.Equal("worker.activation.unavailable", data.RootElement.GetProperty("error").GetProperty("code").GetString());
                }
            }
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task UnrelatedDevGraphChangesPreserveInstalledAppFingerprintButSourceOverrideInvalidatesIt()
    {
        var fixture = CreateFixture();
        var installed = CreatePackageLayout(fixture.Paths.RootPath, "installed", "builder.package", "1.0.0");
        var project = CreatePackageLayout(fixture.Paths.RootPath, "project", "project.package", "1.0.0").InstallPath;
        var replacement = CreatePackageLayout(fixture.Paths.RootPath, "replacement", "builder.package", "1.0.0").InstallPath;
        const string token = "owner-token-retention-retention-retention";
        try
        {
            await AddInstalledPackageAsync(fixture.Store, installed);
            Assert.True((await fixture.Host.LoadInstalledPackagesAsync()).Success);
            var before = Assert.Single(fixture.Host.GetActivePackageUiSnapshots());
            Assert.NotNull(before.ActivationFingerprint);
            await fixture.Host.ReplaceDevPackageOwnerAsync("owner-a", Mutation(fixture.Host.RuntimeInstanceId, token, "add", 1, project, false));
            var added = fixture.Host.GetActivePackageUiSnapshots().Single(source => source.PackageId == "builder.package");
            Assert.Equal(before.ActivationFingerprint, added.ActivationFingerprint);
            Assert.Equal(before.ProjectionReference!.CacheKey, added.ProjectionReference!.CacheKey);
            await fixture.Host.ReleaseDevPackageOwnerAsync("owner-a", new(fixture.Host.RuntimeInstanceId, token));
            Assert.Equal(before.ActivationFingerprint, Assert.Single(fixture.Host.GetActivePackageUiSnapshots()).ActivationFingerprint);
            await fixture.Host.ReplaceDevPackageOwnerAsync("owner-a", Mutation(fixture.Host.RuntimeInstanceId, token, "override", 1, replacement, false));
            Assert.NotEqual(before.ActivationFingerprint, Assert.Single(fixture.Host.GetActivePackageUiSnapshots()).ActivationFingerprint);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task WarmRuntime_LeaseKeepsInstanceAndReleaseRestoresInstalledPackage()
    {
        var fixture = CreateFixture();
        var installed = CreatePackageLayout(fixture.Paths.RootPath, "installed", "test.package", "1.0.0");
        var dev = CreatePackageLayout(fixture.Paths.RootPath, "dev", "test.package", "2.0.0").InstallPath;
        try
        {
            await AddInstalledPackageAsync(fixture.Store, installed);
            Assert.True((await fixture.Host.LoadInstalledPackagesAsync()).Success);
            var runtimeInstanceId = fixture.Host.RuntimeInstanceId;

            var lease = await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(runtimeInstanceId, "owner-token-aaaaaaaaaaaaaaaaaaaa", "mutation-1", 1, dev, watch: true));

            Assert.Equal(runtimeInstanceId, lease.RuntimeInstanceId);
            Assert.Equal("2.0.0", Assert.Single(fixture.Host.GetActivePackages()).Version);
            await fixture.Host.ReleaseDevPackageOwnerAsync(
                "owner-a",
                new DevPackageOwnerReleaseRequest(runtimeInstanceId, "owner-token-aaaaaaaaaaaaaaaaaaaa"));
            Assert.Equal("1.0.0", Assert.Single(fixture.Host.GetActivePackages()).Version);
            Assert.Equal(runtimeInstanceId, fixture.Host.RuntimeInstanceId);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task DesiredSetReplacement_RemovesDevOnlyPackageNotInNextRevision()
    {
        var fixture = CreateFixture();
        var first = CreatePackageLayout(fixture.Paths.RootPath, "dev-a", "a.package", "1.0.0").InstallPath;
        var second = CreatePackageLayout(fixture.Paths.RootPath, "dev-b", "b.package", "1.0.0").InstallPath;
        const string token = "owner-token-bbbbbbbbbbbbbbbbbbbb";
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(fixture.Host.RuntimeInstanceId, token, "mutation-1", 1, first, watch: false));
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(fixture.Host.RuntimeInstanceId, token, "mutation-2", 2, second, watch: false));

            Assert.Equal(["b.package"], fixture.Host.GetActivePackages().Select(package => package.PackageId));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task MutationRetry_IsIdempotent()
    {
        var fixture = CreateFixture();
        var dev = CreatePackageLayout(fixture.Paths.RootPath, "dev", "test.package", "1.0.0").InstallPath;
        var request = Mutation(
            fixture.Host.RuntimeInstanceId,
            "owner-token-cccccccccccccccccccc",
            "mutation-1",
            1,
            dev,
            watch: false);
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            var first = await fixture.Host.ReplaceDevPackageOwnerAsync("owner-a", request);
            var generation = fixture.Host.SessionGeneration;

            var retry = await fixture.Host.ReplaceDevPackageOwnerAsync("owner-a", request);

            Assert.Equal(first.Revision, retry.Revision);
            Assert.Equal(first.MutationId, retry.MutationId);
            Assert.Equal(generation, fixture.Host.SessionGeneration);
            Assert.True(retry.ExpiresAtUtc >= first.ExpiresAtUtc);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Replacement_EmitsStructuredTimingDiagnosticsWithoutSensitiveValues()
    {
        var logger = new CollectingLogger();
        var fixture = CreateFixture(logger: logger);
        var dev = CreatePackageLayout(fixture.Paths.RootPath, "timed-dev", "timed.package", "1.0.0").InstallPath;
        const string token = "owner-token-timing-timing-timing";
        const string mutationId = "timed-mutation-1";
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            logger.Clear();

            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "timed-owner",
                Mutation(fixture.Host.RuntimeInstanceId, token, mutationId, 1, dev, watch: true));

            string[] expectedTemplates =
            [
                "Waited {ElapsedMilliseconds} ms for the dev package owner gate for mutation {MutationId}",
                "Validated {PackageSourceKind} package {PackageId} in {ElapsedMilliseconds} ms",
                "Materialized canonical shadow for package {PackageId} in {ElapsedMilliseconds} ms",
                "Materialized development Runtime target projection for package {PackageId} in {ElapsedMilliseconds} ms",
                "Loaded {ContractCount} RPC contract(s) for package {PackageId} in {ElapsedMilliseconds} ms",
                "Prepared merged package session from {InstalledPackageCount} installed package(s) and {DevPackageCount} dev overlay(s) with {SessionPackageCount} session package(s), {WarningCount} warning(s), and {ErrorCount} error(s) in {ElapsedMilliseconds} ms",
                "Started package worker {PackageId} in {ElapsedMilliseconds} ms",
                "Activated package Runtime generation {SessionGeneration} for {PackageWorkerCount} package worker(s) in {ElapsedMilliseconds} ms",
                "Published package session snapshot for generation {SessionGeneration} in {ElapsedMilliseconds} ms",
                "Published package session generation {SessionGeneration} with {ActivePackageCount} active package(s) in {ElapsedMilliseconds} ms",
                "Synchronized {WatchTargetCount} dev package watcher target(s) in {ElapsedMilliseconds} ms after waiting {GateWaitMilliseconds} ms for the watcher transition gate",
                "Dev package owner mutation {MutationId} for {OwnerId} completed with {MutationOutcome}, {PackageCount} package(s), and generation {SessionGeneration} in {ElapsedMilliseconds} ms",
            ];
            var entries = logger.Entries;
            var timingEntries = expectedTemplates
                .Select(template => Assert.Single(entries, entry => string.Equals(entry.Template, template, StringComparison.Ordinal)))
                .ToArray();

            Assert.All(timingEntries, entry =>
            {
                Assert.True(entry.Properties.TryGetValue("ElapsedMilliseconds", out var elapsed));
                Assert.True(Assert.IsType<double>(elapsed) >= 0);
            });
            Assert.Contains(timingEntries, entry =>
                entry.Properties.GetValueOrDefault("MutationId") as string == mutationId);
            Assert.Contains(timingEntries, entry =>
                entry.Properties.GetValueOrDefault("PackageId") as string == "timed.package");
            var diagnostics = string.Join(
                Environment.NewLine,
                timingEntries.SelectMany(entry =>
                    entry.Properties.Values.Select(value => value?.ToString() ?? string.Empty).Append(entry.Message)));
            Assert.DoesNotContain(token, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(dev, diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task MutationRetry_WithChangedPayload_IsRejected()
    {
        var fixture = CreateFixture();
        var dev = CreatePackageLayout(fixture.Paths.RootPath, "dev", "test.package", "1.0.0").InstallPath;
        const string token = "owner-token-payload-payload-payload";
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(fixture.Host.RuntimeInstanceId, token, "mutation-1", 1, dev, watch: false));

            await Assert.ThrowsAsync<RuntimeConflictException>(() =>
                fixture.Host.ReplaceDevPackageOwnerAsync(
                    "owner-a",
                    Mutation(fixture.Host.RuntimeInstanceId, token, "mutation-1", 1, dev, watch: true)));

            Assert.False((await fixture.Host.GetPackageSessionStatusAsync("test.package"))?.WatchEnabled);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReleaseRetry_IsIdempotent()
    {
        var fixture = CreateFixture();
        var dev = CreatePackageLayout(fixture.Paths.RootPath, "dev", "test.package", "1.0.0").InstallPath;
        const string token = "owner-token-release-release-release";
        var release = new DevPackageOwnerReleaseRequest(fixture.Host.RuntimeInstanceId, token);
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(fixture.Host.RuntimeInstanceId, token, "mutation-1", 1, dev, watch: false));

            await fixture.Host.ReleaseDevPackageOwnerAsync("owner-a", release);
            await fixture.Host.ReleaseDevPackageOwnerAsync("owner-a", release);

            Assert.Empty(fixture.Host.GetActivePackages());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task WrongOwnerToken_IsRejectedWithoutChangingSession()
    {
        var fixture = CreateFixture();
        var dev = CreatePackageLayout(fixture.Paths.RootPath, "dev", "test.package", "1.0.0").InstallPath;
        const string token = "owner-token-dddddddddddddddddddd";
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(fixture.Host.RuntimeInstanceId, token, "mutation-1", 1, dev, watch: false));
            var generation = fixture.Host.SessionGeneration;

            await Assert.ThrowsAsync<RuntimeAuthenticationException>(() => fixture.Host.HeartbeatDevPackageOwnerAsync(
                "owner-a",
                new DevPackageOwnerHeartbeatRequest(
                    fixture.Host.RuntimeInstanceId,
                    "wrong-owner-token-xxxxxxxxxxxxxxxx")));

            Assert.Equal(generation, fixture.Host.SessionGeneration);
            Assert.True(fixture.Host.ContainsDevPackageOwner("owner-a"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task DifferentFoldersForSamePackageId_AreRejectedAtomically()
    {
        var fixture = CreateFixture();
        var first = CreatePackageLayout(fixture.Paths.RootPath, "dev-a", "test.package", "1.0.0").InstallPath;
        var second = CreatePackageLayout(fixture.Paths.RootPath, "dev-b", "test.package", "2.0.0").InstallPath;
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(fixture.Host.RuntimeInstanceId, "owner-token-eeeeeeeeeeeeeeeeeeee", "mutation-1", 1, first, watch: false));
            var generation = fixture.Host.SessionGeneration;

            await Assert.ThrowsAsync<RuntimeConflictException>(() => fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-b",
                Mutation(fixture.Host.RuntimeInstanceId, "owner-token-ffffffffffffffffffff", "mutation-1", 1, second, watch: false)));

            Assert.Equal(generation, fixture.Host.SessionGeneration);
            Assert.Equal("1.0.0", Assert.Single(fixture.Host.GetActivePackages()).Version);
            Assert.False(fixture.Host.ContainsDevPackageOwner("owner-b"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task GenerationActivationFailure_RestoresPreviousSessionBeforeRejectingOwner()
    {
        var fixture = CreateFixture(new RuntimeLifecyclePolicyOptions
        {
            PackageRuntimeGenerationActivationAttempts = 1,
        });
        var installed = CreatePackageLayout(
            fixture.Paths.RootPath,
            "installed",
            "installed.package",
            "1.0.0");
        var failingDev = CreatePackageLayout(
            fixture.Paths.RootPath,
            "dev",
            "generation.failing.package",
            "1.0.0").InstallPath;
        try
        {
            await AddInstalledPackageAsync(fixture.Store, installed);
            Assert.True((await fixture.Host.LoadInstalledPackagesAsync()).Success);

            var retainedWorker = fixture.Host.ActiveSession.LoadedPackageMap[installed.PackageId].Worker;
            await Assert.ThrowsAsync<RuntimePackageValidationException>(() =>
                fixture.Host.ReplaceDevPackageOwnerAsync(
                    "owner-a",
                    Mutation(
                        fixture.Host.RuntimeInstanceId,
                        "owner-token-restore-restore-restore",
                        "mutation-1",
                        1,
                        failingDev,
                        watch: false)));

            var active = Assert.Single(fixture.Host.GetActivePackages());
            Assert.Equal("installed.package", active.PackageId);
            Assert.Same(retainedWorker, fixture.Host.ActiveSession.LoadedPackageMap[installed.PackageId].Worker);
            Assert.False(fixture.Host.ContainsDevPackageOwner("owner-a"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancellationAfterPublication_RestoresPreviousSessionBeforePropagating()
    {
        var fixture = CreateFixture(new RuntimeLifecyclePolicyOptions
        {
            PackageRuntimeGenerationActivationAttempts = 1,
        });
        var installed = CreatePackageLayout(
            fixture.Paths.RootPath,
            "installed",
            "installed.package",
            "1.0.0");
        var controlPath = Path.Combine(fixture.Paths.RootPath, "generation-commit-control");
        Directory.CreateDirectory(controlPath);
        var commitStartedPath = Path.Combine(controlPath, "generation-commit-started");
        var triggerPath = Path.Combine(controlPath, "cancel-generation-version.txt");
        var cancellingDev = CreatePackageLayout(
            fixture.Paths.RootPath,
            "dev",
            "generation.cancelling.package",
            "1.0.0",
            triggerPath,
            commitStartedPath).InstallPath;
        using var cancellation = new CancellationTokenSource();
        File.WriteAllText(triggerPath, "1.0.0");
        try
        {
            await AddInstalledPackageAsync(fixture.Store, installed);
            Assert.True((await fixture.Host.LoadInstalledPackagesAsync()).Success);

            var replacement = fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(
                    fixture.Host.RuntimeInstanceId,
                    "owner-token-cancel-cancel-cancel",
                    "mutation-1",
                    1,
                    cancellingDev,
                    watch: false),
                cancellation.Token);
            var commitDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
            while (!File.Exists(commitStartedPath) && DateTimeOffset.UtcNow < commitDeadline)
            {
                await Task.Delay(20);
            }
            Assert.True(File.Exists(commitStartedPath), "Generation activation did not reach its cancellable commit.");
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacement);
            var active = Assert.Single(fixture.Host.GetActivePackages());
            Assert.Equal("installed.package", active.PackageId);
            Assert.False(fixture.Host.ContainsDevPackageOwner("owner-a"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancellationAfterReleasePublication_RestoresOwnedDevSessionBeforePropagating()
    {
        var fixture = CreateFixture(new RuntimeLifecyclePolicyOptions
        {
            PackageRuntimeGenerationActivationAttempts = 1,
        });
        var controlPath = Path.Combine(fixture.Paths.RootPath, "generation-commit-control");
        Directory.CreateDirectory(controlPath);
        var commitStartedPath = Path.Combine(controlPath, "generation-commit-started");
        var triggerPath = Path.Combine(controlPath, "cancel-generation-version.txt");
        var installed = CreatePackageLayout(
            fixture.Paths.RootPath,
            "installed",
            "generation.cancelling.package",
            "1.0.0",
            triggerPath,
            commitStartedPath);
        var dev = CreatePackageLayout(
            fixture.Paths.RootPath,
            "dev",
            "generation.cancelling.package",
            "2.0.0").InstallPath;
        const string token = "owner-token-release-cancel-release";
        using var cancellation = new CancellationTokenSource();
        try
        {
            await AddInstalledPackageAsync(fixture.Store, installed);
            Assert.True((await fixture.Host.LoadInstalledPackagesAsync()).Success);
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(
                    fixture.Host.RuntimeInstanceId,
                    token,
                    "mutation-1",
                    1,
                    dev,
                    watch: false));
            Assert.Equal("2.0.0", Assert.Single(fixture.Host.GetActivePackages()).Version);

            File.WriteAllText(triggerPath, "1.0.0");
            var release = fixture.Host.ReleaseDevPackageOwnerAsync(
                "owner-a",
                new DevPackageOwnerReleaseRequest(fixture.Host.RuntimeInstanceId, token),
                cancellation.Token);
            var commitDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
            while (!File.Exists(commitStartedPath) && DateTimeOffset.UtcNow < commitDeadline)
            {
                await Task.Delay(20);
            }
            Assert.True(File.Exists(commitStartedPath), "Owner release did not reach its cancellable commit.");
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => release);
            Assert.True(fixture.Host.ContainsDevPackageOwner("owner-a"));
            Assert.Equal("2.0.0", Assert.Single(fixture.Host.GetActivePackages()).Version);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task SameFolderCoownership_AggregatesWatchAndReleasesIndependently()
    {
        var fixture = CreateFixture();
        var dev = CreatePackageLayout(fixture.Paths.RootPath, "dev", "test.package", "1.0.0").InstallPath;
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(fixture.Host.RuntimeInstanceId, "owner-token-gggggggggggggggggggg", "mutation-1", 1, dev, watch: false));
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-b",
                Mutation(fixture.Host.RuntimeInstanceId, "owner-token-hhhhhhhhhhhhhhhhhhhh", "mutation-1", 1, dev, watch: true));
            Assert.True((await fixture.Host.GetPackageSessionStatusAsync("test.package"))?.WatchEnabled);

            var observed = await fixture.Host.HeartbeatDevPackageOwnerAsync("owner-a",
                new(fixture.Host.RuntimeInstanceId, "owner-token-gggggggggggggggggggg"));
            Assert.False(Assert.Single(observed.Packages).Watch);
            Assert.True(observed.Packages[0].EffectiveWatch);

            await fixture.Host.ReleaseDevPackageOwnerAsync(
                "owner-b",
                new DevPackageOwnerReleaseRequest(fixture.Host.RuntimeInstanceId, "owner-token-hhhhhhhhhhhhhhhhhhhh"));
            Assert.False((await fixture.Host.GetPackageSessionStatusAsync("test.package"))?.WatchEnabled);
            var unobserved = await fixture.Host.HeartbeatDevPackageOwnerAsync("owner-a",
                new(fixture.Host.RuntimeInstanceId, "owner-token-gggggggggggggggggggg"));
            Assert.False(Assert.Single(unobserved.Packages).EffectiveWatch);
            Assert.Single(fixture.Host.GetActivePackages());

            await fixture.Host.ReleaseDevPackageOwnerAsync(
                "owner-a",
                new DevPackageOwnerReleaseRequest(fixture.Host.RuntimeInstanceId, "owner-token-gggggggggggggggggggg"));
            Assert.Empty(fixture.Host.GetActivePackages());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExpiredLease_IsReapedAndRemovesDevOnlyPackage()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var fixture = CreateFixture(
            new RuntimeLifecyclePolicyOptions { DevPackageOwnerLeaseLifetime = TimeSpan.FromSeconds(10) },
            clock);
        var dev = CreatePackageLayout(fixture.Paths.RootPath, "dev", "test.package", "1.0.0").InstallPath;
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(fixture.Host.RuntimeInstanceId, "owner-token-iiiiiiiiiiiiiiiiiiii", "mutation-1", 1, dev, watch: false));

            clock.Advance(TimeSpan.FromSeconds(11));
            await fixture.Host.ReapDevPackageOwnersAsync();

            Assert.Empty(fixture.Host.GetActivePackages());
            Assert.False(fixture.Host.ContainsDevPackageOwner("owner-a"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static DevPackageOwnerMutationRequest Mutation(
        Guid runtimeInstanceId,
        string token,
        string mutationId,
        long revision,
        string folder,
        bool watch)
        => new(
            runtimeInstanceId,
            token,
            mutationId,
            revision,
            [new DevPackageOwnerFolder(folder, watch)]);

    private static TestFixture CreateFixture(
        RuntimeLifecyclePolicyOptions? policy = null,
        TimeProvider? timeProvider = null,
        ILogger<RuntimePackageSessionTestHost>? logger = null,
        RuntimeEventStreamService? events = null,
        RuntimeOperationGate? operationGate = null,
        RuntimePackageRecoveryQueue? recoveryQueue = null,
        IExecutionRuntimeLaunchResolver? executionRuntimeLaunchResolver = null,
        PackagePolicyAdmission? packagePolicy = null,
        Func<PackageSessionState, RuntimeProtocolDescriptor, PackageUiSnapshotStore, RuntimeRpcReadinessEvaluator, AppProcessPolicySessions>? appPolicyFactory = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-dev-owner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new RuntimePackagePaths(root);
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var host = new RuntimePackageSessionTestHost(
            logger ?? NullLogger<RuntimePackageSessionTestHost>.Instance,
            store,
            installer,
            operationGate: operationGate,
            eventStream: events,
            lifecyclePolicy: policy,
            timeProvider: timeProvider,
            executionRuntimeLaunchResolver: executionRuntimeLaunchResolver,
            recoveryQueue: recoveryQueue,
            packagePolicy: packagePolicy,
            appPolicyFactory: appPolicyFactory);
        host.MarkBootstrapReady();
        return new TestFixture(paths, store, host);
    }

    private static InstalledPackageRecord CreatePackageLayout(
        string rootPath,
        string folderName,
        string packageId,
        string version,
        string? generationCommitTriggerPath = null,
        string? generationCommitStartedPath = null)
    {
        var packageFolder = Path.Combine(rootPath, folderName, packageId, version);
        var assemblyPath = typeof(PackageSessionOverlayTestPackageModule).Assembly.Location;
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            packageFolder,
            packageId,
            version,
            assemblyPath);
        if (generationCommitTriggerPath is not null && generationCommitStartedPath is not null)
        {
            File.WriteAllText(
                Path.Combine(packageFolder, "payload", "shared", "generation-commit-control.json"),
                JsonSerializer.Serialize(new
                {
                    triggerPath = generationCommitTriggerPath,
                    startedPath = generationCommitStartedPath,
                }));
            CanonicalPackageTestBuilder.WriteContentIndex(packageFolder);
        }
        return CanonicalPackageTestBuilder.CreateInstalledRecord(packageFolder, packageId, version);
    }

    private static async Task AddInstalledPackageAsync(InstalledPackageStore store, InstalledPackageRecord package)
        => await store.WriteAsync((await store.ListAsync()).Append(package).ToArray());

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class CollectingLogger : ILogger<RuntimePackageSessionTestHost>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var value in values)
                {
                    properties[value.Key] = value.Value;
                }
            }
            _entries.Enqueue(new LogEntry(
                properties.GetValueOrDefault("{OriginalFormat}") as string ?? string.Empty,
                formatter(state, exception),
                properties));
        }

        public void Clear()
        {
            while (_entries.TryDequeue(out _))
            {
            }
        }
    }

    private sealed record LogEntry(
        string Template,
        string Message,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed record TestFixture(
        RuntimePackagePaths Paths,
        InstalledPackageStore Store,
        RuntimePackageSessionTestHost Host) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Host.ShutdownAsync();
            try
            {
                Directory.Delete(Paths.RootPath, recursive: true);
            }
            catch
            {
            }
        }
    }
}
