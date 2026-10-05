using System.Security.Cryptography;
using Sunder.Package.Format;

namespace Sunder.ExecutionRuntime.Build;

internal static class ExecutionRuntimeArchiveBuilder
{
    public static async Task<SunderExecutionRuntimeArchiveValidationResult> BuildAsync(
        string lockPath,
        string expectedLockKind,
        string runtimeId,
        string version,
        string rid,
        string sourceArchivePath,
        string nativeAttestationPath,
        string outputPath,
        ISunderExecutionRuntimeArchiveSigner signer,
        ISunderExecutionRuntimeArchiveSignatureVerifier verifier,
        SunderExecutionRuntimeTrustedKey releaseKey,
        SunderExecutionRuntimeTrustedKey attestationKey,
        CancellationToken cancellationToken = default)
    {
        var lockDocument = await ExecutionRuntimeLockLoader.LoadAsync(
            lockPath,
            expectedLockKind,
            cancellationToken);
        var entries = lockDocument.Entries!
            .Where(entry => entry is not null
                            && string.Equals(entry.Id, runtimeId, StringComparison.Ordinal)
                            && string.Equals(entry.Version, version, StringComparison.Ordinal)
                            && string.Equals(entry.Rid, rid, StringComparison.Ordinal))
            .ToArray();
        if (entries.Length != 1)
        {
            throw new InvalidDataException(
                $"Lock must contain exactly one build entry for '{runtimeId}' version '{version}' RID '{rid}'.");
        }

        return await BuildAsync(
            lockDocument,
            entries[0]!,
            sourceArchivePath,
            nativeAttestationPath,
            outputPath,
            signer,
            verifier,
            releaseKey,
            attestationKey,
            cancellationToken);
    }

