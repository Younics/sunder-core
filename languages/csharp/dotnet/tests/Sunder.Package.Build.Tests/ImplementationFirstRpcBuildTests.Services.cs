using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    public async Task OrdinaryQuickstartBuildsRebuildsAndPacksWithUnchangedRetiredHistory()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-ordinary-quickstart");
        var root = ResolvePhysicalTemporaryPath(scope.CreateDirectory("package"));
        var repository = FindRepositoryRoot();
        var sample = Path.Combine(repository, "languages/csharp/dotnet/samples/Sunder.Package.Quickstart");
        foreach (var path in Directory.EnumerateFiles(sample, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sample, path);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")
                || Path.GetExtension(path) is not (".cs" or ".axaml" or ".csproj")
                    && Path.GetFileName(path) is not ("sunder-contracts.lock.json" or "sunder-contracts.owned.json")) continue;
            var destination = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination);
        }

        // Retain the sample's actual source/project intent while isolating all outputs
        // and consuming the already-built owned SDK/tool closure rather than rebuilding it.
        var taskAssembly = typeof(GenerateSunderRpcBindingsTask).Assembly.Location;
        var project = Path.Combine(root, "Sunder.Package.Quickstart.csproj");
        var document = XDocument.Load(project);
        document.Descendants("ProjectReference").Remove();
        document.Descendants("Import").Single().SetAttributeValue("Project",
            Path.Combine(repository, "languages/csharp/dotnet/packages/Sunder.Package.Build/build/Sunder.Package.Build.targets"));
        document.Descendants("RuntimeIdentifiers").Single().Value = RuntimeInformation.RuntimeIdentifier;
        document.Root!.AddFirst(new XElement("PropertyGroup",
            new XElement("TargetFramework", "net10.0"), new XElement("Nullable", "enable"),
            new XElement("TreatWarningsAsErrors", "true"), new XElement("NuGetAudit", "false"),
            new XElement("SunderPackageBuildTaskAssembly", taskAssembly), new XElement("SunderPackageBuildTaskSourceIsImmutable", "true")));
        document.Root.Add(new XElement("ItemGroup",
            new[] { "Sunder.Sdk", "Sunder.Sdk.Worker", "Sunder.Sdk.Avalonia" }.Select(name => new XElement("Reference",
                new XAttribute("Include", name), new XAttribute("HintPath", Path.Combine(Path.GetDirectoryName(taskAssembly)!, name + ".dll")), new XAttribute("Private", "true"))),
            new XElement("PackageReference", new XAttribute("Include", "Avalonia"), new XAttribute("Version", "12.0.3")),
            new XElement("PackageReference", new XAttribute("Include", "Microsoft.Extensions.DependencyInjection"), new XAttribute("Version", "10.0.8")),
            new XElement("PackageReference", new XAttribute("Include", "Microsoft.Extensions.Logging.Abstractions"), new XAttribute("Version", "10.0.8"))));
        document.Save(project);
        var baseline = Path.Combine(root, "sunder-contracts.owned.json");
        var history = await File.ReadAllBytesAsync(baseline);
        var emptyFeed = scope.CreateDirectory("empty-feed");
        AssertSuccess(await RunDotnetAsync(root, ["restore", project, "--source", emptyFeed, "--disable-parallel", "--nologo"]));
        document.Root.Add(new XElement("Target", new XAttribute("Name", "RejectImplicitQuickstartRestore"), new XAttribute("BeforeTargets", "Restore"),
            new XElement("Error", new XAttribute("Text", "The isolated Quickstart must not restore after explicit setup."))));
        document.Save(project);
        AssertSuccess(await RunDotnetAsync(root, ["msbuild", project, "-t:Build;Rebuild;PackSunderPackage", "-nodeReuse:false", "-v:quiet"]));
        Assert.Equal(history, await File.ReadAllBytesAsync(baseline));
        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            Path.Combine(root, "bin/Debug/net10.0/Sunder.Package.Quickstart.1.1.0.sunderpkg"), scope.GetPath("extracted"));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(2, result.Manifest!.Targets!.Count);
        var bundle = Assert.Single(result.Manifest.ContractBundles!)!;
        Assert.Equal("package", bundle.Exposure); Assert.Equal("1.1.0", bundle.Version);
        var descriptor = SunderRpcContractDescriptor.Parse(await File.ReadAllBytesAsync(
            Path.Combine(scope.GetPath("extracted"), "payload/shared", bundle.DescriptorPath!)));
        Assert.Equal(new[] { "greet", "greet-many", "reset" }, Assert.Single(descriptor.Services).Methods.Select(method => method.MethodId).Order(StringComparer.Ordinal));
        var use = Assert.Single(result.Manifest.UsesContracts!)!;
        Assert.Equal("app", use.Role); Assert.True(use.Required);
        Assert.Equal(new[] { "discover", "invoke", "subscribe" }, use.Actions);
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    public async Task OrdinaryPublicServiceMigrationRetainsPrivateHistoryAndFrozenPublicBytes()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-ordinary-public-service");
        var root = ResolvePhysicalTemporaryPath(scope.GetPath("package"));
        var project = await CreateSingleProjectFixtureAsync(root, immutableTasks: true, legacyBaseline: true);
        var baselinePath = Path.Combine(root, "sunder-contracts.owned.json");
        using var before = JsonDocument.Parse(await File.ReadAllBytesAsync(baselinePath));
        var originalPrivate = before.RootElement.GetProperty("contracts").GetProperty("greeting").Clone();
        AssertSuccess(await RunDotnetAsync(root, ["msbuild", project, "-t:MigratePrivateSunderContracts", "-nodeReuse:false", "-v:quiet"]));
        using (var migrated = JsonDocument.Parse(await File.ReadAllBytesAsync(baselinePath)))
        {
            Assert.Empty(migrated.RootElement.GetProperty("contracts").EnumerateObject());
            Assert.True(JsonElement.DeepEquals(originalPrivate, migrated.RootElement.GetProperty("retiredPrivateContracts").GetProperty("greeting")));
        }
        // Private history remains authentic; its public successor uses the sole
        // current authoring surface. An explicit default App policy is optional.
        var source = SingleProjectSource.Replace("[SunderService]", "[SunderService(SunderServiceExposure.Public)]\n[SunderAppUse]", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(root, "Mixed.cs"), source);
        AssertSuccess(await RunDotnetAsync(root, ["msbuild", project, "-t:AcceptSunderContracts;FreezeSunderContracts;Build", "-nodeReuse:false", "-v:quiet"]));
        var baselineBytes = await File.ReadAllBytesAsync(baselinePath);
        using var frozen = JsonDocument.Parse(baselineBytes);
        var publicDescriptor = frozen.RootElement.GetProperty("contracts").GetProperty("greeting").GetProperty("current").GetProperty("canonicalDescriptor").GetString();
        await File.WriteAllTextAsync(Path.Combine(root, "Mixed.cs"), source.Replace("[SunderAppUse]", "", StringComparison.Ordinal));
        AssertSuccess(await RunDotnetAsync(root, ["msbuild", project, "-t:PackSunderPackage", "-nodeReuse:false", "-v:quiet"]));
        Assert.Equal(baselineBytes, await File.ReadAllBytesAsync(baselinePath));
        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(Path.Combine(root, "bin/Debug/net10.0/SingleProject.7.6.5.sunderpkg"), scope.GetPath("extracted"));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var bundle = Assert.Single(result.Manifest!.ContractBundles!)!;
        Assert.Equal("public", bundle.Exposure); Assert.Equal("1.0.0", bundle.Version);
        Assert.Equal(publicDescriptor, await File.ReadAllTextAsync(Path.Combine(scope.GetPath("extracted"), "payload/shared", bundle.DescriptorPath!)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    public async Task OrdinaryPrivateServicesBuildCheckFreezeAndPrebuiltPackWithoutBaseline(bool app, bool imports)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-ordinary-private-service");
        var root = ResolvePhysicalTemporaryPath(scope.GetPath("package"));
        var project = await CreateSingleProjectFixtureAsync(root, immutableTasks: true);
        var baseline = Path.Combine(root, "sunder-contracts.owned.json");
        Assert.False(File.Exists(baseline));
        var source = SingleProjectSource;
        if (!app) source = source[..source.IndexOf("[SunderPackageView", StringComparison.Ordinal)];
        if (imports) source = "using Sunder.Generated.Packages.Example_Dependency_Services;\n" + source
            .Replace("GreetingService(IBackendFormatter formatter)", "GreetingService(IBackendFormatter formatter, IPackageApi remote)", StringComparison.Ordinal)
            .Replace("formatter.Format(request.Name)", "formatter.Format(request.Name) + remote.GetType().Name", StringComparison.Ordinal)
            .Replace("GreetingViewModel(IGreetingService service)", "GreetingViewModel(IGreetingService service, IPackageApi remote)", StringComparison.Ordinal)
            .Replace("Name = \"UI\"", "Name = remote.GetType().Name", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(root, "Mixed.cs"), source);
        var projectText = await File.ReadAllTextAsync(project);
        await File.WriteAllTextAsync(project, projectText.Replace("<Version>7.6.5</Version>", "<Version>2.0.0-beta.1+build.7</Version><SunderContractCacheRoot>$(MSBuildProjectDirectory)/.sunder/imports</SunderContractCacheRoot>", StringComparison.Ordinal)
            .Replace("</Project>", """
                <Target Name="RejectPrebuiltCompilation" BeforeTargets="Build;Compile;CoreCompile;Restore;ResolveReferences;GenerateGlobalUsings"
                        Condition="'$(SunderPackageUsePrebuiltLeaves)' == 'true'"><Error Text="Unexpected prebuilt compilation or restore." /></Target>
                </Project>
                """, StringComparison.Ordinal));
        if (imports)
        {
            var fixtures = Path.Combine(FindRepositoryRoot(), "specs/rpc/v1");
            var selected = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(fixtures, "authoring/conformance/valid/package-api-contract-lock.json")))!;
            selected["packageImports"]!["example.dependency"]!.AsObject().Remove("consumerRoles");
            await File.WriteAllTextAsync(Path.Combine(root, "sunder-contracts.lock.json"), selected.ToJsonString(new JsonSerializerOptions
                { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
            var cache = Path.Combine(root, ".sunder/imports"); Directory.CreateDirectory(cache); SunderRpcContractCacheStore.InitializeRoot(cache);
            var descriptor = SunderRpcContractDescriptor.Parse(await File.ReadAllBytesAsync(Path.Combine(fixtures, "conformance/valid/minimal.rpc.json")));
            await File.WriteAllBytesAsync(SunderRpcContractCache.GetDescriptorPath(cache, descriptor.Sha256), descriptor.GetCanonicalUtf8());
            File.Copy(Path.Combine(fixtures, "authoring/conformance/valid/cache-entry-receipt.json"), SunderRpcContractCache.GetReceiptPath(cache, descriptor.Sha256));
            ModelProjectionTestEvidence.Seed(cache, SunderRpcAuthoringDocuments.ReadContractLock(
                Path.Combine(fixtures, "authoring/conformance/valid/package-api-contract-lock.json")).PackageArtifacts["example.dependency"]);
            AssertSuccess(await RunDotnetAsync(root, ["msbuild", project, "-t:MigrateSunderPackageApiConsumerRoles", "-p:SunderPackageId=example.dependency",
                "-p:SunderPackageApiConsumerRoles=app%2Cruntime", "-nodeReuse:false", "-v:quiet"]));
        }
        AssertSuccess(await RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "-v:quiet"]));
        Assert.False(File.Exists(baseline));
        AssertSuccess(await RunDotnetAsync(root, ["msbuild", project, "-t:CheckSunderContracts;FreezeSunderContracts", "-nodeReuse:false", "-v:quiet"]));
        Assert.False(File.Exists(baseline));
        var rid = RuntimeInformation.RuntimeIdentifier;
        var bundle = Path.Combine(root, "bin/Debug/net10.0/sunder-targets", rid);
        var evidence = await File.ReadAllBytesAsync(Path.Combine(bundle, "role-inputs.v1.json"));
        var selection = Path.Combine(bundle, "selection.json");
        await File.WriteAllBytesAsync(selection, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1,
            inputs = new[] { new { runtimeIdentifier = rid, path = "role-inputs.v1.json", sha256 = Convert.ToHexStringLower(SHA256.HashData(evidence)) } } }));
        var archive = Path.Combine(root, "bin/private.sunderpkg");
        AssertSuccess(await RunDotnetAsync(root, ["msbuild", project, "-t:PackSunderPackage", "-p:SunderPackageUsePrebuiltLeaves=true", "-p:BuildProjectReferences=false",
            "-p:SunderPackagePrebuiltRoleInputsFile=" + selection, "-p:SunderPackageOutputPath=" + archive, "-nodeReuse:false", "-v:quiet"]));
        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archive, scope.GetPath("extracted"));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(app ? 2 : 1, result.Manifest!.Targets!.Count);
        Assert.All(result.Manifest.ContractBundles!.Where(bundle => bundle!.Source == "owned"), bundle => Assert.Equal("2.0.0-beta.1+build.7", bundle!.Version));
        if (imports) Assert.Equal(new[] { "app", "runtime" }, Assert.Single(result.Manifest.PackageApiImports!)!.UseRefs!.Select(reference => reference!.Role).Order(StringComparer.Ordinal));
        Assert.False(File.Exists(baseline));
    }
}
