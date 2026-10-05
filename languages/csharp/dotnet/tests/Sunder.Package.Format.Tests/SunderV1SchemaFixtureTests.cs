using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class SunderV1SchemaFixtureTests
{
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [Fact]
    public void PackageManifestFixture_IsStrictManifestV1AndPassesCoreValidation()
    {
        using var schema = ReadSchema(
            "PackageV1Conformance",
            "execution-runtime-requirement.schema.json",
            "Sunder Package Execution Runtime Requirement V1");
        var bytes = File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "PackageV1Conformance",
            "conformance",
            "valid",
            "runtime-backed-worker.sunder-package.json"));
        var manifest = JsonSerializer.Deserialize<SunderPackageManifest>(bytes, StrictJson)!;
        var errors = new List<string>();

        PackageTargetValidator.Validate(
            manifest,
            [
                ArchiveRelativePath.Parse("payload/shared/lib/Example.App.dll"),
                ArchiveRelativePath.Parse("payload/shared/lib/Example.Worker.dll"),
            ],
            errors);

        Assert.Empty(errors);
        Assert.Equal(1, manifest.ManifestVersion);
        var worker = Assert.Single(manifest.Targets!, static target => target!.Kind == SunderPackageFormat.WorkerTargetKind)!;
        Assert.Equal(SunderPackageFormat.DotNetExecutionRuntimeId, worker.ExecutionRuntime!.Id);
        Assert.Equal(SunderPackageFormat.DotNetWorkerModuleAbi, worker.ExecutionRuntime.ModuleAbi);
        using var requirement = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(worker.ExecutionRuntime));
        Assert.True(JsonSchemaTestValidator.IsValid(schema.RootElement, requirement.RootElement));
    }

    [Fact]
    public void PackageManifestSchema_TracksWorkerOnlyRuntimeRequirementAndOptionalAppBuildMetadata()
    {
        using var schema = ReadSchema(
            "PackageV1Conformance",
            "execution-runtime-requirement.schema.json",
            "Sunder Package Execution Runtime Requirement V1");
        var fixture = JsonNode.Parse(File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "PackageV1Conformance",
            "conformance",
            "valid",
            "runtime-backed-worker.sunder-package.json")))!.AsObject();
        var targets = fixture["targets"]!.AsArray().Select(static target => target!.AsObject()).ToArray();
        var app = targets.Single(static target => target["role"]!.GetValue<string>() == "app");
        app.Remove("targetFramework");
        app.Remove("sdkVersion");
        using (var optionalAppMetadata = JsonDocument.Parse(fixture.ToJsonString()))
        {
            var manifest = JsonSerializer.Deserialize<SunderPackageManifest>(optionalAppMetadata.RootElement, StrictJson)!;
            var errors = new List<string>();
            PackageTargetValidator.Validate(
                manifest,
                [
                    ArchiveRelativePath.Parse("payload/shared/lib/Example.App.dll"),
                    ArchiveRelativePath.Parse("payload/shared/lib/Example.Worker.dll"),
                ],
                errors);
            Assert.Empty(errors);
        }

        var worker = targets.Single(static target => target["role"]!.GetValue<string>() == "runtime");
        using (var invalidRequirement = JsonDocument.Parse(worker["executionRuntime"]!.DeepClone().ToJsonString().Replace(
                   "sunder.runtime.dotnet",
                   "Sunder.Runtime.DotNet",
                   StringComparison.Ordinal)))
        {
            Assert.False(JsonSchemaTestValidator.IsValid(schema.RootElement, invalidRequirement.RootElement));
        }
        worker.Remove("executionRuntime");
        var missingRuntimeManifest = JsonSerializer.Deserialize<SunderPackageManifest>(fixture.ToJsonString(), StrictJson)!;
        var missingRuntimeErrors = new List<string>();
        PackageTargetValidator.Validate(
            missingRuntimeManifest,
            [
                ArchiveRelativePath.Parse("payload/shared/lib/Example.App.dll"),
                ArchiveRelativePath.Parse("payload/shared/lib/Example.Worker.dll"),
            ],
            missingRuntimeErrors);
        Assert.Contains(missingRuntimeErrors, static error => error.Contains("is missing executionRuntime", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("dotnet-linux-x64.manifest.json", SunderExecutionRuntimeFormat.DotNetRuntimeId, SunderExecutionRuntimeFormat.DotNetExecArgument)]
    [InlineData("node-linux-x64.manifest.json", SunderExecutionRuntimeFormat.NodeRuntimeId, SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder)]
    public void RuntimeManifestFixtures_AreCanonicalV1(string fileName, string runtimeId, string firstArgument)
    {
        var bytes = ReadRuntimeFixture(fileName);
        var manifest = SunderExecutionRuntimeCanonicalJson.DeserializeManifest(bytes)!;

        Assert.Equal(bytes, SunderExecutionRuntimeCanonicalJson.SerializeManifest(manifest));
        AssertCanonicalVector(fileName, bytes);
        Assert.Equal(runtimeId, manifest.Id);
        Assert.Equal(firstArgument, manifest.Launch!.Arguments![0]);
        Assert.Equal(manifest.Version, manifest.Upstream!.Version);
    }

    [Fact]
    public void RuntimeContentIndexFixture_IsCanonicalV1()
    {
        var bytes = ReadRuntimeFixture("content-index.json");
        var contentIndex = SunderExecutionRuntimeCanonicalJson.DeserializeContentIndex(bytes)!;

        Assert.Equal(bytes, SunderExecutionRuntimeCanonicalJson.SerializeContentIndex(contentIndex));
        AssertCanonicalVector("content-index.json", bytes);
        Assert.Equal(3, contentIndex.Files!.Count);
    }

    [Fact]
    public void NativeAttestationFixture_IsCanonicalAndHasStableSignatureStatement()
    {
        var bytes = ReadRuntimeFixture("dotnet-linux-x64.native-attestation.json");
        var attestation = SunderExecutionRuntimeCanonicalJson.DeserializeNativeAttestation(bytes)!;
        var signatureInput = SunderExecutionRuntimeNativeAttestationSignatures.CreateSignatureInput(attestation);

        Assert.Equal(bytes, SunderExecutionRuntimeCanonicalJson.SerializeNativeAttestation(attestation));
        AssertCanonicalVector("dotnet-linux-x64.native-attestation.json", bytes);
        Assert.StartsWith(
            SunderExecutionRuntimeFormat.NativeAttestationSignatureDomain,
            System.Text.Encoding.ASCII.GetString(signatureInput),
            StringComparison.Ordinal);
        var domainLength = System.Text.Encoding.ASCII.GetByteCount(
            SunderExecutionRuntimeFormat.NativeAttestationSignatureDomain);
        using var statement = JsonDocument.Parse(signatureInput.AsMemory(domainLength));
        Assert.False(statement.RootElement.GetProperty("signing").TryGetProperty("signature", out _));
        using var vector = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "ExecutionRuntimeV1Conformance",
            "native-attestation-signature-vector.json")));
        Assert.Equal(
            SunderExecutionRuntimeFormat.NativeAttestationSignatureDomain,
            vector.RootElement.GetProperty("domain").GetString());
        Assert.Equal(
            vector.RootElement.GetProperty("statementSha256").GetString(),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(signatureInput.AsSpan(domainLength))).ToLowerInvariant());
    }

    private static byte[] ReadRuntimeFixture(string fileName)
    {
        var bytes = File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "ExecutionRuntimeV1Conformance",
            "conformance",
            "valid",
            fileName));
        return bytes.Length > 0 && bytes[^1] == (byte)'\n' ? bytes[..^1] : bytes;
    }

    private static void AssertCanonicalVector(string fileName, byte[] canonicalBytes)
    {
        using var vectors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "ExecutionRuntimeV1Conformance",
            "canonical-json-vectors.json")));
        var expected = vectors.RootElement
            .GetProperty("conformance/valid/" + fileName)
            .GetString();
        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(canonicalBytes)).ToLowerInvariant();
        Assert.Equal(expected, actual);
    }

    private static JsonDocument ReadSchema(string root, string fileName, string title)
    {
        var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, root, fileName)));
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal(title, document.RootElement.GetProperty("title").GetString());
        Assert.False(document.RootElement.GetProperty("additionalProperties").GetBoolean());
        return document;
    }
}
