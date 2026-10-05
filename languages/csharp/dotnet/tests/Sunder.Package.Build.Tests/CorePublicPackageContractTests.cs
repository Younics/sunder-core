using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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
                            .Where(reference => !reference.TryGetProperty("ReferenceOutputAssembly", out var output)
                                || !string.Equals(output.GetString(), "false", StringComparison.OrdinalIgnoreCase))
                            .Select(reference => Path.GetFileNameWithoutExtension(
                                reference.GetProperty("FullPath").GetString()))
                             .Order(StringComparer.Ordinal));
                    var tool = Assert.Single(references.EnumerateArray(), reference =>
                        Path.GetFileNameWithoutExtension(reference.GetProperty("FullPath").GetString()) == "Sunder.ExecutionRuntime.Build");
                    Assert.Equal("false", tool.GetProperty("ReferenceOutputAssembly").GetString());
                    Assert.Equal("false", tool.GetProperty("BuildReference").GetString());
                    Assert.Equal("all", tool.GetProperty("PrivateAssets").GetString());
                    Assert.Equal("all", tool.GetProperty("ExcludeAssets").GetString());
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
            var entryNames = archive.Entries.Select(entry => entry.FullName).ToArray();
            Assert.Equal(entryNames.Length, entryNames.Distinct(StringComparer.Ordinal).Count());
            var entries = entryNames.ToHashSet(StringComparer.Ordinal);
            VerifyNugetEntries(package, entries);
            if (package.PackageId == "Sunder.Package.Build")
            {
                Assert.DoesNotContain(entries, entry => entry.Contains("BouncyCastle", StringComparison.Ordinal));
            }
            if (package.PackageId == "Sunder.Sdk.Stacks")
            {
                Assert.Equal(
                    package.RequiredEntries.Where(static entry => entry.EndsWith(".rpc.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal),
                    entries.Where(static entry => entry.EndsWith(".rpc.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
                AssertStackDescriptorResources(archive, repositoryRoot);
                await AssertStackBundleConsumerAsync(archive, scope.CreateDirectory("consumer", package.PackageId), repositoryRoot);
            }
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
        Assert.Equal("sunder.worker.v1", expected.WorkerProtocol);
        Assert.Equal("worker-protocol.v1", expected.WorkerCapability);
        Assert.Equal(1, expected.WorkerProtocolVersion);
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

    [Theory]
    [InlineData("Sunder.Sdk", "lib/net10.0/Sunder.Sdk.dll")]
    [InlineData("Sunder.Package.Build", "tasks/net10.0/Sunder.Package.Format.dll")]
    [Trait("Category", "Artifact")]
    [Trait("Size", "Small")]
    public void PublicNugetOracle_RejectsSeededMissingRequiredArtifact(string packageId, string missingEntry)
    {
        var package = Assert.Single(LoadInventory().Nuget, candidate => candidate.PackageId == packageId);
        var entries = package.RequiredEntries
            .Concat(["README.md", "LICENSE", "icon.png", $"{package.PackageId}.nuspec"])
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(missingEntry, package.RequiredEntries);
        entries.Remove(missingEntry);

        Assert.ThrowsAny<Exception>(() => VerifyNugetEntries(package, entries));
    }

    private static async Task<EvaluatedProject> EvaluateProjectAsync(string projectPath, string repositoryRoot)
    {
        var startInfo = DotnetStartInfo(repositoryRoot);
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-nodeReuse:false");
        startInfo.ArgumentList.Add("-p:Configuration=Release");
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
        startInfo.ArgumentList.Add("Release");
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
        startInfo.ArgumentList.Add("Release");
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

    private static void AssertStackDescriptorResources(ZipArchive archive, string repositoryRoot)
    {
        var assembly = Assert.Single(
            archive.Entries,
            static entry => entry.FullName == "lib/net10.0/Sunder.Sdk.Stacks.dll");
        using var bytes = new MemoryStream();
        using (var content = assembly.Open()) content.CopyTo(bytes);
        bytes.Position = 0;
        using var pe = new PEReader(bytes);
        var metadata = pe.GetMetadataReader();
        var references = metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
        Assert.Equal(["Sunder.Sdk"], references.Where(name => name.StartsWith("Sunder.", StringComparison.Ordinal)));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Build", StringComparison.Ordinal));
        var resourceHandle = Assert.Single(metadata.ManifestResources, handle =>
            metadata.GetString(metadata.GetManifestResource(handle).Name)
                .EndsWith(".rpc.json", StringComparison.Ordinal));
        var resource = metadata.GetManifestResource(resourceHandle);
        Assert.Equal("Sunder.Sdk.Stacks.Contracts.sunder.stack.contributor.rpc.json", metadata.GetString(resource.Name));
        Assert.True(resource.Implementation.IsNil);

        var resourceSection = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress).GetContent();
        var offset = checked((int)resource.Offset);
        Assert.InRange(offset, 0, resourceSection.Length - sizeof(int));
        var length = BinaryPrimitives.ReadInt32LittleEndian(resourceSection.AsSpan(offset, sizeof(int)));
        Assert.InRange(length, 0, resourceSection.Length - offset - sizeof(int));
        var embeddedBytes = resourceSection.AsSpan(offset + sizeof(int), length).ToArray();

        var descriptorEntry = Assert.Single(
            archive.Entries,
            static entry => entry.FullName == "contracts/sunder.stack.contributor.rpc.json");
        using var packedBytes = new MemoryStream();
        using (var content = descriptorEntry.Open()) content.CopyTo(packedBytes);
        var sourceBytes = File.ReadAllBytes(Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Sdk.Stacks",
            "Contracts",
            "sunder.stack.contributor.rpc.json"));
        Assert.Equal(sourceBytes, packedBytes.ToArray());
        Assert.Equal(sourceBytes, embeddedBytes);
    }

    private static async Task AssertStackBundleConsumerAsync(
        ZipArchive archive,
        string consumerDirectory,
        string repositoryRoot)
    {
        var propsPath = Path.Combine(consumerDirectory, "buildTransitive", "Sunder.Sdk.Stacks.props");
        var descriptorPath = Path.Combine(consumerDirectory, "contracts", "sunder.stack.contributor.rpc.json");
        Directory.CreateDirectory(Path.GetDirectoryName(propsPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(descriptorPath)!);
        await ExtractEntryAsync(archive, "buildTransitive/Sunder.Sdk.Stacks.props", propsPath);
        await ExtractEntryAsync(archive, "contracts/sunder.stack.contributor.rpc.json", descriptorPath);

        var projectPath = Path.Combine(consumerDirectory, "consumer.proj");
        new XDocument(
            new XElement("Project",
                new XElement("Import", new XAttribute("Project", propsPath))))
            .Save(projectPath);
        var startInfo = DotnetStartInfo(repositoryRoot);
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-nodeReuse:false");
        startInfo.ArgumentList.Add("-p:Configuration=Release");
        startInfo.ArgumentList.Add("-p:SunderPackageProject=true");
        startInfo.ArgumentList.Add("-getItem:SunderContractBundle");
        await using var process = CapturedProcess.Start(startInfo);
        var result = await process.EnsureSuccessAsync(TimeSpan.FromSeconds(30));
        var start = result.StandardOutput.IndexOf('{');
        var end = result.StandardOutput.LastIndexOf('}');
        Assert.True(start >= 0 && end > start, result.CombinedOutput);
        using var document = JsonDocument.Parse(result.StandardOutput[start..(end + 1)]);
        var bundle = Assert.Single(document.RootElement.GetProperty("Items").GetProperty("SunderContractBundle").EnumerateArray());
        Assert.Equal(Path.GetFullPath(descriptorPath), Path.GetFullPath(bundle.GetProperty("FullPath").GetString()!));
        Assert.Equal("sunder.stack.contributor", bundle.GetProperty("ContractId").GetString());
        Assert.Equal("2.0.0", bundle.GetProperty("Version").GetString());
        Assert.Equal("contracts/sunder.stack.contributor.rpc.json", bundle.GetProperty("DescriptorPath").GetString());
        Assert.Equal("public", bundle.GetProperty("Exposure").GetString());
    }

    private static async Task ExtractEntryAsync(ZipArchive archive, string entryName, string outputPath)
    {
        var entry = Assert.Single(archive.Entries, candidate => candidate.FullName == entryName);
        await using var source = entry.Open();
        await using var destination = File.Create(outputPath);
        await source.CopyToAsync(destination);
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
        var expected = package.RequiredEntries
            .Concat(["README.md", "LICENSE", "icon.png", $"{package.PackageId}.nuspec"])
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            expected,
            entries.Where(static entry => !IsGeneratedOpenPackagingEnvelope(entry)).Order(StringComparer.Ordinal));
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
