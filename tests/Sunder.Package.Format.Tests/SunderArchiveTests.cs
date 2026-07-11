using System.IO.Compression;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class SunderArchiveTests
{
    [Theory]
    [InlineData("../outside")]
    [InlineData("a/../outside")]
    [InlineData("/absolute")]
    [InlineData("//server/share")]
    [InlineData("C:/windows/path")]
    [InlineData("C:\\windows\\path")]
    [InlineData("\\\\server\\share")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("a/")]
    [InlineData("a\\b")]
    [InlineData("a\0b")]
    [InlineData("a\u001fb")]
    [InlineData("a/b.")]
    [InlineData("a/b ")]
    [InlineData("a/file:stream")]
    [InlineData("payload/CON.txt")]
    [InlineData("payload/naïve.txt")]
    public void ArchiveRelativePath_RejectsNonPortablePaths(string value)
        => Assert.False(ArchiveRelativePath.TryParse(value, 240, 32, out _, out _));

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("C:/windows/path")]
    [InlineData("C:\\windows\\path")]
    public async Task ExtractAtomicAsync_RejectsTraversalAndCrossPlatformAbsoluteEntries(string entryPath)
    {
        var root = CreateTempDirectory();
        var archivePath = CreateArchive(root, (entryPath, "content", 0));
        var destination = Path.Combine(root, "extracted");

        await Assert.ThrowsAsync<InvalidDataException>(() => SunderArchive.ExtractAtomicAsync(archivePath, destination));

        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("A.txt", "a.txt")]
    [InlineData("a", "a/b.txt")]
    [InlineData("a/b.txt", "a")]
    public async Task ExtractAtomicAsync_RejectsDuplicateCaseAndDirectoryFileCollisions(string first, string second)
    {
        var root = CreateTempDirectory();
        var archivePath = CreateArchive(root, (first, "first", 0), (second, "second", 0));
        var destination = Path.Combine(root, "extracted");

        await Assert.ThrowsAsync<InvalidDataException>(() => SunderArchive.ExtractAtomicAsync(archivePath, destination));

        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task ExtractAtomicAsync_RejectsUnixSymlinkAttributes()
    {
        var root = CreateTempDirectory();
        var archivePath = CreateArchive(root, ("payload/link", "target", (0xa000 | 0x1ff) << 16));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => SunderArchive.ExtractAtomicAsync(archivePath, Path.Combine(root, "extracted")));

        Assert.Contains("link", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractAtomicAsync_RejectsWindowsReparseAttributes()
    {
        var root = CreateTempDirectory();
        var archivePath = CreateArchive(root, ("payload/link", "target", (int)FileAttributes.ReparsePoint));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => SunderArchive.ExtractAtomicAsync(archivePath, Path.Combine(root, "extracted")));

        Assert.Contains("link", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractAtomicAsync_RejectsCompressionBombRatio()
    {
        var root = CreateTempDirectory();
        var archivePath = CreateArchive(root, ("payload/zeros.bin", new string('0', 100_000), 0));
        var options = SunderArchiveExtractionOptions.Default with { MaxCompressionRatio = 2 };

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => SunderArchive.ExtractAtomicAsync(archivePath, Path.Combine(root, "extracted"), options));

        Assert.Contains("compression-ratio", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractAtomicAsync_EnforcesEntryCountAndCleansPartialOutput()
    {
        var root = CreateTempDirectory();
        var archivePath = CreateArchive(root, ("one", "1", 0), ("two", "2", 0));
        var destination = Path.Combine(root, "extracted");
        var options = SunderArchiveExtractionOptions.Default with { MaxEntries = 1 };

        await Assert.ThrowsAsync<InvalidDataException>(() => SunderArchive.ExtractAtomicAsync(archivePath, destination, options));

        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task ExtractAtomicAsync_EnforcesPerEntryAndTotalByteLimits()
    {
        var root = CreateTempDirectory();
        var perEntryArchive = CreateArchive(root, ("large", "123456", 0));
        var totalArchive = CreateArchive(root, ("one", "1234", 0), ("two", "5678", 0));

        await Assert.ThrowsAsync<InvalidDataException>(() => SunderArchive.ExtractAtomicAsync(
            perEntryArchive,
            Path.Combine(root, "per-entry"),
            SunderArchiveExtractionOptions.Default with { MaxEntryUncompressedBytes = 5 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => SunderArchive.ExtractAtomicAsync(
            totalArchive,
            Path.Combine(root, "total"),
            SunderArchiveExtractionOptions.Default with { MaxTotalUncompressedBytes = 7 }));
    }

    [Fact]
    public async Task ExtractAtomicAsync_EnforcesPathLengthAndDepthLimits()
    {
        var root = CreateTempDirectory();
        var longPathArchive = CreateArchive(root, ("payload/long-name.txt", "x", 0));
        var deepPathArchive = CreateArchive(root, ("a/b/c/file", "x", 0));

        await Assert.ThrowsAsync<InvalidDataException>(() => SunderArchive.ExtractAtomicAsync(
            longPathArchive,
            Path.Combine(root, "long"),
            SunderArchiveExtractionOptions.Default with { MaxPathLength = 10 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => SunderArchive.ExtractAtomicAsync(
            deepPathArchive,
            Path.Combine(root, "deep"),
            SunderArchiveExtractionOptions.Default with { MaxPathDepth = 3 }));
    }

    [Fact]
    public async Task ExtractAtomicAsync_ObservesCancellationAndDoesNotPublishDestination()
    {
        var root = CreateTempDirectory();
        var archivePath = CreateArchive(root, ("payload/file", "content", 0));
        var destination = Path.Combine(root, "extracted");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SunderArchive.ExtractAtomicAsync(
            archivePath,
            destination,
            SunderArchiveExtractionOptions.Default with { CancellationToken = cancellation.Token }));

        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task PackageInspector_RejectsOversizedMetadata()
    {
        var root = CreateTempDirectory();
        var archivePath = CreateArchive(
            root,
            (SunderPackageFormat.ManifestPath, new string(' ', 200), 0),
            (SunderPackageFormat.ContentIndexPath, "{\"schemaVersion\":1,\"files\":[]}", 0));

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "extracted"),
            SunderArchiveExtractionOptions.Default with { MaxMetadataJsonBytes = 100 });

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("100-byte limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WriteDeterministicAsync_ProducesByteIdenticalSortedArchives()
    {
        var root = CreateTempDirectory();
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "payload"));
        await File.WriteAllTextAsync(Path.Combine(source, "z.txt"), "last");
        await File.WriteAllTextAsync(Path.Combine(source, "payload", "a.txt"), "first");
        var first = Path.Combine(root, "first.zip");
        var second = Path.Combine(root, "second.zip");

        await SunderArchive.WriteDeterministicAsync(source, first);
        File.SetLastWriteTimeUtc(Path.Combine(source, "z.txt"), DateTime.UtcNow.AddDays(1));
        await SunderArchive.WriteDeterministicAsync(source, second);

        Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
        using var archive = ZipFile.OpenRead(first);
        Assert.Equal(["payload/a.txt", "z.txt"], archive.Entries.Select(entry => entry.FullName).ToArray());
        Assert.All(archive.Entries, entry => Assert.Equal(1980, entry.LastWriteTime.Year));
    }

    private static string CreateArchive(string root, params (string Path, string Content, int ExternalAttributes)[] entries)
    {
        var archivePath = Path.Combine(root, $"archive-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Path, CompressionLevel.Optimal);
            entry.ExternalAttributes = item.ExternalAttributes;
            using var writer = new StreamWriter(entry.Open());
            writer.Write(item.Content);
        }

        return archivePath;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
