using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Sunder.Sdk.Rpc;
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
                var descriptorEntry = Assert.Single(
                    archive.Entries,
                    entry => entry.FullName.EndsWith(
                        "/Sunder.Package.Template.Protocol/Contracts/sample.rpc.json",
                        StringComparison.Ordinal));
                using var descriptorDocument = JsonDocument.Parse(descriptorEntry.Open());
                var descriptorRoot = descriptorDocument.RootElement;
                Assert.Equal(2, descriptorRoot.GetProperty("descriptorVersion").GetInt32());
                Assert.Equal("sunder.rpc.values.v1", descriptorRoot.GetProperty("schemaProfile").GetString());
                Assert.Equal("2.0.0", descriptorRoot.GetProperty("version").GetString());
                Assert.Equal(JsonValueKind.Object, descriptorRoot.GetProperty("errors").ValueKind);
                var services = descriptorRoot.GetProperty("services");
                Assert.Equal(JsonValueKind.Object, services.ValueKind);
                var greet = services.GetProperty("sample").GetProperty("methods").GetProperty("greet");
                Assert.Equal("idempotent", greet.GetProperty("idempotency").GetString());
                Assert.Empty(greet.GetProperty("errors").EnumerateArray());
                Assert.Equal(
                    "#/$defs/SampleRequest",
                    greet.GetProperty("requestSchema").GetProperty("$ref").GetString());
                Assert.Equal(
                    "#/$defs/SampleResponse",
                    greet.GetProperty("responseSchema").GetProperty("$ref").GetString());
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
            Assert.Equal(9, sunderReferences.Length);
            Assert.All(sunderReferences, reference => Assert.Equal(TestVersionRange, reference.Attribute("Version")?.Value));

            Assert.True(File.Exists(Path.Combine(output, "Escaped.Sample.csproj")));
            Assert.True(File.Exists(Path.Combine(output, "Escaped.Sample.Protocol", "Escaped.Sample.Protocol.csproj")));
            Assert.True(File.Exists(Path.Combine(output, "Escaped.Sample.Worker", "Escaped.Sample.Worker.csproj")));
            Assert.True(File.Exists(Path.Combine(output, "Escaped.Sample.App", "Escaped.Sample.App.csproj")));
            Assert.True(File.Exists(Path.Combine(output, "Escaped.Sample.Protocol", "Contracts", "sample.rpc.json")));
            Assert.False(Directory.Exists(Path.Combine(output, "Escaped.Sample.Protocol", "Generated")));
            Assert.True(File.Exists(Path.Combine(output, "Escaped.Sample.Worker", "SampleRpcProvider.cs")));
            Assert.False(Directory.Exists(Path.Combine(output, "Escaped.Sample")));
            var workerProgram = await File.ReadAllTextAsync(Path.Combine(
                output,
                "Escaped.Sample.Worker",
                "Program.cs"));
            Assert.Contains("SunderWorker.RunAsync", workerProgram, StringComparison.Ordinal);
            Assert.DoesNotContain("SunderWorkerV3.RunAsync", workerProgram, StringComparison.Ordinal);

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
            var workerProject = XDocument.Load(Path.Combine(
                output,
                "Escaped.Sample.Worker",
                "Escaped.Sample.Worker.csproj"));
            var sampleBundle = Assert.Single(
                workerProject.Descendants("SunderContractBundle"),
                item => item.Attribute("ContractId")?.Value == "escaped.sample.sample");
            Assert.Equal("2.0.0", sampleBundle.Attribute("Version")?.Value);
            Assert.Equal("package", sampleBundle.Attribute("Exposure")?.Value);
            var sampleProvider = Assert.Single(
                workerProject.Descendants("SunderRpcProvider"),
                item => item.Attribute("Include")?.Value == "escaped.sample.sample");
            Assert.Equal("runtime", sampleProvider.Attribute("Role")?.Value);
            Assert.Equal("2.0.0", sampleProvider.Attribute("ContractVersion")?.Value);
            Assert.Equal("owner", sampleProvider.Attribute("Audience")?.Value);
            Assert.Null(sampleProvider.Attribute("VersionRange"));
            var appProject = XDocument.Load(Path.Combine(
                output,
                "Escaped.Sample.App",
                "Escaped.Sample.App.csproj"));
            var sampleUse = Assert.Single(appProject.Descendants("SunderUsesContract"));
            Assert.Equal("app", sampleUse.Attribute("Role")?.Value);
            Assert.Equal("2.0.0", sampleUse.Attribute("ContractVersion")?.Value);
            Assert.Equal("true", sampleUse.Attribute("Required")?.Value);
            Assert.Equal("discover;invoke", sampleUse.Attribute("Actions")?.Value);
            Assert.Null(sampleUse.Attribute("VersionRange"));
            var stackProperties = XDocument.Load(Path.Combine(
                output,
                "Escaped.Sample.Worker",
                "Stacks.PackageReferences.props"));
            var stackProvider = Assert.Single(stackProperties.Descendants("SunderRpcProvider"));
            Assert.Equal("runtime", stackProvider.Attribute("Role")?.Value);
            Assert.Equal("2.0.0", stackProvider.Attribute("ContractVersion")?.Value);
            Assert.Equal("public", stackProvider.Attribute("Audience")?.Value);
            Assert.Null(stackProvider.Attribute("VersionRange"));
            foreach (var roleProjectPath in new[]
                     {
                          Path.Combine(output, "Escaped.Sample.Worker", "Escaped.Sample.Worker.csproj"),
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
                Assert.False(Directory.Exists(Path.Combine(output, projectName + ".Protocol", "Generated")));
                var descriptorPath = Path.Combine(
                    output,
                    projectName + ".Protocol",
                    "Contracts",
                    "sample.rpc.json");
                var generatedBindingPath = Path.Combine(
                    output,
                    projectName + ".Protocol",
                    "obj",
                    "Debug",
                    "net10.0",
                    "sunder-rpc",
                    "SampleRpc.g.cs");
                Assert.True(File.Exists(generatedBindingPath), $"RPC binding was not generated: {generatedBindingPath}");
                var descriptor = SunderRpcContractDescriptor.Parse(await File.ReadAllBytesAsync(descriptorPath));
                Assert.Equal(SunderRpcProtocol.DescriptorVersionV2, descriptor.DescriptorVersion);
                Assert.Equal(packageId + ".sample", descriptor.ContractId);
                Assert.Equal("2.0.0", descriptor.Version);
                Assert.Matches("^[0-9a-f]{64}$", descriptor.Sha256);
                Assert.Matches("^[0-9a-f]{64}$", Assert.IsType<string>(descriptor.AbiSha256));
                Assert.Equal(
                    SunderRpcCSharpGenerator.Generate(descriptor, projectName + ".Protocol.Generated"),
                    await File.ReadAllTextAsync(generatedBindingPath));
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
                var bundledSampleDescriptor = Path.Combine(
                    devPackage,
                    "payload",
                    "shared",
                    "contracts",
                    "sample.rpc.json");
                Assert.True(File.Exists(bundledSampleDescriptor));
                Assert.Equal(descriptor.GetCanonicalUtf8(), await File.ReadAllBytesAsync(bundledSampleDescriptor));
                SunderRpcContractDescriptor? stackDescriptor = null;
                if (shape.Options.Contains("--withStacks", StringComparer.Ordinal))
                {
                    var bundledStackDescriptor = Path.Combine(
                        devPackage,
                        "payload",
                        "shared",
                        "contracts",
                        "sunder.stack.contributor.rpc.json");
                    Assert.True(File.Exists(bundledStackDescriptor));
                    stackDescriptor = SunderRpcContractDescriptor.Parse(
                        await File.ReadAllBytesAsync(bundledStackDescriptor));
                    Assert.Equal(SunderRpcProtocol.DescriptorVersionV2, stackDescriptor.DescriptorVersion);
                    Assert.Equal("sunder.stack.contributor", stackDescriptor.ContractId);
                    Assert.Equal("2.0.0", stackDescriptor.Version);
                    Assert.Matches("^[0-9a-f]{64}$", Assert.IsType<string>(stackDescriptor.AbiSha256));
                }
                Assert.False(File.Exists(Path.Combine(devPackage, ".sunder-generated-output")));
                Assert.False(Directory.Exists(Path.Combine(devPackage, "lib")));
                Assert.False(Directory.Exists(Path.Combine(devPackage, "assets")));
                using (var manifest = JsonDocument.Parse(
                           await File.ReadAllTextAsync(Path.Combine(devPackage, "manifest", "sunder-package.json"))))
                {
                    Assert.Equal(1, manifest.RootElement.GetProperty("archiveFormatVersion").GetInt32());
                    Assert.Equal(3, manifest.RootElement.GetProperty("manifestVersion").GetInt32());
                    Assert.Equal("1.0.0", manifest.RootElement.GetProperty("version").GetString());
                    var targets = manifest.RootElement.GetProperty("targets").EnumerateArray().ToArray();
                    Assert.Equal(shape.TargetCount, targets.Length);
                    Assert.Contains(targets, target => target.GetProperty("role").GetString() == "runtime");
                    Assert.Equal(
                        shape.HasApp,
                        targets.Any(target => target.GetProperty("role").GetString() == "app"));
                    Assert.All(targets, target =>
                    {
                        var capabilities = target.GetProperty("requiredHostCapabilities")
                            .EnumerateArray()
                            .Select(static capability => capability.GetString())
                            .ToArray();
                        Assert.Contains("rpc.v2", capabilities);
                        Assert.DoesNotContain("rpc.v1", capabilities);
                        if (target.GetProperty("role").GetString() == "runtime")
                        {
                            Assert.Equal("worker", target.GetProperty("kind").GetString());
                            Assert.Contains("worker-protocol.v3", capabilities);
                            Assert.DoesNotContain("worker-protocol.v1", capabilities);
                            Assert.Contains("sdk-baseline-1-1.v1", capabilities);
                        }
                    });
                    var contracts = manifest.RootElement.GetProperty("contractBundles").EnumerateArray().ToArray();
                    Assert.Equal(shape.Options.Contains("--withStacks", StringComparer.Ordinal) ? 2 : 1, contracts.Length);
                    var sampleContract = Assert.Single(
                        contracts,
                        contract => contract.GetProperty("contractId").GetString() == packageId + ".sample");
                    Assert.Equal("2.0.0", sampleContract.GetProperty("version").GetString());
                    Assert.Equal("package", sampleContract.GetProperty("exposure").GetString());
                    Assert.Equal(descriptor.Sha256, sampleContract.GetProperty("descriptorSha256").GetString());
                    Assert.Equal(descriptor.AbiSha256, sampleContract.GetProperty("abiSha256").GetString());
                    Assert.False(sampleContract.TryGetProperty("sha256", out _));
                    if (shape.Options.Contains("--withStacks", StringComparer.Ordinal))
                    {
                        var stackContract = Assert.Single(
                            contracts,
                            contract => contract.GetProperty("contractId").GetString() == "sunder.stack.contributor");
                        Assert.Equal("2.0.0", stackContract.GetProperty("version").GetString());
                        Assert.Equal("public", stackContract.GetProperty("exposure").GetString());
                        Assert.Equal(
                            stackDescriptor!.Sha256,
                            stackContract.GetProperty("descriptorSha256").GetString());
                        Assert.Equal(
                            stackDescriptor.AbiSha256,
                            stackContract.GetProperty("abiSha256").GetString());
                    }
                    var uses = manifest.RootElement.GetProperty("usesContracts").EnumerateArray().ToArray();
                    if (shape.HasApp)
                    {
                        var use = Assert.Single(uses);
                        Assert.Equal("app", use.GetProperty("role").GetString());
                        Assert.Equal(packageId + ".sample", use.GetProperty("contractId").GetString());
                        Assert.Equal("2.0.0", use.GetProperty("contractVersion").GetString());
                        Assert.Equal(descriptor.AbiSha256, use.GetProperty("abiSha256").GetString());
                        Assert.True(use.GetProperty("required").GetBoolean());
                        Assert.Equal(
                            ["discover", "invoke"],
                            use.GetProperty("actions").EnumerateArray()
                                .Select(static action => action.GetString()!)
                                .ToArray());
                        Assert.False(use.TryGetProperty("versionRange", out _));
                    }
                    else
                    {
                        Assert.Empty(uses);
                    }
                    var providers = manifest.RootElement.GetProperty("provides").EnumerateArray().ToArray();
                    Assert.Equal(shape.Options.Contains("--withStacks", StringComparer.Ordinal) ? 2 : 1, providers.Length);
                    var sampleProvider = Assert.Single(
                        providers,
                        provider => provider.GetProperty("providerId").GetString() == packageId + ".sample");
                    Assert.Equal("runtime", sampleProvider.GetProperty("role").GetString());
                    Assert.Equal(packageId + ".sample", sampleProvider.GetProperty("contractId").GetString());
                    Assert.Equal("2.0.0", sampleProvider.GetProperty("contractVersion").GetString());
                    Assert.Equal("owner", sampleProvider.GetProperty("audience").GetString());
                    Assert.Equal(descriptor.Sha256, sampleProvider.GetProperty("descriptorSha256").GetString());
                    Assert.Equal(descriptor.AbiSha256, sampleProvider.GetProperty("abiSha256").GetString());
                    Assert.False(sampleProvider.TryGetProperty("contractSha256", out _));
                    if (shape.Options.Contains("--withStacks", StringComparer.Ordinal))
                    {
                        var provider = Assert.Single(
                            providers,
                            item => item.GetProperty("providerId").GetString() == packageId + ".stack");
                        Assert.Equal(packageId + ".stack", provider.GetProperty("providerId").GetString());
                        Assert.Equal("runtime", provider.GetProperty("role").GetString());
                        Assert.Equal("sunder.stack.contributor", provider.GetProperty("contractId").GetString());
                        Assert.Equal("2.0.0", provider.GetProperty("contractVersion").GetString());
                        Assert.Equal("public", provider.GetProperty("audience").GetString());
                        Assert.Equal(
                            stackDescriptor!.Sha256,
                            provider.GetProperty("descriptorSha256").GetString());
                        Assert.Equal(
                            stackDescriptor.AbiSha256,
                            provider.GetProperty("abiSha256").GetString());
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
                Assert.Equal(
                    shape.Options.Contains("--withStacks", StringComparer.Ordinal),
                    archive.Entries.Any(entry => entry.FullName == "payload/shared/contracts/sunder.stack.contributor.rpc.json"));
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
            Path.Combine(repositoryRoot, "languages", "csharp", "dotnet", "packages", "Sunder.Sdk.Worker", "Sunder.Sdk.Worker.csproj"),
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
            new[] { "Sunder.Sdk", "Sunder.Sdk.Avalonia", "Sunder.Sdk.Stacks", "Sunder.Sdk.Worker", "Sunder.Package.Build" },
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
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
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (OperatingSystem.IsMacOS() && tempRoot.StartsWith("/var/", StringComparison.Ordinal))
        {
            tempRoot = "/private" + tempRoot;
        }
        var path = Path.Combine(tempRoot, "sunder-package-template-tests", Guid.NewGuid().ToString("N"));
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
