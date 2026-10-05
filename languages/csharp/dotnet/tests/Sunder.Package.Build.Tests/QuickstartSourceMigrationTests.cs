using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Sdk.Rpc;
using System.Text.Json.Nodes;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class QuickstartSourceMigrationTests
{
    [Fact]
    public void SourceOwnedQuickstartPreservesWireIdentityAndRemovesOnlyThePreReleaseSchemaAnnotation()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var dotnet = Path.Combine(directory.FullName, "languages", "csharp", "dotnet");
        var sample = Path.Combine(dotnet, "samples", "Sunder.Package.Quickstart");
        var before = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Path.Combine(dotnet, "tests", "Sunder.Package.Build.Tests", "Fixtures", "Quickstart.Greeting.before-source.json")));
        var baseline = SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(Path.Combine(sample, "sunder-contracts.owned.json"));
        var current = baseline.Contracts["greeting"].Current!;
        // The descriptor-first sample annotated the RPC document as a JSON Schema
        // metaschema. Source authoring omits that annotation; retain the original
        // oracle and prove it is the only descriptor change, with identical ABI.
        var expected = JsonNode.Parse(before.GetCanonicalUtf8())!.AsObject();
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", expected["$schema"]!.GetValue<string>());
        Assert.True(expected.Remove("$schema"));
        var sourceShape = SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(expected));
        Assert.Equal(sourceShape.GetCanonicalUtf8(), current.Descriptor.GetCanonicalUtf8());
        Assert.NotEqual(before.Sha256, current.Descriptor.Sha256);
        Assert.Equal(before.AbiSha256, current.Descriptor.AbiSha256);
        Assert.False(File.Exists(Path.Combine(sample, "Sunder.Package.Quickstart.Protocol", "Sunder.Package.Quickstart.Protocol.csproj")));
        var project = Assert.Single(Directory.EnumerateFiles(sample, "*.csproj", SearchOption.AllDirectories), path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));
        var projectText = File.ReadAllText(project);
        Assert.DoesNotContain(".Protocol", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("SunderPackageAppProject", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("SunderPackagePrimaryProject", projectText, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(sample, "Sunder.Package.Quickstart.Worker", "Program.cs")));
        Assert.Contains("IGreetingService", File.ReadAllText(Path.Combine(sample, "Views", "QuickstartViewModel.cs")), StringComparison.Ordinal);
    }
}
