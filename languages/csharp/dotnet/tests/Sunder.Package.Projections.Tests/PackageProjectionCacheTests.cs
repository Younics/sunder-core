using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Projections;
using Xunit;

namespace Sunder.Package.Projections.Tests;

public sealed class PackageProjectionCacheTests
{
    [Fact]
    public void KeyIncludesRoleTargetsRulesAndProjectedInventory()
    {
        using var fixture = ProjectionFixture.Create();
        var baseline = fixture.CreateRequest(PackageProjectionRole.App);
        var baselineKey = PackageProjectionCache.ComputeCacheKey(baseline);

        Assert.NotEqual(baselineKey, PackageProjectionCache.ComputeCacheKey(
            fixture.CreateRequest(PackageProjectionRole.Runtime)));
        Assert.NotEqual(baselineKey, PackageProjectionCache.ComputeCacheKey(
            baseline with { TargetKeys = ["app/alternate"] }));
        Assert.NotEqual(baselineKey, PackageProjectionCache.ComputeCacheKey(
            baseline with { ProjectionRulesIdentity = "alternate-rules" }));
        Assert.NotEqual(baselineKey, PackageProjectionCache.ComputeCacheKey(
            baseline with
            {
                Files = baseline.Files.Select(file => file with
                {
                    ProjectionRelativePath = "alternate/" + file.ProjectionRelativePath,
                }).ToArray(),
            }));
    }

    [Fact]
    public async Task ConcurrentCacheInstancesPublishOneImmutableObject()
    {
        using var fixture = ProjectionFixture.Create();
        var firstCache = fixture.CreateCache();
        var secondCache = fixture.CreateCache();
        var request = fixture.CreateRequest(PackageProjectionRole.App);

        var acquisitions = await Task.WhenAll(
            firstCache.GetOrBuildAndLeaseAsync(request, fixture.CreateOwner("first")),
            secondCache.GetOrBuildAndLeaseAsync(request, fixture.CreateOwner("second")));
        try
        {
            Assert.Single(acquisitions, static item => item.Outcome == PackageProjectionCacheOutcome.Miss);
            Assert.Single(acquisitions, static item => item.Outcome == PackageProjectionCacheOutcome.Hit);
            Assert.Single(acquisitions.Select(static item => item.RootPath).Distinct(PathComparer));
            Assert.Equal(0, acquisitions.Single(static item => item.Outcome == PackageProjectionCacheOutcome.Hit).Metrics.BytesCopied);
            Assert.True(acquisitions.Single(static item => item.Outcome == PackageProjectionCacheOutcome.Miss).Metrics.BytesCopied > 0);
        }
        finally
        {
            foreach (var acquisition in acquisitions)
            {
                await acquisition.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task OfflineResetValidationLeavesStaleLeasesUntouchedUntilDeletionPreparation()
    {
        using var fixture = ProjectionFixture.Create();
        var initialCache = fixture.CreateCache(new TestProcessIdentityProvider(PackageProjectionProcessProbe.Matching));
        var acquisition = await initialCache.GetOrBuildAndLeaseAsync(
            fixture.CreateRequest(PackageProjectionRole.App),
            fixture.CreateOwner("abandoned"));
        var leasePath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(initialCache.RootPath, "leases"),
            "*.json",
            SearchOption.AllDirectories));
        var resetCache = fixture.CreateCache(new TestProcessIdentityProvider(PackageProjectionProcessProbe.BirthMismatch));

        await using (var reset = await resetCache.AcquireOfflineResetScopeAsync())
        {
            await reset.ValidateAsync();

            Assert.True(File.Exists(leasePath));

            var recovery = await reset.PrepareForDeletionAsync();
            Assert.Equal(0, recovery.ActiveLeaseCount);
            Assert.Equal(1, recovery.RecoveredLeaseCount);
            Assert.False(File.Exists(leasePath));
        }

        await acquisition.DisposeAsync();
    }

    [Fact]
    public async Task OfflineResetScopeBlocksProjectionAdmissionWithoutCreatingRevisionRoot()
    {
        using var fixture = ProjectionFixture.Create();
        var cache = fixture.CreateCache();

        await using (var reset = await cache.AcquireOfflineResetScopeAsync())
        {
            await reset.ValidateAsync();
            using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrBuildAndLeaseAsync(
                fixture.CreateRequest(PackageProjectionRole.App),
                fixture.CreateOwner("blocked"),
                canceled.Token));

            Assert.False(Directory.Exists(cache.RootPath));
        }

        await using var admitted = await cache.GetOrBuildAndLeaseAsync(
            fixture.CreateRequest(PackageProjectionRole.App),
            fixture.CreateOwner("admitted"));
        Assert.True(Directory.Exists(admitted.RootPath));
    }

