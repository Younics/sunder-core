using System.Diagnostics;
using Sunder.Package.Build.Tasks;
using Xunit;
using static Sunder.Package.Build.Tests.PackageManifestFixture;

namespace Sunder.Package.Build.Tests;

[Collection("Package build artifacts")]
public sealed class PackageDevOutputSafetyTests
{
    [Theory]
    [InlineData("project")]
    [InlineData("target")]
    [InlineData("root")]
    [InlineData("outside")]
    [InlineData("project-child")]
    [InlineData("nested-target-child")]
    public void ValidateDevOutputPath_RejectsUnsafeDeletionRoots(string kind)
    {
        var project = CreateTempDirectory();
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(target);
        var output = kind switch
        {
            "project" => project,
            "target" => target,
            "root" => Path.GetPathRoot(project)!,
            "project-child" => Path.Combine(project, "sunder-dev"),
            "nested-target-child" => Path.Combine(target, "nested", "sunder-dev"),
            _ => Path.Combine(Path.GetTempPath(), "unrelated-sunder-output"),
        };
        var buildEngine = new TestBuildEngine();
        var task = new ValidateSunderDevOutputPathTask
        {
            BuildEngine = buildEngine,
            DevOutputPath = output,
            ProjectDirectory = project,
            TargetDirectory = target,
            DiscoveryRoot = target,
        };

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains("unsafe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateDevOutputPath_AcceptsDedicatedTargetChild()
    {
        var project = CreateTempDirectory();
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var task = new ValidateSunderDevOutputPathTask
        {
            BuildEngine = new TestBuildEngine(),
            DevOutputPath = Path.Combine(target, "sunder-dev"),
            ProjectDirectory = project,
            TargetDirectory = target,
            DiscoveryRoot = target,
        };

        Assert.True(task.Execute());
        Assert.True(Path.EndsInDirectorySeparator(task.NormalizedDevOutputPath));
    }

    [Theory]
    [InlineData("src")]
    [InlineData("Assets")]
    public void ValidateDevOutputPath_RejectsSourceTreeTarget(string sourceDirectory)
    {
        var project = CreateTempDirectory();
        var target = Path.Combine(project, sourceDirectory, "generated");
        var task = new ValidateSunderDevOutputPathTask
        {
            BuildEngine = new TestBuildEngine(),
            DevOutputPath = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName),
            ProjectDirectory = project,
            TargetDirectory = target,
            DiscoveryRoot = target,
        };

        Assert.False(task.Execute());
    }

    [Fact]
    public void ValidateDevOutputPath_RejectsExistingUnmarkedDirectory()
    {
        var project = CreateTempDirectory();
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "keep.txt"), "keep");
        var task = new ValidateSunderDevOutputPathTask
        {
            BuildEngine = new TestBuildEngine(),
            DevOutputPath = output,
            ProjectDirectory = project,
            TargetDirectory = target,
            DiscoveryRoot = target,
        };

        Assert.False(task.Execute());
        Assert.True(File.Exists(Path.Combine(output, "keep.txt")));
    }

    [Fact]
    public void ValidateDevOutputPath_AcceptsExistingMarkedDirectory()
    {
        var project = CreateTempDirectory();
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        Directory.CreateDirectory(output);
        File.WriteAllText(ValidateSunderDevOutputPathTask.GetOwnershipMarkerPath(output), "generated");
        var task = new ValidateSunderDevOutputPathTask
        {
            BuildEngine = new TestBuildEngine(),
            DevOutputPath = output,
            ProjectDirectory = project,
            TargetDirectory = target,
            DiscoveryRoot = target,
        };

        Assert.True(task.Execute());
        Assert.Equal(
            ValidateSunderDevOutputPathTask.GetOwnershipMarkerPath(output),
            task.OwnershipMarkerPath);
        Assert.False(File.Exists(Path.Combine(output, ValidateSunderDevOutputPathTask.MarkerFileName)));
    }

    [Fact]
    public async Task ValidateDevOutputPathTarget_RejectsUnmarkedDirectoryWithoutDeletingIt()
    {
        var project = CreateTempDirectory();
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        Directory.CreateDirectory(output);
        var sentinel = Path.Combine(output, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        var targetFile = Path.Combine(
            FindRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.targets");
        var projectFile = Path.Combine(project, "TargetTest.proj");
        File.WriteAllText(projectFile, $$"""
            <Project>
              <PropertyGroup>
                <Configuration>Debug</Configuration>
                <TargetFramework>net10.0</TargetFramework>
                <BaseIntermediateOutputPath>obj/</BaseIntermediateOutputPath>
                <TargetDir>{{target}}{{Path.DirectorySeparatorChar}}</TargetDir>
                <SunderDevOutputPath>{{output}}</SunderDevOutputPath>
                <SunderPackageBuildTaskAssembly>{{typeof(ValidateSunderDevOutputPathTask).Assembly.Location}}</SunderPackageBuildTaskAssembly>
                <SunderPackageBuildTaskSourceIsImmutable>true</SunderPackageBuildTaskSourceIsImmutable>
              </PropertyGroup>
              <Import Project="{{targetFile}}" />
            </Project>
            """);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = project,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "msbuild", projectFile, "-t:_ValidateSunderDevOutputPath", "-nologo" },
        })!;
        var outputText = await process.StandardOutput.ReadToEndAsync();
        var errorText = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("generated-output marker", outputText + errorText, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(sentinel));
    }
}
