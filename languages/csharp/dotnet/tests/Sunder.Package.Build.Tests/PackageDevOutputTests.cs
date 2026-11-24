using System.Security.Cryptography;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Worker;
using Xunit;
using MSBuildTaskItem = Microsoft.Build.Utilities.TaskItem;
using static Sunder.Package.Build.Tests.PackageBuildProcessFixture;
using static Sunder.Package.Build.Tests.PackageManifestFixture;
using static Sunder.Package.Build.Tests.PackageOutputFixture;

namespace Sunder.Package.Build.Tests;

[Collection("Package build artifacts")]
public sealed class PackageDevOutputTests
{
    [Theory]
    [InlineData("osx-arm64", "libresolved.dylib")]
    [InlineData("linux-arm64", "libresolved.so")]
    [InlineData("win-arm64", "libresolved.dll")]
    public async Task AppDevOutput_PreservesResolvedFlatNativeBytesInTheExactRid(string rid, string nativeName)
    {
        var root = CreateTempDirectory();
        var seed = CreateCanonicalLeaf(root, "app", rid, "nested-native");
        var project = Path.GetDirectoryName(seed)!;
        var target = Path.Combine(project, "target");
        var devOutput = Path.Combine(target, "sunder-dev");
        var packageCache = Path.Combine(project, "package-cache");
        Directory.CreateDirectory(packageCache);
        var reference = Path.Combine(packageCache, nativeName);
        var resolvedOutput = Path.Combine(target, nativeName);
        await File.WriteAllTextAsync(reference, "original NuGet bytes");
        await File.WriteAllTextAsync(resolvedOutput, "resolved build output bytes");
        await File.WriteAllTextAsync(Path.Combine(target, "libunrelated.so"), "untracked output");
        var targets = Path.GetFullPath(Path.Combine(FindTaskDirectory(), "..", "build", "Sunder.Package.Build.targets"));
        var projectFile = Path.Combine(project, "Emit.proj");
        await File.WriteAllTextAsync(projectFile, $$"""
            <Project>
              <PropertyGroup>
                <IntermediateOutputPath>obj/</IntermediateOutputPath>
                <TargetDir>{{target}}{{Path.DirectorySeparatorChar}}</TargetDir>
                <RuntimeIdentifier>{{rid}}</RuntimeIdentifier>
                <SunderPackageCompilationRole>app</SunderPackageCompilationRole>
                <SunderDevOutputPath>{{devOutput}}</SunderDevOutputPath>
                <SunderDevOutputDiscoveryRoot>{{target}}</SunderDevOutputDiscoveryRoot>
                <SunderGeneratedPackageManifest>{{Path.Combine(project, "sunder-package.json")}}</SunderGeneratedPackageManifest>
                <SunderPackageBuildProject>{{Path.Combine(root, "missing.csproj")}}</SunderPackageBuildProject>
                <SunderPackageBuildTaskAssembly>{{typeof(EmitSunderDevOutputTask).Assembly.Location}}</SunderPackageBuildTaskAssembly>
              </PropertyGroup>
              <ItemGroup>
                <ReferenceCopyLocalPaths Include="{{reference}}" AssetType="native" />
              </ItemGroup>
              <Import Project="{{targets}}" />
              <Target Name="GenerateSunderPackageManifest" />
              <Target Name="Run" DependsOnTargets="EmitSunderDevOutput" />
            </Project>
            """);
        var result = await ReadProcessAsync(StartMsbuild(projectFile, project));
        Assert.True(result.ExitCode == 0, result.Output);

        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(devOutput);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        var plan = SunderPackageTargetResolver.CreateProjectionPlan(validation.Manifest!, validation.ContentIndex!, new("app", rid));
        var native = Assert.Single(plan.Files, file => file.LogicalPath.ToString() == "lib/" + nativeName);
        Assert.Equal(SunderPackagePayloadLayer.Target, native.Layer);
        Assert.Equal("resolved build output bytes", await File.ReadAllTextAsync(native.PhysicalPath.ToPlatformPath(devOutput)));
        Assert.Equal("original NuGet bytes", await File.ReadAllTextAsync(reference));
        Assert.DoesNotContain(plan.Files, file => file.LogicalPath.ToString().EndsWith("libunrelated.so", StringComparison.Ordinal));
        Assert.Contains(plan.Files, file => file.LogicalPath.ToString() == $"lib/runtimes/{rid}/native/fixture.bin");
    }

