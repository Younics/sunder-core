using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sunder.Package.Format;

public static class SunderPackageProjectionArchiveWriter
{
    private const int CopyBufferSize = 81920;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly SunderPackageJsonContext JsonContext = new(JsonOptions);
    private static readonly ArchiveRelativePath ManifestPath = ArchiveRelativePath.Parse(SunderPackageProjectionFormat.ManifestPath);

    public static Task<SunderPackageProjectionDescriptor> WriteAsync(
        string extractedPackagePath,
        SunderPackageManifest manifest,
        SunderPackageContentIndex contentIndex,
        string sourceArchiveSha256,
        SunderPackageProjectionKey key,
        string projectionArchivePath,
        CancellationToken cancellationToken = default)
        => WriteAsync(
            extractedPackagePath,
            manifest,
            contentIndex,
            sourceArchiveSha256,
            key,
            projectionArchivePath,
            SunderArchiveExtractionOptions.Default with { CancellationToken = cancellationToken });

    public static async Task<SunderPackageProjectionDescriptor> WriteAsync(
        string extractedPackagePath,
        SunderPackageManifest manifest,
        SunderPackageContentIndex contentIndex,
        string sourceArchiveSha256,
        SunderPackageProjectionKey key,
        string projectionArchivePath,
        SunderArchiveExtractionOptions options)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(contentIndex);
        ArgumentNullException.ThrowIfNull(options);
        if (!key.IsValid)
        {
            throw new ArgumentException("Projection key is invalid.", nameof(key));
        }
        if (!PackageContentSignatureValidator.IsLowercaseSha256(sourceArchiveSha256))
        {
            throw new ArgumentException("Source archive identity must be a lowercase SHA-256 hash.", nameof(sourceArchiveSha256));
        }

