using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.ExecutionRuntime.Build.Tests;

public sealed class ExecutionRuntimeArchiveBuilderTests
{
    [Fact]
    public async Task Builder_ProducesByteIdenticalValidatedArchivesForEveryLockedRuntime()
    {
        var document = await ExecutionRuntimeLockLoader.LoadAsync(
            FixtureArtifacts.LockPath,
            ExecutionRuntimeLockPolicy.TestFixtureKind);
        var goldenHashes = File.ReadAllLines(FixtureArtifacts.ArchiveGoldensPath)
            .Where(static line => line.Length > 0)
            .ToArray();
        var actualHashes = new List<string>();

        foreach (var entry in document.Entries!)
        {
            var root = FixtureArtifacts.CreateTempDirectory();
            try
            {
                var sourcePath = await FixtureArtifacts.WriteSourceAsync(entry!, root);
                Assert.Equal(
                    entry!.Source!.Sha256,
                    await ExecutionRuntimeSourceMaterializer.HashFileAsync(sourcePath));
                var attestationPath = await FixtureArtifacts.WriteAttestationAsync(entry, root);
                var firstPath = Path.Combine(root, "first.sunderruntime");
                var secondPath = Path.Combine(root, "second.sunderruntime");
                var signer = new FixtureSigner(entry.Trust!.ReleaseSigningKeyId!, FixtureArtifacts.ReleaseKey);
                var verifier = new FixtureVerifier();
                var releaseKey = new SunderExecutionRuntimeTrustedKey(
                    entry.Trust.ReleaseSigningKeyId!,
                    FixtureArtifacts.ReleaseKey);
                var attestationKey = new SunderExecutionRuntimeTrustedKey(
                    entry.Trust.AttestationSigningKeyId!,
                    FixtureArtifacts.AttestationKey);

                await ExecutionRuntimeArchiveBuilder.BuildAsync(
                    FixtureArtifacts.LockPath,
                    ExecutionRuntimeLockPolicy.TestFixtureKind,
                    entry.Id!,
                    entry.Version!,
                    entry.Rid!,
                    sourcePath,
                    attestationPath,
                    firstPath,
                    signer,
                    verifier,
                    releaseKey,
                    attestationKey);
                await ExecutionRuntimeArchiveBuilder.BuildAsync(
                    FixtureArtifacts.LockPath,
                    ExecutionRuntimeLockPolicy.TestFixtureKind,
                    entry.Id!,
                    entry.Version!,
                    entry.Rid!,
                    sourcePath,
                    attestationPath,
                    secondPath,
                    signer,
                    verifier,
                    releaseKey,
                    attestationKey);

                Assert.Equal(await File.ReadAllBytesAsync(firstPath), await File.ReadAllBytesAsync(secondPath));
                actualHashes.Add(
                    $"{entry.Id}/{entry.Rid} {await ExecutionRuntimeSourceMaterializer.HashFileAsync(firstPath)}");
                var result = await SunderExecutionRuntimeArchiveInspector.InspectAsync(
                    firstPath,
                    Path.Combine(root, "validated.sunderruntime"),
                    Path.Combine(root, "staging"),
                    new FixtureVerifier(),
                    releaseKey,
                    new FixtureVerifier(),
                    attestationKey);
                Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
                Assert.True(result.SignatureVerified);
                Assert.True(result.NativeAttestationSignatureVerified);
                Assert.Equal(entry.Id, result.Manifest!.Id);
                Assert.Equal(entry.Version, result.Manifest.Version);
                Assert.Equal(entry.Rid, result.Manifest.Rid);
                Assert.Equal(entry.Source.Uri, result.Manifest.Upstream!.SourceUri);
                Assert.Equal(entry.Source.Sha256, result.Manifest.Upstream.SourceSha256);
                Assert.Equal(entry.Profile!.LaunchExecutable, result.Manifest.Launch!.Executable);
                Assert.Equal(4, result.ContentInventory.Count);
            }
            finally
            {
                FixtureArtifacts.DeleteDirectory(root);
            }
        }
        Assert.Equal(goldenHashes, actualHashes);
    }

