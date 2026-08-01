using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Format;

public static class SunderPackageProjectionArchiveInspector
{
    private const int CopyBufferSize = 81920;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly SunderPackageJsonContext JsonContext = new(JsonOptions);
    private static readonly ArchiveRelativePath DescriptorPath = ArchiveRelativePath.Parse(SunderPackageProjectionFormat.DescriptorPath);
    private static readonly ArchiveRelativePath ManifestPath = ArchiveRelativePath.Parse(SunderPackageProjectionFormat.ManifestPath);
    private static readonly ArchiveRelativePath ContentIndexPath = ArchiveRelativePath.Parse(SunderPackageProjectionFormat.ContentIndexPath);
    private static readonly HashSet<string> MetadataPaths = new(StringComparer.Ordinal)
    {
        SunderPackageProjectionFormat.DescriptorPath,
        SunderPackageProjectionFormat.ManifestPath,
        SunderPackageProjectionFormat.ContentIndexPath,
    };

    public static Task<SunderPackageProjectionArchiveValidationResult> ExtractAndValidateAsync(
        string projectionArchivePath,
        string stagingPath,
        CancellationToken cancellationToken = default)
        => ExtractAndValidateAsync(
            projectionArchivePath,
            stagingPath,
            SunderArchiveExtractionOptions.Default with { CancellationToken = cancellationToken });

    public static async Task<SunderPackageProjectionArchiveValidationResult> ExtractAndValidateAsync(
        string projectionArchivePath,
        string stagingPath,
        SunderArchiveExtractionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        await SunderArchive.ExtractAtomicAsync(projectionArchivePath, stagingPath, options);
        return await ValidateExtractedProjectionAsync(
            stagingPath,
            options.MaxMetadataJsonBytes,
            options.CancellationToken);
    }

    public static void ExtractArchive(string projectionArchivePath, string stagingPath)
        => SunderArchive.ExtractAtomic(projectionArchivePath, stagingPath);

    public static Task<SunderPackageProjectionArchiveValidationResult> ValidateExtractedProjectionAsync(
        string stagingPath,
        CancellationToken cancellationToken = default)
        => ValidateExtractedProjectionAsync(
            stagingPath,
            SunderArchiveExtractionOptions.Default.MaxMetadataJsonBytes,
            cancellationToken);

