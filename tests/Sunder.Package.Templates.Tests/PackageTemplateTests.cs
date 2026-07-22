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

    [Fact]
    public async Task PackedTemplate_UsesPackageVersionAndEscapesGeneratedInputs()
    {
        var repositoryRoot = FindRepositoryRoot();
        var root = CreateTempDirectory();
        try
        {
            var packageProject = Path.Combine(
                repositoryRoot,
                "src",
                "Sdk",
                "Sunder.Package.Templates",
                "Sunder.Package.Templates.csproj");
            var pack = await RunDotnetAsync(
                repositoryRoot,
                "pack",
                packageProject,
                "-c",
                ResolveConfiguration(),
                "--no-build",
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
            const string hostContractsPackageId = "Host.Contracts&\"Quoted\"";
            var output = Path.Combine(root, "generated");
            var generate = await RunDotnetAsync(
                repositoryRoot,
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
                "--createInPlace",
                "--output",
                output,
                "--withAvalonia",
                "--withStacks",
                "--withContracts",
                "--withHostContracts",
                "--hostPackageId",
                "host.package",
                "--hostContractsPackageId",
                hostContractsPackageId,
                "--hostContractsVersionRange",
                "[1.2.3,1.3.0)");
            AssertSuccess(generate);

            var xmlFiles = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path) is ".csproj" or ".props" or ".axaml")
                .Select(XDocument.Load)
                .ToArray();
            var sunderReferences = xmlFiles
                .SelectMany(static document => document.Descendants("PackageReference"))
                .Where(reference => reference.Attribute("Include")?.Value.StartsWith("Sunder.", StringComparison.Ordinal) == true)
                .ToArray();
            Assert.Equal(5, sunderReferences.Length);
            Assert.All(sunderReferences, reference => Assert.Equal(TestVersionRange, reference.Attribute("Version")?.Value));

            var packageMetadata = await File.ReadAllTextAsync(Path.Combine(output, "PackageMetadata.cs"));
            Assert.Contains("Name = \"Package & \\\"Quoted\\\" <Name> \\\\ Path\"", packageMetadata, StringComparison.Ordinal);

            var view = XDocument.Load(Path.Combine(output, "PackageViews", "DefaultPackageView.axaml"));
            Assert.Equal(
                packageName,
                view.Descendants().Single(element => element.Name.LocalName == "TextBlock"
                    && element.Attribute("FontSize") is not null).Attribute("Text")?.Value);

            var hostReferences = XDocument.Load(Path.Combine(output, "HostContracts.PackageReferences.props"));
            Assert.Equal(
                hostContractsPackageId,
                Assert.Single(hostReferences.Descendants("PackageReference")).Attribute("Include")?.Value);
            Assert.Equal(
                "[1.2.3,1.3.0)",
                Assert.Single(hostReferences.Descendants("PackageReference")).Attribute("Version")?.Value);
            Assert.Contains(
                "VersionRange = \">=1.1.0 <1.2.0\"",
                packageMetadata,
                StringComparison.Ordinal);

            foreach (var generatedFile in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                         .Where(path => Path.GetExtension(path) is ".cs" or ".csproj" or ".props" or ".axaml" or ".md"))
            {
                Assert.DoesNotContain("SUNDER_", await File.ReadAllTextAsync(generatedFile), StringComparison.Ordinal);
            }
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
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
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
}
