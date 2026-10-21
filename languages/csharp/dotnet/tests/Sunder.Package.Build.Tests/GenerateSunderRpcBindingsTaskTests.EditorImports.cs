using Sunder.Package.Build.Tasks;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class GenerateSunderRpcBindingsTaskTests
{
    [Fact]
    public void Execute_EditorImportsDoesNotReadIncompleteSourceOrRequireBuiltProjectMetadata()
    {
        var root = CreateTempDirectory();
        try
        {
            var task = CreateSourceTask(root, Path.Combine(root, "obj/sunder-design-time"), Path.Combine(root, "Unsaved.cs"), [Path.Combine(root, "Unbuilt.dll")]);
            task.GenerationMode = "EditorImports"; task.GeneratePackageRoles = true;
            Assert.True(task.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)task.BuildEngine).Errors));
            Assert.DoesNotContain(task.CompileItems, item => item.ItemSpec.EndsWith("service-apis.g.cs", StringComparison.Ordinal));
            Assert.All(task.CompileItems, item => Assert.Contains("sunder-editor-snapshots", item.ItemSpec, StringComparison.Ordinal));
            Assert.Empty(task.ContractBundles); Assert.Empty(task.RpcProviders); Assert.Empty(task.RpcUses);
            Assert.Empty(task.PackageProjectExports); Assert.Empty(task.AuthoringReceipts); Assert.Empty(task.PackageRolePlans);
            Assert.False(File.Exists(Path.Combine(root, "sunder-contracts.owned.json")));
            Assert.False(File.Exists(Path.Combine(root, "sunder-contracts.lock.json")));
            var first = task.CompileItems.Select(item => item.ItemSpec).ToArray();
            var times = first.Select(File.GetLastWriteTimeUtc).ToArray();
            Assert.True(task.Execute());
            Assert.Equal(first, task.CompileItems.Select(item => item.ItemSpec));
            Assert.Equal(times, first.Select(File.GetLastWriteTimeUtc));
            // Neither the mutable editor tree nor its immutable snapshots are
            // evidence that a private package lost an owned public baseline.
            GenerateSunderRpcBindingsTask.VerifyNoLostPublicBaseline(root, Path.Combine(root, "obj/sunder-rpc"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("missing-editor-proof")]
    [InlineData("changed-generated-source")]
    [InlineData("artifact-eligible")]
    [InlineData("unknown-member")]
    [InlineData("unlisted-file")]
    [InlineData("public-receipt")]
    [InlineData("artifact-directory")]
    public void EditorEvidence_CannotDismissPublicOrUnprovenArtifactMarkers(string mutation)
    {
        var root = CreateTempDirectory();
        try
        {
            var task = CreateSourceTask(root, Path.Combine(root, "obj/sunder-design-time"), Path.Combine(root, "Unsaved.cs"), []);
            task.GenerationMode = "EditorImports"; task.GeneratePackageRoles = true;
            Assert.True(task.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)task.BuildEngine).Errors));
            var editor = Path.Combine(root, "obj/sunder-design-time/sunder-rpc");
            var proof = Path.Combine(editor, "design-time-only.json");
            if (mutation == "missing-editor-proof") File.Delete(proof);
            if (mutation == "changed-generated-source") File.AppendAllText(Path.Combine(editor, "imports.g.cs"), "\n// changed\n");
            if (mutation is "artifact-eligible" or "unknown-member")
            {
                var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(proof))!.AsObject();
                document[mutation == "artifact-eligible" ? "artifactEligible" : "extra"] = true;
                File.WriteAllText(proof, document.ToJsonString());
            }
            if (mutation == "unlisted-file") File.WriteAllText(Path.Combine(editor, "unexpected.cs"), "// unreviewed");
            if (mutation == "public-receipt") File.WriteAllText(Path.Combine(editor, "authoring-receipt.v1.json"),
                """{"contracts":[{"contractKey":"owned","exposure":"public"}],"bundles":[{"contractKey":"owned","source":"owned"}]}""");
            if (mutation == "artifact-directory")
            {
                var artifact = Path.Combine(root, "obj/artifact/sunder-rpc"); Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
                Directory.Move(editor, artifact);
                File.Move(GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(editor), GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(artifact));
            }
            Assert.Throws<InvalidDataException>(() => GenerateSunderRpcBindingsTask.VerifyNoLostPublicBaseline(root, Path.Combine(root, "obj/sunder-rpc")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EvidenceDiscovery_ToleratesVanishedDirectoriesButRejectsReplacements(bool replaced)
    {
        var root = CreateTempDirectory();
        try
        {
            var transient = Path.Combine(root, "operation.sunder-output.lock.transition.candidate-test");
            Directory.CreateDirectory(transient);
            var observed = false;
            void BeforeRead(string path)
            {
                if (path != transient) return;
                observed = true; Directory.Delete(path);
                if (replaced) File.WriteAllText(path, "not a directory");
            }
            if (replaced) Assert.Throws<IOException>(() => GenerateSunderRpcBindingsTask.VerifyNoLostPublicBaseline(root, Path.Combine(root, "obj/sunder-rpc"), BeforeRead));
            else GenerateSunderRpcBindingsTask.VerifyNoLostPublicBaseline(root, Path.Combine(root, "obj/sunder-rpc"), BeforeRead);
            Assert.True(observed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EvidenceDiscovery_BoundsEvidenceRatherThanAccumulatedVerifiedCompilerCopies()
    {
        var root = CreateTempDirectory();
        try
        {
            var snapshots = CreateRetainedCompileEvidence(root, files: 512, snapshots: 65);
            Assert.True(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Count() > 32_768);
            GenerateSunderRpcBindingsTask.VerifyNoLostPublicBaseline(root, Path.Combine(root, "obj/sunder-rpc"));

            // A different configuration/RID/handoff must still reveal a lost public
            // baseline, even after the ordinary entry limit has been exceeded.
            var evidence = Path.Combine(root, "obj/Release/net10.0/sunder-analysis/linux-x64/sunder-package-role-plans/retained/sunder-rpc/authoring-receipt.v1.json");
            Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
            File.WriteAllText(evidence, PublicEvidenceReceipt);
            Assert.Contains("existing owned-contract baseline is required", Assert.Throws<InvalidDataException>(() =>
                GenerateSunderRpcBindingsTask.VerifyNoLostPublicBaseline(root, Path.Combine(root, "obj/sunder-rpc"))).Message, StringComparison.Ordinal);
            File.Delete(evidence);

            // A directory name/marker alone never earns a larger discovery budget.
            // Every snapshot must match its exact descriptor content identity.
            foreach (var snapshot in snapshots) File.AppendAllText(Path.Combine(snapshot, "compilation-inputs.v1.json"), " ");
            Assert.Contains("source-limit-exceeded", Assert.Throws<InvalidDataException>(() =>
                GenerateSunderRpcBindingsTask.VerifyNoLostPublicBaseline(root, Path.Combine(root, "obj/sunder-rpc"))).Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("unlisted-evidence")]
    [InlineData("source-directory")]
    [InlineData("malformed-evidence")]
    [InlineData("proof-replacement")]
    [InlineData("ancestor-replacement")]
    [InlineData("vanished-snapshot")]
    public void EvidenceDiscovery_RetainedCompilerProofCannotHideEvidenceOrPathChanges(string mutation)
    {
        var root = CreateTempDirectory();
        var moved = root + "-moved";
        try
        {
            var snapshot = Assert.Single(CreateRetainedCompileEvidence(root, files: 2, snapshots: 1));
            var source = Path.Combine(snapshot, "roles/worker/source/0.cs");
            if (mutation is "unlisted-evidence" or "malformed-evidence" or "source-directory")
            {
                var directory = Path.Combine(snapshot, "unlisted");
                if (mutation == "source-directory") { File.Delete(source); directory = source; }
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "authoring-receipt.v1.json"), mutation == "malformed-evidence" ? "{" : PublicEvidenceReceipt);
                if (mutation == "malformed-evidence") Assert.Contains("invalid UTF-8", Assert.Throws<InvalidDataException>(() =>
                    GenerateSunderRpcBindingsTask.VerifyNoLostPublicBaseline(root, Path.Combine(root, "obj/sunder-rpc"))).Message, StringComparison.Ordinal);
                else Assert.Contains("existing owned-contract baseline is required", Assert.Throws<InvalidDataException>(() =>
                    GenerateSunderRpcBindingsTask.VerifyNoLostPublicBaseline(root, Path.Combine(root, "obj/sunder-rpc"))).Message, StringComparison.Ordinal);
                return;
            }
            var visits = 0;
            void BeforeRead(string path)
            {
                if (path != snapshot || ++visits != 2) return;
                if (mutation == "proof-replacement")
                {
                    var proof = Path.Combine(snapshot, "compilation-inputs.v1.json");
                    var timestamp = File.GetLastWriteTimeUtc(proof);
                    File.WriteAllText(proof, File.ReadAllText(proof).Replace("PrivatePackage", "PrivauePackage", StringComparison.Ordinal));
                    File.SetLastWriteTimeUtc(proof, timestamp);
                }
                if (mutation == "vanished-snapshot") Directory.Delete(snapshot, recursive: true);
                if (mutation == "ancestor-replacement")
                {
                    var ancestor = Path.GetDirectoryName(snapshot)!;
                    Directory.Move(ancestor, moved);
                    File.WriteAllText(ancestor, "replacement is not a directory");
                }
            }
            if (mutation == "vanished-snapshot") GenerateSunderRpcBindingsTask.VerifyNoLostPublicBaseline(root, Path.Combine(root, "obj/sunder-rpc"), BeforeRead);
            else Assert.Throws<IOException>(() => GenerateSunderRpcBindingsTask.VerifyNoLostPublicBaseline(root, Path.Combine(root, "obj/sunder-rpc"), BeforeRead));
            Assert.Equal(2, visits);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            if (Directory.Exists(moved)) Directory.Delete(moved, recursive: true);
        }
    }

    private const string PublicEvidenceReceipt = """{"contracts":[{"contractKey":"owned","exposure":"public"}],"bundles":[{"contractKey":"owned","source":"owned"}]}""";

    private static string[] CreateRetainedCompileEvidence(string root, int files, int snapshots)
    {
        var inputs = CreateTempDirectory();
        try
        {
            var bytes = "// retained generated source\n"u8.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var compile = Enumerable.Range(0, files).Select(index => new { path = $"roles/worker/source/{index}.cs", sha256 = hash }).ToArray();
            Directory.CreateDirectory(Path.Combine(inputs, "roles/worker/source"));
            foreach (var file in compile.Take(2)) File.WriteAllBytes(Path.Combine(inputs, file.path), bytes);
            return Enumerable.Range(0, snapshots).Select(index =>
            {
                using var plan = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    compilationCacheRoot = Path.Combine(root, "obj/Debug/net10.0/sunder-analysis/osx-arm64/sunder-package-compilations"),
                    projectIdentity = "PrivatePackage", assemblyName = "PrivatePackage", configuration = "Debug", targetFramework = "net10.0",
                    runtimeIdentifier = "osx-arm64", packageVersion = "1.0." + index, defineConstants = Array.Empty<string>(),
                    workerCompile = compile.Take(2).ToArray(), workerReferences = Array.Empty<string>(), workerRuntimeReferences = Array.Empty<string>(),
                    resources = Array.Empty<string>(), workerDependencies = (object?)null, frameworkInputs = Array.Empty<string>(),
                    frameworkProperties = new { }, frameworkGenerators = Array.Empty<string>(),
                }));
                var snapshot = SunderRoleCompilationSnapshot.Publish(plan.RootElement, inputs, "worker");
                if (files <= 2) return snapshot;
                // Expand the real producer's small output into historical copies.
                // This regression owns discovery, not 33,280 redundant Publish reads.
                var path = Path.Combine(snapshot, "compilation-inputs.v1.json");
                var descriptor = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
                descriptor["compile"] = JsonSerializer.SerializeToNode(compile);
                var contents = JsonSerializer.SerializeToUtf8Bytes(descriptor);
                foreach (var file in compile.Skip(2)) File.WriteAllBytes(Path.Combine(snapshot, file.path), bytes);
                File.WriteAllBytes(path, contents);
                var sealedPath = Path.Combine(Path.GetDirectoryName(snapshot)!, "worker-" + Convert.ToHexStringLower(SHA256.HashData(contents)));
                Directory.Move(snapshot, sealedPath);
                return sealedPath;
            }).ToArray();
        }
        finally { Directory.Delete(inputs, recursive: true); }
    }

    [Theory]
    [InlineData("EditorImports", "obj")]
    [InlineData("Combined", "obj/sunder-design-time")]
    public void Execute_EditorAndArtifactGenerationCannotShareOutputDirectory(string mode, string intermediate)
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "Package.cs"); File.WriteAllText(source, ImplementationFirstSource);
            var task = CreateSourceTask(root, Path.Combine(root, intermediate), source, RoleReferences());
            task.GenerationMode = mode; task.GeneratePackageRoles = true;
            Assert.False(task.Execute()); Assert.Empty(task.CompileItems);
            Assert.Contains(((TestBuildEngine)task.BuildEngine).Errors, error => error.Contains("design-time-invalid", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