    // Signers remain injectable only through the internal build boundary. Private keys are never file or CLI inputs.
    internal static async Task<SunderExecutionRuntimeArchiveValidationResult> BuildAsync(
        ExecutionRuntimeLockDocument lockDocument,
        ExecutionRuntimeLockEntry entry,
        string sourceArchivePath,
        string nativeAttestationPath,
        string outputPath,
        ISunderExecutionRuntimeArchiveSigner signer,
        ISunderExecutionRuntimeArchiveSignatureVerifier verifier,
        SunderExecutionRuntimeTrustedKey releaseKey,
        SunderExecutionRuntimeTrustedKey attestationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lockDocument);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(releaseKey);
        ArgumentNullException.ThrowIfNull(attestationKey);
        var lockErrors = ExecutionRuntimeLockValidator.Validate(lockDocument);
        if (lockErrors.Count > 0)
        {
            throw new InvalidDataException(
                "Execution-runtime lock validation failed:" + Environment.NewLine
                + string.Join(Environment.NewLine, lockErrors.Select(static error => $"- {error}")));
        }
        if (lockDocument.Entries is null || !lockDocument.Entries.Contains(entry))
        {
            throw new InvalidDataException("Build entry must belong to the validated execution-runtime lock.");
        }
        if (entry.Source is null
            || entry.Profile is null
            || entry.Recipe is null
            || entry.License is null
            || entry.Trust is null)
        {
            throw new InvalidDataException("A complete validated execution-runtime lock entry is required.");
        }
        if (!string.Equals(signer.KeyId, entry.Trust.ReleaseSigningKeyId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Injected signer '{signer.KeyId}' does not match locked release key '{entry.Trust.ReleaseSigningKeyId}'.");
        }
        ValidateTrustedKey(releaseKey, entry.Trust.ReleaseSigningKeyId!, "release");
        ValidateTrustedKey(attestationKey, entry.Trust.AttestationSigningKeyId!, "native-attestation");
        if (!File.Exists(nativeAttestationPath))
        {
            throw new FileNotFoundException(
                $"Native execution-runtime attestation '{nativeAttestationPath}' was not found.",
                nativeAttestationPath);
        }
        if ((File.GetAttributes(nativeAttestationPath) & FileAttributes.ReparsePoint) != 0
            || new FileInfo(nativeAttestationPath).Length is <= 0 or > SunderExecutionRuntimeFormat.MaxMetadataBytes)
        {
            throw new InvalidDataException(
                $"Native execution-runtime attestation must be a non-link file of at most {SunderExecutionRuntimeFormat.MaxMetadataBytes} bytes.");
        }
        if (!outputPath.EndsWith(SunderExecutionRuntimeFormat.Extension, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Execution-runtime output must use the '{SunderExecutionRuntimeFormat.Extension}' extension.");
        }
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (File.Exists(fullOutputPath) || Directory.Exists(fullOutputPath))
        {
            throw new IOException($"Execution-runtime output already exists: '{fullOutputPath}'.");
        }
        var outputRoot = Path.GetDirectoryName(fullOutputPath)
                         ?? throw new InvalidDataException("Execution-runtime output must have a parent directory.");
        Directory.CreateDirectory(outputRoot);
        SunderArchiveFileSystem.EnsureNotLink(outputRoot);

        var workingRoot = Path.Combine(
            outputRoot,
            $".{Path.GetFileName(fullOutputPath)}.build-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(workingRoot);
            var materializedFiles = await ExecutionRuntimeSourceMaterializer.MaterializeAsync(
                lockDocument.LockKind!,
                entry,
                sourceArchivePath,
                Path.Combine(workingRoot, "upstream"),
                cancellationToken);
            var payloadFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in materializedFiles)
            {
                payloadFiles.Add(file.Key, file.Value);
            }

            var attestationSnapshotPath = Path.Combine(workingRoot, "native-build-attestation.v1.json");
            var attestationBytes = await SnapshotAttestationAsync(
                nativeAttestationPath,
                attestationSnapshotPath,
                cancellationToken);
            var attestation = StrictJson.Deserialize<SunderExecutionRuntimeNativeAttestation>(
                attestationBytes,
                "Native execution-runtime attestation");
            ValidateAttestation(entry, attestation);
            VerifyAttestation(attestation, verifier, attestationKey);
            var attestationSha256 = Convert.ToHexString(SHA256.HashData(attestationBytes)).ToLowerInvariant();
            payloadFiles.Add(SunderExecutionRuntimeFormat.NativeAttestationPath, attestationSnapshotPath);

            var manifest = new SunderExecutionRuntimeManifest
            {
                SchemaVersion = SunderExecutionRuntimeFormat.CurrentSchemaVersion,
                Id = entry.Id,
                Version = entry.Version,
                PackagingRevision = entry.PackagingRevision,
                Rid = entry.Rid,
                ModuleAbi = entry.Profile.ModuleAbi,
                WorkerProtocol = entry.Profile.WorkerProtocol,
                Launch = new SunderExecutionRuntimeLaunch
                {
                    Executable = entry.Profile.LaunchExecutable,
                    Arguments = entry.Profile.LaunchArguments,
                },
                ContentIndex = SunderExecutionRuntimeFormat.ContentIndexPath,
                Upstream = new SunderExecutionRuntimeUpstream
                {
                    Name = entry.Source.Name,
                    Version = entry.Version,
                    SourceUri = entry.Source.Uri,
                    SourceSha256 = entry.Source.Sha256,
                },
                Provenance = new SunderExecutionRuntimeProvenance
                {
                    AttestationPath = SunderExecutionRuntimeFormat.NativeAttestationPath,
                    AttestationSha256 = attestationSha256,
                    AttestationKeyId = entry.Trust.AttestationSigningKeyId,
                },
                Licenses = entry.License.Spdx,
                Signing = new SunderExecutionRuntimeSigning
                {
                    Algorithm = SunderExecutionRuntimeFormat.SigningAlgorithm,
                    KeyId = entry.Trust.ReleaseSigningKeyId,
                    Signature = SunderExecutionRuntimeFormat.SignaturePath,
                },
            };

            var candidatePath = Path.Combine(workingRoot, "candidate.sunderruntime");
            await SunderExecutionRuntimeArchiveWriter.WriteAsync(
                manifest,
                candidatePath,
                payloadFiles,
                signer,
                cancellationToken);
            var validatedPath = Path.Combine(workingRoot, "validated.sunderruntime");
            var inspection = await SunderExecutionRuntimeArchiveInspector.InspectAsync(
                candidatePath,
                validatedPath,
                Path.Combine(workingRoot, "inspection"),
                verifier,
                releaseKey,
                verifier,
                attestationKey,
                cancellationToken);
            if (!inspection.Success
                || !inspection.SignatureVerified
                || !inspection.NativeAttestationSignatureVerified
                || inspection.Manifest is null
                || !string.Equals(inspection.Manifest.Id, entry.Id, StringComparison.Ordinal)
                || !string.Equals(inspection.Manifest.Version, entry.Version, StringComparison.Ordinal)
                || inspection.Manifest.PackagingRevision != entry.PackagingRevision
                || !string.Equals(inspection.Manifest.Rid, entry.Rid, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Built execution-runtime archive failed trusted final inspection: "
                    + string.Join(" ", inspection.Errors));
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(validatedPath, fullOutputPath);
            return inspection with { ValidatedArchivePath = fullOutputPath };
        }
        finally
        {
            TryDeleteDirectory(workingRoot);
        }
    }

    private static void ValidateTrustedKey(
        SunderExecutionRuntimeTrustedKey trustedKey,
        string expectedKeyId,
        string role)
    {
        if (!string.Equals(trustedKey.KeyId, expectedKeyId, StringComparison.Ordinal)
            || trustedKey.PublicKey.Length != SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes)
        {
            throw new InvalidDataException(
                $"Trusted {role} key must provide the locked identity '{expectedKeyId}' and exactly {SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes} public-key bytes.");
        }
    }

    private static void VerifyAttestation(
        SunderExecutionRuntimeNativeAttestation attestation,
        ISunderExecutionRuntimeArchiveSignatureVerifier verifier,
        SunderExecutionRuntimeTrustedKey trustedKey)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(attestation.Signing!.Signature!);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Native attestation signature is not valid base64.", exception);
        }
        try
        {
            if (verifier.Verify(
                    SunderExecutionRuntimeNativeAttestationSignatures.CreateSignatureInput(attestation),
                    signature,
                    trustedKey.PublicKey.Span))
            {
                return;
            }
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("Native attestation signature verification failed.", exception);
        }
        throw new InvalidDataException(
            "Native attestation signature does not verify with the locked trusted key.");
    }

    private static async Task<byte[]> SnapshotAttestationAsync(
        string sourcePath,
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                $"Native execution-runtime attestation '{sourcePath}' was not found.",
                sourcePath);
        }
        if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Native execution-runtime attestation must not be a link.");
        }
        var observed = new FileInfo(sourcePath);
        if (observed.Length is <= 0 or > SunderExecutionRuntimeFormat.MaxMetadataBytes)
        {
            throw new InvalidDataException(
                $"Native execution-runtime attestation must contain at most {SunderExecutionRuntimeFormat.MaxMetadataBytes} bytes.");
        }
        var length = observed.Length;
        var writeTime = observed.LastWriteTimeUtc;
        var bytes = new byte[checked((int)length)];
        await using (var stream = new FileStream(
                         sourcePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            if (stream.ReadByte() != -1)
            {
                throw new InvalidDataException("Native execution-runtime attestation changed while being snapshotted.");
            }
        }
        observed.Refresh();
        if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0
            || observed.Length != length
            || observed.LastWriteTimeUtc != writeTime)
        {
            throw new InvalidDataException("Native execution-runtime attestation changed while being snapshotted.");
        }
        await File.WriteAllBytesAsync(snapshotPath, bytes, cancellationToken);
        return bytes;
    }

    private static void ValidateAttestation(
        ExecutionRuntimeLockEntry entry,
        SunderExecutionRuntimeNativeAttestation attestation)
    {
        if (attestation.SchemaVersion != SunderExecutionRuntimeFormat.CurrentNativeAttestationVersion
            || !string.Equals(attestation.RuntimeId, entry.Id, StringComparison.Ordinal)
            || !string.Equals(attestation.RuntimeVersion, entry.Version, StringComparison.Ordinal)
            || attestation.PackagingRevision != entry.PackagingRevision
            || !string.Equals(attestation.Rid, entry.Rid, StringComparison.Ordinal)
            || !string.Equals(attestation.UpstreamSha256, entry.Source!.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Native attestation identity does not match the exact lock entry.");
        }
        if (!string.Equals(attestation.Workflow?.Name, entry.Trust!.AttestationWorkflow, StringComparison.Ordinal)
            || !string.Equals(attestation.Toolchain?.Name, entry.Recipe!.Toolchain!.Name, StringComparison.Ordinal)
            || !string.Equals(attestation.Toolchain?.Version, entry.Recipe.Toolchain.Version, StringComparison.Ordinal)
            || !string.Equals(attestation.LauncherSmoke?.Result, SunderExecutionRuntimeFormat.LauncherSmokePassed, StringComparison.Ordinal)
            || !string.Equals(attestation.Signing?.Algorithm, SunderExecutionRuntimeFormat.SigningAlgorithm, StringComparison.Ordinal)
            || !string.Equals(attestation.Signing?.KeyId, entry.Trust.AttestationSigningKeyId, StringComparison.Ordinal)
            || !IsCanonicalSignature(attestation.Signing?.Signature))
        {
            throw new InvalidDataException("Native attestation recipe, smoke result, or signer identity does not match the lock entry.");
        }

        var expectedSignature = entry.Trust.PlatformSignature == ExecutionRuntimeLockPolicy.RequiredTrustCheck
            ? SunderExecutionRuntimeFormat.PlatformCheckPassed
            : SunderExecutionRuntimeFormat.PlatformCheckNotApplicable;
        var expectedNotarization = entry.Trust.Notarization == ExecutionRuntimeLockPolicy.RequiredTrustCheck
            ? SunderExecutionRuntimeFormat.PlatformCheckPassed
            : SunderExecutionRuntimeFormat.PlatformCheckNotApplicable;
        if (!string.Equals(attestation.Platform?.Signature, expectedSignature, StringComparison.Ordinal)
            || !string.Equals(attestation.Platform?.Notarization, expectedNotarization, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Native attestation platform results do not satisfy the lock trust profile.");
        }
    }

    private static bool IsCanonicalSignature(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length == SunderExecutionRuntimeFormat.Ed25519SignatureBytes
                   && string.Equals(Convert.ToBase64String(bytes), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