    public static async Task<SunderPackageProjectionArchiveValidationResult> ValidateExtractedProjectionAsync(
        string stagingPath,
        int maxMetadataJsonBytes,
        CancellationToken cancellationToken = default)
    {
        if (maxMetadataJsonBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMetadataJsonBytes));
        }

        var warnings = new List<string>();
        var errors = new List<string>();
        var metadataFiles = new Dictionary<string, MetadataFile>(StringComparer.Ordinal);
        foreach (var path in new[] { DescriptorPath, ManifestPath, ContentIndexPath })
        {
            var filePath = SunderArchive.ResolveFile(stagingPath, path);
            if (!File.Exists(filePath))
            {
                errors.Add($"Projection archive is missing {path}.");
            }
        }
        if (errors.Count > 0)
        {
            return new SunderPackageProjectionArchiveValidationResult(null, null, null, warnings, errors);
        }

        SunderPackageProjectionDescriptor? descriptor;
        SunderPackageManifest? manifest;
        SunderPackageContentIndex? contentIndex;
        try
        {
            metadataFiles[SunderPackageProjectionFormat.DescriptorPath] = await ReadMetadataAsync(
                stagingPath, DescriptorPath, maxMetadataJsonBytes, cancellationToken);
            metadataFiles[SunderPackageProjectionFormat.ManifestPath] = await ReadMetadataAsync(
                stagingPath, ManifestPath, maxMetadataJsonBytes, cancellationToken);
            metadataFiles[SunderPackageProjectionFormat.ContentIndexPath] = await ReadMetadataAsync(
                stagingPath, ContentIndexPath, maxMetadataJsonBytes, cancellationToken);

            descriptor = StrictJsonSerializer.Deserialize(
                metadataFiles[SunderPackageProjectionFormat.DescriptorPath].Json,
                JsonContext.SunderPackageProjectionDescriptor);
            manifest = StrictJsonSerializer.Deserialize(
                metadataFiles[SunderPackageProjectionFormat.ManifestPath].Json,
                JsonContext.SunderPackageManifest);
            contentIndex = StrictJsonSerializer.Deserialize(
                metadataFiles[SunderPackageProjectionFormat.ContentIndexPath].Json,
                JsonContext.SunderPackageContentIndex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
                                               or InvalidDataException
                                               or IOException
                                               or UnauthorizedAccessException
                                               or DecoderFallbackException)
        {
            errors.Add($"Failed to parse projection metadata: {exception.Message}");
            return new SunderPackageProjectionArchiveValidationResult(null, null, null, warnings, errors);
        }

        var key = ValidateDescriptor(descriptor, errors);
        ValidateManifest(manifest, descriptor, key, stagingPath, errors);
        ValidateManifestHash(
            descriptor,
            metadataFiles[SunderPackageProjectionFormat.ManifestPath].Bytes,
            errors);
        ValidateProjectionIdentity(
            descriptor,
            metadataFiles[SunderPackageProjectionFormat.ContentIndexPath].Bytes,
            errors);
        var payloadPaths = await ValidateContentAsync(
            contentIndex,
            key,
            stagingPath,
            errors,
            cancellationToken);

        return new SunderPackageProjectionArchiveValidationResult(
            descriptor,
            manifest,
            contentIndex,
            warnings,
            errors)
        {
            ExtractedProjection = errors.Count == 0 && key is { } validKey
                ? new SunderPackageExtractedProjection(
                    Path.GetFullPath(stagingPath),
                    validKey,
                    payloadPaths)
                : null,
        };
    }

    private static SunderPackageProjectionKey? ValidateDescriptor(
        SunderPackageProjectionDescriptor? descriptor,
        ICollection<string> errors)
    {
        if (descriptor is null)
        {
            errors.Add("Projection descriptor is empty or invalid.");
            return null;
        }
        if (descriptor.ProjectionFormatVersion != SunderPackageProjectionFormat.CurrentProjectionFormatVersion)
        {
            errors.Add($"Projection descriptor must declare projectionFormatVersion {SunderPackageProjectionFormat.CurrentProjectionFormatVersion}.");
        }
        if (!PackageId.TryParse(descriptor.PackageId, out _))
        {
            errors.Add($"Projection packageId '{descriptor.PackageId}' must use lowercase dot-separated ASCII identifiers.");
        }
        if (!SemanticVersion.TryParse(descriptor.PackageVersion, out _))
        {
            errors.Add($"Projection packageVersion '{descriptor.PackageVersion}' must be strict SemVer 2.0.");
        }
        if (!PackageContentSignatureValidator.IsLowercaseSha256(descriptor.SourceArchiveSha256))
        {
            errors.Add("Projection sourceArchiveSha256 must be a lowercase SHA-256 hash.");
        }
        if (!PackageContentSignatureValidator.IsLowercaseSha256(descriptor.ManifestSha256))
        {
            errors.Add("Projection manifestSha256 must be a lowercase SHA-256 hash.");
        }
        if (!PackageContentSignatureValidator.IsLowercaseSha256(descriptor.ProjectionSha256))
        {
            errors.Add("Projection projectionSha256 must be a lowercase SHA-256 hash.");
        }
        if (!SunderPackageProjectionKey.TryCreate(descriptor.Kind, descriptor.Rid, out var key))
        {
            errors.Add(
                "Projection kind/RID must be 'shared' with a null RID, or 'app'/'runtime' with an exact supported RID.");
            return null;
        }

        return key;
    }

    private static void ValidateManifest(
        SunderPackageManifest? manifest,
        SunderPackageProjectionDescriptor? descriptor,
        SunderPackageProjectionKey? key,
        string stagingPath,
        ICollection<string> errors)
    {
        if (manifest is null)
        {
            errors.Add("Projection package manifest is empty or invalid.");
            return;
        }
        if (manifest.ArchiveFormatVersion != SunderPackageFormat.CurrentArchiveFormatVersion)
        {
            errors.Add($"Projection package manifest must declare archiveFormatVersion {SunderPackageFormat.CurrentArchiveFormatVersion}.");
        }
        if (manifest.ManifestVersion != SunderPackageFormat.CurrentManifestVersion)
        {
            errors.Add($"Projection package manifest must declare manifestVersion {SunderPackageFormat.CurrentManifestVersion}.");
        }
        if (!PackageId.TryParse(manifest.Id, out _))
        {
            errors.Add($"Projection package id '{manifest.Id}' must use lowercase dot-separated ASCII identifiers.");
        }
        if (!SemanticVersion.TryParse(manifest.Version, out _))
        {
            errors.Add($"Projection package version '{manifest.Version}' must be strict SemVer 2.0.");
        }
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 256)
        {
            errors.Add("Projection package manifest name must be non-empty and at most 256 characters.");
        }
        if (manifest.Summary is not null
            && (string.IsNullOrWhiteSpace(manifest.Summary) || manifest.Summary.Length > 2048))
        {
            errors.Add("Projection package manifest summary must be non-empty and at most 2048 characters when declared.");
        }
        if (descriptor is not null)
        {
            if (!string.Equals(descriptor.PackageId, manifest.Id, StringComparison.Ordinal))
            {
                errors.Add("Projection descriptor packageId does not match the package manifest id.");
            }
            if (!string.Equals(descriptor.PackageVersion, manifest.Version, StringComparison.Ordinal))
            {
                errors.Add("Projection descriptor packageVersion does not match the package manifest version.");
            }
        }

        PackageDependencyValidator.Validate(manifest.DependsOn, errors);
        if (manifest.Targets is null)
        {
            errors.Add("Projection package manifest is missing targets.");
        }
        else if (manifest.Targets.Count == 0
                 && !(manifest.ContractBundles?.Any(static bundle => bundle is not null) ?? false))
        {
            errors.Add("Projection package manifest must declare at least one target or contract bundle.");
        }

        try
        {
            SunderPackageTargetResolver.EnumerateTargets(manifest);
        }
        catch (InvalidDataException exception)
        {
            errors.Add($"Projection package manifest has invalid target declarations: {exception.Message}");
        }
        foreach (var target in (manifest.Targets ?? []).Take(SunderPackageFormat.MaxTargets))
        {
            if (target is not null)
            {
                ValidateTargetMetadata(target, errors);
            }
        }
        PackageWebViewValidator.ValidateDeclarations(manifest, errors, "Projection package");

        if (key is not { } validKey)
        {
            return;
        }
        if (validKey.TryGetTargetKey(out var targetKey))
        {
            try
            {
                if (!SunderPackageTargetResolver.TryResolveTarget(manifest, targetKey, out _))
                {
                    errors.Add($"Projection package manifest does not declare exact target '{targetKey}'.");
                }
            }
            catch (InvalidDataException exception)
            {
                errors.Add($"Projection package manifest has an ambiguous exact target '{targetKey}': {exception.Message}");
            }
        }

        if (validKey.Kind == SunderPackageProjectionFormat.SharedKind)
        {
            var physicalFiles = SunderArchive.EnumerateFiles(stagingPath).ToDictionary(
                static file => file.Path.ToString(),
                static file => file.FullPath,
                StringComparer.Ordinal);
            PackageAssetValidator.ValidateIcon(manifest, physicalFiles, errors);
            PackageContractValidator.Validate(manifest, physicalFiles, errors);
        }
    }

    private static void ValidateTargetMetadata(
        SunderPackageTargetManifest target,
        ICollection<string> errors)
    {
        var displayKey = $"{target.Role ?? "unknown"}/{target.Rid ?? "unknown"}";
        if (!SunderPackageFormat.IsTargetKind(target.Role, target.Kind))
        {
            errors.Add($"Projection package target '{displayKey}' has invalid kind '{target.Kind}'.");
        }

        if (PackageArchivePathValidator.TryParse(
                target.EntryPoint,
                $"projection target '{displayKey}' entryPoint",
                errors,
                out _,
                required: true))
        {
            var expectedExtension = target.Kind switch
            {
                SunderPackageFormat.AvaloniaTargetKind or SunderPackageFormat.DotnetTargetKind => ".dll",
                SunderPackageFormat.WebTargetKind => ".html",
                _ => null,
            };
            if (expectedExtension is not null
                && !target.EntryPoint!.EndsWith(expectedExtension, StringComparison.Ordinal))
            {
                errors.Add($"Projection package target '{displayKey}' entryPoint must end with '{expectedExtension}'.");
            }
        }

        if (target.TargetFramework is not null
            && (target.TargetFramework.Length == 0
                || target.TargetFramework.Length > 128
                || target.TargetFramework != target.TargetFramework.Trim()
                || target.TargetFramework.Any(static character => character is < '!' or > '~')))
        {
            errors.Add($"Projection package target '{displayKey}' targetFramework is invalid.");
        }
        if (target.SdkVersion is not null && !SemanticVersion.TryParse(target.SdkVersion, out _))
        {
            errors.Add($"Projection package target '{displayKey}' sdkVersion must be strict SemVer 2.0.");
        }
        if (target.RequiredHostCapabilities is null)
        {
            errors.Add($"Projection package target '{displayKey}' is missing requiredHostCapabilities.");
            return;
        }
        if (target.RequiredHostCapabilities.Count > SunderPackageFormat.MaxHostCapabilities)
        {
            errors.Add($"Projection package target '{displayKey}' declares too many requiredHostCapabilities.");
        }

        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in target.RequiredHostCapabilities.Take(SunderPackageFormat.MaxHostCapabilities))
        {
            if (!SunderPackageFormat.IsHostCapabilityId(capability))
            {
                errors.Add($"Projection package target '{displayKey}' declares invalid host capability '{capability}'.");
            }
            else if (!capabilities.Add(capability!))
            {
                errors.Add($"Projection package target '{displayKey}' declares host capability '{capability}' more than once.");
            }
        }
    }

    private static void ValidateManifestHash(
        SunderPackageProjectionDescriptor? descriptor,
        byte[] manifestBytes,
        ICollection<string> errors)
    {
        if (descriptor is null || !PackageContentSignatureValidator.IsLowercaseSha256(descriptor.ManifestSha256))
        {
            return;
        }

        var actualHash = Hash(manifestBytes);
        if (!string.Equals(actualHash, descriptor.ManifestSha256, StringComparison.Ordinal))
        {
            errors.Add("Projection package manifest SHA-256 does not match manifestSha256.");
        }
    }

    private static void ValidateProjectionIdentity(
        SunderPackageProjectionDescriptor? descriptor,
        byte[] contentIndexBytes,
        ICollection<string> errors)
    {
        if (descriptor?.PackageId is null
            || descriptor.PackageVersion is null
            || descriptor.SourceArchiveSha256 is null
            || descriptor.Kind is null
            || descriptor.ManifestSha256 is null
            || descriptor.ProjectionSha256 is null)
        {
            return;
        }

        var actualHash = SunderPackageProjectionIdentity.Compute(
            descriptor.ProjectionFormatVersion,
            descriptor.PackageId,
            descriptor.PackageVersion,
            descriptor.SourceArchiveSha256,
            descriptor.Kind,
            descriptor.Rid,
            descriptor.ManifestSha256,
            contentIndexBytes);
        if (!string.Equals(actualHash, descriptor.ProjectionSha256, StringComparison.Ordinal))
        {
            errors.Add("Projection content identity does not match projectionSha256.");
        }
    }

    private static async Task<IReadOnlyList<ArchiveRelativePath>> ValidateContentAsync(
        SunderPackageContentIndex? contentIndex,
        SunderPackageProjectionKey? key,
        string stagingPath,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<(ArchiveRelativePath Path, string FullPath)> actualFiles;
        IReadOnlyList<ArchiveRelativePath> actualDirectories;
        try
        {
            actualFiles = SunderArchive.EnumerateFiles(stagingPath);
            actualDirectories = SunderArchiveFileSystem.EnumerateDirectories(stagingPath);
            var registry = new ArchivePathRegistry();
            foreach (var file in actualFiles)
            {
                if (!ArchiveRelativePath.TryParse(
                        file.Path.ToString(),
                        SunderPackageFormat.MaxArchivePathLength,
                        SunderPackageFormat.MaxArchivePathDepth,
                        out _,
                        out var error))
                {
                    errors.Add($"Projection file path '{file.Path}' is unsafe: {error}.");
                }
                registry.Register(file.Path, isDirectory: false);
            }
            foreach (var directory in actualDirectories)
            {
                if (!ArchiveRelativePath.TryParse(
                        directory.ToString(),
                        SunderPackageFormat.MaxArchivePathLength,
                        SunderPackageFormat.MaxArchivePathDepth,
                        out _,
                        out var error))
                {
                    errors.Add($"Projection directory path '{directory}' is unsafe: {error}.");
                }
                registry.Register(directory, isDirectory: true);
            }
        }
        catch (InvalidDataException exception)
        {
            errors.Add($"Projection staging paths are unsafe: {exception.Message}");
            return [];
        }

        var actualByPath = actualFiles.ToDictionary(
            static file => file.Path.ToString(),
            static file => file.FullPath,
            StringComparer.Ordinal);
        var actualContentIsBounded = ValidateActualBounds(actualFiles, errors);
        var indexedPaths = new HashSet<string>(StringComparer.Ordinal);
        if (contentIndex is null)
        {
            errors.Add("Projection content index is empty or invalid.");
        }
        else if (contentIndex.Files is null)
        {
            errors.Add("Projection content index is missing files.");
        }
        else
        {
            if (contentIndex.SchemaVersion != SunderPackageProjectionFormat.CurrentContentIndexVersion)
            {
                errors.Add($"Projection content index must declare schemaVersion {SunderPackageProjectionFormat.CurrentContentIndexVersion}.");
            }
            if (contentIndex.Files.Count > SunderPackageFormat.MaxContentIndexEntries)
            {
                errors.Add($"Projection content index contains too many files; the limit is {SunderPackageFormat.MaxContentIndexEntries}.");
            }

            string? previousPath = null;
            long declaredBytes = 0;
            var indexedRegistry = new ArchivePathRegistry();
            foreach (var entry in contentIndex.Files.Take(SunderPackageFormat.MaxContentIndexEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is null)
                {
                    errors.Add("Projection content index contains a null file entry.");
                    continue;
                }
                if (!PackageArchivePathValidator.TryParse(
                        entry.Path,
                        "projection content-index path",
                        errors,
                        out var path,
                        required: true,
                        maxLength: SunderPackageFormat.MaxArchivePathLength,
                        maxDepth: SunderPackageFormat.MaxArchivePathDepth))
                {
                    continue;
                }

                var pathText = path.ToString();
                if (previousPath is not null
                    && string.CompareOrdinal(previousPath, pathText) >= 0)
                {
                    errors.Add("Projection content index files must be strictly ordered by ordinal path.");
                }
                previousPath = pathText;
                if (!indexedPaths.Add(pathText))
                {
                    errors.Add($"Projection content index contains duplicate path '{pathText}'.");
                    continue;
                }
                try
                {
                    indexedRegistry.Register(path, isDirectory: false);
                }
                catch (InvalidDataException exception)
                {
                    errors.Add($"Projection content index path '{pathText}' is not portable: {exception.Message}");
                }

                if (key is not { } validKey
                    || !SunderPackageProjectionFormat.TryMapPayloadPath(validKey, path, out _, out _))
                {
                    errors.Add($"Projection content index path '{pathText}' does not belong to the declared projection.");
                }
                if (entry.Size < 0)
                {
                    errors.Add($"Projection content index path '{pathText}' has invalid size {entry.Size}.");
                }
                else if (entry.Size > SunderArchiveExtractionOptions.Default.MaxEntryUncompressedBytes)
                {
                    errors.Add($"Projection content index path '{pathText}' exceeds the per-entry size limit.");
                }
                else if (entry.Size > SunderArchiveExtractionOptions.Default.MaxTotalUncompressedBytes - declaredBytes)
                {
                    errors.Add("Projection content index exceeds the total uncompressed size limit.");
                }
                else
                {
                    declaredBytes += entry.Size;
                }
                if (!PackageContentSignatureValidator.IsLowercaseSha256(entry.Sha256))
                {
                    errors.Add($"Projection content index path '{pathText}' must declare a lowercase SHA-256 hash.");
                }

                actualByPath.TryGetValue(pathText, out var filePath);
                await ValidateIndexedFileAsync(
                    entry,
                    pathText,
                    filePath,
                    actualContentIsBounded,
                    errors,
                    cancellationToken);
            }
        }

        var payloadPaths = new List<ArchiveRelativePath>();
        foreach (var actualFile in actualFiles)
        {
            var pathText = actualFile.Path.ToString();
            if (MetadataPaths.Contains(pathText))
            {
                continue;
            }
            if (key is not { } validKey
                || !SunderPackageProjectionFormat.TryMapPayloadPath(validKey, actualFile.Path, out _, out _))
            {
                errors.Add($"Projection archive contains file outside its declared payload layers: '{pathText}'.");
                continue;
            }

            payloadPaths.Add(actualFile.Path);
            if (!indexedPaths.Contains(pathText))
            {
                errors.Add($"Projection archive contains unindexed payload file '{pathText}'.");
            }
        }

        foreach (var directory in actualDirectories)
        {
            if (key is not { } validKey
                || !SunderPackageProjectionFormat.IsAllowedDirectoryPath(validKey, directory.ToString()))
            {
                errors.Add($"Projection archive contains directory outside canonical roots: '{directory}'.");
            }
        }
        if (key is { } projectionKey && projectionKey.TryGetTargetKey(out var targetKey))
        {
            try
            {
                SunderPackageTargetResolver.CreateProjectionPlan(targetKey, payloadPaths);
            }
            catch (InvalidDataException exception)
            {
                errors.Add($"Projection payload has an invalid target union: {exception.Message}");
            }
        }

        return payloadPaths
            .OrderBy(static path => path.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    private static bool ValidateActualBounds(
        IReadOnlyList<(ArchiveRelativePath Path, string FullPath)> actualFiles,
        ICollection<string> errors)
    {
        var options = SunderArchiveExtractionOptions.Default;
        var bounded = true;
        if (actualFiles.Count > options.MaxEntries)
        {
            errors.Add($"Projection staging directory contains too many files; the limit is {options.MaxEntries}.");
            bounded = false;
        }

        long totalBytes = 0;
        foreach (var file in actualFiles)
        {
            var length = new FileInfo(file.FullPath).Length;
            if (length > options.MaxEntryUncompressedBytes)
            {
                errors.Add($"Projection file '{file.Path}' exceeds the per-entry size limit.");
                bounded = false;
            }
            if (length > options.MaxTotalUncompressedBytes - totalBytes)
            {
                errors.Add("Projection staging directory exceeds the total uncompressed size limit.");
                bounded = false;
            }
            else
            {
                totalBytes += length;
            }
        }
        return bounded;
    }

    private static async Task ValidateIndexedFileAsync(
        SunderPackageContentIndexEntry entry,
        string path,
        string? filePath,
        bool actualContentIsBounded,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        if (filePath is null)
        {
            errors.Add($"Projection content index references missing payload file '{path}'.");
            return;
        }

        var info = new FileInfo(filePath);
        if (info.Length != entry.Size)
        {
            errors.Add($"Projection payload file '{path}' size mismatch.");
        }
        if (!actualContentIsBounded
            || entry.Size < 0
            || !PackageContentSignatureValidator.IsLowercaseSha256(entry.Sha256))
        {
            return;
        }

        await using var stream = File.OpenRead(filePath);
        var actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!string.Equals(actualHash, entry.Sha256, StringComparison.Ordinal))
        {
            errors.Add($"Projection payload file '{path}' SHA-256 mismatch.");
        }
    }

    private static async Task<MetadataFile> ReadMetadataAsync(
        string rootPath,
        ArchiveRelativePath path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var filePath = SunderArchive.ResolveFile(rootPath, path);
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException($"Metadata file '{path}' exceeds the {maximumBytes}-byte limit.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return new MetadataFile(bytes, StrictUtf8.GetString(bytes));
    }

    private static string Hash(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record MetadataFile(byte[] Bytes, string Json);
}
