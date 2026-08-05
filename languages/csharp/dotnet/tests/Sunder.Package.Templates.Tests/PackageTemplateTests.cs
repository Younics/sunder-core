using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Sunder.Package.Templates.Tests;

public sealed class PackageTemplateTests
{
    private const string TestVersion = "9.8.7-preview.4";
    private const string TestVersionRange = "[9.8.0,9.9.0)";
    private const string CurrentVersion = "1.1.0";

    [Fact]
    public async Task PackedTemplate_UsesPackageVersionAndEscapesGeneratedInputs()
    {
        var repositoryRoot = FindRepositoryRoot();
        var root = CreateTempDirectory();
        try
        {
            var packageProject = Path.Combine(
                repositoryRoot,
                "languages",
                "csharp",
                "dotnet",
                "packages",
                "Sunder.Package.Templates",
                "Sunder.Package.Templates.csproj");
            var pack = await RunDotnetAsync(
                repositoryRoot,
                "pack",
                packageProject,
                "-c",
                ResolveConfiguration(),
                "--no-restore",
                "-o",
                root,
                $"-p:Version={TestVersion}",
                $"-p:PackageVersion={TestVersion}",
                "-p:GeneratePackageOnBuild=false");
            AssertSuccess(pack);

            var packagePath = Path.Combine(root, $"Sunder.Package.Templates.{TestVersion}.nupkg");
            Assert.True(File.Exists(packagePath), $"Template package was not created: {packagePath}");
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                var configuration = Assert.Single(
                    archive.Entries,
                    entry => entry.FullName == "content/sunder-package/.template.config/template.json");
                using var document = JsonDocument.Parse(configuration.Open());
                Assert.Equal(
                    TestVersion,
                    document.RootElement
                        .GetProperty("symbols")
                        .GetProperty("coordinatedPackageVersion")
                        .GetProperty("parameters")
                        .GetProperty("value")
                        .GetString());
                Assert.Equal(
                    TestVersionRange,
                    document.RootElement
                        .GetProperty("symbols")
                        .GetProperty("coordinatedPackageVersionRange")
                        .GetProperty("parameters")
                        .GetProperty("value")
                        .GetString());
                var symbols = document.RootElement.GetProperty("symbols");
                Assert.True(symbols.TryGetProperty("withHostDependency", out _));
                Assert.Contains(
                    archive.Entries,
                    entry => entry.FullName.EndsWith(
                        "/Sunder.Package.Template.Protocol/Contracts/sample.rpc.json",
                        StringComparison.Ordinal));
            }

            var hive = Path.Combine(root, "template-hive");
            AssertSuccess(await RunDotnetAsync(
                repositoryRoot,
                "new",
                "--debug:custom-hive",
                hive,
                "install",
                packagePath));

            var missingIdentity = await RunDotnetAsync(
                repositoryRoot,
                "new",
                "--debug:custom-hive",
                hive,
                "sunder-package",
                "--name",
                "Missing.Identity",
                "--output",
                Path.Combine(root, "missing-identity"));
            Assert.NotEqual(0, missingIdentity.ExitCode);

            const string packageName = "Package & \"Quoted\" <Name> \\ Path";
            var output = Path.Combine(root, "Escaped.Sample");
            var generate = await RunDotnetAsync(
                root,
                "new",
                "--debug:custom-hive",
                hive,
                "sunder-package",
                "--name",
                "Escaped.Sample",
                "--packageId",
                "escaped.sample",
                "--packageName",
                packageName,
                "--withAvalonia",
                "--withStacks",
                "--withHostDependency",
                "--hostPackageId",
                "host.package",
                "--hostPackageVersionRange",
                ">=1.1.0 <1.2.0");
            AssertSuccess(generate);

            var xmlFiles = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path) is ".csproj" or ".props" or ".axaml")
                .Select(XDocument.Load)
                .ToArray();
            var sunderReferences = xmlFiles
                .SelectMany(static document => document.Descendants("PackageReference"))
                .Where(reference => reference.Attribute("Include")?.Value.StartsWith("Sunder.", StringComparison.Ordinal) == true)
                .ToArray();
            Assert.Equal(8, sunderReferences.Length);
            Assert.All(sunderReferences, reference => Assert.Equal(TestVersionRange, reference.Attribute("Version")?.Value));

            Assert.True(File.Exists(Path.Combine(output, "Escaped.Sample.csproj")));
            Assert.True(File.Exists(Path.Combine(output, "Escaped.Sample.Protocol", "Escaped.Sample.Protocol.csproj")));
            Assert.True(File.Exists(Path.Combine(output, "Escaped.Sample.Runtime", "Escaped.Sample.Runtime.csproj")));
            Assert.True(File.Exists(Path.Combine(output, "Escaped.Sample.App", "Escaped.Sample.App.csproj")));
            Assert.True(File.Exists(Path.Combine(output, "Escaped.Sample.Protocol", "Contracts", "sample.rpc.json")));
            Assert.True(File.Exists(Path.Combine(output, "Escaped.Sample.Protocol", "Generated", "SampleRpc.g.cs")));
            Assert.False(Directory.Exists(Path.Combine(output, "Escaped.Sample")));

            var sharedProperties = XDocument.Load(Path.Combine(output, "Sunder.Package.props"));
            Assert.Equal("1.0.0", sharedProperties.Descendants("Version").Single().Value);
            Assert.Equal("false", sharedProperties.Descendants("IsPackable").Single().Value);
            Assert.Equal("true", sharedProperties.Descendants("RestorePackagesWithLockFile").Single().Value);

            var generatedProjects = Directory.EnumerateFiles(output, "*.csproj", SearchOption.AllDirectories)
                .Select(XDocument.Load)
                .ToArray();
            Assert.Equal(4, generatedProjects.Length);
            Assert.All(generatedProjects, project =>
            {
                Assert.Empty(project.Descendants("Version"));
                Assert.Empty(project.Descendants("IsPackable"));
                Assert.Contains(project.Descendants("Import"), import =>
                    import.Attribute("Project")?.Value.EndsWith("Sunder.Package.props", StringComparison.Ordinal) == true);
            });

            var protocolProject = XDocument.Load(Path.Combine(
                output,
                "Escaped.Sample.Protocol",
                "Escaped.Sample.Protocol.csproj"));
            Assert.DoesNotContain(protocolProject.Descendants("PackageId"), _ => true);
            foreach (var roleProjectPath in new[]
                     {
                         Path.Combine(output, "Escaped.Sample.Runtime", "Escaped.Sample.Runtime.csproj"),
                         Path.Combine(output, "Escaped.Sample.App", "Escaped.Sample.App.csproj"),
                     })
            {
                var protocolReference = XDocument.Load(roleProjectPath)
                    .Descendants("ProjectReference")
                    .Single(reference => reference.Attribute("Include")?.Value.Contains(".Protocol", StringComparison.Ordinal) == true);
                Assert.Equal("all", protocolReference.Attribute("PrivateAssets")?.Value);
            }

            var packageMetadata = await File.ReadAllTextAsync(Path.Combine(output, "PackageMetadata.cs"));
            Assert.Contains("Name = \"Package & \\\"Quoted\\\" <Name> \\\\ Path\"", packageMetadata, StringComparison.Ordinal);

            var view = XDocument.Load(Path.Combine(output, "Escaped.Sample.App", "PackageViews", "DefaultPackageView.axaml"));
            Assert.Equal(
                packageName,
                view.Descendants().Single(element => element.Name.LocalName == "TextBlock"
                    && element.Attribute("FontSize") is not null).Attribute("Text")?.Value);

            Assert.Contains(
                "VersionRange = \">=1.1.0 <1.2.0\"",
                packageMetadata,
                StringComparison.Ordinal);

            foreach (var generatedFile in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                         .Where(path => Path.GetExtension(path) is ".cs" or ".csproj" or ".props" or ".axaml" or ".md" or ".json"))
            {
                Assert.DoesNotContain("SUNDER_", await File.ReadAllTextAsync(generatedFile), StringComparison.Ordinal);
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task GeneratedAggregate_BuildsAndPacksUniversalPackage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var root = CreateTempDirectory();
        try
        {
            var packageProject = Path.Combine(
                repositoryRoot,
                "languages",
                "csharp",
                "dotnet",
                "packages",
                "Sunder.Package.Templates",
                "Sunder.Package.Templates.csproj");
            AssertSuccess(await RunDotnetAsync(
                repositoryRoot,
                "pack",
                packageProject,
                "-c",
                ResolveConfiguration(),
                "--no-restore",
                "-o",
                root,
                $"-p:Version={CurrentVersion}",
                $"-p:PackageVersion={CurrentVersion}",
                "-p:GeneratePackageOnBuild=false"));

            var templatePackage = Path.Combine(root, $"Sunder.Package.Templates.{CurrentVersion}.nupkg");
            var hive = Path.Combine(root, "template-hive");
            AssertSuccess(await RunDotnetAsync(
                repositoryRoot,
                "new",
                "--debug:custom-hive",
                hive,
                "install",
                templatePackage));

            var localFeed = Path.Combine(root, "feed");
            await PackLocalSunderFeedAsync(repositoryRoot, localFeed);

            var packagesPath = Path.Combine(root, "packages");
            var shapes = new[]
            {
                new GeneratedShape("Default", [], 6, HasApp: false, HasDependency: false),
                new GeneratedShape("Avalonia", ["--withAvalonia"], 12, HasApp: true, HasDependency: false),
                new GeneratedShape("Stacks", ["--withStacks"], 6, HasApp: false, HasDependency: false),
                new GeneratedShape(
                    "HostDependency",
                    ["--withHostDependency", "--hostPackageId", "host.package", "--hostPackageVersionRange", ">=1.1.0 <1.2.0"],
                    6,
                    HasApp: false,
                    HasDependency: true),
            };
            foreach (var shape in shapes)
            {
                var projectName = "Buildable." + shape.Name;
                var packageId = "buildable." + shape.Name.ToLowerInvariant();
                var output = Path.Combine(root, "buildable-" + shape.Name.ToLowerInvariant());
                var generateArguments = new List<string>
                {
                    "new",
                    "--debug:custom-hive",
                    hive,
                    "sunder-package",
                    "--name",
                    projectName,
                    "--packageId",
                    packageId,
                    "--packageName",
                    "Buildable " + shape.Name,
                    "--output",
                    output,
                };
                generateArguments.AddRange(shape.Options);
                AssertSuccess(await RunDotnetAsync(repositoryRoot, generateArguments.ToArray()));

                var aggregateProject = Path.Combine(output, projectName + ".csproj");
                AssertSuccess(await RunDotnetAsync(
                    output,
                    "restore",
                    aggregateProject,
                    "--source",
                    localFeed,
                    "--source",
                    "https://api.nuget.org/v3/index.json",
                    "--packages",
                    packagesPath,
                    "--disable-parallel",
                    "--ignore-failed-sources"));
                AssertSuccess(await RunDotnetAsync(
                    output,
                    "build",
                    aggregateProject,
                    "--no-restore",
                    "--disable-build-servers",
                    "-m:1"));

                var protocolProjectPath = Path.Combine(
                    output,
                    projectName + ".Protocol",
                    projectName + ".Protocol.csproj");
                var protocolProject = XDocument.Load(protocolProjectPath);
                Assert.Contains(protocolProject.Descendants("Import"), import =>
                    import.Attribute("Project")?.Value.EndsWith("Sunder.Package.props", StringComparison.Ordinal) == true);
                Assert.True(File.Exists(Path.Combine(
                    output,
                    projectName + ".Protocol",
                    "Generated",
                    "SampleRpc.g.cs")));
                foreach (var generatedProject in Directory.EnumerateFiles(output, "*.csproj", SearchOption.AllDirectories))
                {
                    Assert.True(
                        File.Exists(Path.Combine(Path.GetDirectoryName(generatedProject)!, "packages.lock.json")),
                        $"Restore did not create a lock file for {generatedProject}.");
                }

                var aggregateOutput = Path.Combine(output, "bin", "Debug", "net10.0");
                var devPackage = Path.Combine(aggregateOutput, "sunder-dev");
                Assert.True(File.Exists(Path.Combine(devPackage, "manifest", "sunder-package.json")));
                Assert.True(File.Exists(Path.Combine(devPackage, "manifest", "content-index.json")));
                Assert.True(File.Exists(Path.Combine(devPackage, "payload", "shared", "contracts", "sample.rpc.json")));
                Assert.False(File.Exists(Path.Combine(devPackage, ".sunder-generated-output")));
                Assert.False(Directory.Exists(Path.Combine(devPackage, "lib")));
                Assert.False(Directory.Exists(Path.Combine(devPackage, "assets")));
                using (var manifest = JsonDocument.Parse(
                           await File.ReadAllTextAsync(Path.Combine(devPackage, "manifest", "sunder-package.json"))))
                {
                    Assert.Equal(1, manifest.RootElement.GetProperty("archiveFormatVersion").GetInt32());
                    Assert.Equal("1.0.0", manifest.RootElement.GetProperty("version").GetString());
                    var targets = manifest.RootElement.GetProperty("targets").EnumerateArray().ToArray();
                    Assert.Equal(shape.TargetCount, targets.Length);
                    Assert.Contains(targets, target => target.GetProperty("role").GetString() == "runtime");
                    Assert.Equal(
                        shape.HasApp,
                        targets.Any(target => target.GetProperty("role").GetString() == "app"));
                    var contracts = manifest.RootElement.GetProperty("contractBundles").EnumerateArray().ToArray();
                    Assert.Equal(shape.Options.Contains("--withStacks", StringComparer.Ordinal) ? 2 : 1, contracts.Length);
                    Assert.Contains(
                        contracts,
                        contract => contract.GetProperty("contractId").GetString() == packageId + ".sample");
                    if (shape.Options.Contains("--withStacks", StringComparer.Ordinal))
                    {
                        Assert.Contains(
                            contracts,
                            contract => contract.GetProperty("contractId").GetString() == "sunder.stack.contributor");
                    }
                    Assert.Empty(manifest.RootElement.GetProperty("usesContracts").EnumerateArray());
                    var providers = manifest.RootElement.GetProperty("provides").EnumerateArray().ToArray();
                    if (shape.Options.Contains("--withStacks", StringComparer.Ordinal))
                    {
                        var provider = Assert.Single(providers);
                        Assert.Equal(packageId + ".stack", provider.GetProperty("providerId").GetString());
                        Assert.Equal("sunder.stack.contributor", provider.GetProperty("contractId").GetString());
                    }
                    else
                    {
                        Assert.Empty(providers);
                    }
                    Assert.Equal(
                        shape.HasDependency ? 1 : 0,
                        manifest.RootElement.GetProperty("dependsOn").GetArrayLength());
                }

                AssertSuccess(await RunDotnetAsync(
                    output,
                    "msbuild",
                    aggregateProject,
                    "-t:PackSunderPackage",
                    "-p:Configuration=Debug",
                    "-m:1",
                    "-nr:false",
                    "-nologo"));
                var archivePath = Path.Combine(aggregateOutput, $"{projectName}.1.0.0.sunderpkg");
                Assert.True(File.Exists(archivePath), $"Universal package was not created: {archivePath}");
                using var archive = ZipFile.OpenRead(archivePath);
                Assert.Contains(archive.Entries, entry => entry.FullName == "manifest/sunder-package.json");
                Assert.Contains(archive.Entries, entry => entry.FullName == "manifest/content-index.json");
                Assert.Contains(archive.Entries, entry => entry.FullName == "payload/shared/contracts/sample.rpc.json");
                Assert.DoesNotContain(archive.Entries, entry => entry.FullName == "sunder-package.json");
                Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("payload/lib/", StringComparison.Ordinal));
                Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("payload/assets/", StringComparison.Ordinal));
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task PackLocalSunderFeedAsync(string repositoryRoot, string localFeed)
    {
        Directory.CreateDirectory(localFeed);
        var packageProjects = new[]
        {
            Path.Combine(repositoryRoot, "languages", "csharp", "dotnet", "packages", "Sunder.Sdk", "Sunder.Sdk.csproj"),
            Path.Combine(repositoryRoot, "languages", "csharp", "dotnet", "packages", "Sunder.Sdk.Avalonia", "Sunder.Sdk.Avalonia.csproj"),
            Path.Combine(repositoryRoot, "languages", "csharp", "dotnet", "packages", "Sunder.Sdk.Stacks", "Sunder.Sdk.Stacks.csproj"),
            Path.Combine(repositoryRoot, "languages", "csharp", "dotnet", "packages", "Sunder.Package.Build", "Sunder.Package.Build.csproj"),
        };
        foreach (var packageProject in packageProjects)
        {
            AssertSuccess(await RunDotnetAsync(
                repositoryRoot,
                "pack",
                packageProject,
                "-c",
                ResolveConfiguration(),
                "--no-restore",
                "--disable-build-servers",
                "-o",
                localFeed,
                $"-p:Version={CurrentVersion}",
                $"-p:PackageVersion={CurrentVersion}",
                "-p:GeneratePackageOnBuild=false"));
        }

        Assert.All(
            new[] { "Sunder.Sdk", "Sunder.Sdk.Avalonia", "Sunder.Sdk.Stacks", "Sunder.Package.Build" },
            packageId => Assert.True(
                File.Exists(Path.Combine(localFeed, $"{packageId}.{CurrentVersion}.nupkg")),
                $"Local feed package was not created for {packageId}."));
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
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
            return new ProcessResult(
                -1,
                standardOutput.IsCompletedSuccessfully ? await standardOutput : string.Empty,
                $"dotnet {string.Join(' ', arguments)} timed out."
                + (standardError.IsCompletedSuccessfully
                    ? Environment.NewLine + await standardError
                    : string.Empty));
        }
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static void AssertSuccess(ProcessResult result)
        => Assert.True(
            result.ExitCode == 0,
            $"dotnet exited with {result.ExitCode}.{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");

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
        var path = Path.Combine(Path.GetTempPath(), "sunder-package-template-tests", Guid.NewGuid().ToString("N"));
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
            // A failed assertion is more useful than best-effort temporary cleanup failure.
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record GeneratedShape(
        string Name,
        IReadOnlyList<string> Options,
        int TargetCount,
        bool HasApp,
        bool HasDependency);
}
