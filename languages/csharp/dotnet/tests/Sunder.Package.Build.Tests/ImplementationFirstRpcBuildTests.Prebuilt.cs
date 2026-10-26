using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Build.Framework;
using TaskItem = Microsoft.Build.Utilities.TaskItem;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    public async Task SingleProject_QualifiedPrebuiltPackRelocatesAndPreservesExactRidBytesWithoutBuildOrRestore()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-qualified-prebuilt");
        var native = ResolvePhysicalTemporaryPath(scope.GetPath("native-checkout"));
        var project = await CreateSingleProjectFixtureAsync(native, immutableTasks: true);
        var rpcFixtures = Path.Combine(FindRepositoryRoot(), "specs/rpc/v1");
        var contractLock = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(rpcFixtures, "authoring/conformance/valid/package-api-contract-lock.json")))!;
        contractLock["packageArtifacts"]!["example.dependency"]!.AsObject().Remove("registryOrigin");
        contractLock["providerImports"]!["minimalProvider"] = new JsonObject { ["packageId"] = "example.dependency", ["contractId"] = "example.rpc.v1.minimal" };
        await File.WriteAllTextAsync(Path.Combine(native, "sunder-contracts.lock.json"), contractLock.ToJsonString(new JsonSerializerOptions
            { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        var cache = Path.Combine(native, ".sunder/rpc-cache"); Directory.CreateDirectory(cache); SunderRpcContractCacheStore.InitializeRoot(cache);
        var descriptor = SunderRpcContractDescriptor.Parse(await File.ReadAllBytesAsync(Path.Combine(rpcFixtures, "conformance/valid/minimal.rpc.json")));
        await File.WriteAllBytesAsync(SunderRpcContractCache.GetDescriptorPath(cache, descriptor.Sha256), descriptor.GetCanonicalUtf8());
        File.Copy(Path.Combine(rpcFixtures, "authoring/conformance/valid/cache-entry-receipt.json"), SunderRpcContractCache.GetReceiptPath(cache, descriptor.Sha256));
        ModelProjectionTestEvidence.Seed(cache, SunderRpcAuthoringDocuments.ReadContractLock(Path.Combine(native, "sunder-contracts.lock.json")).PackageArtifacts["example.dependency"]);
        await File.WriteAllTextAsync(Path.Combine(native, "ImportedProvider.cs"), """
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            using Sunder.Generated.Packages.Example_Dependency_Services;
            [SunderServiceProvider(typeof(IMinimalProviderService), ProviderId = "example.generated.imported")]
            public sealed partial class ImportedProvider : IMinimalProviderService
            {
                public ValueTask PingAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
            }
            """);
        Directory.CreateDirectory(Path.Combine(native, "helper"));
        await File.WriteAllTextAsync(Path.Combine(native, "helper/Helper.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup>
              <ItemGroup><PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.13" /></ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(native, "helper/Helper.cs"), "public static class Helper { public static string Render(string value) => value; }");
        await File.WriteAllTextAsync(Path.Combine(native, "Mixed.cs"), SingleProjectSource.Replace("Format(string name) => name;", "Format(string name) => Helper.Render(name);", StringComparison.Ordinal));
        var first = RuntimeInformation.RuntimeIdentifier; var second = first == "linux-x64" ? "win-x64" : "linux-x64";
        var text = (await File.ReadAllTextAsync(project)).Replace($"<RuntimeIdentifiers>{first}</RuntimeIdentifiers>",
            $"<RuntimeIdentifiers>{first};{second}</RuntimeIdentifiers><EnableRuntimePackDownload>false</EnableRuntimePackDownload><EnableAppHostPackDownload>false</EnableAppHostPackDownload>", StringComparison.Ordinal);
        text = text.Replace("</Project>", """
            <PropertyGroup>
              <SunderContractCacheRoot>$(MSBuildProjectDirectory)/.sunder/rpc-cache</SunderContractCacheRoot>
              <SunderPackageRoleInputSourceRoot>$(MSBuildProjectDirectory)</SunderPackageRoleInputSourceRoot>
            </PropertyGroup>
            <ItemGroup><Compile Remove="helper/**/*.cs" /><ProjectReference Include="helper/Helper.csproj" SunderPackageAssetRole="worker" /></ItemGroup>
            <ItemGroup Condition="'$(RuntimeIdentifier)' != ''">
              <Content Include="native/$(RuntimeIdentifier).txt" CopyToOutputDirectory="Always" CopyToPublishDirectory="Always"
                       TargetPath="native.txt" SunderPackageAssetRole="worker" />
            </ItemGroup>
            <Target Name="ForbidPrebuiltExecution" BeforeTargets="Build;Compile;CoreCompile;Restore;ResolveReferences;GenerateGlobalUsings"
                    Condition="'$(SunderPackageUsePrebuiltLeaves)' == 'true'">
              <Error Text="Prebuilt pack attempted compilation/restore/source generation." />
            </Target>
            </Project>
            """, StringComparison.Ordinal);
        await File.WriteAllTextAsync(project, text);
        Directory.CreateDirectory(Path.Combine(native, "native"));
        Directory.CreateDirectory(Path.Combine(native, "Assets"));
        await File.WriteAllBytesAsync(Path.Combine(native, "Assets/image.bin"), [1, 2, 3, 4]);
        foreach (var rid in new[] { first, second }) await File.WriteAllTextAsync(Path.Combine(native, "native", rid + ".txt"), "native bytes for " + rid);
        AssertSuccess(await RunDotnetAsync(native, ["restore", project, "--disable-parallel", "--nologo", "-v:quiet"]));
        var handoff = ResolvePhysicalTemporaryPath(scope.CreateDirectory("native-artifacts"));
        var selections = new JsonArray();
        foreach (var rid in new[] { first, second })
        {
            AssertSuccess(await RunDotnetAsync(native, ["build", project, "--no-restore", "--disable-build-servers", "-r", rid, "--nologo", "-v:quiet"]));
            var source = Path.Combine(native, "bin/Debug/net10.0/sunder-targets", rid);
            Assert.True(File.Exists(Path.Combine(source, "role-inputs.v1.json")));
            CopyDirectory(source, Path.Combine(handoff, rid));
            var evidence = await File.ReadAllBytesAsync(Path.Combine(handoff, rid, "role-inputs.v1.json"));
            using (var json = JsonDocument.Parse(evidence)) AssertMatchesSchema(Path.Combine(FindRepositoryRoot(), "specs/package/v1/prebuilt-role-inputs/evidence.schema.json"), json.RootElement);
            selections.Add(new JsonObject { ["runtimeIdentifier"] = rid, ["path"] = rid + "/role-inputs.v1.json", ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(evidence)) });
            Assert.DoesNotContain(native, System.Text.Encoding.UTF8.GetString(evidence), StringComparison.Ordinal);
        }
        var relocated = ResolvePhysicalTemporaryPath(scope.GetPath("relocated-checkout")); CopyDirectory(native, relocated);
        Directory.Delete(native, recursive: true); // No native-machine obj, compiler output or source path remains available.
        project = Path.Combine(relocated, "SingleProject.csproj");
        AssertSuccess(await RunDotnetAsync(relocated, ["restore", project, "--disable-parallel", "--nologo", "-v:quiet"]));
        var selection = Path.Combine(handoff, "selection.json");
        await File.WriteAllTextAsync(selection, new JsonObject { ["schemaVersion"] = 1, ["inputs"] = selections }.ToJsonString());
        using (var json = JsonDocument.Parse(await File.ReadAllBytesAsync(selection))) AssertMatchesSchema(Path.Combine(FindRepositoryRoot(), "specs/package/v1/prebuilt-role-inputs/selection.schema.json"), json.RootElement);
        // Fan-in needs the reviewed lock and retained receipts, not the native
        // descriptor cache or any project-reference compiler output.
        Directory.Delete(Path.Combine(relocated, ".sunder"), recursive: true);
        var inputsBefore = HashTree(handoff);
        AssertSuccess(await Pack());
        Assert.Equal(inputsBefore, HashTree(handoff));
        var archive = Path.Combine(relocated, "bin/Debug/net10.0/SingleProject.7.6.5.sunderpkg");
        var validated = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archive, scope.GetPath("extracted"));
        Assert.True(validated.Success, string.Join(Environment.NewLine, validated.Errors)); Assert.Equal(4, validated.Manifest!.Targets!.Count);
        Assert.Single(validated.Manifest.ProviderImports!); Assert.Single(validated.Manifest.PackageApiImports!);
        Assert.Contains(validated.Manifest.Provides!, value => value!.ProviderId == "example.generated.imported");
        Assert.Contains(validated.Manifest.UsesContracts!, value => value!.ContractId == descriptor.ContractId && value.Role == "runtime");
        foreach (var rid in new[] { first, second })
        {
            var projection = SunderPackageTargetResolver.CreateProjectionPlan(validated.Manifest, validated.ContentIndex!, new("runtime", rid));
            var payload = Assert.Single(projection.Files, file => file.LogicalPath.ToString().EndsWith("native.txt", StringComparison.Ordinal));
            Assert.Equal("native bytes for " + rid, await File.ReadAllTextAsync(payload.PhysicalPath.ToPlatformPath(scope.GetPath("extracted"))));
        }
        var prior = await File.ReadAllBytesAsync(archive);
        var sourcePath = Path.Combine(relocated, "Mixed.cs"); var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
        await File.AppendAllTextAsync(sourcePath, "\n// stale reviewed source\n");
        await Reject(); await File.WriteAllBytesAsync(sourcePath, sourceBytes);
        await File.WriteAllTextAsync(Path.Combine(relocated, "NewSource.cs"), "internal sealed class AddedSource;");
        await Reject(); File.Delete(Path.Combine(relocated, "NewSource.cs"));
        var helperPath = Path.Combine(relocated, "helper/Helper.cs"); var helperBytes = await File.ReadAllBytesAsync(helperPath);
        await File.AppendAllTextAsync(helperPath, "\n// changed source dependency\n"); await Reject(); await File.WriteAllBytesAsync(helperPath, helperBytes);
        var assetPath = Path.Combine(relocated, "Assets/image.bin");
        await File.WriteAllBytesAsync(assetPath, [4, 3, 2, 1]); await Reject(); await File.WriteAllBytesAsync(assetPath, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(Path.Combine(relocated, "Assets/added.bin"), [5]); await Reject(); File.Delete(Path.Combine(relocated, "Assets/added.bin"));
        var assetsPath = Path.Combine(relocated, "obj/project.assets.json"); var assetsBytes = await File.ReadAllBytesAsync(assetsPath);
        var assets = JsonNode.Parse(assetsBytes)!;
        var library = assets["libraries"]!.AsObject().First(pair => pair.Value!["type"]!.GetValue<string>() == "package").Value!;
        library["sha512"] = "changed restored package content identity";
        await File.WriteAllTextAsync(assetsPath, assets.ToJsonString()); await Reject(); await File.WriteAllBytesAsync(assetsPath, assetsBytes);
        var originalSelection = await File.ReadAllBytesAsync(selection);
        foreach (var property in new[] { "-p:RuntimeIdentifier=linux-musl-x64", "-p:BuildProjectReferences=true", "-p:SunderPackageBuildTaskSourceIsImmutable=false",
                     "-p:SunderPackageComposeRuntimeIdentifiers=" + first + "%3B" + first }) await Reject(property);
        var missing = JsonNode.Parse(originalSelection)!; missing["inputs"]!.AsArray().RemoveAt(1);
        await File.WriteAllTextAsync(selection, missing.ToJsonString()); await Reject(); await File.WriteAllBytesAsync(selection, originalSelection);
        var subsetArchive = Path.Combine(relocated, "bin/Debug/net10.0/subset.sunderpkg");
        await File.WriteAllTextAsync(selection, missing.ToJsonString());
        AssertSuccess(await Pack("-p:RuntimeIdentifier=" + first, "-p:SunderPackageOutputPath=" + subsetArchive));
        var subset = await SunderPackageArchiveInspector.ExtractAndValidateAsync(subsetArchive, scope.GetPath("subset"));
        Assert.True(subset.Success, string.Join(Environment.NewLine, subset.Errors)); Assert.Equal(2, subset.Manifest!.Targets!.Count);
        Assert.All(subset.Manifest.Targets!, target => Assert.Equal(first, target!.Rid));
        await File.WriteAllBytesAsync(selection, originalSelection);
        var evidencePath = Path.Combine(handoff, first, "role-inputs.v1.json"); var originalEvidence = await File.ReadAllBytesAsync(evidencePath);
        var unknown = JsonNode.Parse(originalEvidence)!; unknown["unknown"] = true;
        await File.WriteAllTextAsync(evidencePath, unknown.ToJsonString()); await Reject(); await File.WriteAllBytesAsync(evidencePath, originalEvidence);
        var payloadPath = Directory.GetFiles(Path.Combine(handoff, first, "sunder-dev"), "native.txt", SearchOption.AllDirectories).Single();
        var originalPayload = await File.ReadAllBytesAsync(payloadPath);
        await File.WriteAllTextAsync(payloadPath, "changed native bytes");
        // Reindexing cannot turn altered payload into the tested native input.
        var indexPath = Path.Combine(handoff, first, "sunder-dev", SunderPackageFormat.ContentIndexPath); var originalIndex = await File.ReadAllBytesAsync(indexPath);
        var manifestPath = Path.Combine(handoff, first, "sunder-dev", SunderPackageFormat.ManifestPath); var originalManifest = await File.ReadAllBytesAsync(manifestPath);
        PackageContentIndexer.Write(Path.Combine(handoff, first, "sunder-dev"));
        await Reject(); await File.WriteAllBytesAsync(payloadPath, originalPayload); await File.WriteAllBytesAsync(indexPath, originalIndex);
        AssertSuccess(await Pack()); Assert.Equal(prior, await File.ReadAllBytesAsync(archive));

        var validatedInputs = new List<ITaskItem>();
        foreach (var rid in new[] { first, second })
        {
            var evaluated = await RunDotnetAsync(relocated, ["msbuild", project, "-t:_ValidateSunderPrebuiltRoleInput", "-getItem:_SunderValidatedRoleInput",
                "-p:SunderPackageUsePrebuiltLeaves=true", "-p:BuildProjectReferences=false", "-p:RuntimeIdentifier=" + rid,
                "-p:SunderPackagePrebuiltRoleInputsFile=" + selection, "-nodeReuse:false", "-nologo", "-v:quiet"]);
            AssertSuccess(evaluated);
            using var json = JsonDocument.Parse(evaluated.StandardOutput);
            var value = Assert.Single(json.RootElement.GetProperty("Items").GetProperty("_SunderValidatedRoleInput").EnumerateArray());
            var item = new TaskItem(value.GetProperty("Identity").GetString()!);
            foreach (var name in new[] { "Frame", "Sha256", "SourceRoot", "ExternalLocations", "ToolDirectory", "EvidenceSha256", "SelectionFile", "SelectionSha256", "RuntimeIdentifier" })
                item.SetMetadata(name, value.GetProperty(name).GetString()!);
            validatedInputs.Add(item);
        }
        ManageSunderPackageRoleInputsTask Prepared() => new()
        {
            BuildEngine = new PrebuiltTestBuildEngine(), Action = "Pack", ProjectDirectory = relocated, PackageVersion = "7.6.5",
            PackageOutputPath = archive, SelectionFile = selection, ExpectedRuntimeIdentifiers = [first, second], ValidatedInputs = validatedInputs.ToArray(),
        };
        foreach (var cancel in new[] { true, false })
        {
            var operation = Prepared(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            operation.BeforeInputLeaseAcquisition = () => entered.SetResult();
            Task<bool> attempt;
            using (GeneratedOutputLock.Acquire([evidencePath]))
            {
                attempt = System.Threading.Tasks.Task.Run(operation.Execute);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (cancel)
                {
                    operation.Cancel(); Assert.False(await attempt.WaitAsync(TimeSpan.FromSeconds(10)));
                }
                else await File.AppendAllTextAsync(sourcePath, "\n// source changed while the native-input lease was held\n");
            }
            Assert.False(await attempt.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(prior, await File.ReadAllBytesAsync(archive));
            await File.WriteAllBytesAsync(sourcePath, sourceBytes);
        }
        var lateEdit = Prepared();
        lateEdit.BeforeArchivePublication = () => File.AppendAllText(payloadPath, "changed at final publication fence");
        Assert.False(lateEdit.Execute()); Assert.Equal(prior, await File.ReadAllBytesAsync(archive)); await File.WriteAllBytesAsync(payloadPath, originalPayload);
        var lateCancel = Prepared(); lateCancel.BeforeArchivePublication = lateCancel.Cancel;
        Assert.False(lateCancel.Execute()); Assert.Equal(prior, await File.ReadAllBytesAsync(archive));
        var good = Prepared(); Assert.True(good.Execute(), string.Join(Environment.NewLine, ((PrebuiltTestBuildEngine)good.BuildEngine).Errors));

        // Even a freshly recomputed outer digest cannot admit a structurally
        // unsupported, mixed or stale native qualification against current inputs.
        foreach (var mutation in new[] { "unknown", "schema", "version", "configuration", "framework", "rid", "toolset", "role", "receipt", "app-policy", "null-file", "extra-file" })
        {
            var changed = JsonNode.Parse(originalEvidence)!;
            switch (mutation)
            {
                case "unknown": changed["unknown"] = true; break;
                case "schema": changed["schemaVersion"] = 2; break;
                case "version": changed["inputs"]!["packageVersion"] = "8.0.0"; break;
                case "configuration": changed["inputs"]!["configuration"] = "Release"; break;
                case "framework": changed["inputs"]!["targetFramework"] = "net10.0-windows"; break;
                case "rid": changed["inputs"]!["runtimeIdentifier"] = second; break;
                case "toolset": changed["inputs"]!["toolset"]![0]!["sha256"] = new string('f', 64); break;
                case "role": changed["rolePlan"]!["roles"]!.AsArray().RemoveAt(1); break;
                case "receipt": changed["rolePlan"]!["roles"]![1]!["receipt"] = changed["rolePlan"]!["roles"]![0]!["receipt"]!.GetValue<string>(); break;
                case "app-policy":
                    var app = JsonNode.Parse(changed["rolePlan"]!["roles"]![1]!["receipt"]!.GetValue<string>())!;
                    app["contracts"]![0]!["appUse"] = new JsonObject { ["required"] = false, ["actions"] = new JsonArray("discover") };
                    app["uses"]![0]!["required"] = false; app["uses"]![0]!["actions"] = new JsonArray("discover");
                    changed["rolePlan"]!["roles"]![1]!["receipt"] = app.ToJsonString(new JsonSerializerOptions
                        { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
                    var manifest = JsonNode.Parse(originalManifest)!;
                    var use = manifest["usesContracts"]!.AsArray().Single(value => value!["role"]!.GetValue<string>() == "app")!;
                    use["required"] = false; use["actions"] = new JsonArray("discover");
                    await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
                    PackageContentIndexer.Write(Path.Combine(handoff, first, "sunder-dev"));
                    foreach (var file in changed["files"]!.AsArray())
                    {
                        var contents = await File.ReadAllBytesAsync(Path.Combine(handoff, first, "sunder-dev", file!["path"]!.GetValue<string>()));
                        file["bytes"] = contents.LongLength; file["sha256"] = Convert.ToHexStringLower(SHA256.HashData(contents));
                    }
                    break;
                case "null-file": changed["rolePlan"]!["roles"]![0]!["generatedInputs"]![0]!["path"] = null; break;
                case "extra-file": await File.WriteAllTextAsync(Path.Combine(handoff, first, "sunder-dev/payload/shared/unindexed.txt"), "extra"); break;
            }
            var altered = JsonSerializer.SerializeToUtf8Bytes(changed); await File.WriteAllBytesAsync(evidencePath, altered);
            var selected = JsonNode.Parse(originalSelection)!; selected["inputs"]![0]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(altered));
            await File.WriteAllTextAsync(selection, selected.ToJsonString());
            await Reject();
            await File.WriteAllBytesAsync(evidencePath, originalEvidence); await File.WriteAllBytesAsync(selection, originalSelection);
            await File.WriteAllBytesAsync(manifestPath, originalManifest); await File.WriteAllBytesAsync(indexPath, originalIndex);
            File.Delete(Path.Combine(handoff, first, "sunder-dev/payload/shared/unindexed.txt"));
        }
        var unexpectedBaseline = Path.Combine(relocated, "sunder-contracts.owned.json");
        await File.WriteAllTextAsync(unexpectedBaseline, "{}"); await Reject(); File.Delete(unexpectedBaseline);
        foreach (var name in new[] { "sunder-contracts.lock.json" })
        {
            var path = Path.Combine(relocated, name); var bytes = await File.ReadAllBytesAsync(path);
            await File.AppendAllTextAsync(path, " "); await Reject(); await File.WriteAllBytesAsync(path, bytes);
        }
        Assert.Equal(inputsBefore, HashTree(handoff));

        Task<ProcessResult> Pack(params string[] properties) => RunDotnetAsync(relocated, ["msbuild", project, "-t:PackSunderPackage", "-p:SunderPackageUsePrebuiltLeaves=true",
            "-p:BuildProjectReferences=false", "-p:SunderPackagePrebuiltRoleInputsFile=" + selection, "-nodeReuse:false", "-nologo", "-v:quiet", .. properties]);
        async Task Reject(params string[] properties)
        {
            var failed = await Pack(properties); Assert.NotEqual(0, failed.ExitCode);
            Assert.DoesNotContain("Prebuilt pack attempted", failed.StandardOutput + failed.StandardError, StringComparison.Ordinal);
            Assert.Equal(prior, await File.ReadAllBytesAsync(archive));
        }
    }

    private sealed class PrebuiltTestBuildEngine : IBuildEngine
    {
        public List<string> Errors { get; } = [];
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => string.Empty;
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e.Message ?? "error");
        public void LogWarningEvent(BuildWarningEventArgs e) { }
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public bool BuildProjectFile(string projectFileName, string[] targetNames, System.Collections.IDictionary globalProperties, System.Collections.IDictionary targetOutputs)
            => throw new InvalidOperationException("Prebuilt validation must not invoke a build.");
    }
}
