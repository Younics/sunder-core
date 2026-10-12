using Sunder.Package.Build.Tasks;
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
