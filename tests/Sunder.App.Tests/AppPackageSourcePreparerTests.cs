using System.IO.Compression;
using System.Security.Cryptography;
using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageSourcePreparerTests
{
    [Theory]
    [InlineData("../outside.txt", 0)]
    [InlineData("payload/link", (0xa000 | 0x1ff) << 16)]
    public async Task PrepareAsync_RejectsMaliciousArchiveEntriesWithoutPublishing(
        string maliciousPath,
        int externalAttributes)
    {
        var root = CreateTempDirectory();
        try
        {
            var archive = CreateArchive(
                ("sunder-package.json", "{\"id\":\"agent\"}", 0),
                (maliciousPath, "malicious", externalAttributes));

            await Assert.ThrowsAsync<InvalidDataException>(() => PrepareAsync(root, archive));

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_RejectsPortableCaseCollisionsWithoutPublishing()
    {
        var root = CreateTempDirectory();
        try
        {
            var archive = CreateArchive(
                ("sunder-package.json", "{\"id\":\"agent\"}", 0),
                ("assets/icon.png", "first", 0),
                ("assets/ICON.png", "second", 0));

            await Assert.ThrowsAsync<InvalidDataException>(() => PrepareAsync(root, archive));

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_RejectsHashMismatchWithoutPublishing()
    {
        var root = CreateTempDirectory();
        try
        {
            var archive = CreateArchive(("sunder-package.json", "{\"id\":\"agent\"}", 0));
            var snapshot = CreateSnapshot(new string('0', 64));
            var preparer = new AppPackageSourcePreparer(root);

            await Assert.ThrowsAsync<InvalidDataException>(() => preparer.PrepareAsync(
                snapshot,
                (_, destination, cancellationToken) => destination.WriteAsync(archive, cancellationToken).AsTask(),
                CancellationToken.None));

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task<AppPreparedPackageSource?> PrepareAsync(string root, byte[] archive)
    {
        var hash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        var preparer = new AppPackageSourcePreparer(root);
        return preparer.PrepareAsync(
            CreateSnapshot(hash),
            (_, destination, cancellationToken) => destination.WriteAsync(archive, cancellationToken).AsTask(),
            CancellationToken.None);
    }

    private static PackageUiSnapshotDescriptor CreateSnapshot(string hash)
        => new("agent", PackageSourceKind.Dev, 1, hash, "snapshot", "packages/ui-snapshots/snapshot");

    private static byte[] CreateArchive(params (string Path, string Content, int ExternalAttributes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Path, CompressionLevel.Fastest);
                entry.ExternalAttributes = item.ExternalAttributes;
                using var writer = new StreamWriter(entry.Open());
                writer.Write(item.Content);
            }
        }

        return stream.ToArray();
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-app-source-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