    [Fact]
    public async Task SourceValidation_RejectsTamperedLockedBytesBeforeExtraction()
    {
        var document = await ExecutionRuntimeLockLoader.LoadAsync(FixtureArtifacts.LockPath);
        var entry = document.Entries![0]!;
        var root = FixtureArtifacts.CreateTempDirectory();
        try
        {
            var sourcePath = await FixtureArtifacts.WriteSourceAsync(entry, root);
            await File.AppendAllTextAsync(sourcePath, "tamper");

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => ExecutionRuntimeSourceMaterializer.ValidateAsync(
                    document.LockKind!,
                    entry,
                    sourcePath));
            Assert.Contains("SHA-256 mismatch", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Builder_RejectsSignerOutsideTheLockedTrustIdentity()
    {
        var document = await ExecutionRuntimeLockLoader.LoadAsync(FixtureArtifacts.LockPath);
        var entry = document.Entries![0]!;
        var root = FixtureArtifacts.CreateTempDirectory();
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => ExecutionRuntimeArchiveBuilder.BuildAsync(
                    FixtureArtifacts.LockPath,
                    ExecutionRuntimeLockPolicy.TestFixtureKind,
                    entry.Id!,
                    entry.Version!,
                    entry.Rid!,
                    Path.Combine(root, "unused-source"),
                    Path.Combine(root, "unused-attestation"),
                    Path.Combine(root, "unused-output"),
                    new FixtureSigner("sunder.release.test.wrong", FixtureArtifacts.ReleaseKey),
                    new FixtureVerifier(),
                    new SunderExecutionRuntimeTrustedKey(
                        entry.Trust!.ReleaseSigningKeyId!,
                        FixtureArtifacts.ReleaseKey),
                    new SunderExecutionRuntimeTrustedKey(
                        entry.Trust.AttestationSigningKeyId!,
                        FixtureArtifacts.AttestationKey)));
            Assert.Contains("does not match locked release key", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Builder_RejectsUnverifiedNativeAttestationWithoutPublishingOutput()
    {
        var document = await ExecutionRuntimeLockLoader.LoadAsync(FixtureArtifacts.LockPath);
        var entry = document.Entries![0]!;
        var root = FixtureArtifacts.CreateTempDirectory();
        try
        {
            var sourcePath = await FixtureArtifacts.WriteSourceAsync(entry, root);
            var attestationPath = await FixtureArtifacts.WriteAttestationAsync(entry, root);
            var attestation = JsonNode.Parse(await File.ReadAllTextAsync(attestationPath))!.AsObject();
            var signature = Convert.FromBase64String(attestation["signing"]!["signature"]!.GetValue<string>());
            signature[0] ^= 1;
            attestation["signing"]!["signature"] = Convert.ToBase64String(signature);
            await File.WriteAllTextAsync(attestationPath, attestation.ToJsonString());
            var outputPath = Path.Combine(root, "rejected.sunderruntime");

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => ExecutionRuntimeArchiveBuilder.BuildAsync(
                    FixtureArtifacts.LockPath,
                    ExecutionRuntimeLockPolicy.TestFixtureKind,
                    entry.Id!,
                    entry.Version!,
                    entry.Rid!,
                    sourcePath,
                    attestationPath,
                    outputPath,
                    new FixtureSigner(entry.Trust!.ReleaseSigningKeyId!, FixtureArtifacts.ReleaseKey),
                    new FixtureVerifier(),
                    new SunderExecutionRuntimeTrustedKey(
                        entry.Trust.ReleaseSigningKeyId!,
                        FixtureArtifacts.ReleaseKey),
                    new SunderExecutionRuntimeTrustedKey(
                        entry.Trust.AttestationSigningKeyId!,
                        FixtureArtifacts.AttestationKey)));

            Assert.Contains("does not verify", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Builder_RejectsBadReleaseSignatureDuringFinalInspectionWithoutPublishingOutput()
    {
        var document = await ExecutionRuntimeLockLoader.LoadAsync(FixtureArtifacts.LockPath);
        var entry = document.Entries![0]!;
        var root = FixtureArtifacts.CreateTempDirectory();
        try
        {
            var sourcePath = await FixtureArtifacts.WriteSourceAsync(entry, root);
            var attestationPath = await FixtureArtifacts.WriteAttestationAsync(entry, root);
            var outputPath = Path.Combine(root, "rejected.sunderruntime");

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => ExecutionRuntimeArchiveBuilder.BuildAsync(
                    FixtureArtifacts.LockPath,
                    ExecutionRuntimeLockPolicy.TestFixtureKind,
                    entry.Id!,
                    entry.Version!,
                    entry.Rid!,
                    sourcePath,
                    attestationPath,
                    outputPath,
                    new FixtureSigner(entry.Trust!.ReleaseSigningKeyId!, FixtureArtifacts.AttestationKey),
                    new FixtureVerifier(),
                    new SunderExecutionRuntimeTrustedKey(
                        entry.Trust.ReleaseSigningKeyId!,
                        FixtureArtifacts.ReleaseKey),
                    new SunderExecutionRuntimeTrustedKey(
                        entry.Trust.AttestationSigningKeyId!,
                        FixtureArtifacts.AttestationKey)));

            Assert.Contains("failed trusted final inspection", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Builder_RejectsLinkedOutputParentWithoutPublishingOutput()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var document = await ExecutionRuntimeLockLoader.LoadAsync(FixtureArtifacts.LockPath);
        var entry = document.Entries![0]!;
        var root = FixtureArtifacts.CreateTempDirectory();
        try
        {
            var sourcePath = await FixtureArtifacts.WriteSourceAsync(entry, root);
            var attestationPath = await FixtureArtifacts.WriteAttestationAsync(entry, root);
            var actualOutputRoot = Path.Combine(root, "actual-output");
            var linkedOutputRoot = Path.Combine(root, "linked-output");
            Directory.CreateDirectory(actualOutputRoot);
            Directory.CreateSymbolicLink(linkedOutputRoot, actualOutputRoot);
            var outputPath = Path.Combine(linkedOutputRoot, "rejected.sunderruntime");

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => ExecutionRuntimeArchiveBuilder.BuildAsync(
                    FixtureArtifacts.LockPath,
                    ExecutionRuntimeLockPolicy.TestFixtureKind,
                    entry.Id!,
                    entry.Version!,
                    entry.Rid!,
                    sourcePath,
                    attestationPath,
                    outputPath,
                    new FixtureSigner(entry.Trust!.ReleaseSigningKeyId!, FixtureArtifacts.ReleaseKey),
                    new FixtureVerifier(),
                    new SunderExecutionRuntimeTrustedKey(
                        entry.Trust.ReleaseSigningKeyId!,
                        FixtureArtifacts.ReleaseKey),
                    new SunderExecutionRuntimeTrustedKey(
                        entry.Trust.AttestationSigningKeyId!,
                        FixtureArtifacts.AttestationKey)));

            Assert.Contains("symbolic link", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFileSystemEntries(actualOutputRoot));
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ReleaseSetValidation_BindsAllDriverDigestsAndRejectsSubstitutionOmissionAndPartialSet()
    {
        var document = await ExecutionRuntimeLockLoader.LoadAsync(FixtureArtifacts.LockPath);
        var root = FixtureArtifacts.CreateTempDirectory();
        try
        {
            var releaseRoot = Path.Combine(root, "release");
            var nativeReceiptsRoot = Path.Combine(root, "native-receipts");
            var releaseKeys = Path.Combine(root, "release-keys");
            var attestationKeys = Path.Combine(root, "attestation-keys");
            Directory.CreateDirectory(releaseRoot);
            Directory.CreateDirectory(nativeReceiptsRoot);
            Directory.CreateDirectory(releaseKeys);
            Directory.CreateDirectory(attestationKeys);
            foreach (var keyId in document.Entries!.Select(entry => entry!.Trust!.ReleaseSigningKeyId!).Distinct())
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(releaseKeys, keyId + ".ed25519.pub"),
                    FixtureArtifacts.ReleaseKey);
            }
            foreach (var keyId in document.Entries!.Select(entry => entry!.Trust!.AttestationSigningKeyId!).Distinct())
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(attestationKeys, keyId + ".ed25519.pub"),
                    FixtureArtifacts.AttestationKey);
            }

            foreach (var entry in document.Entries!.Select(static entry => entry!))
            {
                var laneRoot = Path.Combine(releaseRoot, entry.Rid!);
                Directory.CreateDirectory(laneRoot);
                var sourcePath = await FixtureArtifacts.WriteSourceAsync(entry, root);
                var attestationPath = await FixtureArtifacts.WriteAttestationAsync(entry, root);
                await ExecutionRuntimeArchiveBuilder.BuildAsync(
                    FixtureArtifacts.LockPath,
                    ExecutionRuntimeLockPolicy.TestFixtureKind,
                    entry.Id!,
                    entry.Version!,
                    entry.Rid!,
                    sourcePath,
                    attestationPath,
                    Path.Combine(laneRoot, entry.Id + SunderExecutionRuntimeFormat.Extension),
                    new FixtureSigner(entry.Trust!.ReleaseSigningKeyId!, FixtureArtifacts.ReleaseKey),
                    new FixtureVerifier(),
                    new SunderExecutionRuntimeTrustedKey(
                        entry.Trust.ReleaseSigningKeyId!,
                        FixtureArtifacts.ReleaseKey),
                    new SunderExecutionRuntimeTrustedKey(
                        entry.Trust.AttestationSigningKeyId!,
                        FixtureArtifacts.AttestationKey));
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            var releaseDrivers = ExecutionRuntimeLockPolicy.Rids
                .Select(rid => new
                {
                    rid,
                    sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"reviewed-driver/{rid}")))
                        .ToLowerInvariant(),
                })
                .ToArray();
            var releaseDriversPath = Path.Combine(root, "release-drivers.json");
            var releaseDriversBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                kind = "https://sunderapp.io/acceptance/runtime-release-drivers/v1",
                drivers = releaseDrivers,
            });
            await File.WriteAllBytesAsync(
                releaseDriversPath,
                releaseDriversBytes);
            foreach (var driver in releaseDrivers)
            {
                var nativeExitCode = await ExecutionRuntimeBuildCli.RunAsync(
                    [
                        "validate-native-output",
                        "--lock", FixtureArtifacts.LockPath,
                        "--kind", ExecutionRuntimeLockPolicy.TestFixtureKind,
                        "--rid", driver.rid,
                        "--directory", Path.Combine(releaseRoot, driver.rid),
                        "--release-public-keys", releaseKeys,
                        "--attestation-public-keys", attestationKeys,
                        "--release-driver-sha256", driver.sha256,
                        "--receipt", Path.Combine(nativeReceiptsRoot, driver.rid + ".json"),
                    ],
                    output,
                    error,
                    new FixtureVerifier());
                Assert.Equal(0, nativeExitCode);
            }

            var receiptPath = Path.Combine(root, "release-set.json");
            var exitCode = await ExecutionRuntimeBuildCli.RunAsync(
                [
                    "validate-release-set",
                    "--lock", FixtureArtifacts.LockPath,
                    "--kind", ExecutionRuntimeLockPolicy.TestFixtureKind,
                    "--directory", releaseRoot,
                    "--native-receipts", nativeReceiptsRoot,
                    "--release-drivers", releaseDriversPath,
                    "--release-public-keys", releaseKeys,
                    "--attestation-public-keys", attestationKeys,
                    "--receipt", receiptPath,
                ],
                output,
                error,
                new FixtureVerifier());

            Assert.True(exitCode == 0, error.ToString());
            using (var receipt = JsonDocument.Parse(await File.ReadAllBytesAsync(receiptPath)))
            {
                Assert.Equal(12, receipt.RootElement.GetProperty("artifacts").GetArrayLength());
                Assert.Equal(6, receipt.RootElement.GetProperty("rids").GetArrayLength());
                Assert.Equal(6, receipt.RootElement.GetProperty("releaseDrivers").GetArrayLength());
                Assert.Equal(6, receipt.RootElement.GetProperty("nativeOutputReceipts").GetArrayLength());
                Assert.Equal(
                    await ExecutionRuntimeSourceMaterializer.HashFileAsync(releaseDriversPath),
                    receipt.RootElement.GetProperty("releaseDriversSha256").GetString());
            }

            var substitutedRid = ExecutionRuntimeLockPolicy.Rids[0];
            var substitutedReceiptPath = Path.Combine(nativeReceiptsRoot, substitutedRid + ".json");
            var originalReceiptBytes = await File.ReadAllBytesAsync(substitutedReceiptPath);
            var substitutedReceipt = JsonNode.Parse(originalReceiptBytes)!.AsObject();
            substitutedReceipt["releaseDriverSha256"] = releaseDrivers[1].sha256;
            await File.WriteAllTextAsync(substitutedReceiptPath, substitutedReceipt.ToJsonString());
            error.GetStringBuilder().Clear();
            var substitutedExitCode = await ExecutionRuntimeBuildCli.RunAsync(
                [
                    "validate-release-set",
                    "--lock", FixtureArtifacts.LockPath,
                    "--kind", ExecutionRuntimeLockPolicy.TestFixtureKind,
                    "--directory", releaseRoot,
                    "--native-receipts", nativeReceiptsRoot,
                    "--release-drivers", releaseDriversPath,
                    "--release-public-keys", releaseKeys,
                    "--attestation-public-keys", attestationKeys,
                    "--receipt", Path.Combine(root, "substituted-release-set.json"),
                ],
                output,
                error,
                new FixtureVerifier());
            Assert.Equal(1, substitutedExitCode);
            Assert.Contains("reviewed release-driver digest", error.ToString(), StringComparison.Ordinal);

            var omittedReceipt = JsonNode.Parse(originalReceiptBytes)!.AsObject();
            omittedReceipt.Remove("releaseDriverSha256");
            await File.WriteAllTextAsync(substitutedReceiptPath, omittedReceipt.ToJsonString());
            error.GetStringBuilder().Clear();
            var omittedExitCode = await ExecutionRuntimeBuildCli.RunAsync(
                [
                    "validate-release-set",
                    "--lock", FixtureArtifacts.LockPath,
                    "--kind", ExecutionRuntimeLockPolicy.TestFixtureKind,
                    "--directory", releaseRoot,
                    "--native-receipts", nativeReceiptsRoot,
                    "--release-drivers", releaseDriversPath,
                    "--release-public-keys", releaseKeys,
                    "--attestation-public-keys", attestationKeys,
                    "--receipt", Path.Combine(root, "omitted-release-set.json"),
                ],
                output,
                error,
                new FixtureVerifier());
            Assert.Equal(1, omittedExitCode);
            Assert.Contains("strict JSON", error.ToString(), StringComparison.OrdinalIgnoreCase);
            await File.WriteAllBytesAsync(substitutedReceiptPath, originalReceiptBytes);

            var mixedDrivers = JsonNode.Parse(releaseDriversBytes)!.AsObject();
            var mixedDriverEntries = mixedDrivers["drivers"]!.AsArray();
            mixedDriverEntries[0]!["sha256"] = releaseDrivers[1].sha256;
            mixedDriverEntries[1]!["sha256"] = releaseDrivers[0].sha256;
            await File.WriteAllTextAsync(releaseDriversPath, mixedDrivers.ToJsonString());
            error.GetStringBuilder().Clear();
            var mixedDriversExitCode = await ExecutionRuntimeBuildCli.RunAsync(
                [
                    "validate-release-set",
                    "--lock", FixtureArtifacts.LockPath,
                    "--kind", ExecutionRuntimeLockPolicy.TestFixtureKind,
                    "--directory", releaseRoot,
                    "--native-receipts", nativeReceiptsRoot,
                    "--release-drivers", releaseDriversPath,
                    "--release-public-keys", releaseKeys,
                    "--attestation-public-keys", attestationKeys,
                    "--receipt", Path.Combine(root, "mixed-driver-release-set.json"),
                ],
                output,
                error,
                new FixtureVerifier());
            Assert.Equal(1, mixedDriversExitCode);
            Assert.Contains("reviewed release-driver digest", error.ToString(), StringComparison.Ordinal);
            await File.WriteAllBytesAsync(releaseDriversPath, releaseDriversBytes);

            error.GetStringBuilder().Clear();
            File.Delete(Directory.EnumerateFiles(
                Path.Combine(releaseRoot, ExecutionRuntimeLockPolicy.Rids[0]),
                "*" + SunderExecutionRuntimeFormat.Extension).First());
            var partialExitCode = await ExecutionRuntimeBuildCli.RunAsync(
                [
                    "validate-release-set",
                    "--lock", FixtureArtifacts.LockPath,
                    "--kind", ExecutionRuntimeLockPolicy.TestFixtureKind,
                    "--directory", releaseRoot,
                    "--native-receipts", nativeReceiptsRoot,
                    "--release-drivers", releaseDriversPath,
                    "--release-public-keys", releaseKeys,
                    "--attestation-public-keys", attestationKeys,
                    "--receipt", Path.Combine(root, "partial-release-set.json"),
                ],
                output,
                error,
                new FixtureVerifier());

            Assert.Equal(1, partialExitCode);
            Assert.Contains("exactly two", error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inspect_RejectsSignedArchiveMetadataThatDiffersFromLock(bool changeAttestationWorkflow)
    {
        var document = await ExecutionRuntimeLockLoader.LoadAsync(FixtureArtifacts.LockPath);
        var entry = document.Entries![0]!;
        var root = FixtureArtifacts.CreateTempDirectory();
        try
        {
            var sourcePath = await FixtureArtifacts.WriteSourceAsync(entry, root);
            var attestationPath = await FixtureArtifacts.WriteAttestationAsync(entry, root);
            var validArchivePath = Path.Combine(root, "valid.sunderruntime");
            var releaseKey = new SunderExecutionRuntimeTrustedKey(
                entry.Trust!.ReleaseSigningKeyId!,
                FixtureArtifacts.ReleaseKey);
            var attestationKey = new SunderExecutionRuntimeTrustedKey(
                entry.Trust.AttestationSigningKeyId!,
                FixtureArtifacts.AttestationKey);
            await ExecutionRuntimeArchiveBuilder.BuildAsync(
                FixtureArtifacts.LockPath,
                ExecutionRuntimeLockPolicy.TestFixtureKind,
                entry.Id!,
                entry.Version!,
                entry.Rid!,
                sourcePath,
                attestationPath,
                validArchivePath,
                new FixtureSigner(entry.Trust.ReleaseSigningKeyId!, FixtureArtifacts.ReleaseKey),
                new FixtureVerifier(),
                releaseKey,
                attestationKey);

            var extractedRoot = Path.Combine(root, "extracted");
            ZipFile.ExtractToDirectory(validArchivePath, extractedRoot);
            var manifestPath = Path.Combine(
                extractedRoot,
                SunderExecutionRuntimeFormat.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
            var manifest = SunderExecutionRuntimeCanonicalJson.DeserializeManifest(
                               await File.ReadAllBytesAsync(manifestPath))!;
            if (changeAttestationWorkflow)
            {
                var extractedAttestationPath = Path.Combine(
                    extractedRoot,
                    SunderExecutionRuntimeFormat.NativeAttestationPath.Replace('/', Path.DirectorySeparatorChar));
                var attestation = SunderExecutionRuntimeCanonicalJson.DeserializeNativeAttestation(
                                      await File.ReadAllBytesAsync(extractedAttestationPath))!;
                attestation = attestation with
                {
                    Workflow = attestation.Workflow! with { Name = "different.approved.workflow" },
                    Signing = attestation.Signing! with { Signature = null },
                };
                attestation = attestation with
                {
                    Signing = attestation.Signing! with
                    {
                        Signature = Convert.ToBase64String(FixtureArtifacts.CreateTestSignature(
                            SunderExecutionRuntimeNativeAttestationSignatures.CreateSignatureInput(attestation),
                            FixtureArtifacts.AttestationKey)),
                    },
                };
                var attestationBytes = SunderExecutionRuntimeCanonicalJson.SerializeNativeAttestation(attestation);
                await File.WriteAllBytesAsync(extractedAttestationPath, attestationBytes);
                manifest = manifest with
                {
                    Provenance = manifest.Provenance! with
                    {
                        AttestationSha256 = Convert.ToHexString(SHA256.HashData(attestationBytes)).ToLowerInvariant(),
                    },
                };
            }
            else
            {
                manifest = manifest with
                {
                    Upstream = manifest.Upstream! with
                    {
                        SourceUri = "https://different.invalid/runtime.zip",
                    },
                };
            }

            var payloadFiles = SunderArchiveFileSystem.EnumerateFiles(extractedRoot)
                .Where(file => SunderExecutionRuntimeFormat.IsPayloadPath(file.Path.ToString()))
                .ToDictionary(file => file.Path.ToString(), file => file.FullPath, StringComparer.Ordinal);
            var divergentArchivePath = Path.Combine(root, "divergent.sunderruntime");
            await SunderExecutionRuntimeArchiveWriter.WriteAsync(
                manifest,
                divergentArchivePath,
                payloadFiles,
                new FixtureSigner(entry.Trust.ReleaseSigningKeyId!, FixtureArtifacts.ReleaseKey));

            var genericInspection = await SunderExecutionRuntimeArchiveInspector.InspectAsync(
                divergentArchivePath,
                Path.Combine(root, "generic-validated.sunderruntime"),
                Path.Combine(root, "generic-staging"),
                new FixtureVerifier(),
                releaseKey,
                new FixtureVerifier(),
                attestationKey);
            Assert.True(genericInspection.Success, string.Join(Environment.NewLine, genericInspection.Errors));

            var releaseKeyPath = Path.Combine(root, "release-key.ed25519.pub");
            var attestationKeyPath = Path.Combine(root, "attestation-key.ed25519.pub");
            await File.WriteAllBytesAsync(releaseKeyPath, FixtureArtifacts.ReleaseKey);
            await File.WriteAllBytesAsync(attestationKeyPath, FixtureArtifacts.AttestationKey);
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = await ExecutionRuntimeBuildCli.RunAsync(
                [
                    "inspect",
                    "--lock", FixtureArtifacts.LockPath,
                    "--kind", ExecutionRuntimeLockPolicy.TestFixtureKind,
                    "--id", entry.Id!,
                    "--version", entry.Version!,
                    "--rid", entry.Rid!,
                    "--archive", divergentArchivePath,
                    "--release-public-key", releaseKeyPath,
                    "--attestation-public-key", attestationKeyPath,
                ],
                output,
                error,
                new FixtureVerifier());

            Assert.Equal(1, exitCode);
            Assert.Contains("metadata differs from the lock", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("tar.gz")]
    public async Task SourceMaterializer_ValidatesProductionArchiveContainers(string archiveFormat)
    {
        var document = await ExecutionRuntimeLockLoader.LoadAsync(FixtureArtifacts.LockPath);
        var entry = document.Entries!
            .First(item => archiveFormat == "zip"
                ? item!.Rid == "win-x64"
                : item!.Rid == "linux-x64")!;
        var root = FixtureArtifacts.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, archiveFormat == "zip" ? "source.zip" : "source.tar.gz");
            if (archiveFormat == "zip")
            {
                CreateZipSource(entry, sourcePath);
            }
            else
            {
                CreateTarGzipSource(entry, sourcePath);
            }
            var sourceHash = await ExecutionRuntimeSourceMaterializer.HashFileAsync(sourcePath);
            var productionEntry = entry with
            {
                Source = entry.Source! with
                {
                    ArchiveFormat = archiveFormat,
                    Sha256 = sourceHash,
                },
            };

            var files = await ExecutionRuntimeSourceMaterializer.MaterializeAsync(
                ExecutionRuntimeLockPolicy.ProductionKind,
                productionEntry,
                sourcePath,
                Path.Combine(root, "materialized"));

            Assert.Contains(productionEntry.Profile!.LaunchExecutable!, files.Keys);
            Assert.Contains($"payload/runtime/{productionEntry.License!.Files![0]!.Path}", files.Keys);
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SourceMaterializer_OnlySkipsExplicitlyLockedSymlinkPaths()
    {
        var document = await ExecutionRuntimeLockLoader.LoadAsync(FixtureArtifacts.LockPath);
        var entry = document.Entries!
            .First(item => item!.Id == SunderExecutionRuntimeFormat.NodeRuntimeId && item.Rid == "linux-x64")!;
        var root = FixtureArtifacts.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "node.tar.gz");
            CreateTarGzipSource(entry, sourcePath, symlinkPath: "bin/npm");
            var sourceHash = await ExecutionRuntimeSourceMaterializer.HashFileAsync(sourcePath);
            var productionEntry = entry with
            {
                Source = entry.Source! with
                {
                    ArchiveFormat = ExecutionRuntimeLockPolicy.TarGzipArchiveFormat,
                    Sha256 = sourceHash,
                },
                Recipe = entry.Recipe! with { ExcludedSourcePaths = ["bin/npm"] },
            };

            var files = await ExecutionRuntimeSourceMaterializer.MaterializeAsync(
                ExecutionRuntimeLockPolicy.ProductionKind,
                productionEntry,
                sourcePath,
                Path.Combine(root, "excluded"));
            Assert.DoesNotContain("payload/runtime/bin/npm", files.Keys);

            var unexcludedEntry = productionEntry with
            {
                Recipe = productionEntry.Recipe! with { ExcludedSourcePaths = [] },
            };
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => ExecutionRuntimeSourceMaterializer.MaterializeAsync(
                    ExecutionRuntimeLockPolicy.ProductionKind,
                    unexcludedEntry,
                    sourcePath,
                    Path.Combine(root, "unexcluded")));
            Assert.Contains("is not a regular file", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SourceMaterializer_SupportsCanonicalDotPrefixedArchiveRoot()
    {
        var document = await ExecutionRuntimeLockLoader.LoadAsync(FixtureArtifacts.LockPath);
        var entry = document.Entries!
            .First(item => item!.Id == SunderExecutionRuntimeFormat.DotNetRuntimeId && item.Rid == "win-x64")!;
        var root = FixtureArtifacts.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "dotnet.zip");
            using (var archive = ZipFile.Open(sourcePath, ZipArchiveMode.Create))
            {
                WriteZipEntry(archive, "./LICENSE.txt", FixtureArtifacts.LicenseText);
                WriteZipEntry(archive, "./dotnet.exe", "launcher\n");
                WriteZipEntry(archive, "./runtime.dat", "runtime\n");
            }
            var sourceHash = await ExecutionRuntimeSourceMaterializer.HashFileAsync(sourcePath);
            var productionEntry = entry with
            {
                Source = entry.Source! with
                {
                    ArchiveFormat = ExecutionRuntimeLockPolicy.ZipArchiveFormat,
                    SourceRoot = ".",
                    Sha256 = sourceHash,
                },
            };

            var files = await ExecutionRuntimeSourceMaterializer.MaterializeAsync(
                ExecutionRuntimeLockPolicy.ProductionKind,
                productionEntry,
                sourcePath,
                Path.Combine(root, "materialized"));

            Assert.Contains("payload/runtime/dotnet.exe", files.Keys);
            Assert.Contains("payload/runtime/LICENSE.txt", files.Keys);
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    private static void CreateZipSource(ExecutionRuntimeLockEntry entry, string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteZipEntry(
            archive,
            $"{entry.Source!.SourceRoot}/{entry.License!.Files![0]!.Path}",
            FixtureArtifacts.LicenseText);
        WriteZipEntry(
            archive,
            $"{entry.Source.SourceRoot}/{entry.Recipe!.LauncherSource}",
            "launcher\n");
        WriteZipEntry(archive, $"{entry.Source.SourceRoot}/runtime.dat", "runtime\n");
    }

    private static void WriteZipEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static void CreateTarGzipSource(
        ExecutionRuntimeLockEntry entry,
        string path,
        string? symlinkPath = null)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: false);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
        WriteTarEntry(
            writer,
            $"{entry.Source!.SourceRoot}/{entry.License!.Files![0]!.Path}",
            FixtureArtifacts.LicenseText);
        WriteTarEntry(
            writer,
            $"{entry.Source.SourceRoot}/{entry.Recipe!.LauncherSource}",
            "launcher\n");
        WriteTarEntry(writer, $"{entry.Source.SourceRoot}/runtime.dat", "runtime\n");
        if (symlinkPath is not null)
        {
            writer.WriteEntry(new PaxTarEntry(
                TarEntryType.SymbolicLink,
                $"{entry.Source.SourceRoot}/{symlinkPath}")
            {
                LinkName = "../lib/npm.js",
                ModificationTime = DateTimeOffset.UnixEpoch,
            });
        }
    }

    private static void WriteTarEntry(TarWriter writer, string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(bytes, writable: false);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = stream,
            ModificationTime = DateTimeOffset.UnixEpoch,
        };
        writer.WriteEntry(entry);
    }
}
