using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Format;

namespace Sunder.Runtime.Host.Services;

internal static class PackageProjectionMaterializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task MaterializeProjectionAsync(
        string sourceRoot,
        string destinationRoot,
        SunderPackageManifest manifest,
        SunderPackageContentIndex contentIndex,
        SunderPackageTargetKey targetKey,
        CancellationToken cancellationToken,
        PackageSessionStartupTracePackage? startupTrace = null)
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
                cancellationToken,
                startupTrace);
        }
    }

    public static async Task MaterializeCanonicalPackageAsync(
        string sourceRoot,
        string destinationRoot,
        SunderPackageContentIndex contentIndex,
        CancellationToken cancellationToken,
        PackageSessionStartupTracePackage? startupTrace = null)
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
                cancellationToken,
                startupTrace);
        }

        var contentIndexPath = ArchiveRelativePath.Parse(SunderPackageFormat.ContentIndexPath);
        var sourceIndexPath = contentIndexPath.ToPlatformPath(sourceRoot);
        var destinationIndexPath = contentIndexPath.ToPlatformPath(destinationRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationIndexPath)!);
        File.Copy(sourceIndexPath, destinationIndexPath, overwrite: false);
        startupTrace?.AddFileIo(sourceIndexPath, read: true, copied: true);

        var validation = startupTrace is null
            ? await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(
                destinationRoot,
                cancellationToken)
            : await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(
                destinationRoot,
                cancellationToken,
                startupTrace);
        if (!validation.Success)
        {
            throw new InvalidDataException(
                $"Materialized package is invalid: {string.Join(" | ", validation.Errors)}");
        }
    }

    public static async Task<SunderPackageArchiveValidationResult> MaterializeCurrentRidInstallPackageAsync(
        string sourceRoot,
        string destinationRoot,
        SunderPackageManifest sourceManifest,
        SunderPackageContentIndex sourceContentIndex,
        string runtimeIdentifier,
        CancellationToken cancellationToken,
        IReadOnlyList<SunderPackageTargetKey>? acquiredTargets = null)
    {
        if (!SunderPackageFormat.IsRuntimeIdentifier(runtimeIdentifier))
        {
            throw new ArgumentException("Install projection requires a supported exact RID.", nameof(runtimeIdentifier));
        }

        // The source manifest is immutable package-level authority, not a role-local
        // artifact. Acquisition limits payload and activation, never its import graph.
        PackageManifestAdmissionPolicy.EnsureSupported(sourceManifest);
        InstalledPackageProjection.ValidateScope(acquiredTargets, sourceManifest);
        if (acquiredTargets?.Any(key => key.Rid != runtimeIdentifier) == true)
            throw new InvalidDataException("Install projection acquisition requires the exact current RID.");
        var selectedTargets = (sourceManifest.Targets ?? [])
            .Where(target => target is not null
                             && string.Equals(target.Rid, runtimeIdentifier, StringComparison.Ordinal)
                             && InstalledPackageProjection.Includes(acquiredTargets, target.Role, target.Rid))
            .Select(static target => target!)
            .ToArray();
        var selectedKeys = selectedTargets
            .Select(static target => new SunderPackageTargetKey(target.Role!, target.Rid!))
            .ToArray();
        var entries = GetEntries(sourceContentIndex);
        var selectedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.Values)
        {
            if (entry.Path!.StartsWith(SunderPackageFormat.SharedPayloadRoot, StringComparison.Ordinal))
            {
                selectedPaths.Add(entry.Path);
            }
        }
        foreach (var key in selectedKeys)
        {
            foreach (var file in SunderPackageTargetResolver.CreateProjectionPlan(
                         sourceManifest,
                         sourceContentIndex,
                         key).Files)
            {
                selectedPaths.Add(file.PhysicalPath.ToString());
            }
        }

        Directory.CreateDirectory(destinationRoot);
        foreach (var pathText in selectedPaths.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ArchiveRelativePath.Parse(
                pathText,
                SunderPackageFormat.MaxArchivePathLength,
                SunderPackageFormat.MaxArchivePathDepth);
            await CopyAndValidateAsync(
                path.ToPlatformPath(sourceRoot),
                path.ToPlatformPath(destinationRoot),
                entries[pathText],
                cancellationToken);
        }

        var manifestPath = ArchiveRelativePath.Parse(SunderPackageFormat.ManifestPath)
            .ToPlatformPath(destinationRoot);
        await CopyAndValidateAsync(ArchiveRelativePath.Parse(SunderPackageFormat.ManifestPath).ToPlatformPath(sourceRoot),
            manifestPath, entries[SunderPackageFormat.ManifestPath], cancellationToken);

        var projectedEntries = new List<SunderPackageContentIndexEntry>();
        foreach (var path in Directory.EnumerateFiles(destinationRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(destinationRoot, path), StringComparer.Ordinal))
        {
            await using var stream = File.OpenRead(path);
            projectedEntries.Add(new SunderPackageContentIndexEntry(
                Path.GetRelativePath(destinationRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
                Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant(),
                stream.Length));
        }

        var contentIndexPath = ArchiveRelativePath.Parse(SunderPackageFormat.ContentIndexPath)
            .ToPlatformPath(destinationRoot);
        await File.WriteAllTextAsync(
            contentIndexPath,
            JsonSerializer.Serialize(
                new SunderPackageContentIndex(
                    SunderPackageFormat.CurrentContentIndexVersion,
                    projectedEntries),
                JsonOptions) + "\n",
            cancellationToken);

        var validation = await SunderPackageArchiveInspector.ValidateExtractedProjectionSetAsync(
            destinationRoot, selectedKeys, cancellationToken);
        if (!validation.Success)
        {
            throw new InvalidDataException(
                $"Current-RID install projection is invalid: {string.Join(" | ", validation.Errors)}");
        }
        return validation;
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
        CancellationToken cancellationToken,
        PackageSessionStartupTracePackage? startupTrace = null)
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
        long written = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                copied = checked(copied + read);
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written = checked(written + read);
            }
            await destination.FlushAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (startupTrace is not null)
            {
                try
                {
                    written = destination.Position;
                }
                catch
                {
                    // Instrumentation never changes projection materialization outcomes.
                }
                startupTrace.AddIo(
                    bytesRead: copied,
                    bytesHashed: copied,
                    bytesCopied: written);
            }
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