    [Fact]
    public async Task EmitDevOutput_CreatesValidatedCanonicalTreeAndIsolatesNativeRids()
    {
        var root = CreateTempDirectory();
        var targetDirectory = Path.Combine(root, "target");
        var devPackage = Path.Combine(root, "sunder-dev");
        Directory.CreateDirectory(targetDirectory);
        var assemblyPath = Path.Combine(targetDirectory, Path.GetFileName(ManifestAssemblyPath));
        File.Copy(ManifestAssemblyPath, assemblyPath);
        var winNative = Path.Combine(targetDirectory, "runtimes", "win-x64", "native", "fixture.bin");
        var linuxNative = Path.Combine(targetDirectory, "runtimes", "linux-x64", "native", "fixture.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(winNative)!);
        Directory.CreateDirectory(Path.GetDirectoryName(linuxNative)!);
        File.WriteAllText(winNative, "win");
        File.WriteAllText(linuxNative, "linux");

        var manifestPath = Path.Combine(root, "sunder-package.json");
        var manifestEngine = new TestBuildEngine();
        var manifestTask = CreateTask(manifestPath, manifestEngine);
        var target = new MSBuildTaskItem("runtime/win-x64");
        var workerName = CSharpWorkerModuleName();
        var workerPath = Path.Combine(targetDirectory, workerName);
        File.WriteAllText(workerPath, "worker");
        var workerFiles = CreateDotNetWorkerFiles(targetDirectory, "win-x64", workerPath);
        ConfigureWorkerTask(manifestTask, "win-x64", target);
        Assert.True(manifestTask.Execute(), string.Join(Environment.NewLine, manifestEngine.Errors));

        var assets = Path.Combine(root, "authored-assets");
        Directory.CreateDirectory(assets);
        var asset = Path.Combine(assets, "icon.png");
        var assetBytes = RandomNumberGenerator.GetBytes(8192);
        await File.WriteAllBytesAsync(asset, assetBytes);
        if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(asset, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var references = Path.Combine(root, "nuget-cache");
        Directory.CreateDirectory(references);
        var reference = Path.Combine(references, "Sunder.Sdk.Worker.dll");
        var referenceBytes = await File.ReadAllBytesAsync(typeof(SunderWorkerProviderRegistration).Assembly.Location);
        await File.WriteAllBytesAsync(reference, referenceBytes); // A fresh file, not an already shared APFS clone.
        var referenceTime = File.GetLastWriteTimeUtc(reference);
        var cachedReference = new MSBuildTaskItem(reference);
        cachedReference.SetMetadata("RelativePath", Path.GetFileName(reference));
        var sourceChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sourceWatcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        void ObserveInput(object sender, FileSystemEventArgs args)
        {
            if (args.FullPath == asset || args.FullPath == reference) sourceChanged.TrySetResult();
        }
        sourceWatcher.Changed += ObserveInput;
        sourceWatcher.Created += ObserveInput;
        sourceWatcher.EnableRaisingEvents = true;
        var emitEngine = new TestBuildEngine();
        Assert.True(new EmitSunderDevOutputTask
        {
            BuildEngine = emitEngine,
            ManifestPath = manifestPath,
            DevPackagePath = devPackage,
            DiscoveryRoot = root,
            TargetDirectory = targetDirectory,
            WorkerFiles = [.. workerFiles, new MSBuildTaskItem(winNative), cachedReference],
            AssetsDirectory = assets,
            AssetFiles = [new MSBuildTaskItem(asset)],
        }.Execute(), string.Join(Environment.NewLine, emitEngine.Errors));

        Assert.True(File.Exists(Path.Combine(devPackage, "manifest", "sunder-package.json")));
        Assert.True(File.Exists(Path.Combine(devPackage, "manifest", "content-index.json")));
        Assert.True(File.Exists(Path.Combine(devPackage, "payload", "runtime", "win-x64", "lib", workerName)));
        Assert.True(File.Exists(Path.Combine(devPackage, "payload", "runtime", "win-x64", "lib", "runtimes", "win-x64", "native", "fixture.bin")));
        Assert.False(Directory.Exists(Path.Combine(devPackage, "payload", "runtime", "linux-x64")));
        Assert.False(Directory.Exists(Path.Combine(devPackage, "lib")));
        Assert.False(Directory.Exists(Path.Combine(devPackage, "assets")));
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(devPackage);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        var copiedAsset = Path.Combine(devPackage, "payload", "shared", "assets", "icon.png");
        Assert.Equal(assetBytes, await File.ReadAllBytesAsync(copiedAsset));
        var copiedReference = Path.Combine(devPackage, "payload", "runtime", "win-x64", "lib", "Sunder.Sdk.Worker.dll");
        Assert.Equal(referenceBytes, await File.ReadAllBytesAsync(copiedReference));
        Assert.Equal(referenceBytes, await File.ReadAllBytesAsync(reference));
        Assert.Equal(referenceTime, File.GetLastWriteTimeUtc(reference));
        Assert.Equal(referenceTime, File.GetLastWriteTimeUtc(copiedReference));
        if (OperatingSystem.IsMacOS()) Assert.Equal(File.GetUnixFileMode(asset), File.GetUnixFileMode(copiedAsset));
        await Task.Delay(500);
        Assert.False(sourceChanged.Task.IsCompleted, "Copying assets or cached runtime references must not invalidate Watch's input revision.");
        await File.AppendAllTextAsync(asset, "real authored edit");
        await sourceChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)); // The observer itself is live.
    }

