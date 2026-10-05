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
