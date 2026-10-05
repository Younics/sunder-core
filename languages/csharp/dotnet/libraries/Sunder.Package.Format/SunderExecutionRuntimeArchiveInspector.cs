using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Format;

public static class SunderExecutionRuntimeArchiveInspector
{
    public static Task<SunderExecutionRuntimeArchiveValidationResult> InspectAsync(
        string runtimePath,
        string validatedArchivePath,
        string stagingPath,
        ISunderExecutionRuntimeArchiveSignatureVerifier verifier,
        SunderExecutionRuntimeTrustedKey trustedKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(trustedKey);
        return InspectCoreAsync(
            runtimePath,
            validatedArchivePath,
            stagingPath,
            verifier,
            trustedKey,
            nativeAttestationVerifier: null,
            trustedNativeAttestationKey: null,
            observer: null,
            cancellationToken);
    }

    internal static Task<SunderExecutionRuntimeArchiveValidationResult> InspectAsync(
        string runtimePath,
        string validatedArchivePath,
        string stagingPath,
        ISunderExecutionRuntimeArchiveSignatureVerifier verifier,
        SunderExecutionRuntimeTrustedKey trustedKey,
        ISunderExecutionRuntimeArchiveSignatureVerifier nativeAttestationVerifier,
        SunderExecutionRuntimeTrustedKey trustedNativeAttestationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(trustedKey);
        ArgumentNullException.ThrowIfNull(nativeAttestationVerifier);
        ArgumentNullException.ThrowIfNull(trustedNativeAttestationKey);
        return InspectCoreAsync(
            runtimePath,
            validatedArchivePath,
            stagingPath,
            verifier,
            trustedKey,
            nativeAttestationVerifier,
            trustedNativeAttestationKey,
            observer: null,
            cancellationToken);
    }

    internal static Task<SunderExecutionRuntimeArchiveValidationResult> InspectAsync(
        string runtimePath,
        string validatedArchivePath,
        string stagingPath,
        ISunderExecutionRuntimeArchiveSignatureVerifier verifier,
        SunderExecutionRuntimeTrustedKey trustedKey,
        ISunderExecutionRuntimeArchiveInspectionObserver observer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(trustedKey);
        ArgumentNullException.ThrowIfNull(observer);
        return InspectCoreAsync(
            runtimePath,
            validatedArchivePath,
            stagingPath,
            verifier,
            trustedKey,
            nativeAttestationVerifier: null,
            trustedNativeAttestationKey: null,
            observer,
            cancellationToken);
    }

    public static Task<SunderExecutionRuntimeArchiveValidationResult> ExtractAndValidateAsync(
        string runtimePath,
        string validatedArchivePath,
        string stagingPath,
        ISunderExecutionRuntimeArchiveSignatureVerifier verifier,
        SunderExecutionRuntimeTrustedKey trustedKey,
        CancellationToken cancellationToken = default)
        => InspectAsync(runtimePath, validatedArchivePath, stagingPath, verifier, trustedKey, cancellationToken);

