using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    public async Task SingleProject_BodyRollbackAndSourceMembershipOnlyInvalidateOwningCompiler()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-role-membership-build");
        var root = ResolvePhysicalTemporaryPath(scope.GetPath("fixture"));
        var project = await CreateSingleProjectFixtureAsync(root, immutableTasks: true);
        var worker = Path.Combine(root, "bin/Debug/net10.0/sunder-roles/worker", RuntimeInformation.RuntimeIdentifier, "SingleProject.dll");
        var app = Path.Combine(root, "bin/Debug/net10.0/sunder-roles/app", RuntimeInformation.RuntimeIdentifier, "SingleProject.App.dll");
        var source = Path.Combine(root, "RollbackService.cs");
        const string serviceSource = """
            using Sunder.Sdk.Services;
            namespace Example.Generated;
            [SunderService]
            public sealed partial class RollbackService
            {
                public ValueTask<string> ReadAsync(CancellationToken cancellationToken = default)
                    => ValueTask.FromResult("original");
            }
            """;
        await File.WriteAllTextAsync(source, serviceSource);
        AssertSuccess(await Build());
        // A previously validated immutable plan keeps its old source timestamps.
        // Returning to it must invalidate the compiler's current input identity,
        // not republish the intervening service body as an up-to-date assembly.
        var originalWorker = Image(worker); var originalApp = Image(app);
        await File.WriteAllTextAsync(source, serviceSource.Replace("\"original\"", "\"edited\"", StringComparison.Ordinal));
        AssertSuccess(await Build());
        var editedWorker = Image(worker);
        AssertRoleChange(originalWorker, editedWorker, changed: true);
        Assert.Equal(originalApp, Image(app));
        await File.WriteAllTextAsync(source, serviceSource);
        AssertSuccess(await Build());
        AssertRoleChange(editedWorker, Image(worker), changed: true);
        Assert.Equal(originalWorker.Hash, Image(worker).Hash);
        Assert.Equal(originalApp, Image(app));
        foreach (var role in new[] { "App", "Worker", "Shared" })
        {
            var beforeWorker = Image(worker); var beforeApp = Image(app);
            var addition = Path.Combine(root, "RoleAddition.cs");
            await File.WriteAllTextAsync(addition, $$"""
                using Sunder.Sdk.Packaging;
                [SunderPackageCodeRole(SunderPackageCodeRole.{{role}})]
                internal sealed class RoleAddition { public string Value => "{{role}}"; }
                """);
            AssertSuccess(await Build());
            var addedWorker = Image(worker); var addedApp = Image(app);
            AssertRoleChange(beforeWorker, addedWorker, role != "App");
            AssertRoleChange(beforeApp, addedApp, role != "Worker");
            File.Delete(addition);
            AssertSuccess(await Build());
            AssertRoleChange(addedWorker, Image(worker), role != "App");
            AssertRoleChange(addedApp, Image(app), role != "Worker");
            Assert.Equal(beforeWorker.Hash, Image(worker).Hash);
            Assert.Equal(beforeApp.Hash, Image(app).Hash);
        }

        // Custom targets can run after the SDK dependency hash. They must not add
        // or remove unvalidated inputs between projection and compiler execution.
        var projectText = await File.ReadAllTextAsync(project);
        var goodWorker = Image(worker); var goodApp = Image(app);
        var dev = Path.Combine(root, "bin/Debug/net10.0/sunder-dev");
        var goodDev = HashTree(dev);
        await File.WriteAllTextAsync(Path.Combine(root, "obj/late.cs"), "internal sealed class LateInput;");
        foreach (var (change, kind) in new[]
        {
            ("<Compile Include=\"obj/late.cs\" />", "source"),
            ("<Compile Remove=\"@(_SunderRoleCompile)\" />", "source"),
            ("<ReferencePath Include=\"obj/unreviewed.dll\" />", "reference"),
            ("<ReferencePathWithRefAssemblies Remove=\"@(ReferencePathWithRefAssemblies)\" />", "reference"),
        })
        {
            await File.WriteAllTextAsync(project, projectText.Replace("</Project>", $$"""
                <Target Name="ChangeLateCompile" BeforeTargets="_ReadSunderPackageCompilationRole" Condition="'$(SunderPackageCompilationRole)' == 'worker'">
                  <ItemGroup>{{change}}</ItemGroup>
                </Target></Project>
                """, StringComparison.Ordinal));
            var rejected = await Build();
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Contains($"evaluated {kind} set changed after role projection", rejected.StandardOutput + rejected.StandardError, StringComparison.Ordinal);
            Assert.Equal(goodWorker, Image(worker)); Assert.Equal(goodApp, Image(app));
            Assert.Equal(goodDev, HashTree(dev));
        }

        Task<ProcessResult> Build() => RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "--nologo", "-v:quiet"]);
        static (string Hash, DateTime Written) Image(string path) =>
            (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), File.GetLastWriteTimeUtc(path));
        static void AssertRoleChange((string Hash, DateTime Written) before, (string Hash, DateTime Written) after, bool changed)
        {
            if (changed) Assert.NotEqual(before.Hash, after.Hash);
            else Assert.Equal(before, after);
        }
    }
}
