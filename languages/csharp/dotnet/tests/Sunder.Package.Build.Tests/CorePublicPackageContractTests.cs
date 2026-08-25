using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

[CollectionDefinition(nameof(CorePublicPackageContractTests), DisableParallelization = true)]
public sealed class CorePublicPackageContractCollection
{
}

[Collection(nameof(CorePublicPackageContractTests))]
public sealed class CorePublicPackageContractTests
{
    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    [Trait("Size", "Large")]
    public async Task EveryPublicNuget_HasEvaluatedIdentityRoleClosureAndDeterministicArtifact()
    {
        var repositoryRoot = FindRepositoryRoot();
        var inventory = LoadInventory();
        Assert.Equal(1, inventory.SchemaVersion);
        Assert.Equal(6, inventory.Nuget.Count);
        var packageProjects = Directory.EnumerateDirectories(
                Path.Combine(repositoryRoot, "languages", "csharp", "dotnet", "packages"))
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
            .Select(path => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            inventory.Nuget.Select(package => package.Project).Order(StringComparer.Ordinal),
            packageProjects);

        await using var scope = TemporaryDirectoryScope.Create("sunder-core-public-package-contract");
        foreach (var package in inventory.Nuget)
        {
            var projectPath = Path.Combine(repositoryRoot, package.Project.Replace('/', Path.DirectorySeparatorChar));
            await BuildProjectAsync(projectPath, repositoryRoot);
            var evaluation = await EvaluateProjectAsync(projectPath, repositoryRoot);
            Assert.Equal(package.PackageId, evaluation.Properties.GetProperty("PackageId").GetString());
            Assert.Equal("true", evaluation.Properties.GetProperty("IsPackable").GetString());
            Assert.Equal(package.PackageType, evaluation.Properties.GetProperty("PackageType").GetString());
            Assert.Equal(
                package.IncludeBuildOutput ? "true" : "false",
                evaluation.Properties.GetProperty("IncludeBuildOutput").GetString());
            Assert.Equal("net10.0", evaluation.Properties.GetProperty("TargetFramework").GetString());
            if (evaluation.Items.TryGetProperty("ProjectReference", out var references))
            {
                Assert.DoesNotContain(references.EnumerateArray(), reference =>
                    reference.GetProperty("FullPath").GetString()!
                        .Replace('\\', '/')
                        .Contains("/host/", StringComparison.Ordinal));
                if (package.PackageId == "Sunder.Sdk.Stacks")
                {
                    Assert.Equal(
                        ["Sunder.Sdk"],
                        references.EnumerateArray()
                            .Select(reference => Path.GetFileNameWithoutExtension(
                                reference.GetProperty("FullPath").GetString()))
                            .Order(StringComparer.Ordinal));
                }
            }

            var firstDirectory = scope.CreateDirectory("first", package.PackageId);
            var secondDirectory = scope.CreateDirectory("second", package.PackageId);
            var first = await PackAsync(projectPath, firstDirectory, repositoryRoot, inventory.CoordinatedVersion);
            var second = await PackAsync(projectPath, secondDirectory, repositoryRoot, inventory.CoordinatedVersion);
            var firstBytes = await File.ReadAllBytesAsync(first);
            await AssertCanonicalPackageContentEqualAsync(first, second);
            Assert.InRange(firstBytes.LongLength, 1, package.MaximumBytes);

            using var archive = ZipFile.OpenRead(first);
            var entries = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
            VerifyNugetEntries(package, entries);
            var nuspecEntry = Assert.Single(archive.Entries, entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
            using var nuspecStream = nuspecEntry.Open();
            var nuspec = XDocument.Load(nuspecStream);
            XNamespace ns = nuspec.Root!.Name.Namespace;
            var metadata = nuspec.Root.Element(ns + "metadata")!;
            Assert.Equal(package.PackageId, metadata.Element(ns + "id")?.Value);
            Assert.Equal(inventory.CoordinatedVersion, metadata.Element(ns + "version")?.Value);
            if (package.PackageId == "Sunder.Sdk.Stacks")
            {
                Assert.Equal(
                    ["Sunder.Sdk"],
                    metadata.Descendants(ns + "dependency")
                        .Select(dependency => dependency.Attribute("id")?.Value)
                        .Where(static id => id is not null)
                        .Order(StringComparer.Ordinal));
            }
            Assert.DoesNotContain(entries, entry =>
                entry.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                || entry.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry, "[Content_Types].xml", StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("Category", "Contract")]
    [Trait("Size", "Small")]
    public void RuntimePackageManifestInventory_MatchesCurrentRoleAndWorkerContract()
    {
        var expected = LoadInventory().RuntimePackageManifest;

        Assert.Equal(SunderPackageFormat.CurrentArchiveFormatVersion, expected.ArchiveFormatVersion);
        Assert.Equal(SunderPackageFormat.CurrentManifestVersion, expected.ManifestVersion);
        Assert.Equal([SunderPackageFormat.AppHostRole, SunderPackageFormat.RuntimeHostRole], expected.Roles);
        Assert.Equal(SunderPackageFormat.WorkerTargetKind, expected.RuntimeKind);
        Assert.Equal("sunder.worker.v3", expected.WorkerProtocol);
        Assert.Equal("worker-protocol.v3", expected.WorkerCapability);
        Assert.Equal(3, expected.WorkerProtocolVersion);
        Assert.True(expected.RuntimeRequiresExactRid);
        Assert.True(expected.AppHasNoWorkerAuthority);
        Assert.True(expected.RuntimeHasNoAppAuthority);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    [Trait("Size", "Small")]
    public void NugetPackability_DefaultsToFalseAndOnlyPublicDeveloperProjectsOptIn()
    {
        var repositoryRoot = FindRepositoryRoot();
        var centralProperties = XDocument.Load(Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "Directory.Build.props"));
        Assert.Equal(
            "false",
            Assert.Single(centralProperties.Descendants("IsPackable")).Value.Trim(),
            ignoreCase: true);

        var dotnetRoot = Path.Combine(repositoryRoot, "languages", "csharp", "dotnet");
        var explicitlyPackableProjects = Directory.EnumerateFiles(dotnetRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => XDocument.Load(path)
                .Descendants("IsPackable")
                .Any(element => string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            LoadInventory().Nuget.Select(package => package.Project).Order(StringComparer.Ordinal),
            explicitlyPackableProjects);
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Size", "Small")]
    public void PublicNugetOracle_RejectsSeededMissingRequiredArtifact()
    {
        var package = LoadInventory().Nuget[0];
        var entries = package.RequiredEntries.ToHashSet(StringComparer.Ordinal);
        entries.Remove(package.RequiredEntries[0]);

        Assert.ThrowsAny<Exception>(() => VerifyNugetEntries(package, entries));
    }

    private static async Task<EvaluatedProject> EvaluateProjectAsync(string projectPath, string repositoryRoot)
    {
        var startInfo = DotnetStartInfo(repositoryRoot);
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-nodeReuse:false");
        startInfo.ArgumentList.Add("-getProperty:PackageId,IsPackable,PackageType,IncludeBuildOutput,TargetFramework");
        startInfo.ArgumentList.Add("-getItem:ProjectReference");
        await using var process = CapturedProcess.Start(startInfo);
        var result = await process.EnsureSuccessAsync(TimeSpan.FromSeconds(30));
        var start = result.StandardOutput.IndexOf('{');
        var end = result.StandardOutput.LastIndexOf('}');
        Assert.True(start >= 0 && end > start, result.CombinedOutput);
        using var document = JsonDocument.Parse(result.StandardOutput[start..(end + 1)]);
        return new EvaluatedProject(
            document.RootElement.GetProperty("Properties").Clone(),
            document.RootElement.GetProperty("Items").Clone());
    }

    private static async Task BuildProjectAsync(string projectPath, string repositoryRoot)
    {
        var startInfo = DotnetStartInfo(repositoryRoot);
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Debug");
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add("--disable-build-servers");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("-p:GeneratePackageOnBuild=false");
        startInfo.ArgumentList.Add("-p:UseSharedCompilation=false");
        await using var process = CapturedProcess.Start(startInfo);
        await process.EnsureSuccessAsync(TimeSpan.FromMinutes(2));
    }

    private static async Task<string> PackAsync(
        string projectPath,
        string outputDirectory,
        string repositoryRoot,
        string version)
    {
        var startInfo = DotnetStartInfo(repositoryRoot);
        startInfo.ArgumentList.Add("pack");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Debug");
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add("--disable-build-servers");
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add($"-p:PackageVersion={version}");
        startInfo.ArgumentList.Add("-p:UseSharedCompilation=false");
        await using var process = CapturedProcess.Start(startInfo);
        await process.EnsureSuccessAsync(TimeSpan.FromMinutes(2));
        return Assert.Single(Directory.EnumerateFiles(outputDirectory, "*.nupkg"));
    }

    private static async Task AssertCanonicalPackageContentEqualAsync(string firstPath, string secondPath)
    {
        using var first = ZipFile.OpenRead(firstPath);
        using var second = ZipFile.OpenRead(secondPath);
        var firstEntries = first.Entries
            .Where(entry => !IsGeneratedOpenPackagingEnvelope(entry.FullName))
            .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
            .ToArray();
        var secondEntries = second.Entries
            .Where(entry => !IsGeneratedOpenPackagingEnvelope(entry.FullName))
            .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(firstEntries.Select(entry => entry.FullName), secondEntries.Select(entry => entry.FullName));
        for (var index = 0; index < firstEntries.Length; index++)
        {
            await using var firstStream = firstEntries[index].Open();
            await using var secondStream = secondEntries[index].Open();
            using var firstContent = new MemoryStream();
            using var secondContent = new MemoryStream();
            await firstStream.CopyToAsync(firstContent);
            await secondStream.CopyToAsync(secondContent);
            Assert.True(
                firstContent.GetBuffer().AsSpan(0, checked((int)firstContent.Length))
                    .SequenceEqual(secondContent.GetBuffer().AsSpan(0, checked((int)secondContent.Length))),
                $"Package entry '{firstEntries[index].FullName}' changed between deterministic packs.");
        }
    }

    private static bool IsGeneratedOpenPackagingEnvelope(string path)
        => path is "[Content_Types].xml" or "_rels/.rels"
           || path.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal);

    private static ProcessStartInfo DotnetStartInfo(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        return startInfo;
    }

    private static void VerifyNugetEntries(NugetPackageContract package, IReadOnlySet<string> entries)
    {
        foreach (var required in package.RequiredEntries)
        {
            Assert.Contains(required, entries);
        }
        Assert.Contains("README.md", entries);
        Assert.Contains("LICENSE", entries);
        Assert.Contains("icon.png", entries);
    }

    private static PublicPackageInventory LoadInventory()
        => JsonSerializer.Deserialize<PublicPackageInventory>(
               File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestOracles", "core-public-packages.json")),
               new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
           ?? throw new InvalidDataException("Core public package inventory is invalid.");

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Sunder.Core.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Sunder Core repository root.");
    }

    private sealed record EvaluatedProject(JsonElement Properties, JsonElement Items);

    private sealed record PublicPackageInventory(
        int SchemaVersion,
        string CoordinatedVersion,
        IReadOnlyList<NugetPackageContract> Nuget,
        IReadOnlyList<NpmPackageContract> Npm,
        RuntimePackageManifestContract RuntimePackageManifest);

    private sealed record NugetPackageContract(
        string PackageId,
        string Project,
        string Role,
        string PackageType,
        bool IncludeBuildOutput,
        long MaximumBytes,
        IReadOnlyList<string> RequiredEntries);

    private sealed record NpmPackageContract(
        string Name,
        string Project,
        string Role,
        long MaximumBytes,
        IReadOnlyList<string> RequiredEntries);

    private sealed record RuntimePackageManifestContract(
        int ArchiveFormatVersion,
        int ManifestVersion,
        IReadOnlyList<string> Roles,
        string RuntimeKind,
        string WorkerProtocol,
        string WorkerCapability,
        int WorkerProtocolVersion,
        bool RuntimeRequiresExactRid,
        bool AppHasNoWorkerAuthority,
        bool RuntimeHasNoAppAuthority);
}
