using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Package.Format;

namespace Sunder.ExecutionRuntime.Build.Tests;

internal static class FixtureArtifacts
{
    public const string LicenseText = "Synthetic MIT license fixture. Not for production.\n";
    public static readonly byte[] ReleaseKey = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
    public static readonly byte[] AttestationKey = Enumerable.Range(33, 32).Select(static value => (byte)value).ToArray();

    public static string LockPath => FixturePath("test-fixtures.lock.json");

    public static string SchemaPath => FixturePath("execution-runtime-lock.schema.json");

    public static string ArchiveGoldensPath => FixturePath("archive-goldens.sha256");

    public static async Task<string> WriteSourceAsync(
        ExecutionRuntimeLockEntry entry,
        string root,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(root, "source.fixture.json");
        await File.WriteAllBytesAsync(path, CreateSourceBytes(entry), cancellationToken);
        return path;
    }

    public static byte[] CreateSourceBytes(ExecutionRuntimeLockEntry entry)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WritePropertyName("files");
            writer.WriteStartArray();
            WriteSourceFile(
                writer,
                $"{entry.Source!.SourceRoot}/{entry.License!.Files![0]!.Path}",
                LicenseText);
            WriteSourceFile(
                writer,
                $"{entry.Source.SourceRoot}/{entry.Recipe!.LauncherSource}",
                $"Synthetic launcher for {entry.Id} {entry.Version} {entry.Rid}.\n");
            WriteSourceFile(
                writer,
                $"{entry.Source.SourceRoot}/runtime.dat",
                $"Synthetic payload for {entry.Id} {entry.Version} {entry.Rid}.\n");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static async Task<string> WriteAttestationAsync(
        ExecutionRuntimeLockEntry entry,
        string root,
        CancellationToken cancellationToken = default)
    {
        var attestation = new SunderExecutionRuntimeNativeAttestation
        {
            SchemaVersion = SunderExecutionRuntimeFormat.CurrentNativeAttestationVersion,
            RuntimeId = entry.Id,
            RuntimeVersion = entry.Version,
            PackagingRevision = entry.PackagingRevision,
            Rid = entry.Rid,
            UpstreamSha256 = entry.Source!.Sha256,
            Workflow = new SunderExecutionRuntimeAttestationWorkflow
            {
                Name = entry.Trust!.AttestationWorkflow,
                RunId = "fixture-run-1",
                SourceRevision = new string('a', 40),
            },
            Toolchain = new SunderExecutionRuntimeAttestationToolchain
            {
                Name = entry.Recipe!.Toolchain!.Name,
                Version = entry.Recipe.Toolchain.Version,
            },
            LauncherSmoke = new SunderExecutionRuntimeAttestationLauncherSmoke
            {
                Result = SunderExecutionRuntimeFormat.LauncherSmokePassed,
            },
            Platform = new SunderExecutionRuntimeAttestationPlatform
            {
                Signature = entry.Trust.PlatformSignature == ExecutionRuntimeLockPolicy.RequiredTrustCheck
                    ? SunderExecutionRuntimeFormat.PlatformCheckPassed
                    : SunderExecutionRuntimeFormat.PlatformCheckNotApplicable,
                Notarization = entry.Trust.Notarization == ExecutionRuntimeLockPolicy.RequiredTrustCheck
                    ? SunderExecutionRuntimeFormat.PlatformCheckPassed
                    : SunderExecutionRuntimeFormat.PlatformCheckNotApplicable,
            },
            Signing = new SunderExecutionRuntimeAttestationSigning
            {
                Algorithm = SunderExecutionRuntimeFormat.SigningAlgorithm,
                KeyId = entry.Trust.AttestationSigningKeyId,
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

        var path = Path.Combine(root, "native-build-attestation.v1.json");
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(attestation), cancellationToken);
        return path;
    }

    public static byte[] CreateTestSignature(ReadOnlySpan<byte> input, ReadOnlySpan<byte> key)
    {
        var signed = new byte[checked(key.Length + input.Length)];
        key.CopyTo(signed);
        input.CopyTo(signed.AsSpan(key.Length));
        return SHA512.HashData(signed);
    }

    public static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "Sunder.ExecutionRuntime.Build.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static void WriteSourceFile(Utf8JsonWriter writer, string path, string text)
    {
        writer.WriteStartObject();
        writer.WriteString("path", path);
        writer.WriteString("text", text);
        writer.WriteEndObject();
    }
}

internal sealed class FixtureSigner(string keyId, byte[] key) : ISunderExecutionRuntimeArchiveSigner
{
    public string KeyId { get; } = keyId;

    public byte[] Sign(ReadOnlySpan<byte> signatureInput)
        => FixtureArtifacts.CreateTestSignature(signatureInput, key);
}

internal sealed class FixtureVerifier : ISunderExecutionRuntimeArchiveSignatureVerifier
{
    public bool Verify(
        ReadOnlySpan<byte> signatureInput,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> trustedPublicKey)
        => CryptographicOperations.FixedTimeEquals(
            signature,
            FixtureArtifacts.CreateTestSignature(signatureInput, trustedPublicKey));
}
