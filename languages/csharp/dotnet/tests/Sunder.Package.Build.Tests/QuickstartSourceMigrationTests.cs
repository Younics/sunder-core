using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using System.Text.Json.Nodes;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class QuickstartSourceMigrationTests
{
    [Fact]
    public void QuickstartRetainsAuthenticPrivateHistoryAndAuthorsAPackageVersionSuccessor()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var dotnet = Path.Combine(directory.FullName, "languages", "csharp", "dotnet");
        var sample = Path.Combine(dotnet, "samples", "Sunder.Package.Quickstart");
        var before = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Path.Combine(dotnet, "tests", "Sunder.Package.Build.Tests", "Fixtures", "Quickstart.Greeting.before-source.json")));
        var baseline = SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(Path.Combine(sample, "sunder-contracts.owned.json"));
        Assert.Empty(baseline.Contracts);
        var historical = baseline.RetiredPrivateContracts["greeting"].Current!;
        // The descriptor-first sample annotated the RPC document as a JSON Schema
        // metaschema. Source authoring omits that annotation; retain the original
        // oracle and prove it is the only descriptor change, with identical ABI.
        var expected = JsonNode.Parse(before.GetCanonicalUtf8())!.AsObject();
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", expected["$schema"]!.GetValue<string>());
        Assert.True(expected.Remove("$schema"));
        var sourceShape = SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(expected));
        Assert.Equal(sourceShape.GetCanonicalUtf8(), historical.Descriptor.GetCanonicalUtf8());
        Assert.NotEqual(before.Sha256, historical.Descriptor.Sha256);
        Assert.Equal(before.AbiSha256, historical.Descriptor.AbiSha256);
        Assert.Equal("release-frozen", historical.State); Assert.Equal("2.0.0", historical.Descriptor.Version);
        Assert.Equal("1.0.0", historical.SnapshotJson.GetProperty("packageRelease").GetProperty("packageVersion").GetString());
        Assert.False(File.Exists(Path.Combine(sample, "Sunder.Package.Quickstart.Protocol", "Sunder.Package.Quickstart.Protocol.csproj")));
        var project = Assert.Single(Directory.EnumerateFiles(sample, "*.csproj", SearchOption.AllDirectories), path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));
        var projectText = File.ReadAllText(project);
        Assert.DoesNotContain(".Protocol", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("SunderPackageAppProject", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("SunderPackagePrimaryProject", projectText, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(sample, "Sunder.Package.Quickstart.Worker", "Program.cs")));
        Assert.Contains("IGreetingService", File.ReadAllText(Path.Combine(sample, "Views", "QuickstartViewModel.cs")), StringComparison.Ordinal);
        var version = System.Xml.Linq.XDocument.Load(project).Descendants("Version").Single().Value;
        Assert.Equal("1.1.0", version);
        var servicePath = Path.Combine(sample, "Services", "GreetingService.cs"); var source = File.ReadAllText(servicePath);
        Assert.Contains("[SunderService]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SunderRpc", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GreetRequest", source, StringComparison.Ordinal);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(SunderServiceAttribute).Assembly.Location).Distinct(StringComparer.Ordinal).ToArray();
        var result = new SunderRpcCSharpSourceCompiler().Compile(new("docs.sunder.quickstart", version,
            [new(servicePath, source), new("GlobalUsings.cs", "global using System; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks;")], references));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var current = Assert.Single(result.Contracts).Descriptor;
        Assert.Equal(version, current.Version);
        Assert.Equal(new[] { "greet", "greet-many", "reset" }, Assert.Single(current.Services).Methods.Select(method => method.MethodId).Order(StringComparer.Ordinal));
        Assert.NotEqual(historical.Descriptor.Sha256, current.Sha256);
        Assert.NotEqual(historical.Descriptor.AbiSha256, current.AbiSha256);
        Assert.Equal(baseline.ExactBytes, SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline));
    }
}