    [Fact]
    public async Task EmitDevOutput_PreservesPriorCanonicalTreeWhenReplacementFails()
    {
        var root = CreateTempDirectory();
        var devPackage = CreateCanonicalLeaf(root, "runtime", "win-x64", "native");
        var leafRoot = Path.Combine(root, "runtime-win-x64-leaf");
        var priorIndex = await File.ReadAllBytesAsync(Path.Combine(devPackage, "manifest", "content-index.json"));
        var priorManifest = await File.ReadAllBytesAsync(Path.Combine(devPackage, "manifest", "sunder-package.json"));
        var buildEngine = new TestBuildEngine();

        Assert.False(new EmitSunderDevOutputTask
        {
            BuildEngine = buildEngine,
            ManifestPath = Path.Combine(leafRoot, "sunder-package.json"),
            DevPackagePath = devPackage,
            DiscoveryRoot = root,
            TargetDirectory = Path.Combine(leafRoot, "target"),
            WorkerFiles = [new MSBuildTaskItem(Path.Combine(leafRoot, "target", "missing-worker"))],
        }.Execute());

        Assert.Equal(priorIndex, await File.ReadAllBytesAsync(Path.Combine(devPackage, "manifest", "content-index.json")));
        Assert.Equal(priorManifest, await File.ReadAllBytesAsync(Path.Combine(devPackage, "manifest", "sunder-package.json")));
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(devPackage);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
    }

    [Fact]
    public void EmitDevOutput_RejectsMarkerDirectoryWithoutDeletingIt()
    {
        var root = CreateTempDirectory();
        var manifestPath = Path.Combine(root, "sunder-package.json");
        var manifestEngine = new TestBuildEngine();
        Assert.True(CreateTask(manifestPath, manifestEngine).Execute(), string.Join(Environment.NewLine, manifestEngine.Errors));
        var output = Path.Combine(root, "sunder-dev");
        var markerDirectory = ValidateSunderDevOutputPathTask.GetOwnershipMarkerPath(output);
        var sentinel = Path.Combine(markerDirectory, "sentinel.txt");
        Directory.CreateDirectory(markerDirectory);
        File.WriteAllText(sentinel, "preserve");
        var buildEngine = new TestBuildEngine();

        Assert.False(new EmitSunderDevOutputTask
        {
            BuildEngine = buildEngine,
            ManifestPath = manifestPath,
            DevPackagePath = output,
            DiscoveryRoot = root,
            TargetDirectory = root,
        }.Execute());

        Assert.True(Directory.Exists(markerDirectory));
        Assert.Equal("preserve", File.ReadAllText(sentinel));
        Assert.Contains(buildEngine.Errors, error => error.Contains("must be a regular file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmitDevOutput_SerializesConcurrentMsbuildProducersWithoutMixedTrees()
    {
        var root = CreateTempDirectory();
        var manifestPath = Path.Combine(root, "sunder-package.json");
        var manifestEngine = new TestBuildEngine();
        Assert.True(CreateTask(manifestPath, manifestEngine).Execute(), string.Join(Environment.NewLine, manifestEngine.Errors));
        var output = Path.Combine(root, "sunder-dev");
        var projectFiles = new List<string>();
        foreach (var generation in new[] { "first", "second" })
        {
            var target = Path.Combine(root, generation, "target");
            var assets = Path.Combine(root, generation, "assets");
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(assets);
            var assemblyPath = Path.Combine(target, Path.GetFileName(ManifestAssemblyPath));
            File.Copy(ManifestAssemblyPath, assemblyPath);
            var assetPath = Path.Combine(assets, "generation.txt");
            File.WriteAllText(assetPath, generation);
            var projectFile = Path.Combine(root, generation, "Emit.proj");
            File.WriteAllText(projectFile, $$"""
                <Project>
                  <UsingTask TaskName="Sunder.Package.Build.Tasks.EmitSunderDevOutputTask"
                             AssemblyFile="{{typeof(EmitSunderDevOutputTask).Assembly.Location}}" />
                  <ItemGroup>
                    <Managed Include="{{assemblyPath}}" />
                    <Asset Include="{{assetPath}}" />
                  </ItemGroup>
                  <Target Name="Run">
                    <EmitSunderDevOutputTask ManifestPath="{{manifestPath}}"
                                             DevPackagePath="{{output}}"
                                             DiscoveryRoot="{{root}}"
                                             TargetDirectory="{{target}}"
                                             AssetsDirectory="{{assets}}"
                                             ManagedFiles="@(Managed)"
                                             AssetFiles="@(Asset)" />
                  </Target>
                </Project>
                """);
            projectFiles.Add(projectFile);
        }

        var processes = projectFiles.Select(projectFile => StartMsbuild(projectFile, Path.GetDirectoryName(projectFile)!)).ToArray();
        var results = await Task.WhenAll(processes.Select(ReadProcessAsync));

        Assert.All(results, result => Assert.True(result.ExitCode == 0, result.Output));
        Assert.Contains(
            File.ReadAllText(Path.Combine(output, "payload", "shared", "assets", "generation.txt")),
            new[] { "first", "second" });
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(output);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        Assert.True(File.Exists(ValidateSunderDevOutputPathTask.GetOwnershipMarkerPath(output)));
        Assert.False(Directory.Exists(output + ".sunder-output.lock"));
        Assert.False(Directory.Exists(output + ".sunder-output.lock.transition"));
    }
}
