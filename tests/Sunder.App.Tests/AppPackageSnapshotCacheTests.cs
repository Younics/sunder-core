using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Sunder.App.Services;
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
        Assert.Equal("original", File.ReadAllText(Path.Combine(cacheRoot, "objects", snapshot.ContentHash, "content", "lib", "payload.txt")));
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
            Path.Combine(cacheRoot, "objects", snapshot.ContentHash, "content", "lib", "payload.txt"),
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

        Assert.False(Directory.Exists(Path.Combine(cacheRoot, "objects", snapshot.ContentHash)));
        Assert.True(!Directory.Exists(Path.Combine(cacheRoot, "staging"))
                    || !Directory.EnumerateFileSystemEntries(Path.Combine(cacheRoot, "staging")).Any());
    }

    private static byte[] CreateArchive(string packageId, string payload)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "sunder-package.json", $$"""{"id":"{{packageId}}","entryAssembly":"test.dll"}""");
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

    private static PackageUiSnapshotDescriptor CreateSnapshot(string packageId, byte[] archive)
    {
        var hash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        return new PackageUiSnapshotDescriptor(packageId, PackageSourceKind.Dev, 1, hash, Guid.NewGuid().ToString("N"), "snapshot");
    }

    private static string CreateGeneration(string root)
    {
        var path = Path.Combine(root, "generations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

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
