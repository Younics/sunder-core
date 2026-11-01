using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Xunit;
using static Sunder.Package.Build.Tests.PackageBuildProcessFixture;
using static Sunder.Package.Build.Tests.PackageManifestFixture;

namespace Sunder.Package.Build.Tests;

[Collection("Package build artifacts")]
public sealed class PackageSigningTests
{
    [Fact]
    public async Task EmitDevOutput_IndexesPostSigningWorkerBytesAndDetectsLaterTampering()
    {
        var root = CreateTempDirectory();
        var targetDirectory = Path.Combine(root, "target");
        var devPackage = Path.Combine(root, "sunder-dev");
        Directory.CreateDirectory(targetDirectory);
        var rid = RuntimeInformation.RuntimeIdentifier;
        var moduleName = CSharpWorkerModuleName();
        var modulePath = Path.Combine(targetDirectory, moduleName);
        await File.WriteAllBytesAsync(modulePath, "module::platform-signature"u8.ToArray());
        var workerFiles = CreateDotNetWorkerFiles(targetDirectory, rid, modulePath);
        var manifestPath = Path.Combine(root, "sunder-package.json");
        var manifestEngine = new TestBuildEngine();
        var manifestTask = CreateTask(manifestPath, manifestEngine);
        ConfigureWorkerTask(manifestTask, rid);
        Assert.True(manifestTask.Execute(), string.Join(Environment.NewLine, manifestEngine.Errors));

        var emitEngine = new TestBuildEngine();
        Assert.True(new EmitSunderDevOutputTask
        {
            BuildEngine = emitEngine,
            ManifestPath = manifestPath,
            DevPackagePath = devPackage,
            DiscoveryRoot = root,
            TargetDirectory = targetDirectory,
            WorkerFiles = workerFiles,
        }.Execute(), string.Join(Environment.NewLine, emitEngine.Errors));

        var packagedModule = Path.Combine(devPackage, "payload", "runtime", rid, "lib", moduleName);
        var index = JsonSerializer.Deserialize<SunderPackageContentIndex>(await File.ReadAllTextAsync(
            Path.Combine(devPackage, "manifest", "content-index.json")))!;
        var entry = Assert.Single(index.Files!, item => item!.Path == $"payload/runtime/{rid}/lib/{moduleName}")!;
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(modulePath))).ToLowerInvariant(),
            entry.Sha256);

        await File.AppendAllTextAsync(packagedModule, "::tampered");
        var tampered = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(devPackage);
        Assert.False(tampered.Success);
        Assert.Contains(tampered.Errors, error =>
            error.Contains($"payload/runtime/{rid}/lib/{moduleName}", StringComparison.Ordinal)
            && error.Contains("mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PackageTargets_WorkerSigningRunsAfterPublishCollectionAndBeforeContentIndexing()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var targetDirectory = Path.Combine(project, "target");
        var sourceDirectory = Path.Combine(root, "publish");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateDirectory(sourceDirectory);
        var rid = RuntimeInformation.RuntimeIdentifier;
        var moduleName = CSharpWorkerModuleName();
        var sourceModule = Path.Combine(sourceDirectory, moduleName);
        await File.WriteAllTextAsync(sourceModule, "unsigned-worker");
        var workerFiles = CreateDotNetWorkerFiles(sourceDirectory, rid, sourceModule);
        var sourceRuntimeConfig = workerFiles.Single(item => item.ItemSpec.EndsWith(".runtimeconfig.json", StringComparison.Ordinal)).ItemSpec;
        var sourceDeps = workerFiles.Single(item => item.ItemSpec.EndsWith(".deps.json", StringComparison.Ordinal)).ItemSpec;

        var manifestPath = Path.Combine(root, "sunder-package.json");
        var manifestEngine = new TestBuildEngine();
        var manifestTask = CreateTask(manifestPath, manifestEngine);
        ConfigureWorkerTask(manifestTask, rid);
        Assert.True(manifestTask.Execute(), string.Join(Environment.NewLine, manifestEngine.Errors));

        var targetsPath = Path.GetFullPath(Path.Combine(
            FindTaskDirectory(),
            "..",
            "build",
            "Sunder.Package.Build.targets"));
        var devPackage = Path.Combine(targetDirectory, "sunder-dev");
        var projectFile = Path.Combine(project, "SigningOrder.proj");
        await File.WriteAllTextAsync(projectFile, $$"""
            <Project>
              <PropertyGroup>
                <Configuration>Debug</Configuration>
                <TargetFramework>net10.0</TargetFramework>
                <BaseIntermediateOutputPath>obj/</BaseIntermediateOutputPath>
                <IntermediateOutputPath>obj/</IntermediateOutputPath>
                <TargetDir>{{targetDirectory}}{{Path.DirectorySeparatorChar}}</TargetDir>
                <SunderGeneratedPackageManifest>{{manifestPath}}</SunderGeneratedPackageManifest>
                <SunderDevOutputPath>{{devPackage}}</SunderDevOutputPath>
                <SunderDevOutputDiscoveryRoot>{{targetDirectory}}</SunderDevOutputDiscoveryRoot>
                <SunderPackageWorkerSigningTargets>TestSignWorker</SunderPackageWorkerSigningTargets>
                <SunderPackageBuildProject>{{Path.Combine(root, "missing.csproj")}}</SunderPackageBuildProject>
                <SunderPackageBuildTaskAssembly>{{typeof(PackSunderPackageTask).Assembly.Location}}</SunderPackageBuildTaskAssembly>
              </PropertyGroup>
              <Target Name="ComputeFilesToPublish">
                <ItemGroup>
                  <ResolvedFileToPublish Include="{{sourceModule}}" RelativePath="{{moduleName}}" />
                  <ResolvedFileToPublish Include="{{sourceRuntimeConfig}}" RelativePath="{{Path.GetFileName(sourceRuntimeConfig)}}" />
                  <ResolvedFileToPublish Include="{{sourceDeps}}" RelativePath="{{Path.GetFileName(sourceDeps)}}" />
                </ItemGroup>
              </Target>
              <Target Name="TestSignWorker">
                <WriteLinesToFile File="%(SunderPackageWorkerSigningFile.Identity)"
                                  Condition="'%(SunderPackageWorkerSigningFile.Extension)' == '.dll'"
                                  Lines="signed"
                                  Overwrite="false"
                                  Encoding="UTF-8" />
              </Target>
              <Import Project="{{targetsPath}}" />
              <Target Name="GenerateSunderPackageManifest">
                <PropertyGroup>
                  <_SunderHasWorkerTarget>true</_SunderHasWorkerTarget>
                </PropertyGroup>
              </Target>
              <Target Name="Run" DependsOnTargets="EmitSunderDevOutput" />
            </Project>
            """);

        var result = await ReadProcessAsync(StartMsbuild(projectFile, project));

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal("unsigned-worker", await File.ReadAllTextAsync(sourceModule));
        var packagedModule = Path.Combine(devPackage, "payload", "runtime", rid, "lib", moduleName);
        Assert.EndsWith(
            "signed" + Environment.NewLine,
            await File.ReadAllTextAsync(packagedModule),
            StringComparison.Ordinal);
        var contentIndex = JsonSerializer.Deserialize<SunderPackageContentIndex>(await File.ReadAllTextAsync(
            Path.Combine(devPackage, "manifest", "content-index.json")))!;
        var indexedModule = Assert.Single(
            contentIndex.Files!,
            entry => entry!.Path == $"payload/runtime/{rid}/lib/{moduleName}")!;
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(packagedModule))).ToLowerInvariant(),
            indexedModule.Sha256);
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(devPackage);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
    }

    [Fact]
    public async Task PackageTargets_WorkerSigningMutatesPrivateClosureWithoutChangingSources()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(cache);
        var module = Path.Combine(cache, "Worker.dll");
        var managedAssembly = Path.Combine(cache, "Authored.Dependency.dll");
        var documentation = Path.Combine(cache, "Authored.Dependency.xml");
        var runtimeConfiguration = Path.Combine(cache, "runtime.settings.xml");
        var runtimeLibrary = Path.Combine(cache, "libPackage.Native.dylib");
        await File.WriteAllTextAsync(module, "source-module");
        await File.WriteAllTextAsync(managedAssembly, "source-assembly");
        await File.WriteAllTextAsync(documentation, "source-documentation");
        await File.WriteAllTextAsync(runtimeConfiguration, "source-configuration");
        await File.WriteAllTextAsync(runtimeLibrary, "source-runtime");
        var targetsPath = Path.GetFullPath(Path.Combine(
            FindTaskDirectory(),
            "..",
            "build",
            "Sunder.Package.Build.targets"));
        var projectFile = Path.Combine(project, "Signing.proj");
        await File.WriteAllTextAsync(projectFile, $$"""
            <Project>
              <PropertyGroup>
                <IntermediateOutputPath>obj/</IntermediateOutputPath>
                <_SunderHasWorkerTarget>true</_SunderHasWorkerTarget>
                <SunderPackageWorkerSigningTargets>TestSignWorker</SunderPackageWorkerSigningTargets>
                <SunderPackageBuildProject>{{Path.Combine(root, "missing.csproj")}}</SunderPackageBuildProject>
                <SunderPackageBuildTaskAssembly>{{typeof(PackSunderPackageTask).Assembly.Location}}</SunderPackageBuildTaskAssembly>
              </PropertyGroup>
              <Target Name="ComputeFilesToPublish">
                <ItemGroup>
                  <ResolvedFileToPublish Include="{{module}}" RelativePath="Worker.dll" />
                  <ResolvedFileToPublish Include="{{managedAssembly}}" RelativePath="Authored.Dependency.dll" />
                  <ResolvedFileToPublish Include="{{documentation}}" RelativePath="Authored.Dependency.xml" />
                  <ResolvedFileToPublish Include="{{runtimeConfiguration}}" RelativePath="runtime.settings.xml" />
                  <ResolvedFileToPublish Include="{{runtimeLibrary}}" RelativePath="runtimes/osx-arm64/native/libPackage.Native.dylib" />
                </ItemGroup>
              </Target>
              <Target Name="TestSignWorker">
                <WriteLinesToFile File="%(SunderPackageWorkerSigningFile.Identity)"
                                  Lines="signed"
                                  Overwrite="false"
                                  Encoding="UTF-8" />
              </Target>
              <Target Name="Run" DependsOnTargets="_RunSunderPackageWorkerSigning" />
              <Import Project="{{targetsPath}}" />
            </Project>
            """);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = project,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "msbuild", projectFile, "-t:Run", "-nologo" },
        })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, output + error);
        Assert.Equal("source-module", await File.ReadAllTextAsync(module));
        Assert.Equal("source-assembly", await File.ReadAllTextAsync(managedAssembly));
        Assert.Equal("source-documentation", await File.ReadAllTextAsync(documentation));
        Assert.Equal("source-configuration", await File.ReadAllTextAsync(runtimeConfiguration));
        Assert.Equal("source-runtime", await File.ReadAllTextAsync(runtimeLibrary));
        var signingRoot = Path.Combine(project, "obj", "sunder-worker-signing");
        Assert.EndsWith("signed" + Environment.NewLine, await File.ReadAllTextAsync(Path.Combine(signingRoot, "Worker.dll")), StringComparison.Ordinal);
        Assert.EndsWith(
            "signed" + Environment.NewLine,
            await File.ReadAllTextAsync(Path.Combine(signingRoot, "Authored.Dependency.dll")),
            StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(signingRoot, "Authored.Dependency.xml")));
        Assert.EndsWith(
            "signed" + Environment.NewLine,
            await File.ReadAllTextAsync(Path.Combine(signingRoot, "runtime.settings.xml")),
            StringComparison.Ordinal);
        Assert.EndsWith(
            "signed" + Environment.NewLine,
            await File.ReadAllTextAsync(Path.Combine(signingRoot, "runtimes", "osx-arm64", "native", "libPackage.Native.dylib")),
            StringComparison.Ordinal);
    }
}
