using System.Diagnostics;
using System.Runtime.InteropServices;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class PrimaryPackageIntegrationTests
{
    private static readonly string[] SupportedRuntimeIdentifiers =
    [
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "osx-x64",
        "osx-arm64",
    ];

    [Fact]
    public async Task PrimaryProject_ComposesBuildPacksPrebuiltLeavesAndCleansSafely()
    {
        var repositoryRoot = FindRepositoryRoot();
        var fixtureDirectory = Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "tests",
            "Sunder.Package.Build.TestFixtures",
            "WorkerV3Executable");
        var project = Path.Combine(fixtureDirectory, "WorkerV3Executable.csproj");
        var configuration = ResolveConfiguration();
        var outputRoot = Path.Combine(fixtureDirectory, "bin", configuration, "net10.0");
        var composed = Path.Combine(outputRoot, "sunder-dev");
        var currentLeaf = Path.Combine(outputRoot, RuntimeInformation.RuntimeIdentifier, "sunder-dev");
        var root = CreateTempDirectory();

        try
        {
            var buildTrace = Path.Combine(root, "build.trace");
            AssertSuccess(await RunDotnetAsync(
                repositoryRoot,
                "build",
                project,
                "--no-restore",
                "--disable-build-servers",
                "-m:1",
                $"-p:Configuration={configuration}",
                $"-p:SunderPackageBuildTraceFile={buildTrace}"));

            Assert.True(File.Exists(Path.Combine(currentLeaf, "manifest", "sunder-package.json")));
            Assert.True(File.Exists(Path.Combine(composed, "manifest", "sunder-package.json")));
            Assert.NotEqual(Path.GetFullPath(currentLeaf), Path.GetFullPath(composed));
            Assert.Equal(
                [$"{RuntimeInformation.RuntimeIdentifier}|false"],
                await File.ReadAllLinesAsync(buildTrace));

            var buildValidation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(composed);
            Assert.True(buildValidation.Success, string.Join(Environment.NewLine, buildValidation.Errors));
            Assert.Single(buildValidation.Manifest!.Targets!);

            var packTrace = Path.Combine(root, "pack.trace");
            var archivePath = Path.Combine(root, "universal.sunderpkg");
            AssertSuccess(await RunDotnetAsync(
                repositoryRoot,
                "msbuild",
                project,
                "-t:PackSunderPackage",
                $"-p:Configuration={configuration}",
                $"-p:SunderPackageBuildTraceFile={packTrace}",
                $"-p:SunderPackageOutputPath={archivePath}",
                "-m:1",
                "-nr:false",
                "-nologo"));

            var innerBuilds = await File.ReadAllLinesAsync(packTrace);
            Assert.Equal(6, innerBuilds.Length);
            Assert.Equal(
                SupportedRuntimeIdentifiers.Order(StringComparer.Ordinal),
                innerBuilds.Select(static line => line.Split('|')[0]).Order(StringComparer.Ordinal));
            Assert.All(innerBuilds, line => Assert.EndsWith("|true", line, StringComparison.Ordinal));

            var universalValidation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
                archivePath,
                Path.Combine(root, "universal-validation"));
            Assert.True(universalValidation.Success, string.Join(Environment.NewLine, universalValidation.Errors));
            Assert.Equal(6, universalValidation.Manifest!.Targets!.Count);
            var universalPaths = universalValidation.ContentIndex!.Files!
                .Select(static file => file!.Path!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(universalPaths, path =>
                path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && universalPaths.Contains(Path.ChangeExtension(path, ".dll").Replace('\\', '/')));
            Assert.Contains(universalPaths, static path => path.EndsWith("/Sunder.Sdk.pdb", StringComparison.Ordinal));
            Assert.Contains(universalPaths, static path => path.EndsWith("/Sunder.Sdk.Worker.pdb", StringComparison.Ordinal));
            Assert.Contains(universalPaths, static path => path.EndsWith("/WorkerV3Executable.pdb", StringComparison.Ordinal));
            var universalBytes = await File.ReadAllBytesAsync(archivePath);

            var prebuiltTrace = Path.Combine(root, "prebuilt.trace");
            var prebuiltArchive = Path.Combine(root, "prebuilt-universal.sunderpkg");
            AssertSuccess(await RunDotnetAsync(
                repositoryRoot,
                "msbuild",
                project,
                "-t:PackSunderPackage",
                $"-p:Configuration={configuration}",
                "-p:SunderPackageUsePrebuiltLeaves=true",
                "-p:BuildProjectReferences=false",
                $"-p:SunderPackageBuildTraceFile={prebuiltTrace}",
                $"-p:SunderPackageOutputPath={prebuiltArchive}",
                "-m:1",
                "-nr:false",
                "-nologo"));
            Assert.False(File.Exists(prebuiltTrace));
            Assert.Equal(universalBytes, await File.ReadAllBytesAsync(prebuiltArchive));

            var rejectedBuildTrace = Path.Combine(root, "rejected-build.trace");
            var rejectedBuild = await RunDotnetAsync(
                repositoryRoot,
                "build",
                project,
                "--no-restore",
                "--disable-build-servers",
                "-m:1",
                $"-p:Configuration={configuration}",
                "-p:SunderPackageUsePrebuiltLeaves=true",
                "-p:BuildProjectReferences=false",
                $"-p:SunderPackageBuildTraceFile={rejectedBuildTrace}");
            AssertFailure(rejectedBuild, "does not permit Build");
            Assert.False(File.Exists(rejectedBuildTrace));

            var oneRidArchive = Path.Combine(root, "prebuilt-one-rid.sunderpkg");
            AssertSuccess(await RunDotnetAsync(
                repositoryRoot,
                "msbuild",
                project,
                "-t:PackSunderPackage",
                $"-p:Configuration={configuration}",
                "-p:SunderPackageUsePrebuiltLeaves=true",
                "-p:BuildProjectReferences=false",
                $"-p:SunderPackageComposeRuntimeIdentifiers={RuntimeInformation.RuntimeIdentifier}",
                $"-p:SunderPackageOutputPath={oneRidArchive}",
                "-m:1",
                "-nr:false",
                "-nologo"));
            var oneRidValidation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
                oneRidArchive,
                Path.Combine(root, "one-rid-validation"));
            Assert.True(oneRidValidation.Success, string.Join(Environment.NewLine, oneRidValidation.Errors));
            Assert.Single(oneRidValidation.Manifest!.Targets!);

            var additionalRid = SupportedRuntimeIdentifiers.First(rid => rid != RuntimeInformation.RuntimeIdentifier);
            var additionalLeafRoot = Path.Combine(outputRoot, additionalRid);
            var additionalArchive = Path.Combine(root, "prebuilt-with-node-leaf.sunderpkg");
            AssertSuccess(await RunDotnetAsync(
                repositoryRoot,
                "msbuild",
                project,
                "-t:PackSunderPackage",
                $"-p:Configuration={configuration}",
                "-p:SunderPackageUsePrebuiltLeaves=true",
                "-p:BuildProjectReferences=false",
                $"-p:SunderPackageComposeRuntimeIdentifiers={RuntimeInformation.RuntimeIdentifier}",
                $"-p:SunderFixtureAdditionalNodeLeaf={Path.Combine(additionalLeafRoot, "sunder-dev")}",
                $"-p:SunderFixtureAdditionalNodeLeafDiscoveryRoot={additionalLeafRoot}",
                $"-p:SunderPackageOutputPath={additionalArchive}",
                "-m:1",
                "-nr:false",
                "-nologo"));
            var additionalValidation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
                additionalArchive,
                Path.Combine(root, "additional-validation"));
            Assert.True(additionalValidation.Success, string.Join(Environment.NewLine, additionalValidation.Errors));
            Assert.Equal(2, additionalValidation.Manifest!.Targets!.Count);

            var missingFlag = await RunDotnetAsync(
                repositoryRoot,
                "msbuild",
                project,
                "-t:PackSunderPackage",
                $"-p:Configuration={configuration}",
                "-p:SunderPackageUsePrebuiltLeaves=true",
                $"-p:SunderPackageComposeRuntimeIdentifiers={RuntimeInformation.RuntimeIdentifier}",
                "-m:1",
                "-nr:false",
                "-nologo");
            AssertFailure(missingFlag, "requires BuildProjectReferences=false");

            var missingLeaf = await RunDotnetAsync(
                repositoryRoot,
                "msbuild",
                project,
                "-t:PackSunderPackage",
                $"-p:Configuration={configuration}",
                "-p:SunderPackageUsePrebuiltLeaves=true",
                "-p:BuildProjectReferences=false",
                $"-p:SunderPackageComposeRuntimeIdentifiers={RuntimeInformation.RuntimeIdentifier}",
                $"-p:SunderPackagePrimaryTargetLeafRoot={Path.Combine(root, "missing-leaves")}",
                "-m:1",
                "-nr:false",
                "-nologo");
            AssertFailure(missingLeaf, "does not exist");

            AssertSuccess(await RunDotnetAsync(
                repositoryRoot,
                "msbuild",
                project,
                "-t:Clean",
                $"-p:Configuration={configuration}",
                "-m:1",
                "-nr:false",
                "-nologo"));
            Assert.False(Directory.Exists(composed));
            Assert.False(File.Exists(composed + ".sunder-aggregate-generated-output"));
            Assert.True(File.Exists(Path.Combine(currentLeaf, "manifest", "sunder-package.json")));

            var unsafeRoot = Path.Combine(root, "unmarked-output");
            var unsafeOutput = Path.Combine(unsafeRoot, "sunder-dev");
            Directory.CreateDirectory(unsafeOutput);
            var sentinel = Path.Combine(unsafeOutput, "keep.txt");
            await File.WriteAllTextAsync(sentinel, "keep");
            var unsafeClean = await RunDotnetAsync(
                repositoryRoot,
                "msbuild",
                project,
                "-t:CleanSunderPackageComposedOutput",
                $"-p:Configuration={configuration}",
                $"-p:SunderPackageComposedOutputRoot={unsafeRoot}",
                "-m:1",
                "-nr:false",
                "-nologo");
            AssertFailure(unsafeClean, "not a safely marked generated directory");
            Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task<ProcessResult> RunDotnetAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            return new ProcessResult(-1, await standardOutput, await standardError + "dotnet timed out.");
        }
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static void AssertSuccess(ProcessResult result)
        => Assert.True(
            result.ExitCode == 0,
            $"dotnet exited with {result.ExitCode}.{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");

    private static void AssertFailure(ProcessResult result, string expected)
    {
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expected, result.StandardOutput + result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveConfiguration()
        => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent?.Name
           ?? "Debug";

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Sunder Core repository root.");
    }

    private static string CreateTempDirectory()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (OperatingSystem.IsMacOS() && tempRoot.StartsWith("/var/", StringComparison.Ordinal))
        {
            tempRoot = "/private" + tempRoot;
        }
        var path = Path.Combine(tempRoot, "sunder-primary-package-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Preserve the primary assertion when best-effort temporary cleanup fails.
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