    internal static async Task<(SunderExecutionRuntimeManifest Manifest, string ManifestSha256)>
        ReadManifestMetadataAsync(
            string runtimePath,
            CancellationToken cancellationToken = default)
    {
        if (!File.Exists(runtimePath))
        {
            throw new FileNotFoundException($"Execution runtime archive '{runtimePath}' was not found.", runtimePath);
        }
        if (new FileInfo(runtimePath).Length is <= 0 or > SunderExecutionRuntimeFormat.MaxArchiveBytes)
        {
            throw new InvalidDataException(
                $"Execution runtime archive must contain between 1 and {SunderExecutionRuntimeFormat.MaxArchiveBytes} bytes.");
        }

        _ = ValidateArchiveEntryInventory(runtimePath);
        using var archive = ZipFile.OpenRead(runtimePath);
        var entry = archive.Entries.SingleOrDefault(static candidate =>
            string.Equals(candidate.FullName, SunderExecutionRuntimeFormat.ManifestPath, StringComparison.Ordinal));
        if (entry is null)
        {
            throw new InvalidDataException(
                $"Execution runtime archive is missing '{SunderExecutionRuntimeFormat.ManifestPath}'.");
        }

        await using var source = entry.Open();
        using var destination = new MemoryStream(checked((int)entry.Length));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var remaining = SunderExecutionRuntimeFormat.MaxMetadataBytes - destination.Length;
            var read = await source.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining + 1)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (read > remaining)
            {
                throw new InvalidDataException(
                    $"Execution runtime manifest exceeds the {SunderExecutionRuntimeFormat.MaxMetadataBytes}-byte limit.");
            }
            destination.Write(buffer, 0, read);
        }

        var bytes = destination.ToArray();
        var manifest = SunderExecutionRuntimeCanonicalJson.DeserializeManifest(bytes)
                       ?? throw new InvalidDataException("Execution runtime manifest is empty or invalid.");
        if (!bytes.AsSpan().SequenceEqual(SunderExecutionRuntimeCanonicalJson.SerializeManifest(manifest)))
        {
            throw new InvalidDataException("Execution runtime manifest bytes are not canonical V1 JSON.");
        }
        var errors = new List<string>();
        ValidateManifest(manifest, errors);
        if (errors.Count != 0)
        {
            throw new InvalidDataException(string.Join(" | ", errors));
        }
        return (manifest, Hash(bytes));
    }

    private static async Task<SunderExecutionRuntimeArchiveValidationResult> InspectCoreAsync(
        string runtimePath,
        string validatedArchivePath,
        string stagingPath,
        ISunderExecutionRuntimeArchiveSignatureVerifier? verifier,
        SunderExecutionRuntimeTrustedKey? trustedKey,
        ISunderExecutionRuntimeArchiveSignatureVerifier? nativeAttestationVerifier,
        SunderExecutionRuntimeTrustedKey? trustedNativeAttestationKey,
        ISunderExecutionRuntimeArchiveInspectionObserver? observer,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(runtimePath))
        {
            throw new FileNotFoundException($"Execution runtime archive '{runtimePath}' was not found.", runtimePath);
        }

        var snapshot = await CreateArchiveSnapshotAsync(runtimePath, validatedArchivePath, cancellationToken);
        try
        {
            var archiveEntries = ValidateArchiveEntryInventory(snapshot.ArchivePath);
            observer?.ExtractionStarted();
            try
            {
                await SunderArchive.ExtractAtomicAsync(
                    snapshot.ArchivePath,
                    stagingPath,
                    SunderExecutionRuntimeFormat.CreateExtractionOptions(cancellationToken));
                observer?.ExtractionCompleted(succeeded: true, canceled: false);
            }
            catch (OperationCanceledException)
            {
                observer?.ExtractionCompleted(succeeded: false, canceled: true);
                throw;
            }
            catch
            {
                observer?.ExtractionCompleted(succeeded: false, canceled: false);
                throw;
            }
            var result = await ValidateExtractedAsync(
                stagingPath,
                snapshot.Sha256,
                snapshot.Size,
                snapshot.ArchivePath,
                archiveEntries,
                verifier,
                trustedKey,
                nativeAttestationVerifier,
                trustedNativeAttestationKey,
                cancellationToken);
            if (!result.Success)
            {
                SunderArchiveFileSystem.TryDeleteFile(snapshot.ArchivePath);
                return result with { ValidatedArchivePath = null };
            }
            return result;
        }
        catch
        {
            SunderArchiveFileSystem.TryDeleteFile(snapshot.ArchivePath);
            throw;
        }
    }

    internal static async Task<SunderExecutionRuntimeArchiveValidationResult> ValidateExtractedAsync(
        string stagingPath,
        string? archiveSha256,
        long archiveSize,
        string? validatedArchivePath,
        IReadOnlyDictionary<string, ArchiveEntryMetadata>? archiveEntries,
        ISunderExecutionRuntimeArchiveSignatureVerifier? verifier,
        SunderExecutionRuntimeTrustedKey? trustedKey,
        ISunderExecutionRuntimeArchiveSignatureVerifier? nativeAttestationVerifier,
        SunderExecutionRuntimeTrustedKey? trustedNativeAttestationKey,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var actualFiles = SunderArchiveFileSystem.EnumerateFiles(stagingPath);
        var actualFilesByPath = actualFiles.ToDictionary(
            static file => file.Path.ToString(),
            static file => file.FullPath,
            StringComparer.Ordinal);

        ValidateArchiveGrammar(stagingPath, actualFiles, errors);
        foreach (var requiredPath in new[]
                 {
                     SunderExecutionRuntimeFormat.ManifestPath,
                     SunderExecutionRuntimeFormat.ContentIndexPath,
                     SunderExecutionRuntimeFormat.SignaturePath,
                 })
        {
            if (!actualFilesByPath.ContainsKey(requiredPath))
            {
                errors.Add($"Execution runtime archive is missing '{requiredPath}'.");
            }
        }

        if (!actualFilesByPath.TryGetValue(SunderExecutionRuntimeFormat.ManifestPath, out var manifestPath)
            || !actualFilesByPath.TryGetValue(SunderExecutionRuntimeFormat.ContentIndexPath, out var contentIndexPath))
        {
            return CreateResult(null, errors, archiveSha256, archiveSize, validatedArchivePath, null, null, [], null, false, false);
        }

        byte[] manifestBytes;
        byte[] contentIndexBytes;
        try
        {
            manifestBytes = await ReadBoundedAsync(
                manifestPath,
                SunderExecutionRuntimeFormat.MaxMetadataBytes,
                "runtime manifest",
                cancellationToken);
            contentIndexBytes = await ReadBoundedAsync(
                contentIndexPath,
                SunderExecutionRuntimeFormat.MaxMetadataBytes,
                "runtime content index",
                cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            errors.Add(ex.Message);
            return CreateResult(null, errors, archiveSha256, archiveSize, validatedArchivePath, null, null, [], null, false, false);
        }

        var manifestSha256 = Hash(manifestBytes);
        SunderExecutionRuntimeManifest? manifest;
        SunderExecutionRuntimeContentIndex? contentIndex;
        try
        {
            manifest = SunderExecutionRuntimeCanonicalJson.DeserializeManifest(manifestBytes);
            contentIndex = SunderExecutionRuntimeCanonicalJson.DeserializeContentIndex(contentIndexBytes);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            errors.Add($"Failed to parse execution runtime metadata: {ex.Message}");
            return CreateResult(null, errors, archiveSha256, archiveSize, validatedArchivePath, manifestSha256, null, [], null, false, false);
        }

        if (manifest is null)
        {
            errors.Add("Execution runtime manifest is empty or invalid.");
        }
        else if (!manifestBytes.AsSpan().SequenceEqual(SunderExecutionRuntimeCanonicalJson.SerializeManifest(manifest)))
        {
            errors.Add("Execution runtime manifest bytes are not canonical V1 JSON.");
        }
        if (contentIndex is null)
        {
            errors.Add("Execution runtime content index is empty or invalid.");
        }
        else if (!contentIndexBytes.AsSpan().SequenceEqual(SunderExecutionRuntimeCanonicalJson.SerializeContentIndex(contentIndex)))
        {
            errors.Add("Execution runtime content-index bytes are not canonical V1 JSON.");
        }

        if (manifest is not null)
        {
            ValidateManifest(manifest, errors);
            ValidateAndApplyArchiveModes(manifest, actualFiles, archiveEntries, errors);
        }

        var inventory = await ValidateContentIndexAsync(
            contentIndex,
            actualFiles,
            actualFilesByPath,
            errors,
            cancellationToken);
        var launcherRecord = manifest?.Launch?.Executable is { } executable
            ? inventory.FirstOrDefault(record => string.Equals(record.Path, executable, StringComparison.Ordinal))
            : null;
        if (manifest?.Launch?.Executable is not null && launcherRecord is null)
        {
            errors.Add($"Execution runtime launcher '{manifest.Launch.Executable}' is not covered by the content index.");
        }
        else if (launcherRecord?.Size == 0)
        {
            errors.Add("Execution runtime launcher content record must have a positive size.");
        }

        var nativeAttestationSignatureVerified = false;
        SunderExecutionRuntimeNativeAttestation? nativeAttestation = null;
        if (manifest is not null)
        {
            var nativeAttestationValidation = await ValidateNativeAttestationAsync(
                manifest,
                inventory,
                actualFilesByPath,
                nativeAttestationVerifier,
                trustedNativeAttestationKey,
                errors,
                cancellationToken);
            nativeAttestationSignatureVerified = nativeAttestationValidation.SignatureVerified;
            nativeAttestation = nativeAttestationValidation.Attestation;
        }

        var signatureInput = SunderExecutionRuntimeSignatureInput.Create(manifestBytes, contentIndexBytes);
        var contentIdentity = Hash(signatureInput);
        var signatureVerified = false;
        if (actualFilesByPath.TryGetValue(SunderExecutionRuntimeFormat.SignaturePath, out var signaturePath))
        {
            byte[]? signature = null;
            try
            {
                signature = await ReadBoundedAsync(
                    signaturePath,
                    SunderExecutionRuntimeFormat.Ed25519SignatureBytes,
                    "runtime detached signature",
                    cancellationToken);
            }
            catch (InvalidDataException ex)
            {
                errors.Add(ex.Message);
            }
            if (signature is not null && signature.Length != SunderExecutionRuntimeFormat.Ed25519SignatureBytes)
            {
                errors.Add(
                    $"Execution runtime detached signature must contain exactly {SunderExecutionRuntimeFormat.Ed25519SignatureBytes} raw bytes.");
            }
            else if (signature is not null && verifier is not null && trustedKey is not null)
            {
                if (trustedKey.PublicKey.Length != SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes)
                {
                    errors.Add(
                        $"Trusted Ed25519 key '{trustedKey.KeyId}' must contain exactly {SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes} bytes.");
                }
                else if (!string.Equals(manifest?.Signing?.KeyId, trustedKey.KeyId, StringComparison.Ordinal))
                {
                    errors.Add($"Execution runtime signing key '{manifest?.Signing?.KeyId}' is not the caller-supplied trusted key.");
                }
                else
                {
                    try
                    {
                        signatureVerified = verifier.Verify(signatureInput, signature, trustedKey.PublicKey.Span);
                    }
                    catch (CryptographicException ex)
                    {
                        errors.Add($"Execution runtime signature verification failed: {ex.Message}");
                    }
                    if (!signatureVerified && !errors.Any(static error => error.StartsWith("Execution runtime signature verification failed:", StringComparison.Ordinal)))
                    {
                        errors.Add("Execution runtime detached signature does not verify with the caller-supplied trusted key.");
                    }
                }
            }
        }

        return CreateResult(
            manifest,
            errors,
            archiveSha256,
            archiveSize,
            validatedArchivePath,
            manifestSha256,
            contentIdentity,
            inventory,
            launcherRecord,
            signatureVerified,
            nativeAttestationSignatureVerified,
            nativeAttestation);
    }

    private static void ValidateArchiveGrammar(
        string stagingPath,
        IReadOnlyList<(ArchiveRelativePath Path, string FullPath)> actualFiles,
        ICollection<string> errors)
    {
        foreach (var file in actualFiles)
        {
            var path = file.Path.ToString();
            if (!SunderExecutionRuntimeFormat.IsMetadataPath(path)
                && !SunderExecutionRuntimeFormat.IsPayloadPath(path))
            {
                errors.Add($"Execution runtime archive contains regular file outside its closed roots: '{path}'.");
            }
        }
    }

    private static void ValidateManifest(SunderExecutionRuntimeManifest manifest, ICollection<string> errors)
    {
        if (manifest.SchemaVersion != SunderExecutionRuntimeFormat.CurrentSchemaVersion)
        {
            errors.Add($"Execution runtime manifest must declare schemaVersion {SunderExecutionRuntimeFormat.CurrentSchemaVersion}.");
        }
        if (!TryGetExpectedModuleAbi(manifest.Id, out var expectedModuleAbi))
        {
            errors.Add($"Execution runtime id '{manifest.Id}' is not a recognized Sunder-owned runtime.");
        }
        else if (!string.Equals(manifest.ModuleAbi, expectedModuleAbi, StringComparison.Ordinal))
        {
            errors.Add($"Execution runtime id '{manifest.Id}' does not pair with moduleAbi '{manifest.ModuleAbi}'.");
        }
        if (!IsStableSemanticVersion(manifest.Version))
        {
            errors.Add("Execution runtime version must be one exact stable strict SemVer 2.0 version.");
        }
        if (manifest.PackagingRevision is <= 0 or > SunderExecutionRuntimeFormat.MaxPackagingRevision)
        {
            errors.Add(
                $"Execution runtime packagingRevision must be between 1 and {SunderExecutionRuntimeFormat.MaxPackagingRevision}.");
        }
        if (!SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers.Contains(manifest.Rid, StringComparer.Ordinal))
        {
            errors.Add($"Execution runtime RID '{manifest.Rid}' is not supported.");
        }
        if (!string.Equals(manifest.WorkerProtocol, SunderExecutionRuntimeFormat.WorkerProtocol, StringComparison.Ordinal))
        {
            errors.Add($"Execution runtime workerProtocol must be '{SunderExecutionRuntimeFormat.WorkerProtocol}'.");
        }
        if (!string.Equals(manifest.ContentIndex, SunderExecutionRuntimeFormat.ContentIndexPath, StringComparison.Ordinal))
        {
            errors.Add($"Execution runtime contentIndex must be '{SunderExecutionRuntimeFormat.ContentIndexPath}'.");
        }

        ValidateLaunch(manifest.Launch, manifest.Id, manifest.Rid, errors);
        ValidateUpstream(manifest.Upstream, manifest.Version, errors);
        ValidateProvenance(manifest.Provenance, errors);
        ValidateLicenses(manifest.Licenses, errors);
        ValidateSigning(manifest.Signing, errors);
    }

    private static void ValidateLaunch(
        SunderExecutionRuntimeLaunch? launch,
        string? runtimeId,
        string? rid,
        ICollection<string> errors)
    {
        if (launch is null)
        {
            errors.Add("Execution runtime launch is required.");
            return;
        }
        if (!SunderExecutionRuntimeFormat.IsPayloadPath(launch.Executable ?? string.Empty))
        {
            errors.Add("Execution runtime launch.executable must be one safe relative regular file below payload/.");
        }
        else if (rid?.StartsWith("win-", StringComparison.Ordinal) == true
                 && !launch.Executable!.EndsWith(".exe", StringComparison.Ordinal))
        {
            errors.Add("Windows execution runtime launcher must use the .exe extension.");
        }
        else if (rid is not null
                 && !rid.StartsWith("win-", StringComparison.Ordinal)
                 && launch.Executable!.EndsWith(".exe", StringComparison.Ordinal))
        {
            errors.Add("Non-Windows execution runtime launcher must not use the .exe extension.");
        }

        if (launch.Arguments is null)
        {
            errors.Add("Execution runtime launch.arguments is required.");
            return;
        }
        if (launch.Arguments.Count > SunderExecutionRuntimeFormat.MaxLaunchArguments)
        {
            errors.Add($"Execution runtime launch recipe exceeds {SunderExecutionRuntimeFormat.MaxLaunchArguments} arguments.");
        }
        var totalLength = 0;
        foreach (var argument in launch.Arguments)
        {
            if (string.IsNullOrEmpty(argument))
            {
                errors.Add("Execution runtime launch recipe contains an empty argument token.");
                continue;
            }
            totalLength += argument.Length;
            if (argument.Length > SunderExecutionRuntimeFormat.MaxLaunchArgumentLength)
            {
                errors.Add($"Execution runtime launch argument exceeds {SunderExecutionRuntimeFormat.MaxLaunchArgumentLength} characters.");
            }
            if (argument.Any(static character => character is ';' or '&' or '|' or '<' or '>' or '`'))
            {
                errors.Add($"Execution runtime launch argument '{argument}' contains a shell metacharacter.");
            }
            if (argument.Contains('$', StringComparison.Ordinal) || argument.Contains('%', StringComparison.Ordinal))
            {
                errors.Add($"Execution runtime launch argument '{argument}' contains variable expansion syntax.");
            }
            if (argument.StartsWith("/", StringComparison.Ordinal)
                || argument.Contains('\\', StringComparison.Ordinal)
                || (argument.Length >= 2 && char.IsAsciiLetter(argument[0]) && argument[1] == ':')
                || argument.Split('/').Any(static segment => segment == ".."))
            {
                errors.Add($"Execution runtime launch argument '{argument}' contains an absolute or traversing path.");
            }
            if ((argument.Contains('{', StringComparison.Ordinal) || argument.Contains('}', StringComparison.Ordinal))
                && !string.Equals(argument, SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder, StringComparison.Ordinal))
            {
                errors.Add($"Execution runtime launch argument '{argument}' contains an unknown placeholder.");
            }
        }
        if (totalLength > SunderExecutionRuntimeFormat.MaxLaunchArgumentsTotalLength)
        {
            errors.Add(
                $"Execution runtime launch arguments exceed {SunderExecutionRuntimeFormat.MaxLaunchArgumentsTotalLength} total characters.");
        }
        var validRecipe = runtimeId switch
        {
            SunderExecutionRuntimeFormat.DotNetRuntimeId => launch.Arguments.Count == 2
                && string.Equals(launch.Arguments[0], SunderExecutionRuntimeFormat.DotNetExecArgument, StringComparison.Ordinal)
                && string.Equals(launch.Arguments[1], SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder, StringComparison.Ordinal),
            SunderExecutionRuntimeFormat.NodeRuntimeId => launch.Arguments.Count == 1
                && string.Equals(launch.Arguments[0], SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder, StringComparison.Ordinal),
            _ => false,
        };
        if (!validRecipe)
        {
            errors.Add("Execution runtime launch recipe does not match the closed profile for its runtime ID; package-controlled options are not allowed.");
        }
    }

    private static void ValidateUpstream(
        SunderExecutionRuntimeUpstream? upstream,
        string? runtimeVersion,
        ICollection<string> errors)
    {
        if (upstream is null)
        {
            errors.Add("Execution runtime upstream is required.");
            return;
        }
        if (!IsLowerIdentifier(upstream.Name, SunderExecutionRuntimeFormat.MaxUpstreamNameLength))
        {
            errors.Add("Execution runtime upstream.name must be a bounded lower-case identifier.");
        }
        if (!IsStableSemanticVersion(upstream.Version))
        {
            errors.Add("Execution runtime upstream.version must be one exact stable strict SemVer 2.0 version.");
        }
        else if (!string.Equals(upstream.Version, runtimeVersion, StringComparison.Ordinal))
        {
            errors.Add("Execution runtime upstream.version must exactly match the runtime manifest version.");
        }
        if (string.IsNullOrEmpty(upstream.SourceUri)
            || upstream.SourceUri.Length > SunderExecutionRuntimeFormat.MaxSourceUriLength
            || upstream.SourceUri.Any(static character => character is < '!' or > '~')
            || !Uri.TryCreate(upstream.SourceUri, UriKind.Absolute, out var sourceUri)
            || !string.Equals(sourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(sourceUri.UserInfo)
            || !string.IsNullOrEmpty(sourceUri.Fragment))
        {
            errors.Add("Execution runtime upstream.sourceUri must be a bounded absolute HTTPS URI without credentials or a fragment.");
        }
        if (!IsLowercaseSha256(upstream.SourceSha256))
        {
            errors.Add("Execution runtime upstream.sourceSha256 must be a lower-case SHA-256 digest.");
        }
    }

    private static void ValidateProvenance(SunderExecutionRuntimeProvenance? provenance, ICollection<string> errors)
    {
        if (provenance is null)
        {
            errors.Add("Execution runtime provenance is required.");
            return;
        }
        if (!string.Equals(
                provenance.AttestationPath,
                SunderExecutionRuntimeFormat.NativeAttestationPath,
                StringComparison.Ordinal))
        {
            errors.Add(
                $"Execution runtime provenance.attestationPath must be '{SunderExecutionRuntimeFormat.NativeAttestationPath}'.");
        }
        if (!IsLowercaseSha256(provenance.AttestationSha256))
        {
            errors.Add("Execution runtime provenance.attestationSha256 must be a lower-case SHA-256 digest.");
        }
        if (!IsLowerIdentifier(provenance.AttestationKeyId, SunderExecutionRuntimeFormat.MaxKeyIdLength))
        {
            errors.Add("Execution runtime provenance.attestationKeyId must be a bounded lower-case key identifier.");
        }
    }

    private static void ValidateLicenses(IReadOnlyList<string?>? licenses, ICollection<string> errors)
    {
        if (licenses is null || licenses.Count == 0)
        {
            errors.Add("Execution runtime licenses must contain at least one license identifier.");
            return;
        }
        if (licenses.Count > SunderExecutionRuntimeFormat.MaxLicenses)
        {
            errors.Add($"Execution runtime licenses exceed the {SunderExecutionRuntimeFormat.MaxLicenses}-entry limit.");
        }
        string? previous = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var license in licenses.Take(SunderExecutionRuntimeFormat.MaxLicenses))
        {
            if (string.IsNullOrEmpty(license)
                || license.Length > SunderExecutionRuntimeFormat.MaxLicenseLength
                || license.Any(static character => !char.IsAsciiLetterOrDigit(character)
                                                    && character is not '.' and not '-' and not '+'))
            {
                errors.Add("Execution runtime license identifiers must be bounded portable SPDX-style tokens.");
                continue;
            }
            if (!seen.Add(license))
            {
                errors.Add($"Execution runtime licenses contain duplicate identifier '{license}'.");
            }
            if (previous is not null && string.Compare(previous, license, StringComparison.Ordinal) >= 0)
            {
                errors.Add("Execution runtime licenses must be in unique ordinal order.");
            }
            previous = license;
        }
    }

    private static void ValidateSigning(SunderExecutionRuntimeSigning? signing, ICollection<string> errors)
    {
        if (signing is null)
        {
            errors.Add("Execution runtime signing is required.");
            return;
        }
        if (!string.Equals(signing.Algorithm, SunderExecutionRuntimeFormat.SigningAlgorithm, StringComparison.Ordinal))
        {
            errors.Add($"Execution runtime signing.algorithm must be '{SunderExecutionRuntimeFormat.SigningAlgorithm}'.");
        }
        if (!IsLowerIdentifier(signing.KeyId, SunderExecutionRuntimeFormat.MaxKeyIdLength))
        {
            errors.Add("Execution runtime signing.keyId must be a bounded lower-case key identifier.");
        }
        if (!string.Equals(signing.Signature, SunderExecutionRuntimeFormat.SignaturePath, StringComparison.Ordinal))
        {
            errors.Add($"Execution runtime signing.signature must be '{SunderExecutionRuntimeFormat.SignaturePath}'.");
        }
    }

    private static async Task<IReadOnlyList<SunderExecutionRuntimeContentRecord>> ValidateContentIndexAsync(
        SunderExecutionRuntimeContentIndex? contentIndex,
        IReadOnlyList<(ArchiveRelativePath Path, string FullPath)> actualFiles,
        IReadOnlyDictionary<string, string> actualFilesByPath,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        if (contentIndex is null)
        {
            return [];
        }
        if (contentIndex.SchemaVersion != SunderExecutionRuntimeFormat.CurrentContentIndexVersion)
        {
            errors.Add(
                $"Execution runtime content index must declare schemaVersion {SunderExecutionRuntimeFormat.CurrentContentIndexVersion}.");
        }
        if (contentIndex.Files is null)
        {
            errors.Add("Execution runtime content index is missing files.");
            return [];
        }
        if (contentIndex.Files.Count > SunderExecutionRuntimeFormat.MaxContentIndexEntries)
        {
            errors.Add(
                $"Execution runtime content index contains {contentIndex.Files.Count} files; the limit is {SunderExecutionRuntimeFormat.MaxContentIndexEntries}.");
        }

        var inventory = new List<SunderExecutionRuntimeContentRecord>();
        var indexedPaths = new HashSet<string>(StringComparer.Ordinal);
        var portablePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? previousPath = null;
        long declaredTotal = 0;
        foreach (var record in contentIndex.Files.Take(SunderExecutionRuntimeFormat.MaxContentIndexEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record is null)
            {
                errors.Add("Execution runtime content index contains a null content record.");
                continue;
            }
            inventory.Add(record);
            if (!ArchiveRelativePath.TryParse(
                    record.Path,
                    SunderExecutionRuntimeFormat.MaxArchivePathLength,
                    SunderExecutionRuntimeFormat.MaxArchivePathDepth,
                    out var parsedPath,
                    out var pathError))
            {
                errors.Add($"Execution runtime content-index path '{record.Path}' is unsafe: {pathError}.");
                continue;
            }
            var path = parsedPath.ToString();
            if (!SunderExecutionRuntimeFormat.IsPayloadPath(path))
            {
                errors.Add($"Execution runtime content-index path '{path}' must be below payload/.");
            }
            if (!indexedPaths.Add(path))
            {
                errors.Add($"Execution runtime content index contains duplicate path '{path}'.");
            }
            if (portablePaths.TryGetValue(path, out var existing))
            {
                errors.Add($"Execution runtime content-index paths '{existing}' and '{path}' differ only by case.");
            }
            else
            {
                portablePaths.Add(path, path);
            }
            if (previousPath is not null && string.Compare(previousPath, path, StringComparison.Ordinal) >= 0)
            {
                errors.Add("Execution runtime content records must be in unique ordinal path order.");
            }
            previousPath = path;

            var validSize = record.Size >= 0 && record.Size <= SunderExecutionRuntimeFormat.MaxEntryUncompressedBytes;
            if (!validSize)
            {
                errors.Add($"Execution runtime content-index path '{path}' has an invalid size.");
            }
            else if (record.Size > SunderExecutionRuntimeFormat.MaxTotalUncompressedBytes - declaredTotal)
            {
                errors.Add("Execution runtime content index exceeds the total uncompressed size limit.");
            }
            else
            {
                declaredTotal += record.Size;
            }
            var validHash = IsLowercaseSha256(record.Sha256);
            if (!validHash)
            {
                errors.Add($"Execution runtime content-index path '{path}' must declare a lower-case SHA-256 digest.");
            }
            if (!actualFilesByPath.TryGetValue(path, out var filePath))
            {
                errors.Add($"Execution runtime content index references missing regular file '{path}'.");
                continue;
            }
            var actualSize = new FileInfo(filePath).Length;
            if (actualSize != record.Size)
            {
                errors.Add($"Execution runtime file '{path}' size mismatch.");
            }
            if (validSize && validHash && actualSize <= SunderExecutionRuntimeFormat.MaxEntryUncompressedBytes)
            {
                var actualHash = await HashFileAsync(filePath, cancellationToken);
                if (!string.Equals(actualHash, record.Sha256, StringComparison.Ordinal))
                {
                    errors.Add($"Execution runtime file '{path}' SHA-256 mismatch.");
                }
            }
        }

        foreach (var actualFile in actualFiles)
        {
            var path = actualFile.Path.ToString();
            if (SunderExecutionRuntimeFormat.IsMetadataPath(path))
            {
                if (indexedPaths.Contains(path))
                {
                    errors.Add($"Execution runtime metadata file '{path}' must not be indexed.");
                }
                continue;
            }
            if (SunderExecutionRuntimeFormat.IsPayloadPath(path) && !indexedPaths.Contains(path))
            {
                errors.Add($"Execution runtime archive contains unindexed payload file '{path}'.");
            }
        }
        return Array.AsReadOnly(inventory.ToArray());
    }

    private static async Task<(bool SignatureVerified, SunderExecutionRuntimeNativeAttestation? Attestation)>
        ValidateNativeAttestationAsync(
        SunderExecutionRuntimeManifest manifest,
        IReadOnlyList<SunderExecutionRuntimeContentRecord> inventory,
        IReadOnlyDictionary<string, string> actualFilesByPath,
        ISunderExecutionRuntimeArchiveSignatureVerifier? verifier,
        SunderExecutionRuntimeTrustedKey? trustedKey,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        var provenance = manifest.Provenance;
        if (provenance is null)
        {
            return (false, null);
        }
        var record = inventory.FirstOrDefault(item => string.Equals(
            item.Path,
            SunderExecutionRuntimeFormat.NativeAttestationPath,
            StringComparison.Ordinal));
        if (record is null)
        {
            errors.Add("Execution runtime native-build attestation is not covered by the content index.");
            return (false, null);
        }
        if (!string.Equals(record.Sha256, provenance.AttestationSha256, StringComparison.Ordinal))
        {
            errors.Add("Execution runtime provenance attestation digest does not match its content record.");
        }
        if (!actualFilesByPath.TryGetValue(SunderExecutionRuntimeFormat.NativeAttestationPath, out var path))
        {
            return (false, null);
        }

        byte[] bytes;
        try
        {
            bytes = await ReadBoundedAsync(
                path,
                SunderExecutionRuntimeFormat.MaxMetadataBytes,
                "native-build attestation",
                cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            errors.Add(ex.Message);
            return (false, null);
        }

        SunderExecutionRuntimeNativeAttestation? attestation;
        try
        {
            attestation = SunderExecutionRuntimeCanonicalJson.DeserializeNativeAttestation(bytes);
        }
        catch (JsonException ex)
        {
            errors.Add($"Failed to parse execution runtime native-build attestation: {ex.Message}");
            return (false, null);
        }
        if (attestation is null)
        {
            errors.Add("Execution runtime native-build attestation is empty or invalid.");
            return (false, null);
        }
        if (!bytes.AsSpan().SequenceEqual(SunderExecutionRuntimeCanonicalJson.SerializeNativeAttestation(attestation)))
        {
            errors.Add("Execution runtime native-build attestation bytes are not canonical V1 JSON.");
        }
        ValidateNativeAttestation(attestation, manifest, errors);
        if (verifier is null || trustedKey is null)
        {
            return (false, attestation);
        }
        if (trustedKey.PublicKey.Length != SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes)
        {
            errors.Add(
                $"Trusted native-attestation Ed25519 key '{trustedKey.KeyId}' must contain exactly {SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes} bytes.");
            return (false, attestation);
        }
        if (!string.Equals(attestation.Signing?.KeyId, trustedKey.KeyId, StringComparison.Ordinal))
        {
            errors.Add(
                $"Execution runtime native-attestation signing key '{attestation.Signing?.KeyId}' is not the caller-supplied trusted key.");
            return (false, attestation);
        }
        if (!IsCanonicalEd25519Base64(attestation.Signing!.Signature))
        {
            return (false, attestation);
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(attestation.Signing!.Signature!);
        }
        catch (FormatException)
        {
            errors.Add("Execution runtime native-attestation signature is not valid canonical base64.");
            return (false, attestation);
        }
        try
        {
            if (verifier.Verify(
                    SunderExecutionRuntimeNativeAttestationSignatures.CreateSignatureInput(attestation),
                    signature,
                    trustedKey.PublicKey.Span))
            {
                return (true, attestation);
            }
        }
        catch (CryptographicException ex)
        {
            errors.Add($"Execution runtime native-attestation signature verification failed: {ex.Message}");
            return (false, attestation);
        }
        errors.Add("Execution runtime native-attestation signature does not verify with the caller-supplied trusted key.");
        return (false, attestation);
    }

    private static void ValidateNativeAttestation(
        SunderExecutionRuntimeNativeAttestation attestation,
        SunderExecutionRuntimeManifest manifest,
        ICollection<string> errors)
    {
        if (attestation.SchemaVersion != SunderExecutionRuntimeFormat.CurrentNativeAttestationVersion)
        {
            errors.Add(
                $"Execution runtime native-build attestation must declare schemaVersion {SunderExecutionRuntimeFormat.CurrentNativeAttestationVersion}.");
        }
        if (!string.Equals(attestation.RuntimeId, manifest.Id, StringComparison.Ordinal)
            || !string.Equals(attestation.RuntimeVersion, manifest.Version, StringComparison.Ordinal)
            || attestation.PackagingRevision != manifest.PackagingRevision
            || !string.Equals(attestation.Rid, manifest.Rid, StringComparison.Ordinal))
        {
            errors.Add("Execution runtime native-build attestation runtime identity does not match the manifest.");
        }
        if (!IsLowercaseSha256(attestation.UpstreamSha256)
            || !string.Equals(attestation.UpstreamSha256, manifest.Upstream?.SourceSha256, StringComparison.Ordinal))
        {
            errors.Add("Execution runtime native-build attestation upstream digest does not match the manifest.");
        }
        if (attestation.Workflow is null
            || !IsLowerIdentifier(attestation.Workflow.Name, SunderExecutionRuntimeFormat.MaxWorkflowValueLength)
            || !IsSafeIdentityValue(attestation.Workflow.RunId, SunderExecutionRuntimeFormat.MaxWorkflowValueLength)
            || !IsSourceRevision(attestation.Workflow.SourceRevision))
        {
            errors.Add("Execution runtime native-build attestation workflow identity is invalid.");
        }
        if (attestation.Toolchain is null
            || !IsLowerIdentifier(attestation.Toolchain.Name, SunderExecutionRuntimeFormat.MaxWorkflowValueLength)
            || !IsStableSemanticVersion(attestation.Toolchain.Version))
        {
            errors.Add("Execution runtime native-build attestation toolchain identity is invalid.");
        }
        if (!string.Equals(
                attestation.LauncherSmoke?.Result,
                SunderExecutionRuntimeFormat.LauncherSmokePassed,
                StringComparison.Ordinal))
        {
            errors.Add("Execution runtime native-build attestation launcher smoke result must be 'passed'.");
        }
        ValidatePlatformAttestation(attestation.Platform, manifest.Rid, errors);
        if (attestation.Signing is null
            || !string.Equals(attestation.Signing.Algorithm, SunderExecutionRuntimeFormat.SigningAlgorithm, StringComparison.Ordinal)
            || !IsLowerIdentifier(attestation.Signing.KeyId, SunderExecutionRuntimeFormat.MaxKeyIdLength)
            || !string.Equals(attestation.Signing.KeyId, manifest.Provenance?.AttestationKeyId, StringComparison.Ordinal)
            || !IsCanonicalEd25519Base64(attestation.Signing.Signature))
        {
            errors.Add("Execution runtime native-build attestation CI signing identity or signature is invalid.");
        }
    }

    private static void ValidatePlatformAttestation(
        SunderExecutionRuntimeAttestationPlatform? platform,
        string? rid,
        ICollection<string> errors)
    {
        var expectedSignature = rid?.StartsWith("linux-", StringComparison.Ordinal) == true
            ? SunderExecutionRuntimeFormat.PlatformCheckNotApplicable
            : SunderExecutionRuntimeFormat.PlatformCheckPassed;
        var expectedNotarization = rid?.StartsWith("osx-", StringComparison.Ordinal) == true
            ? SunderExecutionRuntimeFormat.PlatformCheckPassed
            : SunderExecutionRuntimeFormat.PlatformCheckNotApplicable;
        if (platform is null
            || !string.Equals(platform.Signature, expectedSignature, StringComparison.Ordinal)
            || !string.Equals(platform.Notarization, expectedNotarization, StringComparison.Ordinal))
        {
            errors.Add(
                $"Execution runtime native-build attestation platform results must be signature='{expectedSignature}' and notarization='{expectedNotarization}' for RID '{rid}'.");
        }
    }

    private static SunderExecutionRuntimeArchiveValidationResult CreateResult(
        SunderExecutionRuntimeManifest? manifest,
        List<string> errors,
        string? archiveSha256,
        long archiveSize,
        string? validatedArchivePath,
        string? manifestSha256,
        string? contentIdentity,
        IReadOnlyList<SunderExecutionRuntimeContentRecord> inventory,
        SunderExecutionRuntimeContentRecord? launcherRecord,
        bool signatureVerified,
        bool nativeAttestationSignatureVerified,
        SunderExecutionRuntimeNativeAttestation? nativeAttestation = null)
        => new()
        {
            Manifest = manifest,
            Errors = Array.AsReadOnly(errors.ToArray()),
            ArchiveSha256 = archiveSha256,
            ArchiveSize = archiveSize,
            ValidatedArchivePath = validatedArchivePath,
            ManifestSha256 = manifestSha256,
            ContentIdentity = contentIdentity,
            ContentInventory = inventory,
            LauncherContentRecord = launcherRecord,
            SignatureVerified = signatureVerified,
            NativeAttestationSignatureVerified = nativeAttestationSignatureVerified,
            NativeAttestation = nativeAttestation,
        };

    private static bool TryGetExpectedModuleAbi(string? runtimeId, out string moduleAbi)
    {
        moduleAbi = runtimeId switch
        {
            SunderExecutionRuntimeFormat.DotNetRuntimeId => SunderExecutionRuntimeFormat.DotNetModuleAbi,
            SunderExecutionRuntimeFormat.NodeRuntimeId => SunderExecutionRuntimeFormat.NodeModuleAbi,
            _ => string.Empty,
        };
        return moduleAbi.Length > 0;
    }

    private static bool IsStableSemanticVersion(string? value)
        => SemanticVersion.TryParse(value, out var version) && version.Prerelease is null;

    private static bool IsLowercaseSha256(string? value)
        => value is { Length: 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsLowerIdentifier(string? value, int maxLength)
        => !string.IsNullOrEmpty(value)
           && value.Length <= maxLength
           && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
           && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
           && !value.Contains("..", StringComparison.Ordinal)
           && value.Split('.').All(static segment => segment.Length > 0
                                                       && segment[0] != '-'
                                                       && segment[^1] != '-')
           && value.All(static character => character is >= 'a' and <= 'z'
                                                   or >= '0' and <= '9'
                                                   or '.'
                                                   or '-');

    private static bool IsSafeIdentityValue(string? value, int maxLength)
        => !string.IsNullOrEmpty(value)
           && value.Length <= maxLength
           && value.All(static character => char.IsAsciiLetterOrDigit(character)
                                                   || character is '.' or '-' or '_');

    private static bool IsSourceRevision(string? value)
        => value is { Length: 40 or 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCanonicalEd25519Base64(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        try
        {
            var decoded = Convert.FromBase64String(value);
            return decoded.Length == SunderExecutionRuntimeFormat.Ed25519SignatureBytes
                   && string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        int maxBytes,
        string label,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maxBytes)
        {
            throw new InvalidDataException($"Execution runtime {label} exceeds the {maxBytes}-byte limit.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    private static async Task<ArchiveSnapshot> CreateArchiveSnapshotAsync(
        string runtimePath,
        string validatedArchivePath,
        CancellationToken cancellationToken)
    {
        var fullRuntimePath = Path.GetFullPath(runtimePath);
        var snapshotPath = Path.GetFullPath(validatedArchivePath);
        if (string.Equals(fullRuntimePath, snapshotPath, StringComparison.Ordinal))
        {
            throw new ArgumentException("The validated archive path must differ from the source runtime path.", nameof(validatedArchivePath));
        }
        if (File.Exists(snapshotPath) || Directory.Exists(snapshotPath))
        {
            throw new IOException($"Validated execution runtime archive destination '{snapshotPath}' already exists.");
        }
        var parentPath = Path.GetDirectoryName(snapshotPath)
                         ?? throw new InvalidOperationException($"Validated archive path '{snapshotPath}' has no parent directory.");
        Directory.CreateDirectory(parentPath);
        var temporaryPath = Path.Combine(parentPath, $".{Path.GetFileName(snapshotPath)}.snapshot-{Guid.NewGuid():N}");
        try
        {
            await using var source = new FileStream(
                runtimePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length > SunderExecutionRuntimeFormat.MaxArchiveBytes)
            {
                throw new InvalidDataException(
                    $"Execution runtime archive exceeds the {SunderExecutionRuntimeFormat.MaxArchiveBytes}-byte limit.");
            }

            await using var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long size = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }
                if (read > SunderExecutionRuntimeFormat.MaxArchiveBytes - size)
                {
                    throw new InvalidDataException(
                        $"Execution runtime archive exceeds the {SunderExecutionRuntimeFormat.MaxArchiveBytes}-byte limit.");
                }
                size += read;
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await destination.FlushAsync(cancellationToken);
            destination.Close();
            File.Move(temporaryPath, snapshotPath);
            return new ArchiveSnapshot(
                snapshotPath,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                size);
        }
        catch
        {
            SunderArchiveFileSystem.TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private static IReadOnlyDictionary<string, ArchiveEntryMetadata> ValidateArchiveEntryInventory(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > SunderExecutionRuntimeFormat.MaxArchiveEntries)
        {
            throw new InvalidDataException(
                $"Execution runtime archive contains {archive.Entries.Count} entries; the limit is {SunderExecutionRuntimeFormat.MaxArchiveEntries}.");
        }

        var entries = new Dictionary<string, ArchiveEntryMetadata>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Execution runtime archive must not contain explicit directory entry '{entry.FullName}'.");
            }

            var path = ArchiveRelativePath.Parse(
                entry.FullName,
                SunderExecutionRuntimeFormat.MaxArchivePathLength,
                SunderExecutionRuntimeFormat.MaxArchivePathDepth).ToString();
            if (!entries.TryAdd(path, new ArchiveEntryMetadata((entry.ExternalAttributes >> 16) & 0xffff)))
            {
                throw new InvalidDataException($"Execution runtime archive contains duplicate entry '{path}'.");
            }
            if (path is SunderExecutionRuntimeFormat.ManifestPath
                or SunderExecutionRuntimeFormat.ContentIndexPath
                or SunderExecutionRuntimeFormat.NativeAttestationPath
                && entry.Length > SunderExecutionRuntimeFormat.MaxMetadataBytes)
            {
                throw new InvalidDataException(
                    $"Execution runtime metadata entry '{path}' exceeds the {SunderExecutionRuntimeFormat.MaxMetadataBytes}-byte limit.");
            }
            if (path == SunderExecutionRuntimeFormat.SignaturePath
                && entry.Length > SunderExecutionRuntimeFormat.Ed25519SignatureBytes)
            {
                throw new InvalidDataException(
                    $"Execution runtime detached signature exceeds {SunderExecutionRuntimeFormat.Ed25519SignatureBytes} bytes.");
            }
        }
        return entries;
    }

    private static void ValidateAndApplyArchiveModes(
        SunderExecutionRuntimeManifest manifest,
        IReadOnlyList<(ArchiveRelativePath Path, string FullPath)> actualFiles,
        IReadOnlyDictionary<string, ArchiveEntryMetadata>? archiveEntries,
        ICollection<string> errors)
    {
        if (archiveEntries is null)
        {
            return;
        }

        var valid = true;
        foreach (var file in actualFiles)
        {
            var path = file.Path.ToString();
            var expectedMode = string.Equals(path, manifest.Launch?.Executable, StringComparison.Ordinal)
                ? 0x81ed
                : 0x81a4;
            if (!archiveEntries.TryGetValue(path, out var metadata)
                || metadata.UnixMode != expectedMode)
            {
                errors.Add(
                    $"Execution runtime archive entry '{path}' must use host-controlled Unix mode 0{Convert.ToString(expectedMode & 0xfff, 8)}.");
                valid = false;
            }
        }
        if (!valid || OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var file in actualFiles)
        {
            var mode = UnixFileMode.UserRead
                       | UnixFileMode.UserWrite
                       | UnixFileMode.GroupRead
                       | UnixFileMode.OtherRead;
            if (string.Equals(file.Path.ToString(), manifest.Launch?.Executable, StringComparison.Ordinal))
            {
                mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            }
            try
            {
                File.SetUnixFileMode(file.FullPath, mode);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Execution runtime archive entry '{file.Path}' permissions could not be applied: {ex.Message}");
            }
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string Hash(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record ArchiveSnapshot(string ArchivePath, string Sha256, long Size);

    internal readonly record struct ArchiveEntryMetadata(int UnixMode);
}