    [Fact]
    public async Task CorruptWarmFileIsQuarantinedAndRebuiltWithoutChangingKey()
    {
        using var fixture = ProjectionFixture.Create();
        var cache = fixture.CreateCache();
        var request = fixture.CreateRequest(PackageProjectionRole.App);
        PackageProjectionReference reference;
        string root;
        await using (var first = await cache.GetOrBuildAndLeaseAsync(request, fixture.CreateOwner("first")))
        {
            reference = first.Reference;
            root = first.RootPath;
        }
        MakeWritable(Path.Combine(root, "entry.bin"));
        await File.WriteAllTextAsync(Path.Combine(root, "entry.bin"), "tampered");

        await using var rebuilt = await cache.GetOrBuildAndLeaseAsync(request, fixture.CreateOwner("second"));

        Assert.Equal(PackageProjectionCacheOutcome.CorruptRebuild, rebuilt.Outcome);
        Assert.Equal(reference.CacheKey, rebuilt.Reference.CacheKey);
        Assert.Equal("entry", await File.ReadAllTextAsync(Path.Combine(rebuilt.RootPath, "entry.bin")));
        Assert.NotEmpty(Directory.EnumerateDirectories(
            Path.Combine(cache.RootPath, "quarantine", "app")));
    }

    [Fact]
    public async Task AppAndRuntimeRootsContainOnlyTheirRoleProjection()
    {
        using var fixture = ProjectionFixture.Create();
        var cache = fixture.CreateCache();
        var appRequest = fixture.CreateRequest(PackageProjectionRole.App);
        var runtimeRequest = fixture.CreateRequest(PackageProjectionRole.Runtime) with
        {
            Files = [fixture.CreateFile("runtime.bin", "worker.bin")],
            WarmValidationPaths = ["worker.bin"],
        };

        await using var app = await cache.GetOrBuildAndLeaseAsync(appRequest, fixture.CreateOwner("app"));
        await using var runtime = await cache.GetOrBuildAndLeaseAsync(runtimeRequest, fixture.CreateOwner("runtime"));

        Assert.NotEqual(app.RootPath, runtime.RootPath);
        Assert.True(File.Exists(Path.Combine(app.RootPath, "entry.bin")));
        Assert.False(File.Exists(Path.Combine(app.RootPath, "worker.bin")));
        Assert.True(File.Exists(Path.Combine(runtime.RootPath, "worker.bin")));
        Assert.False(File.Exists(Path.Combine(runtime.RootPath, "entry.bin")));
    }