        options.Validate();
        var cancellationToken = options.CancellationToken;
        var sourceRoot = Path.GetFullPath(extractedPackagePath);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"Extracted package directory '{sourceRoot}' was not found.");
        }

        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(
            sourceRoot,
            options.MaxMetadataJsonBytes,
            cancellationToken);
        if (!validation.Success)
        {
            throw new InvalidDataException(
                "Projection source is not a validated canonical universal package: "
                + string.Join(" ", validation.Errors));
        }
        if (!CanonicalManifestEquals(manifest, validation.Manifest!)
            || !CanonicalContentIndexEquals(contentIndex, validation.ContentIndex!))
        {
            throw new InvalidDataException("Projection source manifest/content index do not match the supplied validated metadata.");
        }

        manifest = validation.Manifest!;
        contentIndex = validation.ContentIndex!;
        if (key.TryGetTargetKey(out var targetKey)
            && !SunderPackageTargetResolver.TryResolveTarget(manifest, targetKey, out _))
        {
            throw new KeyNotFoundException($"Package manifest does not declare exact target '{targetKey}'.");
        }

        var manifestFilePath = SunderArchive.ResolveFile(sourceRoot, ManifestPath);
        var manifestBytes = await ReadBoundedFileAsync(
            manifestFilePath,
            options.MaxMetadataJsonBytes,
            SunderPackageProjectionFormat.ManifestPath,
            cancellationToken);
        var manifestSha256 = Hash(manifestBytes);
        VerifyManifestIndexEntry(contentIndex, manifestBytes.LongLength, manifestSha256);

        var selectedEntries = SelectEntries(contentIndex, key);
        var projectionIndex = new SunderPackageContentIndex(
            SunderPackageProjectionFormat.CurrentContentIndexVersion,
            selectedEntries);
        var contentIndexBytes = JsonSerializer.SerializeToUtf8Bytes(
            projectionIndex,
            JsonContext.SunderPackageContentIndex);

        var descriptor = new SunderPackageProjectionDescriptor
        {
            ProjectionFormatVersion = SunderPackageProjectionFormat.CurrentProjectionFormatVersion,
            PackageId = manifest.Id,
            PackageVersion = manifest.Version,
            SourceArchiveSha256 = sourceArchiveSha256,
            Kind = key.Kind,
            Rid = key.Rid,
            ManifestSha256 = manifestSha256,
            ProjectionSha256 = SunderPackageProjectionIdentity.Compute(
                SunderPackageProjectionFormat.CurrentProjectionFormatVersion,
                manifest.Id!,
                manifest.Version!,
                sourceArchiveSha256,
                key.Kind,
                key.Rid,
                manifestSha256,
                contentIndexBytes),
        };
        var descriptorBytes = JsonSerializer.SerializeToUtf8Bytes(
            descriptor,
            JsonContext.SunderPackageProjectionDescriptor);

        var stagingPath = Path.Combine(
            Path.GetTempPath(),
            "Sunder.Projections",
            "write",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagingPath);
            await WriteFileAsync(
                stagingPath,
                SunderPackageProjectionFormat.DescriptorPath,
                descriptorBytes,
                cancellationToken);
            await WriteFileAsync(
                stagingPath,
                SunderPackageProjectionFormat.ManifestPath,
                manifestBytes,
                cancellationToken);
            await WriteFileAsync(
                stagingPath,
                SunderPackageProjectionFormat.ContentIndexPath,
                contentIndexBytes,
                cancellationToken);

            foreach (var entry in selectedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CopyIndexedPayloadAsync(sourceRoot, stagingPath, entry, cancellationToken);
            }

            await SunderArchive.WriteDeterministicAsync(stagingPath, projectionArchivePath, options);
            return descriptor;
        }
        finally
        {
            SunderArchiveFileSystem.TryDeleteDirectory(stagingPath);
        }
    }

    private static IReadOnlyList<SunderPackageContentIndexEntry> SelectEntries(
        SunderPackageContentIndex contentIndex,
        SunderPackageProjectionKey key)
    {
        var selected = new List<SunderPackageContentIndexEntry>();
        foreach (var entry in contentIndex.Files ?? [])
        {
            if (entry is null
                || !ArchiveRelativePath.TryParse(
                    entry.Path,
                    SunderPackageFormat.MaxArchivePathLength,
                    SunderPackageFormat.MaxArchivePathDepth,
                    out var path,
                    out _))
            {
                throw new InvalidDataException("Validated package content index contains an invalid entry.");
            }
            if (SunderPackageProjectionFormat.TryMapPayloadPath(key, path, out _, out _))
            {
                selected.Add(entry);
            }
        }

        return selected
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static void VerifyManifestIndexEntry(
        SunderPackageContentIndex contentIndex,
        long manifestSize,
        string manifestSha256)
    {
        var entries = (contentIndex.Files ?? [])
            .Where(static entry => string.Equals(
                entry?.Path,
                SunderPackageProjectionFormat.ManifestPath,
                StringComparison.Ordinal))
            .ToArray();
        if (entries.Length != 1
            || entries[0]!.Size != manifestSize
            || !string.Equals(entries[0]!.Sha256, manifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Canonical package manifest bytes do not match the validated source content index.");
        }
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        int maximumBytes,
        string archivePath,
        CancellationToken cancellationToken)
    {
        SunderArchiveFileSystem.EnsureNotLink(path);
        var info = new FileInfo(path);
        if (info.Length > maximumBytes)
        {
            throw new InvalidDataException($"Metadata file '{archivePath}' exceeds the {maximumBytes}-byte limit.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        SunderArchiveFileSystem.EnsureNotLink(path);
        if (bytes.LongLength != info.Length)
        {
            throw new InvalidDataException($"Metadata file '{archivePath}' changed while the projection was written.");
        }
        return bytes;
    }

    private static async Task CopyIndexedPayloadAsync(
        string sourceRoot,
        string destinationRoot,
        SunderPackageContentIndexEntry entry,
        CancellationToken cancellationToken)
    {
        var path = ArchiveRelativePath.Parse(
            entry.Path!,
            SunderPackageFormat.MaxArchivePathLength,
            SunderPackageFormat.MaxArchivePathDepth);
        var sourcePath = SunderArchive.ResolveFile(sourceRoot, path);
        if (!File.Exists(sourcePath))
        {
            throw new InvalidDataException($"Projection source payload '{path}' is missing.");
        }
        SunderArchiveFileSystem.EnsureNotLink(sourcePath);

        var destinationPath = path.ToPlatformPath(destinationRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferSize];
        long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            copied += read;
            if (copied > entry.Size)
            {
                throw new InvalidDataException($"Projection source payload '{path}' changed size while it was copied.");
            }
            hash.AppendData(buffer.AsSpan(0, read));
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (copied != entry.Size || !string.Equals(actualHash, entry.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Projection source payload '{path}' no longer matches the validated content index.");
        }
        SunderArchiveFileSystem.EnsureNotLink(sourcePath);
    }

    private static async Task WriteFileAsync(
        string rootPath,
        string archivePath,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var path = ArchiveRelativePath.Parse(archivePath).ToPlatformPath(rootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
    }

    private static bool CanonicalManifestEquals(
        SunderPackageManifest left,
        SunderPackageManifest right)
        => JsonSerializer.SerializeToUtf8Bytes(left, JsonContext.SunderPackageManifest)
            .AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right, JsonContext.SunderPackageManifest));

    private static bool CanonicalContentIndexEquals(
        SunderPackageContentIndex left,
        SunderPackageContentIndex right)
        => JsonSerializer.SerializeToUtf8Bytes(left, JsonContext.SunderPackageContentIndex)
            .AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right, JsonContext.SunderPackageContentIndex));

    private static string Hash(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
