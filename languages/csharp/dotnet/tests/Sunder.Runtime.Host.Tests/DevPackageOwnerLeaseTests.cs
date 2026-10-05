using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed class DevPackageOwnerLeaseTests
{
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

            await fixture.Host.ReleaseDevPackageOwnerAsync(
                "owner-b",
                new DevPackageOwnerReleaseRequest(fixture.Host.RuntimeInstanceId, "owner-token-hhhhhhhhhhhhhhhhhhhh"));
            Assert.False((await fixture.Host.GetPackageSessionStatusAsync("test.package"))?.WatchEnabled);
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
        ILogger<RuntimePackageSessionTestHost>? logger = null)
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
            lifecyclePolicy: policy,
            timeProvider: timeProvider);
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