    [Fact]
    public async Task LiveLeasePreventsGcThenTombstoneGraceRemovesEntry()
    {
        using var fixture = ProjectionFixture.Create(gracePeriod: TimeSpan.Zero);
        var cache = fixture.CreateCache();
        var acquisition = await cache.GetOrBuildAndLeaseAsync(
            fixture.CreateRequest(PackageProjectionRole.App),
            fixture.CreateOwner("active"));

        var protectedResult = await cache.CollectGarbageAsync(new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(1, protectedResult.ActiveLeaseCount);
        Assert.Equal(1, protectedResult.PreservedEntryCount);
        Assert.True(Directory.Exists(acquisition.RootPath));

        await acquisition.DisposeAsync();
        var marked = await cache.CollectGarbageAsync(new HashSet<string>(StringComparer.Ordinal));
        var deleted = await cache.CollectGarbageAsync(new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(1, marked.MarkedEntryCount);
        Assert.Equal(1, deleted.TombstonedEntryCount);
        Assert.False(Directory.Exists(acquisition.RootPath));
    }

    [Theory]
    [InlineData(PackageProjectionProcessProbe.Missing)]
    [InlineData(PackageProjectionProcessProbe.BirthMismatch)]
    public async Task RecoveryRemovesLeaseOnlyWhenProcessBirthNoLongerMatches(
        PackageProjectionProcessProbe staleProbe)
    {
        using var fixture = ProjectionFixture.Create();
        var initialProcesses = new TestProcessIdentityProvider(PackageProjectionProcessProbe.Matching);
        var initialCache = fixture.CreateCache(initialProcesses);
        var acquisition = await initialCache.GetOrBuildAndLeaseAsync(
            fixture.CreateRequest(PackageProjectionRole.App),
            fixture.CreateOwner("abandoned"));
        var leasePath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(initialCache.RootPath, "leases"),
            "*.json",
            SearchOption.AllDirectories));
        var recoveringCache = fixture.CreateCache(new TestProcessIdentityProvider(staleProbe));

        var recovered = await recoveringCache.RecoverLeasesAsync();

        Assert.Equal(1, recovered);
        Assert.False(File.Exists(leasePath));
        await acquisition.DisposeAsync();
    }

    [Fact]
    public async Task UnavailableProcessIdentityPreservesLeaseAndProjection()
    {
        using var fixture = ProjectionFixture.Create(gracePeriod: TimeSpan.Zero);
        var initialCache = fixture.CreateCache(new TestProcessIdentityProvider(PackageProjectionProcessProbe.Matching));
        var acquisition = await initialCache.GetOrBuildAndLeaseAsync(
            fixture.CreateRequest(PackageProjectionRole.App),
            fixture.CreateOwner("uncertain"));
        var recoveringCache = fixture.CreateCache(new TestProcessIdentityProvider(PackageProjectionProcessProbe.Unavailable));

        var result = await recoveringCache.CollectGarbageAsync(new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(1, result.ActiveLeaseCount);
        Assert.True(Directory.Exists(acquisition.RootPath));
        await acquisition.DisposeAsync();
    }

    [Fact]
    public async Task BoundRuntimeReservationUsesOwnerLivenessUntilContainmentAttaches()
    {
        using var fixture = ProjectionFixture.Create();
        var processTreeLeaseRoot = Path.Combine(fixture.StateRoot, "separate-runtime-leases");
        var initialCache = fixture.CreateCache(
            new TestProcessIdentityProvider(PackageProjectionProcessProbe.Matching),
            processTreeLeaseRoot);
        var acquisition = await initialCache.GetOrBuildAndLeaseAsync(
            fixture.CreateRequest(PackageProjectionRole.Runtime),
            fixture.CreateOwner("runtime-reserved"));
        var projectionLeasePath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(initialCache.RootPath, "leases"),
            "*.json",
            SearchOption.AllDirectories));
        var processTreeLeaseId = Guid.NewGuid().ToString("N");
        var processTreeLeasePath = Path.Combine(processTreeLeaseRoot, processTreeLeaseId + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(processTreeLeasePath)!);
        await WriteProcessTreeLeaseAsync(processTreeLeasePath, processTreeLeaseId, hasContainment: false);
        await acquisition.BindToProcessTreeLeaseAsync(processTreeLeaseId);
        var liveOwnerCache = fixture.CreateCache(
            new TestProcessIdentityProvider(PackageProjectionProcessProbe.Matching),
            processTreeLeaseRoot);
        var staleOwnerCache = fixture.CreateCache(
            new TestProcessIdentityProvider(PackageProjectionProcessProbe.BirthMismatch),
            processTreeLeaseRoot);

        Assert.Equal(0, await liveOwnerCache.RecoverLeasesAsync());
        Assert.True(File.Exists(projectionLeasePath));

