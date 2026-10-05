using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.ExecutionRuntime.Build;

internal static class ExecutionRuntimeBuildCli
{
    public static Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
        => RunAsync(
            args,
            output,
            error,
            new SunderExecutionRuntimeEd25519SignatureVerifier(),
            cancellationToken);

    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        ISunderExecutionRuntimeArchiveSignatureVerifier verifier,
        CancellationToken cancellationToken = default,
        Action<string>? platformBeforePublication = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(verifier);
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            await output.WriteLineAsync(Usage());
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0] switch
            {
                "validate-lock" => await ValidateLockAsync(args[1..], output, cancellationToken),
                "validate-source" => await ValidateSourceAsync(args[1..], output, cancellationToken),
                "validate-native-lane" => await ValidateNativeLaneAsync(args[1..], output, cancellationToken),
                "development-keys" => await DevelopmentKeysAsync(args[1..], output),
                "development-build" => await DevelopmentBuildAsync(args[1..], output, verifier, cancellationToken),
                "development-publish" => await DevelopmentPublishAsync(args[1..], output, verifier, cancellationToken),
                "build" => await BuildAsync(args[1..], output, verifier, cancellationToken),
                "inspect" => await InspectAsync(args[1..], output, verifier, cancellationToken),
                "validate-native-output" => await ValidateNativeOutputAsync(args[1..], output, verifier, cancellationToken),
                "validate-release-set" => await ValidateReleaseSetAsync(args[1..], output, verifier, cancellationToken),
                "check-platform-catalog-migration" => await CheckPlatformCatalogMigrationAsync(args[1..], output, cancellationToken),
                "platform-contracts" => await PlatformContractsAsync(args[1..], output, cancellationToken, platformBeforePublication),
                "core-stack-transport" => await CoreStackTransportAsync(args[1..], output, cancellationToken),
                _ => throw new CliUsageException($"Unknown command '{args[0]}'."),
            };
        }
        catch (CliUsageException exception)
        {
            await error.WriteLineAsync(exception.Message);
            await error.WriteLineAsync(Usage());
            return 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Execution-runtime validation was canceled.");
            return 130;
        }
        catch (Exception exception) when (exception is InvalidDataException
                                           or IOException
                                           or TimeoutException
                                           or UnauthorizedAccessException
                                           or Win32Exception
                                           or CryptographicException
                                           or HttpRequestException
                                           or ArgumentException)
        {
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static async Task<int> DevelopmentKeysAsync(string[] args, TextWriter output)
    {
        if (args.Length == 0)
        {
            throw new CliUsageException(
                "Command 'development-keys' requires one action: ensure, rotate, or reset.");
        }
        var options = args.Length == 1
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : ParseOptions(args[1..], ["--state-root"]);
        var stateRoot = DevelopmentStateRoot(options);
        switch (args[0])
        {
            case "ensure":
                await WriteDevelopmentTrustAsync(
                    output,
                    DevelopmentExecutionRuntimeKeyStore.Ensure(stateRoot));
                return 0;
            case "rotate":
                await WriteDevelopmentTrustAsync(
                    output,
                    DevelopmentExecutionRuntimeKeyStore.Rotate(stateRoot));
                return 0;
            case "reset":
                DevelopmentExecutionRuntimeKeyStore.Reset(stateRoot);
                await output.WriteLineAsync(
                    "Reset isolated Development execution-runtime signing state. Run 'development-keys ensure' to create a new trust generation.");
                return 0;
            default:
                throw new CliUsageException(
                    $"Unknown development key action '{args[0]}'; expected ensure, rotate, or reset.");
        }
    }

    private static async Task<int> DevelopmentBuildAsync(
        string[] args,
        TextWriter output,
        ISunderExecutionRuntimeArchiveSignatureVerifier verifier,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(
            args,
            [
                "--lock", "--id", "--version", "--rid", "--source",
                "--native-attestation", "--output", "--state-root",
            ]);
        var stateRoot = DevelopmentStateRoot(options);
        var result = await DevelopmentExecutionRuntimeOperations.BuildAsync(
            Required(options, "--lock"),
            Required(options, "--id"),
            Required(options, "--version"),
            Required(options, "--rid"),
            Required(options, "--source"),
            Required(options, "--native-attestation"),
            Required(options, "--output"),
            stateRoot,
            verifier,
            cancellationToken);
        await output.WriteLineAsync(
            $"Built and reinspected Development archive '{result.ValidatedArchivePath}' SHA-256 {result.ArchiveSha256} ({result.ArchiveSize} bytes), content identity {result.ContentIdentity}.");
        return 0;
    }

    private static async Task<int> DevelopmentPublishAsync(
        string[] args,
        TextWriter output,
        ISunderExecutionRuntimeArchiveSignatureVerifier verifier,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(args, ["--registry", "--directory", "--state-root"]);
        if (!Uri.TryCreate(Required(options, "--registry"), UriKind.Absolute, out var registryUri))
        {
            throw new CliUsageException("Option '--registry' must be an absolute URI.");
        }
        var stateRoot = DevelopmentStateRoot(options);
        var result = await DevelopmentExecutionRuntimeOperations.PublishAsync(
            registryUri,
            Required(options, "--directory"),
            stateRoot,
            verifier,
            cancellationToken);
        if (!result.Success)
        {
            throw new InvalidDataException(
                "Development Registry did not promote the execution-runtime release: "
                + string.Join(" ", result.Errors));
        }
        await output.WriteLineAsync(
            "Published the exact six-RID Development execution-runtime release through Registry staging, validation, and promotion.");
        return 0;
    }

    private static Task WriteDevelopmentTrustAsync(
        TextWriter output,
        DevelopmentExecutionRuntimeTrust trust)
        => output.WriteLineAsync(JsonSerializer.Serialize(
            trust,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

    private static async Task<int> BuildAsync(
        string[] args,
        TextWriter output,
        ISunderExecutionRuntimeArchiveSignatureVerifier verifier,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(
            args,
            [
                "--lock", "--kind", "--id", "--version", "--rid", "--source",
                "--native-attestation", "--output", "--release-public-key",
                "--attestation-public-key", "--signer-adapter", "--signer-adapter-sha256",
            ]);
        var lockPath = Required(options, "--lock");
        var kind = RequiredKind(options);
        var id = Required(options, "--id");
        var version = Required(options, "--version");
        var rid = Required(options, "--rid");
        var sourcePath = Required(options, "--source");
        var attestationPath = Required(options, "--native-attestation");
        var outputPath = Required(options, "--output");
        var document = await ExecutionRuntimeLockLoader.LoadAsync(lockPath, kind, cancellationToken);
        var entry = FindEntry(document, id, version, rid);
        var releaseKey = await ReadTrustedKeyAsync(
            Required(options, "--release-public-key"),
            entry.Trust!.ReleaseSigningKeyId!,
            cancellationToken);
        var attestationKey = await ReadTrustedKeyAsync(
            Required(options, "--attestation-public-key"),
            entry.Trust.AttestationSigningKeyId!,
            cancellationToken);
        using var signer = await ExternalExecutionRuntimeSigner.CreateAsync(
            entry.Trust.ReleaseSigningKeyId!,
            Required(options, "--signer-adapter"),
            Required(options, "--signer-adapter-sha256"),
            cancellationToken);
        var result = await ExecutionRuntimeArchiveBuilder.BuildAsync(
            document,
            entry,
            sourcePath,
            attestationPath,
            outputPath,
            signer,
            verifier,
            releaseKey,
            attestationKey,
            cancellationToken);
        await output.WriteLineAsync(
            $"Built and reinspected '{result.ValidatedArchivePath}' SHA-256 {result.ArchiveSha256} ({result.ArchiveSize} bytes), content identity {result.ContentIdentity}.");
        return 0;
    }

    private static async Task<int> InspectAsync(
        string[] args,
        TextWriter output,
        ISunderExecutionRuntimeArchiveSignatureVerifier verifier,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(
            args,
            [
                "--lock", "--kind", "--id", "--version", "--rid", "--archive",
                "--release-public-key", "--attestation-public-key",
            ]);
        var lockPath = Required(options, "--lock");
        var kind = RequiredKind(options);
        var id = Required(options, "--id");
        var version = Required(options, "--version");
        var rid = Required(options, "--rid");
        var archivePath = Required(options, "--archive");
        var document = await ExecutionRuntimeLockLoader.LoadAsync(lockPath, kind, cancellationToken);
        var entry = FindEntry(document, id, version, rid);
        var releaseKey = await ReadTrustedKeyAsync(
            Required(options, "--release-public-key"),
            entry.Trust!.ReleaseSigningKeyId!,
            cancellationToken);
        var attestationKey = await ReadTrustedKeyAsync(
            Required(options, "--attestation-public-key"),
            entry.Trust.AttestationSigningKeyId!,
            cancellationToken);
        var workingRoot = Path.Combine(
            Path.GetTempPath(),
            "Sunder.ExecutionRuntime.Build",
            "inspect",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workingRoot);
            var result = await SunderExecutionRuntimeArchiveInspector.InspectAsync(
                archivePath,
                Path.Combine(workingRoot, "validated.sunderruntime"),
                Path.Combine(workingRoot, "staging"),
                verifier,
                releaseKey,
                verifier,
                attestationKey,
                cancellationToken);
            if (!MatchesExactLockEntry(entry, result))
            {
                throw new InvalidDataException(
                    "Execution-runtime archive does not match the trusted lock entry: "
                    + (result.Errors.Count > 0
                        ? string.Join(" ", result.Errors)
                        : "signed manifest or native-attestation metadata differs from the lock."));
            }
            await output.WriteLineAsync(
                $"Inspected '{archivePath}' SHA-256 {result.ArchiveSha256} ({result.ArchiveSize} bytes), content identity {result.ContentIdentity}.");
            return 0;
        }
        finally
        {
            TryDeleteDirectory(workingRoot);
        }
    }

    private static async Task<int> ValidateNativeOutputAsync(
        string[] args,
        TextWriter output,
        ISunderExecutionRuntimeArchiveSignatureVerifier verifier,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(
            args,
            [
                "--lock", "--kind", "--rid", "--directory", "--release-public-keys",
                "--attestation-public-keys", "--release-driver-sha256", "--receipt",
            ]);
        var lockPath = Required(options, "--lock");
        var kind = RequiredKind(options);
        var rid = Required(options, "--rid");
        var releaseDriverSha256 = RequiredSha256(options, "--release-driver-sha256");
        var directory = Path.GetFullPath(Required(options, "--directory"));
        var receiptPath = Path.GetFullPath(Required(options, "--receipt"));
        if (!Directory.Exists(directory)
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Native output '{directory}' must be a non-link directory.");
        }
        var relativeReceiptPath = Path.GetRelativePath(directory, receiptPath);
        if (!relativeReceiptPath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathFullyQualified(relativeReceiptPath))
        {
            throw new InvalidDataException("Native output receipt must be outside the archive-only output directory.");
        }
        if (File.Exists(receiptPath) || Directory.Exists(receiptPath))
        {
            throw new IOException($"Native output receipt already exists: '{receiptPath}'.");
        }

        var snapshot = await ExecutionRuntimeLockLoader.LoadSnapshotAsync(lockPath, kind, cancellationToken);
        var expectedEntries = snapshot.Document.Entries!
            .Where(entry => entry is not null && string.Equals(entry.Rid, rid, StringComparison.Ordinal))
            .Select(static entry => entry!)
            .ToArray();
        if (expectedEntries.Length != ExecutionRuntimeLockPolicy.RuntimeFamilies.Count)
        {
            throw new InvalidDataException(
                $"Lock must contain one entry for each curated runtime family on RID '{rid}'.");
        }
        var paths = Directory.EnumerateFileSystemEntries(directory).ToArray();
        if (paths.Length != expectedEntries.Length
            || paths.Any(path => !File.Exists(path)
                                 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                                 || !path.EndsWith(SunderExecutionRuntimeFormat.Extension, StringComparison.Ordinal)
                                 || new FileInfo(path).Length <= 0))
        {
            throw new InvalidDataException(
                $"Native output for RID '{rid}' must contain exactly two non-link .sunderruntime files and no other entries.");
        }

        var releaseKeyRoot = Required(options, "--release-public-keys");
        var attestationKeyRoot = Required(options, "--attestation-public-keys");
        var observedRuntimeIds = new HashSet<string>(StringComparer.Ordinal);
        var artifacts = new List<NativeOutputArtifactReceipt>();
        var workingRoot = Path.Combine(
            Path.GetTempPath(),
            "Sunder.ExecutionRuntime.Build",
            "validate-native-output",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workingRoot);
            foreach (var archivePath in paths.OrderBy(static path => path, StringComparer.Ordinal))
            {
                var metadata = await SunderExecutionRuntimeArchiveInspector.ReadManifestMetadataAsync(
                    archivePath,
                    cancellationToken);
                var manifest = metadata.Manifest;
                var entry = FindEntry(snapshot.Document, manifest.Id!, manifest.Version!, manifest.Rid!);
                if (manifest.PackagingRevision != entry.PackagingRevision
                    || !string.Equals(entry.Rid, rid, StringComparison.Ordinal)
                    || !observedRuntimeIds.Add(entry.Id!))
                {
                    throw new InvalidDataException(
                        $"Native output '{archivePath}' is duplicate, mixed, or outside exact RID '{rid}' lock identity.");
                }
                var releaseKey = await ReadTrustedKeyFromDirectoryAsync(
                    releaseKeyRoot,
                    entry.Trust!.ReleaseSigningKeyId!,
                    cancellationToken);
                var attestationKey = await ReadTrustedKeyFromDirectoryAsync(
                    attestationKeyRoot,
                    entry.Trust.AttestationSigningKeyId!,
                    cancellationToken);
                var artifactRoot = Path.Combine(workingRoot, observedRuntimeIds.Count.ToString());
                var result = await SunderExecutionRuntimeArchiveInspector.InspectAsync(
                    archivePath,
                    Path.Combine(artifactRoot, "validated.sunderruntime"),
                    Path.Combine(artifactRoot, "staging"),
                    verifier,
                    releaseKey,
                    verifier,
                    attestationKey,
                    cancellationToken);
                if (!MatchesExactLockEntry(entry, result))
                {
                    throw new InvalidDataException(
                        $"Native output '{archivePath}' failed trusted lock inspection: "
                        + (result.Errors.Count > 0
                            ? string.Join(" ", result.Errors)
                            : "signed manifest or native-attestation metadata differs from the lock."));
                }
                artifacts.Add(new NativeOutputArtifactReceipt(
                    entry.Id!,
                    entry.Version!,
                    entry.PackagingRevision,
                    entry.Rid!,
                    entry.Profile!.ModuleAbi!,
                    entry.Profile.WorkerProtocol!,
                    entry.MinimumHostTrustGeneration,
                    entry.Trust.ReleaseSigningKeyId!,
                    entry.Trust.AttestationSigningKeyId!,
                    Path.GetFileName(archivePath),
                    result.ArchiveSha256!,
                    result.ArchiveSize,
                    result.ManifestSha256!,
                    result.ContentIdentity!,
                    result.Manifest!.Provenance!.AttestationSha256!,
                    result.SignatureVerified,
                    result.NativeAttestationSignatureVerified));
            }
        }
        finally
        {
            TryDeleteDirectory(workingRoot);
        }

        var expectedRuntimeIds = expectedEntries.Select(static entry => entry.Id!).ToHashSet(StringComparer.Ordinal);
        if (!observedRuntimeIds.SetEquals(expectedRuntimeIds))
        {
            throw new InvalidDataException($"Native output for RID '{rid}' does not contain the exact curated runtime set.");
        }
        await WriteJsonAtomicAsync(
            receiptPath,
            new NativeOutputReceipt(
                1,
                "https://sunderapp.io/acceptance/runtime-native-output/v1",
                snapshot.Sha256,
                rid,
                releaseDriverSha256,
                artifacts.OrderBy(static artifact => artifact.RuntimeId, StringComparer.Ordinal).ToArray()),
            cancellationToken);
        await output.WriteLineAsync(
            $"Validated both locked runtime archives for RID '{rid}' and wrote receipt '{receiptPath}'.");
        return 0;
    }

    private static async Task<int> ValidateReleaseSetAsync(
        string[] args,
        TextWriter output,
        ISunderExecutionRuntimeArchiveSignatureVerifier verifier,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(
            args,
            [
                "--lock", "--kind", "--directory", "--release-public-keys",
                "--attestation-public-keys", "--native-receipts", "--release-drivers", "--receipt",
            ]);
        var lockPath = Required(options, "--lock");
        var kind = RequiredKind(options);
        var directory = Path.GetFullPath(Required(options, "--directory"));
        var nativeReceiptsDirectory = Path.GetFullPath(Required(options, "--native-receipts"));
        var releaseDriversPath = Path.GetFullPath(Required(options, "--release-drivers"));
        var receiptPath = Path.GetFullPath(Required(options, "--receipt"));
        if (!Directory.Exists(directory)
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Release-set input '{directory}' must be a non-link directory.");
        }
        var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
        var expectedDirectories = ExecutionRuntimeLockPolicy.Rids
            .Select(rid => Path.Combine(directory, rid))
            .ToHashSet(StringComparer.Ordinal);
        if (entries.Length != expectedDirectories.Count
            || entries.Any(path => !Directory.Exists(path)
                                   || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                                   || !expectedDirectories.Contains(path)))
        {
            throw new InvalidDataException(
                "Release-set input must contain exactly the six canonical RID directories and no other entries.");
        }
        if (File.Exists(receiptPath) || Directory.Exists(receiptPath))
        {
            throw new IOException($"Release-set receipt already exists: '{receiptPath}'.");
        }
        var relativeReceiptPath = Path.GetRelativePath(directory, receiptPath);
        if (!relativeReceiptPath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathFullyQualified(relativeReceiptPath))
        {
            throw new InvalidDataException("Release-set receipt must be outside the six-RID archive root.");
        }
        if (!Directory.Exists(nativeReceiptsDirectory)
            || (File.GetAttributes(nativeReceiptsDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Native receipt input '{nativeReceiptsDirectory}' must be a non-link directory.");
        }
        var expectedReceiptPaths = ExecutionRuntimeLockPolicy.Rids
            .Select(rid => Path.Combine(nativeReceiptsDirectory, rid + ".json"))
            .ToHashSet(StringComparer.Ordinal);
        var receiptEntries = Directory.EnumerateFileSystemEntries(nativeReceiptsDirectory).ToArray();
        if (receiptEntries.Length != expectedReceiptPaths.Count
            || receiptEntries.Any(path => !File.Exists(path)
                                          || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                                          || !expectedReceiptPaths.Contains(path)))
        {
            throw new InvalidDataException(
                "Native receipt input must contain exactly one canonical receipt for each of the six RIDs.");
        }
        var relativeReceiptToNativeReceipts = Path.GetRelativePath(nativeReceiptsDirectory, receiptPath);
        if (!relativeReceiptToNativeReceipts.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            && !Path.IsPathFullyQualified(relativeReceiptToNativeReceipts))
        {
            throw new InvalidDataException("Release-set receipt must be outside the native receipt input directory.");
        }

        var snapshot = await ExecutionRuntimeLockLoader.LoadSnapshotAsync(lockPath, kind, cancellationToken);
        var releaseDriversSnapshot = await ReadFileSnapshotAsync(
            releaseDriversPath,
            "Release-driver identity document",
            32 * 1024,
            cancellationToken);
        var releaseDrivers = StrictJson.Deserialize<ReleaseDriverIdentityDocument>(
            releaseDriversSnapshot.Bytes,
            "Release-driver identity document");
        ValidateReleaseDriverIdentities(releaseDrivers);
        var releaseDriverByRid = releaseDrivers.Drivers!.ToDictionary(
            static driver => driver.Rid!,
            StringComparer.Ordinal);
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "Sunder.ExecutionRuntime.Build",
            "validate-release-set",
            Guid.NewGuid().ToString("N"));
        var artifacts = new List<NativeOutputArtifactReceipt>();
        var nativeOutputReceipts = new List<NativeOutputReceiptReference>();
        var releaseKeyRoot = Required(options, "--release-public-keys");
        var attestationKeyRoot = Required(options, "--attestation-public-keys");
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            foreach (var rid in ExecutionRuntimeLockPolicy.Rids)
            {
                var expectedDriver = releaseDriverByRid[rid];
                var sourceLaneReceiptPath = Path.Combine(nativeReceiptsDirectory, rid + ".json");
                var sourceLaneReceipt = await ReadFileSnapshotAsync(
                    sourceLaneReceiptPath,
                    $"Native-output receipt for RID '{rid}'",
                    1024 * 1024,
                    cancellationToken);
                var laneReceipt = StrictJson.Deserialize<NativeOutputReceipt>(
                    sourceLaneReceipt.Bytes,
                    $"Native-output receipt for RID '{rid}'");
                ValidateNativeOutputReceipt(
                    laneReceipt,
                    snapshot.Sha256,
                    rid,
                    expectedDriver.Sha256!);

                var verifiedLaneReceiptPath = Path.Combine(temporaryRoot, rid + ".json");
                await ValidateNativeOutputAsync(
                    [
                        "--lock", lockPath,
                        "--kind", kind,
                        "--rid", rid,
                        "--directory", Path.Combine(directory, rid),
                        "--release-public-keys", releaseKeyRoot,
                        "--attestation-public-keys", attestationKeyRoot,
                        "--release-driver-sha256", expectedDriver.Sha256!,
                        "--receipt", verifiedLaneReceiptPath,
                    ],
                    TextWriter.Null,
                    verifier,
                    cancellationToken);
                var verifiedLaneReceipt = await ReadFileSnapshotAsync(
                    verifiedLaneReceiptPath,
                    $"Independently generated native-output receipt for RID '{rid}'",
                    1024 * 1024,
                    cancellationToken);
                if (!sourceLaneReceipt.Bytes.AsSpan().SequenceEqual(verifiedLaneReceipt.Bytes))
                {
                    throw new InvalidDataException(
                        $"Native-output receipt for RID '{rid}' does not exactly match independent archive inspection and the reviewed release-driver digest.");
                }
                artifacts.AddRange(laneReceipt.Artifacts!);
                nativeOutputReceipts.Add(new NativeOutputReceiptReference(
                    rid,
                    expectedDriver.Sha256!,
                    Path.GetFileName(sourceLaneReceiptPath),
                    sourceLaneReceipt.Sha256));
            }
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot);
        }

        var expectedIdentities = snapshot.Document.Entries!
            .Select(entry => $"{entry!.Id}/{entry.Version}/{entry.PackagingRevision}/{entry.Rid}")
            .ToHashSet(StringComparer.Ordinal);
        var actualIdentities = artifacts
            .Select(artifact => $"{artifact.RuntimeId}/{artifact.Version}/{artifact.PackagingRevision}/{artifact.Rid}")
            .ToArray();
        if (artifacts.Count != expectedIdentities.Count
            || actualIdentities.Distinct(StringComparer.Ordinal).Count() != artifacts.Count
            || !expectedIdentities.SetEquals(actualIdentities))
        {
            throw new InvalidDataException(
                "Release set is partial, duplicated, mixed, or does not match the exact 12-entry lock transaction.");
        }

        await WriteJsonAtomicAsync(
            receiptPath,
            new ReleaseSetReceipt(
                1,
                "https://sunderapp.io/acceptance/runtime-release-set/v1",
                snapshot.Sha256,
                releaseDriversSnapshot.Sha256,
                ExecutionRuntimeLockPolicy.Rids,
                releaseDrivers.Drivers!,
                nativeOutputReceipts,
                artifacts
                    .OrderBy(static artifact => artifact.RuntimeId, StringComparer.Ordinal)
                    .ThenBy(static artifact => artifact.Rid, StringComparer.Ordinal)
                    .ToArray()),
            cancellationToken);
        await output.WriteLineAsync(
            $"Validated the exact all-six-RID, 12-artifact release transaction and wrote '{receiptPath}'.");
        return 0;
    }

    private static async Task<int> ValidateLockAsync(
        string[] args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(args, ["--lock", "--kind"]);
        var path = Required(options, "--lock");
        var kind = RequiredKind(options);
        var document = await ExecutionRuntimeLockLoader.LoadAsync(path, kind, cancellationToken);
        await output.WriteLineAsync(
            $"Validated {document.Entries!.Count} curated execution-runtime entries from {document.LockKind} lock '{path}'.");
        return 0;
    }

    private static async Task<int> CheckPlatformCatalogMigrationAsync(
        string[] args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(args, ["--descriptor"]);
        var bytes = await SigningFileOperations.ReadStableAsync(
            Required(options, "--descriptor"), SunderRpcProtocol.MaximumDescriptorBytes, cancellationToken);
        SunderRpcContractDescriptor descriptor;
        try
        {
            descriptor = SunderRpcContractDescriptor.Parse(bytes);
        }
        catch (SunderRpcDescriptorException exception)
        {
            throw new InvalidDataException("Invalid Stack migration descriptor.", exception);
        }
        // Reviewed migration evidence for the real SDK Stack contract, not the synthetic catalog ping fixture.
        if (descriptor.ContractId != "sunder.stack.contributor" || descriptor.Version != "2.0.0"
            || descriptor.DescriptorVersion != 1 || descriptor.SchemaProfile != "sunder.rpc.values.v1"
            || descriptor.Sha256 != "38c8f49840f951aec29398d3641ff2306c906aee12ea80c2277db734bce49898"
            || descriptor.AbiSha256 != "6ca6d6385c720290548592de890f3e3ceb86f3d999167f4e80c6afb15f8a02d8"
            || descriptor.Services.Count != 1 || descriptor.Services[0].ServiceId != "stack-contributor"
            || !descriptor.Services[0].Methods.Select(static method => method.MethodId).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { "export", "import", "import-applied", "list-export-items", "metadata", "preview-import" }, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Stack migration descriptor does not match the reviewed identity, descriptor/ABI hashes, and six-method inventory.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteLineAsync(
            "Stack migration descriptor verified only: sunder.stack.contributor 2.0.0, six methods. No source-derived catalog or release authorization was produced.");
        return 0;
    }

    private static async Task<int> CoreStackTransportAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is not ("generate" or "generate-host"))
            throw new CliUsageException("core-stack-transport requires generate or generate-host.");
        var options = ParseOptions(args[1..], ["--core-root", "--request", "--request-sha256", "--request-stamp"]);
        var root = Required(options, "--core-root");
        var request = new CoreStackTransportRequestFile(Required(options, "--request"),
            Required(options, "--request-sha256"), Required(options, "--request-stamp"));
        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteLineAsync(CoreStackTransportGeneration.Generate(root, request, cancellationToken, hostClient: args[0] == "generate-host"));
        return 0;
    }

    private static async Task<int> PlatformContractsAsync(
        string[] args, TextWriter output, CancellationToken cancellationToken, Action<string>? beforePublication)
    {
        if (args.Length == 0) throw new CliUsageException("platform-contracts requires check, accept, freeze, export, or import-publication.");
        string[] allowed = args[0] switch
        {
            "check" => ["--core-root"],
            "accept" => ["--core-root", "--key", "--current-version", "--next-version", "--acknowledge-breaking-change"],
            "freeze" => ["--core-root", "--key", "--catalog-release"],
            "export" => ["--core-root", "--catalog-release", "--output"],
            "import-publication" => ["--core-root", "--catalog", "--registry"],
            _ => throw new CliUsageException($"Unknown Core platform contract action '{args[0]}'."),
        };
        var options = ParseOptions(args[1..], allowed);
        var coreRoot = Required(options, "--core-root");
        options.TryGetValue("--key", out var key);
        options.TryGetValue("--current-version", out var current);
        options.TryGetValue("--next-version", out var next);
        var breaking = false;
        if (options.TryGetValue("--acknowledge-breaking-change", out var acknowledgement)
            && !bool.TryParse(acknowledgement, out breaking))
            throw new CliUsageException("--acknowledge-breaking-change requires true or false.");
        if (current is not null && !SemanticVersion.TryParse(current, out _)
            || next is not null && !SemanticVersion.TryParse(next, out _)
            || current is not null && next is null
            || key is null && (current is not null || next is not null || breaking)
            || breaking && (current is null || next is null))
            throw new CliUsageException("Acceptance version selection requires an exact key and old/new semantic version pair; only initial contracts allow next-version without current-version.");
        var release = args[0] is "freeze" or "export" ? Required(options, "--catalog-release") : null;
        if (release is not null && (!SemanticVersion.TryParse(release, out var version) || version.Prerelease is not null))
            throw new CliUsageException("--catalog-release requires an exact stable semantic version.");
        var destination = args[0] == "export" ? Required(options, "--output") : null;

        var operation = new PlatformContractSourceOperations(coreRoot, cancellationToken);
        if (args[0] == "check")
        {
            operation.Check();
            await output.WriteLineAsync("Core platform contracts match actual source; no files changed.");
        }
        else if (args[0] == "export")
        {
            await operation.WriteCatalogAsync(release!, destination!, beforePublication);
            await output.WriteLineAsync($"Exported canonical Core platform catalog '{destination}' for administrator publication to the selected Registry.");
        }
        else if (args[0] == "import-publication")
        {
            var changed = await operation.ImportPublicationAsync(Required(options, "--catalog"), Required(options, "--registry"), beforePublication);
            await output.WriteLineAsync(changed ? "Imported verified Registry platform publication evidence." : "Platform publication evidence is unchanged.");
        }
        else
        {
            var changed = args[0] == "accept"
                ? operation.Accept(key, current, next, breaking, beforePublication)
                : operation.Freeze(release!, key, beforePublication);
            await output.WriteLineAsync(changed
                ? $"Core platform {args[0]} updated eng/platform-contracts/sunder-contracts.owned.json; no publication state was produced."
                : "Core platform baseline is unchanged; no files changed.");
        }
        return 0;
    }

    private static async Task<int> ValidateSourceAsync(
        string[] args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(
            args,
            ["--lock", "--kind", "--id", "--version", "--rid", "--source"]);
        var lockPath = Required(options, "--lock");
        var kind = RequiredKind(options);
        var id = Required(options, "--id");
        var version = Required(options, "--version");
        var rid = Required(options, "--rid");
        var sourcePath = Required(options, "--source");
        var document = await ExecutionRuntimeLockLoader.LoadAsync(lockPath, kind, cancellationToken);
        var matches = document.Entries!
            .Where(entry => entry is not null
                            && string.Equals(entry.Id, id, StringComparison.Ordinal)
                            && string.Equals(entry.Version, version, StringComparison.Ordinal)
                            && string.Equals(entry.Rid, rid, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"Lock must contain exactly one source for '{id}' version '{version}' RID '{rid}'.");
        }

        await ExecutionRuntimeSourceMaterializer.ValidateAsync(
            document.LockKind!,
            matches[0]!,
            sourcePath,
            cancellationToken);
        await output.WriteLineAsync(
            $"Validated locked source bytes for '{id}' version '{version}' RID '{rid}'.");
        return 0;
    }

    private static async Task<int> ValidateNativeLaneAsync(
        string[] args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(args, ["--lock", "--kind", "--rid", "--runner-profile"]);
        var lockPath = Required(options, "--lock");
        var kind = RequiredKind(options);
        var rid = Required(options, "--rid");
        var runnerProfile = Required(options, "--runner-profile");
        var document = await ExecutionRuntimeLockLoader.LoadAsync(lockPath, kind, cancellationToken);
        var entries = document.Entries!
            .Where(entry => entry is not null && string.Equals(entry.Rid, rid, StringComparison.Ordinal))
            .ToArray();
        if (entries.Length != ExecutionRuntimeLockPolicy.RuntimeFamilies.Count)
        {
            throw new InvalidDataException(
                $"Native RID lane '{rid}' must contain exactly one entry for each curated runtime family.");
        }
        if (entries.Any(entry => !string.Equals(
                entry!.Trust!.NativeRunner,
                runnerProfile,
                StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Native RID lane '{rid}' is not bound to runner profile '{runnerProfile}'.");
        }

        await output.WriteLineAsync(
            $"Validated native RID lane '{rid}' against runner profile '{runnerProfile}'.");
        return 0;
    }

    private static Dictionary<string, string> ParseOptions(string[] args, string[] allowed)
    {
        if (args.Length == 0 || args.Length % 2 != 0)
        {
            throw new CliUsageException("Every command option requires exactly one value.");
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var name = args[index];
            var value = args[index + 1];
            if (!allowed.Contains(name, StringComparer.Ordinal))
            {
                throw new CliUsageException($"Unknown option '{name}'.");
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CliUsageException($"Option '{name}' requires a non-empty value.");
            }
            if (!options.TryAdd(name, value))
            {
                throw new CliUsageException($"Option '{name}' may be specified only once.");
            }
        }
        return options;
    }

    private static ExecutionRuntimeLockEntry FindEntry(
        ExecutionRuntimeLockDocument document,
        string id,
        string version,
        string rid)
    {
        var matches = document.Entries!
            .Where(entry => entry is not null
                            && string.Equals(entry.Id, id, StringComparison.Ordinal)
                            && string.Equals(entry.Version, version, StringComparison.Ordinal)
                            && string.Equals(entry.Rid, rid, StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1
            ? matches[0]!
            : throw new InvalidDataException(
                $"Lock must contain exactly one entry for '{id}' version '{version}' RID '{rid}'.");
    }

    private static bool MatchesExactLockEntry(
        ExecutionRuntimeLockEntry entry,
        SunderExecutionRuntimeArchiveValidationResult result)
    {
        var manifest = result.Manifest;
        var attestation = result.NativeAttestation;
        var expectedPlatformSignature = entry.Trust!.PlatformSignature == ExecutionRuntimeLockPolicy.RequiredTrustCheck
            ? SunderExecutionRuntimeFormat.PlatformCheckPassed
            : SunderExecutionRuntimeFormat.PlatformCheckNotApplicable;
        var expectedNotarization = entry.Trust.Notarization == ExecutionRuntimeLockPolicy.RequiredTrustCheck
            ? SunderExecutionRuntimeFormat.PlatformCheckPassed
            : SunderExecutionRuntimeFormat.PlatformCheckNotApplicable;
        return result.Success
               && result.SignatureVerified
               && result.NativeAttestationSignatureVerified
               && manifest is not null
               && attestation is not null
               && string.Equals(manifest.Id, entry.Id, StringComparison.Ordinal)
               && string.Equals(manifest.Version, entry.Version, StringComparison.Ordinal)
               && manifest.PackagingRevision == entry.PackagingRevision
               && string.Equals(manifest.Rid, entry.Rid, StringComparison.Ordinal)
               && string.Equals(manifest.ModuleAbi, entry.Profile!.ModuleAbi, StringComparison.Ordinal)
               && string.Equals(manifest.WorkerProtocol, entry.Profile.WorkerProtocol, StringComparison.Ordinal)
               && string.Equals(manifest.Launch!.Executable, entry.Profile.LaunchExecutable, StringComparison.Ordinal)
               && manifest.Launch.Arguments!.SequenceEqual(entry.Profile.LaunchArguments!, StringComparer.Ordinal)
               && string.Equals(manifest.Upstream!.Name, entry.Source!.Name, StringComparison.Ordinal)
               && string.Equals(manifest.Upstream.Version, entry.Version, StringComparison.Ordinal)
               && string.Equals(manifest.Upstream.SourceUri, entry.Source.Uri, StringComparison.Ordinal)
               && string.Equals(manifest.Upstream.SourceSha256, entry.Source.Sha256, StringComparison.Ordinal)
               && manifest.Licenses!.SequenceEqual(entry.License!.Spdx!, StringComparer.Ordinal)
               && string.Equals(manifest.Signing!.KeyId, entry.Trust.ReleaseSigningKeyId, StringComparison.Ordinal)
               && string.Equals(manifest.Provenance!.AttestationKeyId, entry.Trust.AttestationSigningKeyId, StringComparison.Ordinal)
               && string.Equals(attestation.RuntimeId, entry.Id, StringComparison.Ordinal)
               && string.Equals(attestation.RuntimeVersion, entry.Version, StringComparison.Ordinal)
               && attestation.PackagingRevision == entry.PackagingRevision
               && string.Equals(attestation.Rid, entry.Rid, StringComparison.Ordinal)
               && string.Equals(attestation.UpstreamSha256, entry.Source.Sha256, StringComparison.Ordinal)
               && string.Equals(attestation.Workflow!.Name, entry.Trust.AttestationWorkflow, StringComparison.Ordinal)
               && string.Equals(attestation.Toolchain!.Name, entry.Recipe!.Toolchain!.Name, StringComparison.Ordinal)
               && string.Equals(attestation.Toolchain.Version, entry.Recipe.Toolchain.Version, StringComparison.Ordinal)
               && string.Equals(attestation.Platform!.Signature, expectedPlatformSignature, StringComparison.Ordinal)
               && string.Equals(attestation.Platform.Notarization, expectedNotarization, StringComparison.Ordinal)
               && string.Equals(attestation.Signing!.KeyId, entry.Trust.AttestationSigningKeyId, StringComparison.Ordinal);
    }

    private static async Task<SunderExecutionRuntimeTrustedKey> ReadTrustedKeyAsync(
        string path,
        string keyId,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Trusted public key '{fullPath}' must be a non-link regular file.");
        }
        var observed = new FileInfo(fullPath);
        if (observed.Length != SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes)
        {
            throw new InvalidDataException(
                $"Trusted public key '{fullPath}' must contain exactly {SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes} raw bytes.");
        }
        var writeTime = observed.LastWriteTimeUtc;
        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        observed.Refresh();
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0
            || observed.Length != bytes.Length
            || observed.LastWriteTimeUtc != writeTime)
        {
            throw new InvalidDataException($"Trusted public key '{fullPath}' changed while being read.");
        }
        return new SunderExecutionRuntimeTrustedKey(keyId, bytes);
    }

    private static Task<SunderExecutionRuntimeTrustedKey> ReadTrustedKeyFromDirectoryAsync(
        string directory,
        string keyId,
        CancellationToken cancellationToken)
    {
        var fullDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(fullDirectory)
            || (File.GetAttributes(fullDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Trusted public-key root '{fullDirectory}' must be a non-link directory.");
        }
        return ReadTrustedKeyAsync(
            Path.Combine(fullDirectory, keyId + ".ed25519.pub"),
            keyId,
            cancellationToken);
    }

    private static async Task<FileSnapshot> ReadFileSnapshotAsync(
        string path,
        string label,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{label} '{fullPath}' must be a non-link regular file.");
        }
        var observed = new FileInfo(fullPath);
        var observedLength = observed.Length;
        var observedWriteTime = observed.LastWriteTimeUtc;
        if (observedLength is <= 0 || observedLength > maximumBytes)
        {
            throw new InvalidDataException($"{label} must contain between 1 and {maximumBytes} bytes.");
        }
        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        observed.Refresh();
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0
            || observed.Length != observedLength
            || observed.LastWriteTimeUtc != observedWriteTime
            || bytes.Length != observedLength)
        {
            throw new InvalidDataException($"{label} changed while being snapshotted.");
        }
        return new FileSnapshot(
            bytes,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static void ValidateReleaseDriverIdentities(ReleaseDriverIdentityDocument document)
    {
        if (document.SchemaVersion != 1
            || !string.Equals(
                document.Kind,
                "https://sunderapp.io/acceptance/runtime-release-drivers/v1",
                StringComparison.Ordinal)
            || document.Drivers is null
            || document.Drivers.Count != ExecutionRuntimeLockPolicy.Rids.Count
            || !document.Drivers.Select(static driver => driver.Rid)
                .SequenceEqual(ExecutionRuntimeLockPolicy.Rids, StringComparer.Ordinal)
            || document.Drivers.Any(driver => driver is null
                                              || !IsLowerSha256(driver.Sha256)))
        {
            throw new InvalidDataException(
                "Release-driver identity document must contain the exact ordered six-RID reviewed SHA-256 set.");
        }
    }

    private static void ValidateNativeOutputReceipt(
        NativeOutputReceipt receipt,
        string runtimeLockSha256,
        string rid,
        string releaseDriverSha256)
    {
        if (receipt.SchemaVersion != 1
            || !string.Equals(
                receipt.Kind,
                "https://sunderapp.io/acceptance/runtime-native-output/v1",
                StringComparison.Ordinal)
            || !string.Equals(receipt.RuntimeLockSha256, runtimeLockSha256, StringComparison.Ordinal)
            || !string.Equals(receipt.Rid, rid, StringComparison.Ordinal)
            || !string.Equals(receipt.ReleaseDriverSha256, releaseDriverSha256, StringComparison.Ordinal)
            || receipt.Artifacts is null
            || receipt.Artifacts.Count != ExecutionRuntimeLockPolicy.RuntimeFamilies.Count)
        {
            throw new InvalidDataException(
                $"Native-output receipt for RID '{rid}' does not bind the exact lock, RID, artifacts, and reviewed release-driver digest.");
        }
    }

    private static bool IsLowerSha256(string? value)
        => value is { Length: 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(path)
                     ?? throw new InvalidDataException("Receipt path must have a parent directory.");
        Directory.CreateDirectory(parent);
        var temporaryPath = Path.Combine(parent, $".{Path.GetFileName(path)}.write-{Guid.NewGuid():N}");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, path);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name)
        => options.TryGetValue(name, out var value)
            ? value
            : throw new CliUsageException($"Option '{name}' is required.");

    private static string RequiredSha256(IReadOnlyDictionary<string, string> options, string name)
    {
        var value = Required(options, name);
        return IsLowerSha256(value)
            ? value
            : throw new CliUsageException($"Option '{name}' must be one exact lower-case SHA-256 digest.");
    }

    private static string? DevelopmentStateRoot(IReadOnlyDictionary<string, string> options)
    {
        options.TryGetValue("--state-root", out var explicitRoot);
        var configuredRoot = DevelopmentExecutionRuntimeKeyStore.ConsumeConfiguredStateRoot();
        if (explicitRoot is not null && configuredRoot is not null)
        {
            throw new CliUsageException(
                $"Specify Development state through either '--state-root' or {DevelopmentExecutionRuntimeKeyStore.StateRootEnvironmentVariable}, not both.");
        }
        return explicitRoot ?? configuredRoot;
    }

    private static string RequiredKind(IReadOnlyDictionary<string, string> options)
    {
        var kind = Required(options, "--kind");
        return kind is ExecutionRuntimeLockPolicy.ProductionKind or ExecutionRuntimeLockPolicy.TestFixtureKind
            ? kind
            : throw new CliUsageException("Option '--kind' must be 'production' or 'test-fixture'.");
    }

    private static string Usage()
        => """
           Usage:
             Sunder.ExecutionRuntime.Build validate-lock --lock <path> --kind <production|test-fixture>
             Sunder.ExecutionRuntime.Build validate-source --lock <path> --kind <production|test-fixture> --id <runtime-id> --version <version> --rid <rid> --source <path>
              Sunder.ExecutionRuntime.Build validate-native-lane --lock <path> --kind <production|test-fixture> --rid <rid> --runner-profile <profile>
              Sunder.ExecutionRuntime.Build development-keys <ensure|rotate|reset> [--state-root <isolated-directory>]
              Sunder.ExecutionRuntime.Build development-build --lock <production-lock> --id <runtime-id> --version <version> --rid <rid> --source <path> --native-attestation <path> --output <path> [--state-root <isolated-directory>]
              Sunder.ExecutionRuntime.Build development-publish --registry <loopback-origin> --directory <six-rid-release-directory> [--state-root <isolated-directory>]
              Sunder.ExecutionRuntime.Build build --lock <path> --kind <production|test-fixture> --id <runtime-id> --version <version> --rid <rid> --source <path> --native-attestation <path> --output <path> --release-public-key <raw-32-byte-path> --attestation-public-key <raw-32-byte-path> --signer-adapter <path> --signer-adapter-sha256 <sha256>
             Sunder.ExecutionRuntime.Build inspect --lock <path> --kind <production|test-fixture> --id <runtime-id> --version <version> --rid <rid> --archive <path> --release-public-key <raw-32-byte-path> --attestation-public-key <raw-32-byte-path>
             Sunder.ExecutionRuntime.Build validate-native-output --lock <path> --kind <production|test-fixture> --rid <rid> --directory <path> --release-public-keys <directory> --attestation-public-keys <directory> --release-driver-sha256 <sha256> --receipt <path>
             Sunder.ExecutionRuntime.Build validate-release-set --lock <path> --kind <production|test-fixture> --directory <six-rid-root> --native-receipts <six-rid-receipt-root> --release-drivers <six-rid-driver-json> --release-public-keys <directory> --attestation-public-keys <directory> --receipt <path>
             Sunder.ExecutionRuntime.Build check-platform-catalog-migration --descriptor <real-sdk-stack-descriptor-path>
             Sunder.ExecutionRuntime.Build platform-contracts check --core-root <root>
             Sunder.ExecutionRuntime.Build platform-contracts accept --core-root <root> [--key <key>] [--current-version <old> --next-version <next>] [--acknowledge-breaking-change <true|false>]
             Sunder.ExecutionRuntime.Build platform-contracts freeze --core-root <root> --catalog-release <stable-version> [--key <key>]
             Sunder.ExecutionRuntime.Build platform-contracts export --core-root <root> --catalog-release <stable-version> --output <new-path>
             Sunder.ExecutionRuntime.Build platform-contracts import-publication --core-root <root> --catalog <retained-catalog> --registry <https-origin>
             Sunder.ExecutionRuntime.Build core-stack-transport generate --core-root <root> --request <evaluated-obj-request> --request-sha256 <writer-hash> --request-stamp <writer-stamp>
             Sunder.ExecutionRuntime.Build core-stack-transport generate-host --core-root <root> --request <evaluated-obj-request> --request-sha256 <writer-hash> --request-stamp <writer-stamp>

           Core platform bootstrap requires --key stack-contributor-rpc-service-adapter --next-version 2.0.0 without --current-version.
           Core platform commands never sign or authorize releases. Payload output never overwrites an existing path.

           Private signing keys and private-key file paths are never accepted. Development commands use only the isolated
           managed state root. The production build command invokes an approved, SHA-256-pinned signer adapter with
           signature input on stdin and requires exactly 64 raw signature bytes on stdout.
           """;

    private sealed class CliUsageException(string message) : Exception(message);

    private sealed record NativeOutputReceipt(
        int SchemaVersion,
        string Kind,
        string RuntimeLockSha256,
        string Rid,
        string ReleaseDriverSha256,
        IReadOnlyList<NativeOutputArtifactReceipt> Artifacts);

    private sealed record NativeOutputArtifactReceipt(
        string RuntimeId,
        string Version,
        int PackagingRevision,
        string Rid,
        string ModuleAbi,
        string WorkerProtocol,
        long MinimumHostTrustGeneration,
        string ReleaseSigningKeyId,
        string NativeAttestationKeyId,
        string ArchiveFile,
        string ArchiveSha256,
        long ArchiveSize,
        string ManifestSha256,
        string ContentIdentity,
        string NativeAttestationSha256,
        bool ReleaseSignatureVerified,
        bool NativeAttestationVerified);

    private sealed record ReleaseSetReceipt(
        int SchemaVersion,
        string Kind,
        string RuntimeLockSha256,
        string ReleaseDriversSha256,
        IReadOnlyList<string> Rids,
        IReadOnlyList<ReleaseDriverIdentity> ReleaseDrivers,
        IReadOnlyList<NativeOutputReceiptReference> NativeOutputReceipts,
        IReadOnlyList<NativeOutputArtifactReceipt> Artifacts);

    private sealed record ReleaseDriverIdentityDocument(
        int SchemaVersion,
        string Kind,
        IReadOnlyList<ReleaseDriverIdentity>? Drivers);

    private sealed record ReleaseDriverIdentity(
        string? Rid,
        string? Sha256);

    private sealed record NativeOutputReceiptReference(
        string Rid,
        string ReleaseDriverSha256,
        string ReceiptFile,
        string ReceiptSha256);

    private sealed record FileSnapshot(
        byte[] Bytes,
        string Sha256);
}
