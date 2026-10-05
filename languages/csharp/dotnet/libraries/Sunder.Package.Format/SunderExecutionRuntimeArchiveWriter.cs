using System.Security.Cryptography;

namespace Sunder.Package.Format;

internal static class SunderExecutionRuntimeArchiveWriter
{
    public static async Task WriteAsync(
        SunderExecutionRuntimeManifest manifest,
        string runtimeOutputPath,
        IReadOnlyDictionary<string, string> payloadFiles,
        ISunderExecutionRuntimeArchiveSigner signer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payloadFiles);
        ArgumentNullException.ThrowIfNull(signer);
        if (!string.Equals(manifest.Signing?.KeyId, signer.KeyId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Execution runtime manifest signing key '{manifest.Signing?.KeyId}' does not match writer key '{signer.KeyId}'.");
        }
        if (payloadFiles.Count > SunderExecutionRuntimeFormat.MaxContentIndexEntries)
        {
            throw new InvalidDataException(
                $"Execution runtime payload contains {payloadFiles.Count} files; the limit is {SunderExecutionRuntimeFormat.MaxContentIndexEntries}.");
        }
        ValidatePayloadSources(payloadFiles);

        var stagingPath = Path.Combine(
            Path.GetTempPath(),
            "Sunder.ExecutionRuntimes",
            "pack",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagingPath);
            await CopyPayloadAsync(stagingPath, payloadFiles, cancellationToken);
            var contentIndex = await CreateContentIndexAsync(stagingPath, cancellationToken);
            var manifestBytes = SunderExecutionRuntimeCanonicalJson.SerializeManifest(manifest);
            var contentIndexBytes = SunderExecutionRuntimeCanonicalJson.SerializeContentIndex(contentIndex);
            await WriteArchiveFileAsync(
                stagingPath,
                SunderExecutionRuntimeFormat.ManifestPath,
                manifestBytes,
                cancellationToken);
            await WriteArchiveFileAsync(
                stagingPath,
                SunderExecutionRuntimeFormat.ContentIndexPath,
                contentIndexBytes,
                cancellationToken);

            var signature = signer.Sign(SunderExecutionRuntimeSignatureInput.Create(manifestBytes, contentIndexBytes));
            if (signature.Length != SunderExecutionRuntimeFormat.Ed25519SignatureBytes)
            {
                throw new InvalidDataException(
                    $"Execution runtime signer must return exactly {SunderExecutionRuntimeFormat.Ed25519SignatureBytes} raw Ed25519 signature bytes.");
            }
            await WriteArchiveFileAsync(
                stagingPath,
                SunderExecutionRuntimeFormat.SignaturePath,
                signature,
                cancellationToken);

            var validation = await SunderExecutionRuntimeArchiveInspector.ValidateExtractedAsync(
                stagingPath,
                archiveSha256: null,
                archiveSize: 0,
                validatedArchivePath: null,
                archiveEntries: null,
                verifier: null,
                trustedKey: null,
                nativeAttestationVerifier: null,
                trustedNativeAttestationKey: null,
                cancellationToken);
            if (validation.Errors.Count > 0)
            {
                throw new InvalidDataException(
                    "Execution runtime staging layout is invalid: " + string.Join(" ", validation.Errors));
            }

            var executablePath = ArchiveRelativePath.Parse(
                manifest.Launch!.Executable!,
                SunderExecutionRuntimeFormat.MaxArchivePathLength,
                SunderExecutionRuntimeFormat.MaxArchivePathDepth);
            await SunderArchiveDeterministicWriter.WriteAsync(
                stagingPath,
                runtimeOutputPath,
                SunderExecutionRuntimeFormat.CreateExtractionOptions(cancellationToken),
                executablePath);
            if (new FileInfo(runtimeOutputPath).Length > SunderExecutionRuntimeFormat.MaxArchiveBytes)
            {
                SunderArchiveFileSystem.TryDeleteFile(runtimeOutputPath);
                throw new InvalidDataException(
                    $"Execution runtime archive exceeds the {SunderExecutionRuntimeFormat.MaxArchiveBytes}-byte limit.");
            }
        }
        finally
        {
            SunderArchiveFileSystem.TryDeleteDirectory(stagingPath);
        }
    }

    private static async Task CopyPayloadAsync(
        string stagingPath,
        IReadOnlyDictionary<string, string> payloadFiles,
        CancellationToken cancellationToken)
    {
        var registry = new ArchivePathRegistry();
        foreach (var (archivePathText, sourcePath) in payloadFiles.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archivePath = ArchiveRelativePath.Parse(
                archivePathText,
                SunderExecutionRuntimeFormat.MaxArchivePathLength,
                SunderExecutionRuntimeFormat.MaxArchivePathDepth);
            if (!SunderExecutionRuntimeFormat.IsPayloadPath(archivePath.ToString()))
            {
                throw new InvalidDataException(
                    $"Execution runtime indexed regular file '{archivePath}' must be below payload/.");
            }
            registry.Register(archivePath, isDirectory: false);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Execution runtime payload source '{sourcePath}' was not found.", sourcePath);
            }
            SunderArchiveFileSystem.EnsureNotLink(sourcePath);
            var observedInfo = new FileInfo(sourcePath);
            if (observedInfo.Length > SunderExecutionRuntimeFormat.MaxEntryUncompressedBytes)
            {
                throw new InvalidDataException(
                    $"Execution runtime payload source '{sourcePath}' exceeds the per-entry size limit.");
            }
            var observedLength = observedInfo.Length;
            var observedWriteTime = observedInfo.LastWriteTimeUtc;
            var destinationPath = archivePath.ToPlatformPath(stagingPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             destinationPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                              bufferSize: 81920,
                              FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyObservedBytesAsync(source, destination, archivePath, observedLength, cancellationToken);
            }
            SunderArchiveFileSystem.EnsureNotLink(sourcePath);
            observedInfo.Refresh();
            if (observedInfo.Length != observedLength || observedInfo.LastWriteTimeUtc != observedWriteTime)
            {
                throw new InvalidDataException(
                    $"Execution runtime payload source '{sourcePath}' changed while the archive was being written.");
            }
        }
    }

    private static async Task CopyObservedBytesAsync(
        FileStream source,
        FileStream destination,
        ArchiveRelativePath archivePath,
        long observedLength,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var remaining = observedLength;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0)
            {
                throw new InvalidDataException(
                    $"Execution runtime payload source for '{archivePath}' changed while the archive was being written.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
        if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
        {
            throw new InvalidDataException(
                $"Execution runtime payload source for '{archivePath}' changed while the archive was being written.");
        }
    }

    private static void ValidatePayloadSources(IReadOnlyDictionary<string, string> payloadFiles)
    {
        var registry = new ArchivePathRegistry();
        long totalBytes = 0;
        foreach (var (archivePathText, sourcePath) in payloadFiles)
        {
            var archivePath = ArchiveRelativePath.Parse(
                archivePathText,
                SunderExecutionRuntimeFormat.MaxArchivePathLength,
                SunderExecutionRuntimeFormat.MaxArchivePathDepth);
            if (!SunderExecutionRuntimeFormat.IsPayloadPath(archivePath.ToString()))
            {
                throw new InvalidDataException(
                    $"Execution runtime indexed regular file '{archivePath}' must be below payload/.");
            }
            registry.Register(archivePath, isDirectory: false);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Execution runtime payload source '{sourcePath}' was not found.", sourcePath);
            }
            SunderArchiveFileSystem.EnsureNotLink(sourcePath);
            var length = new FileInfo(sourcePath).Length;
            if (length > SunderExecutionRuntimeFormat.MaxEntryUncompressedBytes
                || length > SunderExecutionRuntimeFormat.MaxTotalUncompressedBytes - totalBytes)
            {
                throw new InvalidDataException("Execution runtime payload exceeds uncompressed byte limits.");
            }
            if (archivePath.ToString() == SunderExecutionRuntimeFormat.NativeAttestationPath
                && length > SunderExecutionRuntimeFormat.MaxMetadataBytes)
            {
                throw new InvalidDataException(
                    $"Execution runtime native-build attestation exceeds the {SunderExecutionRuntimeFormat.MaxMetadataBytes}-byte limit.");
            }
            totalBytes += length;
        }
    }

    private static async Task<SunderExecutionRuntimeContentIndex> CreateContentIndexAsync(
        string stagingPath,
        CancellationToken cancellationToken)
    {
        var records = new List<SunderExecutionRuntimeContentRecord>();
        long totalBytes = 0;
        foreach (var file in SunderArchiveFileSystem.EnumerateFiles(stagingPath)
                     .OrderBy(static item => item.Path.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file.FullPath);
            if (info.Length > SunderExecutionRuntimeFormat.MaxEntryUncompressedBytes
                || info.Length > SunderExecutionRuntimeFormat.MaxTotalUncompressedBytes - totalBytes)
            {
                throw new InvalidDataException("Execution runtime payload exceeds uncompressed byte limits.");
            }
            totalBytes += info.Length;
            await using var stream = new FileStream(
                file.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            records.Add(new SunderExecutionRuntimeContentRecord
            {
                Path = file.Path.ToString(),
                Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant(),
                Size = info.Length,
            });
        }
        return new SunderExecutionRuntimeContentIndex
        {
            SchemaVersion = SunderExecutionRuntimeFormat.CurrentContentIndexVersion,
            Files = records,
        };
    }

    private static async Task WriteArchiveFileAsync(
        string stagingPath,
        string archivePath,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var relativePath = ArchiveRelativePath.Parse(
            archivePath,
            SunderExecutionRuntimeFormat.MaxArchivePathLength,
            SunderExecutionRuntimeFormat.MaxArchivePathDepth);
        var outputPath = relativePath.ToPlatformPath(stagingPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
    }

}