        await using (var reset = await staleOwnerCache.AcquireOfflineResetScopeAsync())
        {
            await reset.ValidateAsync();
            var recovery = await reset.PrepareForDeletionAsync();
            Assert.Equal(1, recovery.RecoveredLeaseCount);
        }
        Assert.False(File.Exists(projectionLeasePath));
        Assert.True(File.Exists(processTreeLeasePath));
        await acquisition.DisposeAsync();
    }

    [Fact]
    public async Task RuntimeLeaseTracksDurableTreeAndEveryExactProcessIdentity()
    {
        using var fixture = ProjectionFixture.Create();
        var initialCache = fixture.CreateCache(new TestProcessIdentityProvider(PackageProjectionProcessProbe.Matching));
        var acquisition = await initialCache.GetOrBuildAndLeaseAsync(
            fixture.CreateRequest(PackageProjectionRole.Runtime),
            fixture.CreateOwner("runtime-tree"));
        var projectionLeasePath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(initialCache.RootPath, "leases"),
            "*.json",
            SearchOption.AllDirectories));
        var processTreeLeaseId = Guid.NewGuid().ToString("N");
        var processTreeLeasePath = Path.Combine(
            fixture.StateRoot,
            "execution-runtimes",
            "leases",
            processTreeLeaseId + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(processTreeLeasePath)!);
        await WriteProcessTreeLeaseAsync(processTreeLeasePath, processTreeLeaseId, hasContainment: true);
        var guardian = new PackageProjectionProcessIdentity(
            2_000_000_000,
            "guardian-birth",
            DateTimeOffset.Parse("2026-01-01T00:00:01Z"));
        var worker = new PackageProjectionProcessIdentity(
            1_999_999_999,
            "worker-birth",
            DateTimeOffset.Parse("2026-01-01T00:00:02Z"));

        await acquisition.AttachToProcessTreeAsync(processTreeLeaseId, [guardian]);
        await acquisition.AttachToProcessTreeAsync(processTreeLeaseId, [guardian, worker]);

        var linkedRecovery = fixture.CreateCache(new TestProcessIdentityProvider(
            (processId, _) => processId == worker.ProcessId
                ? PackageProjectionProcessProbe.Matching
                : PackageProjectionProcessProbe.BirthMismatch));
        Assert.Equal(0, await linkedRecovery.RecoverLeasesAsync());
        Assert.True(File.Exists(projectionLeasePath));

        var pidReuseRecovery = fixture.CreateCache(new TestProcessIdentityProvider(PackageProjectionProcessProbe.BirthMismatch));
        Assert.Equal(1, await pidReuseRecovery.RecoverLeasesAsync());
        Assert.False(File.Exists(projectionLeasePath));
        Assert.True(File.Exists(processTreeLeasePath));
        await acquisition.DisposeAsync();
    }

    [Fact]
    public async Task MalformedDurableProcessTreeEvidenceFailsClosed()
    {
        using var fixture = ProjectionFixture.Create();
        var processTreeLeaseRoot = Path.Combine(fixture.StateRoot, "separate-runtime-leases");
        var cache = fixture.CreateCache(
            new TestProcessIdentityProvider(PackageProjectionProcessProbe.BirthMismatch),
            processTreeLeaseRoot);
        var acquisition = await cache.GetOrBuildAndLeaseAsync(
            fixture.CreateRequest(PackageProjectionRole.Runtime),
            fixture.CreateOwner("runtime-malformed"));
        var projectionLeasePath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(cache.RootPath, "leases"),
            "*.json",
            SearchOption.AllDirectories));
        var processTreeLeaseId = Guid.NewGuid().ToString("N");
        var processTreeLeasePath = Path.Combine(processTreeLeaseRoot, processTreeLeaseId + ".json");
        Directory.CreateDirectory(processTreeLeaseRoot);
        await File.WriteAllTextAsync(processTreeLeasePath, "not-json");
        await acquisition.BindToProcessTreeLeaseAsync(processTreeLeaseId);

        await Assert.ThrowsAsync<InvalidDataException>(() => cache.RecoverLeasesAsync());

        Assert.True(File.Exists(projectionLeasePath));
        await File.WriteAllTextAsync(processTreeLeasePath, "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.RecoverLeasesAsync());
        Assert.True(File.Exists(projectionLeasePath));
        File.Delete(processTreeLeasePath);
        await acquisition.DisposeAsync();
    }

    [Fact]
    public async Task RuntimeLeaseCannotBeDisposedWhileAttachedTreeIsLive()
    {
        using var fixture = ProjectionFixture.Create();
        var processProbe = PackageProjectionProcessProbe.Matching;
        var cache = fixture.CreateCache(new TestProcessIdentityProvider((_, _) => processProbe));
        var acquisition = await cache.GetOrBuildAndLeaseAsync(
            fixture.CreateRequest(PackageProjectionRole.Runtime),
            fixture.CreateOwner("runtime-live"));
        var leasePath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(cache.RootPath, "leases"),
            "*.json",
            SearchOption.AllDirectories));
        await acquisition.AttachToProcessTreeAsync(
            [new PackageProjectionProcessIdentity(
                901,
                "live-worker-birth",
                DateTimeOffset.Parse("2026-01-01T00:00:01Z"))]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => acquisition.DisposeAsync().AsTask());

        Assert.True(File.Exists(leasePath));
        processProbe = PackageProjectionProcessProbe.Missing;
        await acquisition.DisposeAsync();
        Assert.False(File.Exists(leasePath));
    }

    [Fact]
    public async Task FailedRuntimeStartReleasesBoundReservationAfterDurableTreeLeaseEnds()
    {
        using var fixture = ProjectionFixture.Create();
        var cache = fixture.CreateCache(new TestProcessIdentityProvider(PackageProjectionProcessProbe.Matching));
        var acquisition = await cache.GetOrBuildAndLeaseAsync(
            fixture.CreateRequest(PackageProjectionRole.Runtime),
            fixture.CreateOwner("runtime-start-failed"));
        var projectionLeasePath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(cache.RootPath, "leases"),
            "*.json",
            SearchOption.AllDirectories));
        var processTreeLeaseId = Guid.NewGuid().ToString("N");
        var processTreeLeasePath = Path.Combine(
            fixture.StateRoot,
            "execution-runtimes",
            "leases",
            processTreeLeaseId + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(processTreeLeasePath)!);
        await File.WriteAllTextAsync(processTreeLeasePath, "reserved");
        await acquisition.BindToProcessTreeLeaseAsync(processTreeLeaseId);

        File.Delete(processTreeLeasePath);
        await acquisition.DisposeAsync();

        Assert.False(File.Exists(projectionLeasePath));
    }

    [Fact]
    public async Task WarmHitHashesOnlySelectedFilesAndCopiesNothing()
    {
        using var fixture = ProjectionFixture.Create();
        var cache = fixture.CreateCache();
        var request = fixture.CreateRequest(PackageProjectionRole.App) with
        {
            Files =
            [
                fixture.CreateFile("app.bin", "entry.bin"),
                fixture.CreateFile("shared.bin", "shared.bin"),
            ],
            WarmValidationPaths = ["entry.bin"],
        };
        await using (var built = await cache.GetOrBuildAndLeaseAsync(request, fixture.CreateOwner("build")))
        {
            Assert.Equal(2, built.Metrics.FilesCopied);
        }

        await using var hit = await cache.GetOrBuildAndLeaseAsync(request, fixture.CreateOwner("warm"));

        Assert.Equal(PackageProjectionCacheOutcome.Hit, hit.Outcome);
        Assert.Equal(0, hit.Metrics.BytesCopied);
        Assert.Equal(1, hit.Metrics.FilesHashed);
        Assert.Equal(0, hit.Metrics.FilesCopied);
    }

    [Fact]
    public async Task DeepVerifyFindsCorruptionOutsideWarmLaunchSet()
    {
        using var fixture = ProjectionFixture.Create();
        var cache = fixture.CreateCache();
        var request = fixture.CreateRequest(PackageProjectionRole.App) with
        {
            Files =
            [
                fixture.CreateFile("app.bin", "entry.bin"),
                fixture.CreateFile("shared.bin", "shared.bin"),
            ],
            WarmValidationPaths = ["entry.bin"],
        };
        PackageProjectionReference reference;
        string root;
        await using (var built = await cache.GetOrBuildAndLeaseAsync(request, fixture.CreateOwner("build")))
        {
            reference = built.Reference;
            root = built.RootPath;
        }
        var sharedPath = Path.Combine(root, "shared.bin");
        MakeWritable(sharedPath);
        await File.WriteAllTextAsync(sharedPath, "xxxxxx");

        await using var warm = await cache.AcquireAndLeaseAsync(
            reference,
            fixture.CreateOwner("warm"),
            ["entry.bin"]);
        Assert.Equal(PackageProjectionCacheOutcome.Hit, warm.Outcome);
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.VerifyAsync(reference, deep: true));
    }

    [Fact]
    public async Task CancellationAndInvalidSourceLeaveNoPublishedEntry()
    {
        using var fixture = ProjectionFixture.Create();
        var cache = fixture.CreateCache();
        var request = fixture.CreateRequest(PackageProjectionRole.App);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrBuildAndLeaseAsync(
            request,
            fixture.CreateOwner("canceled"),
            canceled.Token));
        var invalid = request with
        {
            Files = request.Files.Select(file => file with { Sha256 = new string('0', 64) }).ToArray(),
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.GetOrBuildAndLeaseAsync(
            invalid,
            fixture.CreateOwner("invalid")));

        var objectRoot = Path.Combine(cache.RootPath, "objects");
        Assert.Empty(Directory.EnumerateFiles(objectRoot, "receipt.json", SearchOption.AllDirectories));
    }

    private static Task WriteProcessTreeLeaseAsync(
        string path,
        string leaseId,
        bool hasContainment)
    {
        var reservedAtUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var containmentProcessId = 2_000_000_000;
        var processId = 1_999_999_999;
        var containmentKind = OperatingSystem.IsWindows()
            ? "windows-job-v1"
            : "unix-process-group-v1";
        var containmentIdentity = OperatingSystem.IsWindows()
            ? $@"Local\Sunder.ExecutionRuntime.{leaseId}"
            : containmentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var document = new
        {
            schemaVersion = 2,
            leaseId,
            hostInstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            packageId = "test.package",
            generationId = "test-generation",
            activationId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            contentIdentity = new string('a', 64),
            reservedAtUtc,
            processStartRequestedAtUtc = hasContainment ? reservedAtUtc.AddSeconds(1) : (DateTimeOffset?)null,
            containmentGeneration = hasContainment
                ? Guid.Parse("33333333-3333-3333-3333-333333333333")
                : (Guid?)null,
            containmentKind = hasContainment ? containmentKind : null,
            containmentIdentity = hasContainment ? containmentIdentity : null,
            containmentProcessId = hasContainment ? containmentProcessId : (int?)null,
            containmentProcessBirthIdentity = hasContainment ? "guardian-birth" : null,
            containmentProcessStartedAtUtc = hasContainment ? reservedAtUtc.AddSeconds(2) : (DateTimeOffset?)null,
            containmentExecutablePath = hasContainment ? Path.GetFullPath(path) : null,
            containmentAttachedAtUtc = hasContainment ? reservedAtUtc.AddSeconds(3) : (DateTimeOffset?)null,
            processId = hasContainment ? processId : (int?)null,
            processBirthIdentity = hasContainment ? "worker-birth" : null,
            processStartedAtUtc = hasContainment ? reservedAtUtc.AddSeconds(4) : (DateTimeOffset?)null,
            executablePath = hasContainment ? Path.GetFullPath(path) : null,
            runtimeRootPath = hasContainment ? Path.GetDirectoryName(Path.GetFullPath(path)) : null,
            attachedAtUtc = hasContainment ? reservedAtUtc.AddSeconds(5) : (DateTimeOffset?)null,
        };
        return File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void MakeWritable(string path)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private sealed class TestProcessIdentityProvider : IPackageProjectionProcessIdentityProvider
    {
        private readonly Func<int, string, PackageProjectionProcessProbe> _probe;

        public TestProcessIdentityProvider(PackageProjectionProcessProbe probe)
            : this((_, _) => probe)
        {
        }

        public TestProcessIdentityProvider(Func<int, string, PackageProjectionProcessProbe> probe)
        {
            _probe = probe;
        }

        public PackageProjectionProcessIdentity ObserveCurrent()
            => new(73, "test-process-birth", DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        public PackageProjectionProcessProbe Probe(int processId, string birthIdentity)
            => _probe(processId, birthIdentity);
    }

    private sealed class ProjectionFixture : IDisposable
    {
        private readonly string _root;
        private readonly TimeSpan _gracePeriod;

        private ProjectionFixture(string root, TimeSpan gracePeriod)
        {
            _root = root;
            _gracePeriod = gracePeriod;
            StateRoot = Path.Combine(root, "state");
            SourceRoot = Path.Combine(root, "source");
            Directory.CreateDirectory(SourceRoot);
            File.WriteAllText(Path.Combine(SourceRoot, "app.bin"), "entry");
            File.WriteAllText(Path.Combine(SourceRoot, "runtime.bin"), "worker");
            File.WriteAllText(Path.Combine(SourceRoot, "shared.bin"), "shared");
        }

        public string StateRoot { get; }

        public string SourceRoot { get; }

        public static ProjectionFixture Create(TimeSpan? gracePeriod = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "sunder-projection-tests", Guid.NewGuid().ToString("N"));
            return new ProjectionFixture(root, gracePeriod ?? TimeSpan.FromDays(1));
        }

        public PackageProjectionCache CreateCache(
            IPackageProjectionProcessIdentityProvider? processes = null,
            string? executionRuntimeLeaseRootPath = null)
            => new(
                StateRoot,
                new PackageProjectionCacheOptions
                {
                    GarbageCollectionGracePeriod = _gracePeriod,
                    StagingRetention = TimeSpan.Zero,
                    QuarantineRetention = TimeSpan.FromDays(1),
                },
                processes ?? new TestProcessIdentityProvider(PackageProjectionProcessProbe.Matching),
                executionRuntimeLeaseRootPath: executionRuntimeLeaseRootPath);

        public PackageProjectionRequest CreateRequest(PackageProjectionRole role)
            => new(
                SourceRoot,
                "test.package",
                HashText("source-package"),
                role,
                PackageProjectionCache.DefaultRulesIdentity,
                [role == PackageProjectionRole.App ? "app/test-rid" : "runtime/test-rid"],
                [CreateFile(role == PackageProjectionRole.App ? "app.bin" : "runtime.bin", "entry.bin")],
                ["entry.bin"]);

        public PackageProjectionSourceFile CreateFile(string sourcePath, string projectionPath)
        {
            var path = Path.Combine(SourceRoot, sourcePath);
            using var stream = File.OpenRead(path);
            return new PackageProjectionSourceFile(
                sourcePath,
                projectionPath,
                Convert.ToHexStringLower(SHA256.HashData(stream)),
                stream.Length);
        }

        public PackageProjectionOwner CreateOwner(string generation)
            => new("test", Guid.Parse("d0adcf32-9774-4fe1-a04c-0d6aecf8494d"), "test.package", generation);

        public void Dispose()
        {
            MakeTreeWritable(_root);
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        private static string HashText(string value)
            => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

        private static void MakeTreeWritable(string root)
        {
            if (!Directory.Exists(root))
            {
                return;
            }
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                MakeWritable(file);
            }
            if (!OperatingSystem.IsWindows())
            {
                foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    File.SetUnixFileMode(
                        directory,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                File.SetUnixFileMode(
                    root,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }
}
