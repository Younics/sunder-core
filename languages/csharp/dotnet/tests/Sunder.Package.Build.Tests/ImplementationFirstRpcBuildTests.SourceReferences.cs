using System.Runtime.InteropServices;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task SourceReferences_NormalBuildRebuildAndAppExportUseOneSdkAndPreserveAuthoringInputs()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-rpc-source-references");
        var repository = FindRepositoryRoot();
        var dotnet = Path.Combine(repository, "languages", "csharp", "dotnet");
        var fixture = ResolvePhysicalTemporaryPath(scope.GetPath("fixture"));
        CopyDirectory(Path.Combine(dotnet, "tests", "Sunder.Package.Build.TestFixtures", "ImplementationFirstRpc"), fixture);
        var project = Path.Combine(fixture, "ImplementationFirstRpc.csproj");
        var appProject = Path.Combine(fixture, "App", "ImplementationFirstRpc.App.csproj");
        // Real source producers use isolated outputs, including on Windows where
        // the owning test process already has the normal task/SDK assemblies loaded.
        var configuration = "RpcSourceReferences-" + Guid.NewGuid().ToString("N");
        var packages = Path.Combine(dotnet, "packages");
        string[] properties =
        [
            $"-p:Configuration={configuration}",
            $"-p:SunderSdkWorkerProject={Path.Combine(packages, "Sunder.Sdk.Worker", "Sunder.Sdk.Worker.csproj")}",
            $"-p:SunderSdkProject={Path.Combine(packages, "Sunder.Sdk", "Sunder.Sdk.csproj")}",
            $"-p:SunderPackageBuildProject={Path.Combine(packages, "Sunder.Package.Build", "Sunder.Package.Build.csproj")}",
            $"-p:SunderPackageBuildTargetsPath={Path.Combine(packages, "Sunder.Package.Build", "build", "Sunder.Package.Build.targets")}",
        ];
        foreach (var path in new[] { project, appProject })
        {
            // Restore is explicit; the detached fixture has its own NuGet lock
            // shape rather than inheriting the repository's central-version mode.
            AssertSuccess(await RunDotnetAsync(fixture, ["restore", path, "--disable-parallel",
                "-p:RestorePackagesWithLockFile=true", "-p:RestoreLockedMode=false", .. properties]));
            AssertSuccess(await RunDotnetAsync(fixture, ["restore", path, "--locked-mode", "--disable-parallel", .. properties]));
        }
        var traversal = Path.Combine(fixture, "Concurrent.proj");
        File.WriteAllText(traversal, """
            <Project>
              <Target Name="Rebuild">
                <MSBuild Projects="ImplementationFirstRpc.csproj;App/ImplementationFirstRpc.App.csproj" Targets="Rebuild" BuildInParallel="true" />
              </Target>
            </Project>
            """);
        var inputs = AuthoringInputs(fixture);
        foreach (var rebuild in new[] { false, true })
        {
            foreach (var path in new[] { project, appProject })
            {
                string[] target = rebuild ? ["-t:Rebuild"] : [];
                AssertSuccess(await RunDotnetAsync(fixture, ["build", path, "--no-restore", "--disable-build-servers", .. target, .. properties]));
            }
            Assert.Equal(inputs, AuthoringInputs(fixture));
            var neutralIntermediate = Path.Combine(fixture, "obj", configuration, "net10.0");
            Assert.DoesNotContain(Directory.GetFiles(neutralIntermediate), path =>
                Path.GetFileName(path).StartsWith("Sunder.Sdk.", StringComparison.Ordinal));
            var workerOutput = Path.Combine(fixture, "bin", configuration, "net10.0", RuntimeInformation.RuntimeIdentifier, "sunder-dev");
            var appOutput = Path.Combine(fixture, "App", "bin", configuration, "net10.0-windows", "sunder-dev");
            var worker = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(workerOutput);
            var app = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(appOutput);
            Assert.True(worker.Success, string.Join(" | ", worker.Errors));
            Assert.True(app.Success, string.Join(" | ", app.Errors));
            var bundle = Assert.Single(worker.Manifest!.ContractBundles!);
            var appBundle = Assert.Single(app.Manifest!.ContractBundles!);
            Assert.Equal(bundle!.DescriptorSha256, appBundle!.DescriptorSha256);
            Assert.Equal(bundle.AbiSha256, appBundle.AbiSha256);
            Assert.Single(worker.Manifest.Provides!);
            Assert.Empty(worker.Manifest.UsesContracts!);
            Assert.Empty(app.Manifest.Provides!);
            Assert.Equal("app", Assert.Single(app.Manifest.UsesContracts!)!.Role);
            Assert.DoesNotContain(Directory.EnumerateFiles(appOutput, "*.dll", SearchOption.AllDirectories),
                path => Path.GetFileName(path) == "Sunder.Sdk.Worker.dll" || Path.GetFileName(path).Contains("Protocol", StringComparison.Ordinal));
        }

        AssertSuccess(await RunDotnetAsync(fixture, ["msbuild", traversal, "-t:Rebuild", "-m", "-nr:false", .. properties]));
        Assert.Equal(inputs, AuthoringInputs(fixture));

        var generated = Path.Combine(fixture, "obj", configuration, "net10.0", RuntimeInformation.RuntimeIdentifier, GenerateSunderRpcBindingsTask.GeneratedDirectoryName);
        var marker = GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(generated);
        var appGenerated = Path.Combine(fixture, "App", "obj", configuration, "net10.0-windows", GenerateSunderRpcBindingsTask.GeneratedDirectoryName);
        var appMarker = GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(appGenerated);
        var timestamps = ReadTreeTimestamps(generated, marker);
        var appTimestamps = ReadTreeTimestamps(appGenerated, appMarker);
        foreach (var path in new[] { project, appProject })
            AssertSuccess(await RunDotnetAsync(fixture, ["build", path, "--no-restore", "--disable-build-servers", .. properties]));
        Assert.Equal(timestamps, ReadTreeTimestamps(generated, marker));
        Assert.Equal(appTimestamps, ReadTreeTimestamps(appGenerated, appMarker));
        Assert.Equal(inputs, AuthoringInputs(fixture));
        Assert.True(File.Exists(Path.Combine(appGenerated, SunderRpcAuthoringDocuments.AuthoringReceiptFileName)));
    }

    private static string[] AuthoringInputs(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).Order(StringComparer.Ordinal).ToArray();
}
