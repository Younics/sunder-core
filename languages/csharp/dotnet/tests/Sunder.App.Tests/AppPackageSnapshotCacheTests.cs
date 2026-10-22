using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Package.Projections;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageSnapshotCacheTests
{
    [Fact]
    public async Task MaterializeAsync_ReusesValidatedPersistentContentAcrossCacheInstancesWithoutHardlinkMutation()
    {
        var root = CreateTempDirectory();
        var cacheRoot = Path.Combine(root, "cache");
        var archive = CreateArchive("test.package", "original");
        var snapshot = CreateSnapshot("test.package", archive);
        var downloads = 0;
        Task Download(PackageUiSnapshotDescriptor _, Stream destination, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref downloads);
            return destination.WriteAsync(archive, cancellationToken).AsTask();
        }

        var firstCache = new AppPackageSnapshotCache(() => root, Download, cacheRoot);
        var first = await firstCache.MaterializeAsync(snapshot, CreateGeneration(root), CancellationToken.None);
        File.WriteAllText(Path.Combine(first.Folder, "lib", "payload.txt"), "mutated generation");
        var secondCache = new AppPackageSnapshotCache(() => root, Download, cacheRoot);
        var second = await secondCache.MaterializeAsync(snapshot, CreateGeneration(root), CancellationToken.None);

        Assert.Equal(1, downloads);
        Assert.Equal("original", File.ReadAllText(Path.Combine(second.Folder, "lib", "payload.txt")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(GetOnlyObjectPath(cacheRoot), "content", "lib", "payload.txt")));
    }

    [Fact]
    public async Task MaterializeAsync_QuarantinesCorruptContentAndAtomicallyRefills()
    {
        var root = CreateTempDirectory();
        var cacheRoot = Path.Combine(root, "cache");
        var archive = CreateArchive("test.package", "original");
        var snapshot = CreateSnapshot("test.package", archive);
        var downloads = 0;
        async Task Download(PackageUiSnapshotDescriptor _, Stream destination, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref downloads);
            await destination.WriteAsync(archive, cancellationToken);
        }
        var firstCache = new AppPackageSnapshotCache(() => root, Download, cacheRoot);
        await firstCache.MaterializeAsync(snapshot, CreateGeneration(root), CancellationToken.None);
        File.WriteAllText(
            Path.Combine(GetOnlyObjectPath(cacheRoot), "content", "lib", "payload.txt"),
            "corrupt");

        var secondCache = new AppPackageSnapshotCache(() => root, Download, cacheRoot);
        var materialized = await secondCache.MaterializeAsync(snapshot, CreateGeneration(root), CancellationToken.None);

        Assert.Equal(2, downloads);
        Assert.Equal("original", File.ReadAllText(Path.Combine(materialized.Folder, "lib", "payload.txt")));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(cacheRoot, "quarantine")));
    }

    [Fact]
    public async Task MaterializeAsync_SingleFlightsEachHashAndBoundsConcurrentFillsAtTwo()
    {
        var root = CreateTempDirectory();
        var archives = Enumerable.Range(0, 6).ToDictionary(
            index => $"package.{index}",
            index => CreateArchive($"package.{index}", index.ToString()));
        var snapshots = archives.Select(pair => CreateSnapshot(pair.Key, pair.Value)).ToArray();
        var downloads = 0;
        var active = 0;
        var maximumActive = 0;
        async Task Download(PackageUiSnapshotDescriptor snapshot, Stream destination, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref downloads);
            var current = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximumActive, current);
            try
            {
                await Task.Delay(40, cancellationToken);
                await destination.WriteAsync(archives[snapshot.PackageId], cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
        var cache = new AppPackageSnapshotCache(() => root, Download, Path.Combine(root, "cache"));

        await Task.WhenAll(snapshots.Select(snapshot =>
            cache.MaterializeAsync(snapshot, CreateGeneration(root), CancellationToken.None)));

        Assert.Equal(6, downloads);
        Assert.InRange(maximumActive, 1, 2);

        var sharedSnapshot = snapshots[0];
        var freshRoot = Path.Combine(root, "single-flight-cache");
        downloads = 0;
        var singleFlight = new AppPackageSnapshotCache(() => root, Download, freshRoot);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            singleFlight.MaterializeAsync(sharedSnapshot, CreateGeneration(root), CancellationToken.None)));
        Assert.Equal(1, downloads);
    }

    [Fact]
    public async Task MaterializeAsync_CancellationLeavesNoPartialPublishedObject()
    {
        var root = CreateTempDirectory();
        var cacheRoot = Path.Combine(root, "cache");
        var archive = CreateArchive("test.package", "original");
        var snapshot = CreateSnapshot("test.package", archive);
        var cache = new AppPackageSnapshotCache(
            () => root,
            async (_, _, cancellationToken) => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            cacheRoot);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.MaterializeAsync(snapshot, CreateGeneration(root), cancellation.Token));

        Assert.True(!Directory.Exists(Path.Combine(cacheRoot, "objects"))
                    || !Directory.EnumerateFileSystemEntries(Path.Combine(cacheRoot, "objects")).Any());
        Assert.True(!Directory.Exists(Path.Combine(cacheRoot, "staging"))
                    || !Directory.EnumerateFileSystemEntries(Path.Combine(cacheRoot, "staging")).Any());
    }

    [Fact]
    public async Task MaterializeAsync_SameContentForTwoAppRidsUsesSeparateCacheIdentities()
    {
        var root = CreateTempDirectory();
        var cacheRoot = Path.Combine(root, "cache");
        var currentRid = RuntimeInformation.RuntimeIdentifier;
        var alternateRid = SunderPackageFormat.SupportedRuntimeIdentifiers
            .First(rid => !string.Equals(rid, currentRid, StringComparison.Ordinal));
        var archive = CreateArchive("test.package", "payload", [currentRid, alternateRid]);
        var current = CreateSnapshot(
            "test.package",
            archive,
            RuntimeContractTestData.AppTarget(rid: currentRid));
        var alternate = CreateSnapshot(
            "test.package",
            archive,
            RuntimeContractTestData.AppTarget(rid: alternateRid));
        var downloads = 0;
        async Task Download(PackageUiSnapshotDescriptor _, Stream destination, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref downloads);
            await destination.WriteAsync(archive, cancellationToken);
        }
        var cache = new AppPackageSnapshotCache(() => root, Download, cacheRoot);

        await cache.MaterializeAsync(current, CreateGeneration(root), CancellationToken.None);
        await cache.MaterializeAsync(alternate, CreateGeneration(root), CancellationToken.None);

        Assert.Equal(2, downloads);
        Assert.Equal(2, Directory.EnumerateDirectories(Path.Combine(cacheRoot, "objects")).Count());
    }

    [Fact]
    public async Task MaterializeAsync_InstalledReferenceAcquiresSharedProjectionWithoutDownloadOrCopy()
    {
        var root = CreateTempDirectory();
        var runtimeStateRoot = Path.Combine(root, "runtime-state");
        var sourceRoot = Path.Combine(root, "installed-source");
        var manifestPath = Path.Combine(sourceRoot, "manifest", "sunder-package.json");
        var entryPointPath = Path.Combine(sourceRoot, "lib", "test.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(entryPointPath)!);
        File.WriteAllText(
            manifestPath,
            RuntimeContractTestData.CreateAppManifestJson("test.package", "lib/test.dll"));
        File.WriteAllText(entryPointPath, "assembly-placeholder");
        var sourceIdentity = Convert.ToHexStringLower(SHA256.HashData("source-identity"u8));
        var targetKey = $"app/{RuntimeInformation.RuntimeIdentifier}";
        var projectionCache = new PackageProjectionCache(runtimeStateRoot);
        PackageProjectionReference reference;
        string projectionRoot;
        await using (var published = await projectionCache.GetOrBuildAndLeaseAsync(
                         new PackageProjectionRequest(
                             sourceRoot,
                             "test.package",
                             sourceIdentity,
                             PackageProjectionRole.App,
                             PackageProjectionCache.DefaultRulesIdentity,
                             [targetKey],
                             [
                                 CreateProjectionFile(sourceRoot, "manifest/sunder-package.json"),
                                 CreateProjectionFile(sourceRoot, "lib/test.dll"),
                             ],
                             ["manifest/sunder-package.json", "lib/test.dll"]),
                         new PackageProjectionOwner(
                             "runtime-ui-snapshot",
                             Guid.NewGuid(),
                             "test.package",
                             "1")))
        {
            reference = published.Reference;
            projectionRoot = published.RootPath;
        }
        var snapshot = new PackageUiSnapshotDescriptor(
            "test.package",
            PackageSourceKind.Installed,
            1,
            RuntimeContractTestData.AppTarget(),
            reference.CacheKey,
            Guid.NewGuid().ToString("N"),
            string.Empty)
        {
            ProjectionReference = new PackageUiProjectionReferenceDescriptor(
                reference.FormatRevision,
                reference.CacheKey,
                "app",
                reference.SourceContentIdentity,
                reference.ProjectionRulesIdentity,
                reference.TargetKeys),
        };
        var downloadCalled = false;
        var appCache = new AppPackageSnapshotCache(
            () => root,
            (_, _, _) =>
            {
                downloadCalled = true;
                return Task.CompletedTask;
            },
            Path.Combine(root, "app-cache"),
            runtimeStateRoot,
            Guid.NewGuid());

        var prepared = await appCache.MaterializeAsync(
            snapshot,
            CreateGeneration(root),
            CancellationToken.None);
        PackageProjectionAcquisition? activationLease = null;
        try
        {
            Assert.False(downloadCalled);
            Assert.Equal(projectionRoot, prepared.Folder);
            Assert.Equal("assembly-placeholder", File.ReadAllText(Path.Combine(prepared.Folder, "lib", "test.dll")));
            Assert.NotNull(prepared.ProjectionLease);
            Assert.NotEmpty(Directory.EnumerateFiles(
                Path.Combine(projectionCache.RootPath, "leases"),
                "*.json",
                SearchOption.AllDirectories));
            activationLease = Assert.IsType<PackageProjectionAcquisition>(prepared.ProjectionLease.Take());
            await using var runtimeWork = new BackgroundTaskQueueService();
            using var sharedAssemblies = new AppSharedAssemblyRegistry([]);
            var unload = new AppPackageUnloadCoordinator(
                new AppPackageViewRegistry(),
                new AppPackageRuntimeWorkStopper(runtimeWork),
                new AppPackageAssemblyTracker(),
                sharedAssemblies,
                _ => { },
                _ => { });

            var cleanup = await unload.RollBackActivationAsync(
                "test.package",
                packageInfo: null,
                serviceProvider: null,
                loadContext: null,
                projectionLease: activationLease,
                stopRuntimeWork: false);
            activationLease = null;

            Assert.Equal(AppPackageUiCleanupOutcome.Proven, cleanup);
        }
        finally
        {
            if (activationLease is not null)
            {
                await activationLease.DisposeAsync();
            }
            if (prepared.ProjectionLease is not null)
            {
                await prepared.ProjectionLease.DisposeAsync();
            }
        }
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(projectionCache.RootPath, "leases"),
            "*.json",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MaterializeAsync_InstalledSourceWithoutProjectionReferenceIsRejectedWithoutDownload()
    {
        var root = CreateTempDirectory();
        var downloadCalled = false;
        var cache = new AppPackageSnapshotCache(
            () => root,
            (_, _, _) =>
            {
                downloadCalled = true;
                return Task.CompletedTask;
            },
            Path.Combine(root, "app-cache"),
            Path.Combine(root, "runtime-state"));
        var snapshot = CreateSnapshot("test.package", CreateArchive("test.package", "payload")) with
        {
            SourceKind = PackageSourceKind.Installed,
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => cache.MaterializeAsync(
            snapshot,
            CreateGeneration(root),
            CancellationToken.None));

        Assert.False(downloadCalled);
    }

    private static byte[] CreateArchive(
        string packageId,
        string payload,
        IReadOnlyList<string>? runtimeIdentifiers = null)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(
                archive,
                "manifest/sunder-package.json",
                RuntimeContractTestData.CreateAppManifestJson(packageId, "lib/test.dll", runtimeIdentifiers));
            WriteEntry(archive, "lib/payload.txt", payload);
        }
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static PackageProjectionSourceFile CreateProjectionFile(string sourceRoot, string relativePath)
    {
        var path = Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        using var stream = File.OpenRead(path);
        return new PackageProjectionSourceFile(
            relativePath,
            relativePath,
            Convert.ToHexStringLower(SHA256.HashData(stream)),
            stream.Length);
    }

    private static PackageUiSnapshotDescriptor CreateSnapshot(
        string packageId,
        byte[] archive,
        PackageTargetDescriptor? target = null)
    {
        var hash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        return new PackageUiSnapshotDescriptor(packageId, PackageSourceKind.Dev, 1, target ?? RuntimeContractTestData.AppTarget(), hash, Guid.NewGuid().ToString("N"), "snapshot");
    }

    private static string CreateGeneration(string root)
    {
        var path = Path.Combine(root, "generations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetOnlyObjectPath(string cacheRoot)
        => Assert.Single(Directory.EnumerateDirectories(Path.Combine(cacheRoot, "objects")));

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-app-cache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }
}
