using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class SunderExecutionRuntimeArchiveTests
{
    private const string ReleaseKeyId = "sunder.release.test.v1";
    private const string AttestationKeyId = "sunder.ci.test.v1";
    private static readonly byte[] ReleaseKey = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
    private static readonly byte[] AttestationKey = Enumerable.Range(33, 32).Select(static value => (byte)value).ToArray();

    public static TheoryData<string, string, string> RuntimePairings
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var rid in SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers)
            {
                data.Add(
                    SunderExecutionRuntimeFormat.DotNetRuntimeId,
                    SunderExecutionRuntimeFormat.DotNetModuleAbi,
                    rid);
                data.Add(
                    SunderExecutionRuntimeFormat.NodeRuntimeId,
                    SunderExecutionRuntimeFormat.NodeModuleAbi,
                    rid);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RuntimePairings))]
    public async Task WriterAndInspector_RoundTripEveryRuntimePairingAndRid(
        string runtimeId,
        string moduleAbi,
        string rid)
    {
        var fixture = await CreateArtifactAsync(runtimeId, moduleAbi, rid);
        var secondArchivePath = Path.Combine(
            Path.GetDirectoryName(fixture.ArchivePath)!,
            $"second-{Guid.NewGuid():N}.sunderruntime");
        await SunderExecutionRuntimeArchiveWriter.WriteAsync(
            fixture.Manifest,
            secondArchivePath,
            fixture.PayloadFiles,
            new TestSigner(ReleaseKeyId, ReleaseKey));

        var result = await InspectWithTrustedKeyAsync(fixture.ArchivePath, ReleaseKey);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(
            await File.ReadAllBytesAsync(fixture.ArchivePath),
            await File.ReadAllBytesAsync(secondArchivePath));
        Assert.True(result.SignatureVerified);
        Assert.True(result.NativeAttestationSignatureVerified);
        Assert.Equal(runtimeId, result.Manifest!.Id);
        Assert.Equal(rid, result.Manifest.Rid);
        Assert.Equal(64, result.ManifestSha256!.Length);
        Assert.Equal(64, result.ArchiveSha256!.Length);
        Assert.Equal(64, result.ContentIdentity!.Length);
        Assert.Equal(new FileInfo(fixture.ArchivePath).Length, result.ArchiveSize);
        Assert.True(File.Exists(result.ValidatedArchivePath));
        Assert.Equal(result.ArchiveSha256, await HashFileAsync(result.ValidatedArchivePath!));
        Assert.Equal(result.Manifest.Launch!.Executable, result.LauncherContentRecord!.Path);
        Assert.Equal(3, result.ContentInventory.Count);
    }

    [Fact]
    public async Task Writer_WithIdenticalInputs_ProducesByteIdenticalArchives()
    {
        var inputs = await CreateInputsAsync(
            SunderExecutionRuntimeFormat.DotNetRuntimeId,
            SunderExecutionRuntimeFormat.DotNetModuleAbi,
            "linux-x64");
        var first = Path.Combine(inputs.RootPath, "first.sunderruntime");
        var second = Path.Combine(inputs.RootPath, "second.sunderruntime");

        await SunderExecutionRuntimeArchiveWriter.WriteAsync(
            inputs.Manifest,
            first,
            inputs.PayloadFiles,
            new TestSigner(ReleaseKeyId, ReleaseKey));
        await SunderExecutionRuntimeArchiveWriter.WriteAsync(
            inputs.Manifest,
            second,
            inputs.PayloadFiles,
            new TestSigner(ReleaseKeyId, ReleaseKey));

        Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
    }

    [Fact]
    public async Task Writer_AllRuntimePairingArchiveHashesMatchV1Goldens()
    {
        var actual = new List<string>();
        foreach (var rid in SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers)
        {
            foreach (var (runtimeId, moduleAbi) in new[]
                     {
                         (SunderExecutionRuntimeFormat.DotNetRuntimeId, SunderExecutionRuntimeFormat.DotNetModuleAbi),
                         (SunderExecutionRuntimeFormat.NodeRuntimeId, SunderExecutionRuntimeFormat.NodeModuleAbi),
                     })
            {
                var fixture = await CreateArtifactAsync(runtimeId, moduleAbi, rid);
                actual.Add($"{runtimeId}/{rid} {await HashFileAsync(fixture.ArchivePath)}");
            }
        }

        Assert.Equal(
            """
            sunder.runtime.dotnet/win-x64 1609a53b2c30761ea3de163c237e2a1e3f42fd3f7655462a41600751d745ddf5
            sunder.runtime.node/win-x64 408d3e15a9d14c9afd3491bd5540aa3db6aa2252439d884f8f2b5f75faf151cb
            sunder.runtime.dotnet/win-arm64 c9fec890111dac550d5bc485d987d76b9d2e501f2cfb35122b2ff32cd08e59c4
            sunder.runtime.node/win-arm64 266a64354b2b18eacc8d101ba3d299f61ef32220fe00798309c2d90d31960903
            sunder.runtime.dotnet/linux-x64 2bc55e8891c97fcd6638862b4f4d149f94b93ec61ed072da4e64eb6e7154ea75
            sunder.runtime.node/linux-x64 4ef56e7e969e867d59389a86b1aeeece14a1bf9c917ce4f9da84a55b79c04792
            sunder.runtime.dotnet/linux-arm64 3445b06cca9ebe7ab8a802d7a071aac87e2c3389b2ec3bcd9adb513e4950e751
            sunder.runtime.node/linux-arm64 489d6182b959247bdecc7ba0001148ab812dc39db23dbf646245fc322cbb62fc
            sunder.runtime.dotnet/osx-x64 d9884a7688285614e6186eb51f8f495a2d5e806a87c5673f7f8540148dc4650f
            sunder.runtime.node/osx-x64 6791fbdabb68144a2bc5c176c7623c7fb64eee7952822e9eae19f95c77faa9de
            sunder.runtime.dotnet/osx-arm64 bd7d995053625ead09292b356350fd3cfbfb75669248fe739cf7c36124d714d9
            sunder.runtime.node/osx-arm64 ca32937658f60c55d2e23e0cf658927e2d47fcb1fffc0d2fd221707ded2233c0
            """,
            string.Join('\n', actual));
    }

    [Fact]
    public void ReleaseSignatureInput_UsesTheExactV1ByteSequence()
    {
        byte[] manifest = [1, 2, 3];
        byte[] contentIndex = [4, 5];
        var expected = System.Text.Encoding.ASCII
            .GetBytes(SunderExecutionRuntimeFormat.ReleaseSignatureDomain)
            .Concat(manifest)
            .Concat(contentIndex)
            .ToArray();

        Assert.Equal(expected, SunderExecutionRuntimeSignatureInput.Create(manifest, contentIndex));
    }

    [Fact]
    public void Ed25519Verifier_AcceptsRfc8032VectorAndRejectsChangedSignature()
    {
        var publicKey = Convert.FromHexString(
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        var signature = Convert.FromHexString(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155"
            + "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        var verifier = new SunderExecutionRuntimeEd25519SignatureVerifier();

        Assert.True(verifier.Verify([], signature, publicKey));
        signature[0] ^= 1;
        Assert.False(verifier.Verify([], signature, publicKey));
    }

    [Fact]
    public async Task Writer_RecordsOnlyTheLauncherAsExecutable()
    {
        var fixture = await CreateArtifactAsync();

        using var archive = ZipFile.OpenRead(fixture.ArchivePath);
        var launcher = archive.GetEntry(fixture.Manifest.Launch!.Executable!)!;
        var runtime = archive.GetEntry("payload/runtime/runtime.bin")!;

        Assert.Equal(0x81ed, (launcher.ExternalAttributes >> 16) & 0xffff);
        Assert.Equal(0x81a4, (runtime.ExternalAttributes >> 16) & 0xffff);
    }

    [Fact]
    public async Task Inspector_RejectsTamperedLauncherMode()
    {
        var fixture = await CreateArtifactAsync();
        using (var archive = ZipFile.Open(fixture.ArchivePath, ZipArchiveMode.Update))
        {
            archive.GetEntry(fixture.Manifest.Launch!.Executable!)!.ExternalAttributes = unchecked((int)(0x81a4u << 16));
        }

        var result = await InspectWithTrustedKeyAsync(fixture.ArchivePath, ReleaseKey);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("host-controlled Unix mode 0755", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inspector_AppliesHostControlledExtractedModes()
    {
        if (OperatingSystem.IsWindows()) return;
        var fixture = await CreateArtifactAsync();
        var stagingPath = NewStagingPath();

        var result = await SunderExecutionRuntimeArchiveInspector.InspectAsync(
            fixture.ArchivePath,
            NewValidatedArchivePath(),
            stagingPath,
            new TestVerifier(),
            new SunderExecutionRuntimeTrustedKey(ReleaseKeyId, ReleaseKey));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
            File.GetUnixFileMode(Path.Combine([stagingPath, .. fixture.Manifest.Launch!.Executable!.Split('/')])));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            File.GetUnixFileMode(Path.Combine(stagingPath, "payload", "runtime", "runtime.bin")));
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("/absolute.bin")]
    [InlineData("payload\\backslash.bin")]
    public async Task Inspector_RejectsUnsafeArchivePaths(string path)
    {
        var fixture = await CreateArtifactAsync();
        AddArchiveEntry(fixture.ArchivePath, path, [1]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => SunderExecutionRuntimeArchiveInspector.InspectAsync(
                fixture.ArchivePath,
                NewValidatedArchivePath(),
                NewStagingPath(),
                new TestVerifier(),
                new SunderExecutionRuntimeTrustedKey(ReleaseKeyId, ReleaseKey)));

        Assert.Contains("unsafe", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspector_RejectsCaseCollidingArchivePaths()
    {
        var fixture = await CreateArtifactAsync();
        AddArchiveEntry(fixture.ArchivePath, fixture.Manifest.Launch!.Executable!.ToUpperInvariant(), [1]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => SunderExecutionRuntimeArchiveInspector.InspectAsync(
                fixture.ArchivePath,
                NewValidatedArchivePath(),
                NewStagingPath(),
                new TestVerifier(),
                new SunderExecutionRuntimeTrustedKey(ReleaseKeyId, ReleaseKey)));

        Assert.Contains("differ only by case", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspector_RejectsLinkEntries()
    {
        var fixture = await CreateArtifactAsync();
        AddArchiveEntry(fixture.ArchivePath, "payload/link", [1], externalAttributes: 0xA1FF << 16);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => SunderExecutionRuntimeArchiveInspector.InspectAsync(
                fixture.ArchivePath,
                NewValidatedArchivePath(),
                NewStagingPath(),
                new TestVerifier(),
                new SunderExecutionRuntimeTrustedKey(ReleaseKeyId, ReleaseKey)));

        Assert.Contains("link", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspector_RejectsExcessiveCompressionRatio()
    {
        var fixture = await CreateArtifactAsync();
        AddArchiveEntry(fixture.ArchivePath, "payload/compression-bomb.bin", new byte[1024 * 1024]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => SunderExecutionRuntimeArchiveInspector.InspectAsync(
                fixture.ArchivePath,
                NewValidatedArchivePath(),
                NewStagingPath(),
                new TestVerifier(),
                new SunderExecutionRuntimeTrustedKey(ReleaseKeyId, ReleaseKey)));

        Assert.Contains("compression-ratio", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspector_RejectsExplicitDirectoryEntries()
    {
        var fixture = await CreateArtifactAsync();
        AddArchiveEntry(fixture.ArchivePath, "payload/extra/", []);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => InspectWithTrustedKeyAsync(fixture.ArchivePath, ReleaseKey));

        Assert.Contains("explicit directory entry", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspector_RejectsUnindexedPayloadFile()
    {
        var fixture = await CreateArtifactAsync();
        AddArchiveEntry(fixture.ArchivePath, "payload/unindexed.bin", [1, 2, 3]);

        await AssertInvalidAsync(fixture.ArchivePath, "unindexed payload file");
    }

    [Fact]
    public async Task Inspector_RejectsNonCanonicalUnindexedManifestFile()
    {
        var fixture = await CreateArtifactAsync();
        AddArchiveEntry(fixture.ArchivePath, "manifest/extra.json", [1]);

        await AssertInvalidAsync(fixture.ArchivePath, "outside its closed roots");
    }

    [Fact]
    public async Task Inspector_RejectsPayloadHashTamper()
    {
        var fixture = await CreateArtifactAsync();
        ReplaceArchiveEntry(fixture.ArchivePath, fixture.Manifest.Launch!.Executable!, [0, 1, 2, 3]);

        await AssertInvalidAsync(fixture.ArchivePath, "SHA-256 mismatch");
    }

    [Fact]
    public async Task Inspector_RejectsIndexedSizeMismatch()
    {
        var fixture = await CreateArtifactAsync();
        MutateContentIndex(
            fixture.ArchivePath,
            index => index with
            {
                Files = index.Files!
                    .Select((record, recordIndex) => recordIndex == 0 ? record! with { Size = record.Size + 1 } : record)
                    .ToArray(),
            });

        await AssertInvalidAsync(fixture.ArchivePath, "size mismatch");
    }

    [Fact]
    public async Task Inspector_RejectsUppercaseContentDigest()
    {
        var fixture = await CreateArtifactAsync();
        MutateContentIndex(
            fixture.ArchivePath,
            index => index with
            {
                Files = index.Files!
                    .Select((record, recordIndex) => recordIndex == 0
                        ? record! with { Sha256 = record.Sha256!.ToUpperInvariant() }
                        : record)
                    .ToArray(),
            });

        await AssertInvalidAsync(fixture.ArchivePath, "lower-case SHA-256");
    }

    [Fact]
    public async Task Inspector_RejectsDuplicateContentRecord()
    {
        var fixture = await CreateArtifactAsync();
        MutateContentIndex(
            fixture.ArchivePath,
            index => index with { Files = [.. index.Files!, index.Files![0]] });

        await AssertInvalidAsync(fixture.ArchivePath, "duplicate path");
    }

    [Fact]
    public async Task Inspector_RejectsContentIndexThatIndexesMetadata()
    {
        var fixture = await CreateArtifactAsync();
        MutateContentIndex(
            fixture.ArchivePath,
            index => index with
            {
                Files = index.Files!
                    .Select((record, recordIndex) => recordIndex == 0
                        ? record! with { Path = SunderExecutionRuntimeFormat.SignaturePath }
                        : record)
                    .ToArray(),
            });

        await AssertInvalidAsync(fixture.ArchivePath, "must be below payload/");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Inspector_RejectsUnknownContentIndexSchemaVersion(int schemaVersion)
    {
        var fixture = await CreateArtifactAsync();
        MutateContentIndex(fixture.ArchivePath, index => index with { SchemaVersion = schemaVersion });

        await AssertInvalidAsync(fixture.ArchivePath, "content index must declare schemaVersion 1");
    }

    [Theory]
    [InlineData(0, "1.2.3", 1, "schemaVersion 1")]
    [InlineData(2, "1.2.3", 1, "schemaVersion 1")]
    [InlineData(1, "1.2.3-beta.1", 1, "stable strict SemVer")]
    [InlineData(1, "1.2", 1, "stable strict SemVer")]
    [InlineData(1, "1.2.3", 0, "packagingRevision")]
    [InlineData(1, "1.2.3", 1000001, "packagingRevision")]
    public async Task Inspector_RejectsManifestVersionAndRevisionFailures(
        int schemaVersion,
        string version,
        int packagingRevision,
        string expectedError)
    {
        var fixture = await CreateArtifactAsync();
        MutateManifest(
            fixture.ArchivePath,
            manifest => manifest with
            {
                SchemaVersion = schemaVersion,
                Version = version,
                PackagingRevision = packagingRevision,
            });

        await AssertInvalidAsync(fixture.ArchivePath, expectedError);
    }

    [Fact]
    public async Task Inspector_RejectsUnknownRuntimePairing()
    {
        var fixture = await CreateArtifactAsync();
        MutateManifest(
            fixture.ArchivePath,
            manifest => manifest with { ModuleAbi = SunderExecutionRuntimeFormat.NodeModuleAbi });

        await AssertInvalidAsync(fixture.ArchivePath, "does not pair");
    }

    [Theory]
    [InlineData("/payload/bin/launcher", "relative regular file")]
    [InlineData("payload/../launcher", "relative regular file")]
    public async Task Inspector_RejectsUnsafeLauncherPath(string executable, string expectedError)
    {
        var fixture = await CreateArtifactAsync();
        MutateManifest(
            fixture.ArchivePath,
            manifest => manifest with { Launch = manifest.Launch! with { Executable = executable } });

        await AssertInvalidAsync(fixture.ArchivePath, expectedError);
    }

    [Theory]
    [InlineData("{package.arguments}", "unknown placeholder")]
    [InlineData("$(package.entryPoint)", "variable expansion")]
    [InlineData("entry;rm", "shell metacharacter")]
    [InlineData("../entry", "traversing path")]
    [InlineData("--package-options", "package-controlled options")]
    public async Task Inspector_RejectsOpenOrUnsafeLaunchRecipe(string packageToken, string expectedError)
    {
        var fixture = await CreateArtifactAsync();
        MutateManifest(
            fixture.ArchivePath,
            manifest => manifest with
            {
                Launch = manifest.Launch! with
                {
                    Arguments = [SunderExecutionRuntimeFormat.DotNetExecArgument, packageToken],
                },
            });

        await AssertInvalidAsync(fixture.ArchivePath, expectedError);
    }

    [Fact]
    public async Task Inspector_RejectsProvenanceDigestMismatch()
    {
        var fixture = await CreateArtifactAsync();
        MutateManifest(
            fixture.ArchivePath,
            manifest => manifest with
            {
                Provenance = manifest.Provenance! with { AttestationSha256 = new string('0', 64) },
            });

        await AssertInvalidAsync(fixture.ArchivePath, "attestation digest does not match");
    }

    [Fact]
    public async Task Inspector_RejectsAttestationIdentityAndIndexedHashTamper()
    {
        var fixture = await CreateArtifactAsync();
        MutateAttestation(
            fixture.ArchivePath,
            attestation => attestation with { RuntimeVersion = "9.9.9" });

        var result = await InspectAsync(fixture.ArchivePath);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("SHA-256 mismatch", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("runtime identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Inspector_RejectsAttestationPlatformResult()
    {
        var fixture = await CreateArtifactAsync();
        MutateAttestation(
            fixture.ArchivePath,
            attestation => attestation with
            {
                Platform = attestation.Platform! with { Signature = SunderExecutionRuntimeFormat.PlatformCheckPassed },
            });

        await AssertInvalidAsync(fixture.ArchivePath, "platform results");
    }

    [Fact]
    public async Task Inspector_RejectsMalformedDetachedSignature()
    {
        var fixture = await CreateArtifactAsync();
        ReplaceArchiveEntry(
            fixture.ArchivePath,
            SunderExecutionRuntimeFormat.SignaturePath,
            new byte[SunderExecutionRuntimeFormat.Ed25519SignatureBytes - 1]);

        await AssertInvalidAsync(fixture.ArchivePath, "exactly 64 raw bytes");
    }

    [Fact]
    public async Task Inspector_RejectsWrongTrustedKey()
    {
        var fixture = await CreateArtifactAsync();
        var wrongKey = Enumerable.Repeat((byte)0xA5, SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes).ToArray();
        var validatedArchivePath = NewValidatedArchivePath();

        var result = await SunderExecutionRuntimeArchiveInspector.InspectAsync(
            fixture.ArchivePath,
            validatedArchivePath,
            NewStagingPath(),
            new TestVerifier(),
            new SunderExecutionRuntimeTrustedKey(ReleaseKeyId, wrongKey));

        Assert.False(result.Success);
        Assert.False(result.SignatureVerified);
        Assert.Null(result.ValidatedArchivePath);
        Assert.False(File.Exists(validatedArchivePath));
        Assert.Contains(result.Errors, error => error.Contains("does not verify", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Inspector_RejectsWrongTrustedNativeAttestationKey()
    {
        var fixture = await CreateArtifactAsync();
        var wrongKey = Enumerable.Repeat((byte)0xA5, SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes).ToArray();

        var result = await SunderExecutionRuntimeArchiveInspector.InspectAsync(
            fixture.ArchivePath,
            NewValidatedArchivePath(),
            NewStagingPath(),
            new TestVerifier(),
            new SunderExecutionRuntimeTrustedKey(ReleaseKeyId, ReleaseKey),
            new TestVerifier(),
            new SunderExecutionRuntimeTrustedKey(AttestationKeyId, wrongKey));

        Assert.False(result.Success);
        Assert.True(result.SignatureVerified);
        Assert.False(result.NativeAttestationSignatureVerified);
        Assert.Contains(result.Errors, error => error.Contains(
            "native-attestation signature does not verify",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Inspector_ReportsMalformedNativeAttestationSignatureWithoutThrowing()
    {
        var fixture = await CreateArtifactAsync();
        MutateAttestation(
            fixture.ArchivePath,
            attestation => attestation with
            {
                Signing = attestation.Signing! with { Signature = null },
            });

        var result = await SunderExecutionRuntimeArchiveInspector.InspectAsync(
            fixture.ArchivePath,
            NewValidatedArchivePath(),
            NewStagingPath(),
            new TestVerifier(),
            new SunderExecutionRuntimeTrustedKey(ReleaseKeyId, ReleaseKey),
            new TestVerifier(),
            new SunderExecutionRuntimeTrustedKey(AttestationKeyId, AttestationKey));

        Assert.False(result.Success);
        Assert.False(result.NativeAttestationSignatureVerified);
        Assert.Contains(result.Errors, error => error.Contains(
            "native-build attestation CI signing identity or signature is invalid",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Inspector_RejectsUnknownCallerSuppliedKeyId()
    {
        var fixture = await CreateArtifactAsync();

        var result = await SunderExecutionRuntimeArchiveInspector.InspectAsync(
            fixture.ArchivePath,
            NewValidatedArchivePath(),
            NewStagingPath(),
            new TestVerifier(),
            new SunderExecutionRuntimeTrustedKey("sunder.release.unknown.v1", ReleaseKey));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("not the caller-supplied trusted key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Inspector_VerificationCoversExactCanonicalManifestBytes()
    {
        var fixture = await CreateArtifactAsync();
        MutateManifest(
            fixture.ArchivePath,
            manifest => manifest with { Licenses = ["Apache-2.0"] });

        var result = await InspectWithTrustedKeyAsync(fixture.ArchivePath, ReleaseKey);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("does not verify", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Inspector_VerificationCoversExactCanonicalContentIndexBytes()
    {
        var fixture = await CreateArtifactAsync();
        MutateContentIndex(
            fixture.ArchivePath,
            index => index with { SchemaVersion = SunderExecutionRuntimeFormat.CurrentContentIndexVersion + 1 });

        var result = await InspectWithTrustedKeyAsync(fixture.ArchivePath, ReleaseKey);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("does not verify", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Inspector_RejectsNonCanonicalManifestBytes()
    {
        var fixture = await CreateArtifactAsync();
        var bytes = ReadArchiveEntry(fixture.ArchivePath, SunderExecutionRuntimeFormat.ManifestPath);
        ReplaceArchiveEntry(
            fixture.ArchivePath,
            SunderExecutionRuntimeFormat.ManifestPath,
            [.. bytes, (byte)'\n']);

        await AssertInvalidAsync(fixture.ArchivePath, "not canonical V1 JSON");
    }

    [Fact]
    public async Task Inspector_RejectsUnknownManifestProperty()
    {
        var fixture = await CreateArtifactAsync();
        var node = JsonNode.Parse(ReadArchiveEntry(fixture.ArchivePath, SunderExecutionRuntimeFormat.ManifestPath))!.AsObject();
        node["dependencies"] = new JsonArray();
        ReplaceArchiveEntry(
            fixture.ArchivePath,
            SunderExecutionRuntimeFormat.ManifestPath,
            System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()));

        await AssertInvalidAsync(fixture.ArchivePath, "Failed to parse execution runtime metadata");
    }

    private static async Task<ArtifactFixture> CreateArtifactAsync(
        string runtimeId = SunderExecutionRuntimeFormat.DotNetRuntimeId,
        string moduleAbi = SunderExecutionRuntimeFormat.DotNetModuleAbi,
        string rid = "linux-x64")
    {
        var inputs = await CreateInputsAsync(runtimeId, moduleAbi, rid);
        var archivePath = Path.Combine(inputs.RootPath, $"runtime-{Guid.NewGuid():N}.sunderruntime");
        await SunderExecutionRuntimeArchiveWriter.WriteAsync(
            inputs.Manifest,
            archivePath,
            inputs.PayloadFiles,
            new TestSigner(ReleaseKeyId, ReleaseKey));
        return new ArtifactFixture(archivePath, inputs.Manifest, inputs.PayloadFiles);
    }

    private static async Task<ArtifactInputs> CreateInputsAsync(string runtimeId, string moduleAbi, string rid)
    {
        var root = CreateTempDirectory();
        var sources = Path.Combine(root, "sources");
        Directory.CreateDirectory(sources);
        var launcherArchivePath = rid.StartsWith("win-", StringComparison.Ordinal)
            ? "payload/bin/sunder-runtime.exe"
            : "payload/bin/sunder-runtime";
        var launcherSourcePath = Path.Combine(sources, "launcher.bin");
        var runtimeSourcePath = Path.Combine(sources, "runtime.bin");
        var attestationSourcePath = Path.Combine(sources, "attestation.json");
        await File.WriteAllBytesAsync(launcherSourcePath, [0x53, 0x55, 0x4E, 0x44, 0x45, 0x52]);
        await File.WriteAllBytesAsync(runtimeSourcePath, Enumerable.Range(0, 64).Select(static value => (byte)value).ToArray());

        var upstreamSha256 = new string('a', 64);
        var runtimeVersion = runtimeId == SunderExecutionRuntimeFormat.DotNetRuntimeId ? "10.0.1" : "24.0.0";
        var attestation = new SunderExecutionRuntimeNativeAttestation
        {
            SchemaVersion = SunderExecutionRuntimeFormat.CurrentNativeAttestationVersion,
            RuntimeId = runtimeId,
            RuntimeVersion = runtimeVersion,
            PackagingRevision = 7,
            Rid = rid,
            UpstreamSha256 = upstreamSha256,
            Workflow = new SunderExecutionRuntimeAttestationWorkflow
            {
                Name = "sunder.runtime.release",
                RunId = "run-123",
                SourceRevision = new string('b', 40),
            },
            Toolchain = new SunderExecutionRuntimeAttestationToolchain
            {
                Name = runtimeId == SunderExecutionRuntimeFormat.DotNetRuntimeId ? "dotnet-sdk" : "node-build",
                Version = runtimeId == SunderExecutionRuntimeFormat.DotNetRuntimeId ? "10.0.100" : "24.0.0",
            },
            LauncherSmoke = new SunderExecutionRuntimeAttestationLauncherSmoke
            {
                Result = SunderExecutionRuntimeFormat.LauncherSmokePassed,
            },
            Platform = CreatePlatform(rid),
            Signing = new SunderExecutionRuntimeAttestationSigning
            {
                Algorithm = SunderExecutionRuntimeFormat.SigningAlgorithm,
                KeyId = AttestationKeyId,
                Signature = null,
            },
        };
        attestation = attestation with
        {
            Signing = attestation.Signing! with
            {
                Signature = Convert.ToBase64String(CreateTestSignature(
                    SunderExecutionRuntimeNativeAttestationSignatures.CreateSignatureInput(attestation),
                    AttestationKey)),
            },
        };
        var attestationBytes = SunderExecutionRuntimeCanonicalJson.SerializeNativeAttestation(attestation);
        await File.WriteAllBytesAsync(attestationSourcePath, attestationBytes);
        var attestationSha256 = Convert.ToHexString(SHA256.HashData(attestationBytes)).ToLowerInvariant();
        var manifest = new SunderExecutionRuntimeManifest
        {
            SchemaVersion = SunderExecutionRuntimeFormat.CurrentSchemaVersion,
            Id = runtimeId,
            Version = runtimeVersion,
            PackagingRevision = 7,
            Rid = rid,
            ModuleAbi = moduleAbi,
            WorkerProtocol = SunderExecutionRuntimeFormat.WorkerProtocol,
            Launch = new SunderExecutionRuntimeLaunch
            {
                Executable = launcherArchivePath,
                Arguments = runtimeId == SunderExecutionRuntimeFormat.DotNetRuntimeId
                    ? [SunderExecutionRuntimeFormat.DotNetExecArgument, SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder]
                    : [SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder],
            },
            ContentIndex = SunderExecutionRuntimeFormat.ContentIndexPath,
            Upstream = new SunderExecutionRuntimeUpstream
            {
                Name = runtimeId == SunderExecutionRuntimeFormat.DotNetRuntimeId ? "dotnet" : "node",
                Version = runtimeVersion,
                SourceUri = runtimeId == SunderExecutionRuntimeFormat.DotNetRuntimeId
                    ? "https://example.test/dotnet-runtime.tar.gz"
                    : "https://example.test/node-runtime.tar.gz",
                SourceSha256 = upstreamSha256,
            },
            Provenance = new SunderExecutionRuntimeProvenance
            {
                AttestationPath = SunderExecutionRuntimeFormat.NativeAttestationPath,
                AttestationSha256 = attestationSha256,
                AttestationKeyId = AttestationKeyId,
            },
            Licenses = ["MIT"],
            Signing = new SunderExecutionRuntimeSigning
            {
                Algorithm = SunderExecutionRuntimeFormat.SigningAlgorithm,
                KeyId = ReleaseKeyId,
                Signature = SunderExecutionRuntimeFormat.SignaturePath,
            },
        };
        var payloadFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [launcherArchivePath] = launcherSourcePath,
            ["payload/runtime/runtime.bin"] = runtimeSourcePath,
            [SunderExecutionRuntimeFormat.NativeAttestationPath] = attestationSourcePath,
        };
        return new ArtifactInputs(root, manifest, payloadFiles);
    }

    private static SunderExecutionRuntimeAttestationPlatform CreatePlatform(string rid)
        => new()
        {
            Signature = rid.StartsWith("linux-", StringComparison.Ordinal)
                ? SunderExecutionRuntimeFormat.PlatformCheckNotApplicable
                : SunderExecutionRuntimeFormat.PlatformCheckPassed,
            Notarization = rid.StartsWith("osx-", StringComparison.Ordinal)
                ? SunderExecutionRuntimeFormat.PlatformCheckPassed
                : SunderExecutionRuntimeFormat.PlatformCheckNotApplicable,
        };

    private static async Task<SunderExecutionRuntimeArchiveValidationResult> InspectAsync(string archivePath)
        => await InspectWithTrustedKeyAsync(archivePath, ReleaseKey);

    private static async Task<SunderExecutionRuntimeArchiveValidationResult> InspectWithTrustedKeyAsync(
        string archivePath,
        byte[] key)
        => await SunderExecutionRuntimeArchiveInspector.InspectAsync(
            archivePath,
            NewValidatedArchivePath(),
            NewStagingPath(),
            new TestVerifier(),
            new SunderExecutionRuntimeTrustedKey(ReleaseKeyId, key),
            new TestVerifier(),
            new SunderExecutionRuntimeTrustedKey(AttestationKeyId, AttestationKey));

    private static async Task AssertInvalidAsync(string archivePath, string expectedError)
    {
        var result = await InspectAsync(archivePath);
        Assert.False(result.Success);
        Assert.Contains(
            result.Errors,
            error => error.Contains(expectedError, StringComparison.OrdinalIgnoreCase));
    }

    private static void MutateManifest(
        string archivePath,
        Func<SunderExecutionRuntimeManifest, SunderExecutionRuntimeManifest> mutate)
    {
        var manifest = SunderExecutionRuntimeCanonicalJson.DeserializeManifest(
            ReadArchiveEntry(archivePath, SunderExecutionRuntimeFormat.ManifestPath))!;
        ReplaceArchiveEntry(
            archivePath,
            SunderExecutionRuntimeFormat.ManifestPath,
            SunderExecutionRuntimeCanonicalJson.SerializeManifest(mutate(manifest)));
    }

    private static void MutateContentIndex(
        string archivePath,
        Func<SunderExecutionRuntimeContentIndex, SunderExecutionRuntimeContentIndex> mutate)
    {
        var index = SunderExecutionRuntimeCanonicalJson.DeserializeContentIndex(
            ReadArchiveEntry(archivePath, SunderExecutionRuntimeFormat.ContentIndexPath))!;
        ReplaceArchiveEntry(
            archivePath,
            SunderExecutionRuntimeFormat.ContentIndexPath,
            SunderExecutionRuntimeCanonicalJson.SerializeContentIndex(mutate(index)));
    }

    private static void MutateAttestation(
        string archivePath,
        Func<SunderExecutionRuntimeNativeAttestation, SunderExecutionRuntimeNativeAttestation> mutate)
    {
        var attestation = SunderExecutionRuntimeCanonicalJson.DeserializeNativeAttestation(
            ReadArchiveEntry(archivePath, SunderExecutionRuntimeFormat.NativeAttestationPath))!;
        ReplaceArchiveEntry(
            archivePath,
            SunderExecutionRuntimeFormat.NativeAttestationPath,
            SunderExecutionRuntimeCanonicalJson.SerializeNativeAttestation(mutate(attestation)));
    }

    private static byte[] ReadArchiveEntry(string archivePath, string entryPath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        using var stream = archive.GetEntry(entryPath)!.Open();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static void ReplaceArchiveEntry(string archivePath, string entryPath, byte[] bytes)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        archive.GetEntry(entryPath)!.Delete();
        WriteEntry(archive.CreateEntry(entryPath, CompressionLevel.Optimal), bytes);
    }

    private static void AddArchiveEntry(
        string archivePath,
        string entryPath,
        byte[] bytes,
        int externalAttributes = 0)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        entry.ExternalAttributes = externalAttributes;
        WriteEntry(entry, bytes);
    }

    private static void WriteEntry(ZipArchiveEntry entry, byte[] bytes)
    {
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "SunderExecutionRuntimeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string NewStagingPath()
        => Path.Combine(CreateTempDirectory(), "staging");

    private static string NewValidatedArchivePath()
        => Path.Combine(CreateTempDirectory(), "validated.sunderruntime");

    private sealed class TestSigner(string keyId, byte[] key) : ISunderExecutionRuntimeArchiveSigner
    {
        public string KeyId { get; } = keyId;

        public byte[] Sign(ReadOnlySpan<byte> signatureInput)
            => CreateTestSignature(signatureInput, key);
    }

    private sealed class TestVerifier : ISunderExecutionRuntimeArchiveSignatureVerifier
    {
        public bool Verify(
            ReadOnlySpan<byte> signatureInput,
            ReadOnlySpan<byte> signature,
            ReadOnlySpan<byte> trustedPublicKey)
            => CryptographicOperations.FixedTimeEquals(
                signature,
                CreateTestSignature(signatureInput, trustedPublicKey));
    }

    private static byte[] CreateTestSignature(ReadOnlySpan<byte> signatureInput, ReadOnlySpan<byte> key)
    {
        var signedBytes = new byte[key.Length + signatureInput.Length];
        key.CopyTo(signedBytes);
        signatureInput.CopyTo(signedBytes.AsSpan(key.Length));
        return SHA512.HashData(signedBytes);
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private sealed record ArtifactFixture(
        string ArchivePath,
        SunderExecutionRuntimeManifest Manifest,
        IReadOnlyDictionary<string, string> PayloadFiles);

    private sealed record ArtifactInputs(
        string RootPath,
        SunderExecutionRuntimeManifest Manifest,
        IReadOnlyDictionary<string, string> PayloadFiles);
}
