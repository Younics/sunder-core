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

    private static JsonDocument ReadSchema(string root, string fileName, string title)
    {
        var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, root, fileName)));
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal(title, document.RootElement.GetProperty("title").GetString());
        Assert.False(document.RootElement.GetProperty("additionalProperties").GetBoolean());
        return document;
    }
}
