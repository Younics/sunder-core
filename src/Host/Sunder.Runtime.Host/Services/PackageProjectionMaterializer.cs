using System.Buffers;
using System.Security.Cryptography;
using Sunder.Package.Format;

namespace Sunder.Runtime.Host.Services;

internal static class PackageProjectionMaterializer
{
    public static async Task MaterializeProjectionAsync(
        string sourceRoot,
        string destinationRoot,
        SunderPackageManifest manifest,
        SunderPackageContentIndex contentIndex,
        SunderPackageTargetKey targetKey,
        CancellationToken cancellationToken)
    {
        var entries = GetEntries(contentIndex);
        var plan = SunderPackageTargetResolver.CreateProjectionPlan(manifest, contentIndex, targetKey);
        Directory.CreateDirectory(destinationRoot);
        foreach (var projection in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var physicalPath = projection.PhysicalPath.ToString();
            if (!entries.TryGetValue(physicalPath, out var entry))
            {
                throw new InvalidDataException(
                    $"Package target '{targetKey}' projection references unindexed file '{physicalPath}'.");
            }
            await CopyAndValidateAsync(
                projection.PhysicalPath.ToPlatformPath(sourceRoot),
                projection.LogicalPath.ToPlatformPath(destinationRoot),
                entry,
                cancellationToken);
        }
    }

    public static async Task MaterializeCanonicalPackageAsync(
        string sourceRoot,
        string destinationRoot,
        SunderPackageContentIndex contentIndex,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var entry in GetEntries(contentIndex).Values.OrderBy(static item => item.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ArchiveRelativePath.Parse(
                entry.Path!,
                SunderPackageFormat.MaxArchivePathLength,
                SunderPackageFormat.MaxArchivePathDepth);
            await CopyAndValidateAsync(
                path.ToPlatformPath(sourceRoot),
                path.ToPlatformPath(destinationRoot),
                entry,
                cancellationToken);
        }

        var contentIndexPath = ArchiveRelativePath.Parse(SunderPackageFormat.ContentIndexPath);
        var sourceIndexPath = contentIndexPath.ToPlatformPath(sourceRoot);
        var destinationIndexPath = contentIndexPath.ToPlatformPath(destinationRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationIndexPath)!);
        File.Copy(sourceIndexPath, destinationIndexPath, overwrite: false);

        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(
            destinationRoot,
            cancellationToken);
        if (!validation.Success)
        {
            throw new InvalidDataException(
                $"Materialized package is invalid: {string.Join(" | ", validation.Errors)}");
        }
    }

    private static Dictionary<string, SunderPackageContentIndexEntry> GetEntries(
        SunderPackageContentIndex contentIndex)
    {
        if (contentIndex.Files is null)
        {
            throw new InvalidDataException("Package content index is missing files.");
        }
        return contentIndex.Files.ToDictionary(
            static entry => entry?.Path
                ?? throw new InvalidDataException("Package content index contains a null file entry."),
            static entry => entry!,
            StringComparer.Ordinal);
    }

    private static async Task CopyAndValidateAsync(
        string sourcePath,
        string destinationPath,
        SunderPackageContentIndexEntry entry,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != entry.Size)
        {
            throw new InvalidDataException($"Package file '{entry.Path}' changed before projection materialization.");
        }
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long copied = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                copied = checked(copied + read);
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await destination.FlushAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (copied != entry.Size || !string.Equals(actualHash, entry.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Package file '{entry.Path}' failed projection hash validation.");
        }

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(destinationPath);
            mode &= ~(UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(destinationPath, mode);
        }
    }
}
