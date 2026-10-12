using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Sunder.Core.TestSupport;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Templates.Tests;

public sealed partial class PackageTemplateTests
{
    private const string TestVersion = "9.8.7-preview.4";
    private const string TestVersionRange = "[9.8.7-preview.4]";

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
                Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains(".Protocol/", StringComparison.Ordinal)
                    || entry.FullName.EndsWith(".rpc.json", StringComparison.Ordinal));
                var service = Assert.Single(archive.Entries, entry => entry.FullName.EndsWith("/SampleService.cs", StringComparison.Ordinal));
                using var serviceReader = new StreamReader(service.Open());
                var serviceSource = await serviceReader.ReadToEndAsync();
                Assert.Contains("[SunderService]", serviceSource, StringComparison.Ordinal);
                Assert.Contains("using Sunder.Sdk.Services;", serviceSource, StringComparison.Ordinal);
                Assert.DoesNotContain("Sunder.Sdk.Rpc", serviceSource, StringComparison.Ordinal);
                Assert.Contains("Task<string> GreetAsync", serviceSource, StringComparison.Ordinal);
                Assert.Contains("IAsyncEnumerable<string>", serviceSource, StringComparison.Ordinal);
                Assert.DoesNotContain("SampleRequest", serviceSource, StringComparison.Ordinal);
                Assert.Single(archive.Entries, entry => entry.FullName.EndsWith("/sunder-contracts.lock.json", StringComparison.Ordinal));
                Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("/sunder-contracts.owned.json", StringComparison.Ordinal));
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
            Assert.Equal(4, sunderReferences.Length);
            Assert.All(sunderReferences, reference => Assert.Equal(TestVersionRange, reference.Attribute("Version")?.Value));

            Assert.True(File.Exists(Path.Combine(output, "Escaped.Sample.csproj")));
            Assert.False(Directory.Exists(Path.Combine(output, "Escaped.Sample.Protocol")));
            Assert.False(File.Exists(Path.Combine(output, "Escaped.Sample.Worker", "Escaped.Sample.Worker.csproj")));
            Assert.False(Directory.Exists(Path.Combine(output, "Escaped.Sample.App")));
            Assert.Empty(Directory.EnumerateFiles(output, "*.rpc.json", SearchOption.AllDirectories));
            Assert.True(File.Exists(Path.Combine(output, "Services", "SampleService.cs")));
            Assert.True(File.Exists(Path.Combine(output, "sunder-contracts.lock.json")));
            Assert.False(Directory.Exists(Path.Combine(output, "Escaped.Sample")));
            Assert.Empty(Directory.EnumerateFiles(output, "Program.cs", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(output, "PackageModule*.cs", SearchOption.AllDirectories));

            var generatedProjects = Directory.EnumerateFiles(output, "*.csproj", SearchOption.AllDirectories)
                .Select(XDocument.Load)
                .ToArray();
            var generatedProject = Assert.Single(generatedProjects);
            Assert.Equal("1.0.0", Assert.Single(generatedProject.Descendants("Version")).Value);
            Assert.Equal("false", Assert.Single(generatedProject.Descendants("IsPackable")).Value);
            Assert.Equal("true", Assert.Single(generatedProject.Descendants("RestorePackagesWithLockFile")).Value);

            var workerProject = XDocument.Load(Path.Combine(
                output,
                "Escaped.Sample.csproj"));
            Assert.Empty(workerProject.Descendants("SunderPackagePrimaryProject"));
            Assert.Empty(workerProject.Descendants("SelfContained"));
            Assert.Equal("false", Assert.Single(workerProject.Descendants("EnableRuntimePackDownload")).Value);
            Assert.Equal("false", Assert.Single(workerProject.Descendants("EnableAppHostPackDownload")).Value);
            Assert.Empty(workerProject.Descendants("UseAppHost"));
            Assert.Empty(workerProject.Descendants("PublishSingleFile"));
            Assert.Empty(workerProject.Descendants("PublishTrimmed"));
            Assert.Empty(workerProject.Descendants("SunderPackageAppProject"));
            Assert.Empty(workerProject.Descendants("ProjectReference"));
            Assert.Empty(workerProject.Descendants("Compile"));
            Assert.Empty(workerProject.Descendants("SunderPackageTarget"));
            Assert.Empty(workerProject.Descendants("SunderContractBundle"));
            Assert.Empty(workerProject.Descendants("SunderRpcProvider"));
            var stackProperties = XDocument.Load(Path.Combine(
                output,
                "Stacks",
                "Stacks.PackageReferences.props"));
            Assert.Empty(stackProperties.Descendants("SunderRpcProvider"));
            Assert.Empty(stackProperties.Descendants("Target"));
            Assert.Equal("buildTransitive", Assert.Single(stackProperties.Descendants("PackageReference")).Attribute("ExcludeAssets")?.Value);
            var stackSource = await File.ReadAllTextAsync(Path.Combine(output, "Stacks", "StackSupport.cs"));
            Assert.Contains("SunderServiceProvider(typeof(IPackageStackContributor)", stackSource, StringComparison.Ordinal);
            Assert.Contains("using Sunder.Sdk.Services;", stackSource, StringComparison.Ordinal);
            Assert.DoesNotContain("Sunder.Sdk.Rpc", stackSource, StringComparison.Ordinal);
            Assert.DoesNotContain("CreateSunderRpcProviderRegistration()", stackSource, StringComparison.Ordinal);
            Assert.DoesNotContain("SunderStackContributorRpc", stackSource, StringComparison.Ordinal);
            var model = await File.ReadAllTextAsync(Path.Combine(output, "Views", "DefaultPackageViewModel.cs"));
            Assert.Contains("ISampleService", model, StringComparison.Ordinal);
            Assert.DoesNotContain("CreateCallScopeAsync", model, StringComparison.Ordinal);

            var packageMetadata = await File.ReadAllTextAsync(Path.Combine(output, "PackageMetadata.cs"));
            Assert.Contains("Name = \"Package & \\\"Quoted\\\" <Name> \\\\ Path\"", packageMetadata, StringComparison.Ordinal);

            var view = XDocument.Load(Path.Combine(output, "Views", "DefaultPackageView.axaml"));
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

    [Theory]
    [InlineData("Default")]
    [InlineData("Avalonia")]
    [InlineData("AvaloniaNoDefaultView")]
    [InlineData("Stacks")]
    [InlineData("HostDependency")]
    public async Task GeneratedPrimaryProject_BuildsCurrentRidAndPacksUniversalPackage(string shapeName)
    {
        var repositoryRoot = FindRepositoryRoot();
        var root = CreateTempDirectory();
        var currentVersion = "1.1.1-dev.template.g" + Guid.NewGuid().ToString("N");
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
                $"-p:Version={currentVersion}",
                $"-p:PackageVersion={currentVersion}",
                "-p:GeneratePackageOnBuild=false"));

            var templatePackage = Path.Combine(root, $"Sunder.Package.Templates.{currentVersion}.nupkg");
            var hive = Path.Combine(root, "template-hive");
            AssertSuccess(await RunDotnetAsync(
                repositoryRoot,
                "new",
                "--debug:custom-hive",
                hive,
                "install",
                templatePackage));

            var localFeed = Path.Combine(root, "feed");
            var toolConfiguration = await PackLocalSunderFeedAsync(repositoryRoot, localFeed, currentVersion);
            var catalog = Path.Combine(root, "core-platform-catalog.json");
            if (shapeName == "Stacks")
            {
                var tool = Path.Combine(repositoryRoot, "languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build/bin", toolConfiguration + "-sunder-core-transport", "net10.0/Sunder.ExecutionRuntime.Build.dll");
                Assert.True(File.Exists(tool), "The Stack SDK build did not retain its transport producer: " + tool);
                AssertSuccess(await RunDotnetAsync(repositoryRoot, tool, "platform-contracts", "export", "--core-root", repositoryRoot,
                    "--catalog-release", "1.1.0", "--output", catalog));
            }

            var packagesPath = Path.Combine(root, "packages");
            await AssertPackedInternalRpcBindingConsumerAsync(root, localFeed, packagesPath, currentVersion);
            await File.WriteAllTextAsync(Path.Combine(root, "NuGet.Config"), $"<configuration><packageSources><clear/><add key=\"local\" value=\"{System.Security.SecurityElement.Escape(localFeed)}\"/></packageSources></configuration>");
            var shapes = new[]
            {
                new GeneratedShape("Default", [], 1, 6, HasApp: false, HasDependency: false),
                new GeneratedShape("Avalonia", ["--withAvalonia"], 2, 12, HasApp: true, HasDependency: false),
                new GeneratedShape(
                    "AvaloniaNoDefaultView",
                    ["--withAvalonia", "--noDefaultView"],
                    1,
                    6,
                    HasApp: false,
                    HasDependency: false),
                new GeneratedShape("Stacks", ["--withStacks"], 1, 6, HasApp: false, HasDependency: false),
                new GeneratedShape(
                    "HostDependency",
                    ["--withHostDependency", "--hostPackageId", "host.package", "--hostPackageVersionRange", ">=1.1.0 <1.2.0"],
                    1,
                    6,
                    HasApp: false,
                    HasDependency: true),
            };
            foreach (var shape in shapes.Where(shape => shape.Name == shapeName))
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
                if (shape.Name == "AvaloniaNoDefaultView")
                {
                    Assert.False(File.Exists(Path.Combine(output, "Views", "DefaultPackageView.axaml")));
                }

                var primaryProject = Path.Combine(output, projectName + ".csproj");
                Assert.Single(Directory.EnumerateFiles(output, "*.csproj", SearchOption.AllDirectories));
                AssertSuccess(await RunDotnetAsync(
                    output,
                    "restore",
                    primaryProject,
                    "--source",
                    localFeed,
                    "--packages",
                    packagesPath,
                    "--disable-parallel",
                    "--ignore-failed-sources"));
                if (shape.Name == "Stacks")
                {
                    var stackInputs = await CaptureTemplateInputsAsync(output);
                    var emptyLock = await File.ReadAllBytesAsync(Path.Combine(output, "sunder-contracts.lock.json"));
                    var gated = await RunDotnetAsync(output, "build", primaryProject, "--no-restore", "--disable-build-servers");
                    Assert.NotEqual(0, gated.ExitCode);
                    Assert.Contains("platform-api.import-not-found", gated.StandardOutput + gated.StandardError, StringComparison.Ordinal);
                    var originRequired = await RunDotnetAsync(output, "msbuild", primaryProject, "-t:ImplementSunderPlatformApi", "-nr:false", "-nologo",
                        "-p:SunderContractId=sunder.stack.contributor");
                    Assert.NotEqual(0, originRequired.ExitCode);
                    Assert.Contains("platform-api.local-input-required", originRequired.StandardOutput + originRequired.StandardError, StringComparison.Ordinal);
                    Assert.Equal(emptyLock, await File.ReadAllBytesAsync(Path.Combine(output, "sunder-contracts.lock.json")));
                    await AssertTemplateInputsAsync(output, stackInputs);
                    Assert.False(Directory.Exists(Path.Combine(output, "bin", "Debug", "net10.0", "sunder-dev")));
                    AssertSuccess(await RunDotnetAsync(output, "msbuild", primaryProject, "-t:ImplementSunderPlatformApi", "-nr:false", "-nologo",
                        "-p:SunderContractId=sunder.stack.contributor", "-p:SunderPlatformCatalogPath=" + catalog));
                }
                var authored = await CaptureTemplateInputsAsync(output);
                var build = await RunDotnetAsync(
                    output,
                    "build",
                    primaryProject,
                    "--no-restore",
                    "--disable-build-servers");
                AssertSuccess(build);

                Assert.False(Directory.Exists(Path.Combine(output, projectName + ".Protocol")));
                Assert.False(File.Exists(Path.Combine(output, "sunder-contracts.owned.json")));
                var descriptor = SunderRpcContractDescriptor.Parse(await File.ReadAllBytesAsync(Path.Combine(output,
                    "bin/Debug/net10.0/sunder-dev/payload/shared/contracts/sample.rpc.json")));
                var generatedBindingPath = Path.Combine(output, "obj", "Debug", "net10.0",
                    "sunder-analysis", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "sunder-rpc", "bindings", "sample.g.cs");
                Assert.True(File.Exists(generatedBindingPath), $"RPC binding was not generated: {generatedBindingPath}");
                Assert.Equal(SunderRpcProtocol.DescriptorVersion, descriptor.DescriptorVersion);
                Assert.Equal(packageId + ".sample", descriptor.ContractId);
                Assert.Equal("1.0.0", descriptor.Version);
                Assert.Matches("^[0-9a-f]{64}$", descriptor.Sha256);
                Assert.Matches("^[0-9a-f]{64}$", Assert.IsType<string>(descriptor.AbiSha256));
                Assert.Equal(
                    SunderRpcCSharpGenerator.Generate(descriptor, "Sunder.Generated.Rpc.Sample", SunderRpcCSharpAccessibility.Internal),
                    await File.ReadAllTextAsync(generatedBindingPath));
                var predecessor = SunderRpcContractDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes((await File.ReadAllTextAsync(Path.Combine(repositoryRoot,
                    "languages/csharp/dotnet/tests/Sunder.Package.Templates.Tests/Fixtures/Sample.before-source.json"))).Replace("SUNDER_PACKAGE_ID_TEXT", packageId, StringComparison.Ordinal)));
                Assert.Equal("2.0.0", predecessor.Version); // Authentic historical private oracle, not current source authority.
                Assert.NotEqual(predecessor.Sha256, descriptor.Sha256);
                Assert.NotEqual(predecessor.AbiSha256, descriptor.AbiSha256);
                Assert.Equal(new[] { "greet", "greet-many" }, Assert.Single(descriptor.Services).Methods.Select(method => method.MethodId).Order(StringComparer.Ordinal));
                Assert.False(File.Exists(Path.Combine(output, "sunder-contracts.owned.json")));
                await AssertTemplateInputsAsync(output, authored);
                if (shape.Name is "Default" or "Avalonia")
                    await VerifyTemplateRebuildAsync(output, primaryProject, generatedBindingPath);
                foreach (var generatedProject in Directory.EnumerateFiles(output, "*.csproj", SearchOption.AllDirectories))
                {
                    Assert.True(
                        File.Exists(Path.Combine(Path.GetDirectoryName(generatedProject)!, "packages.lock.json")),
                        $"Restore did not create a lock file for {generatedProject}.");
                }

                var primaryOutput = Path.Combine(output, "bin", "Debug", "net10.0");
                var devPackage = Path.Combine(primaryOutput, "sunder-dev");
                var workerLeaf = Path.Combine(
                    primaryOutput,
                    "sunder-roles", "worker",
                    System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                    "sunder-dev");
                Assert.True(File.Exists(Path.Combine(workerLeaf, "manifest", "sunder-package.json")));
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
                Assert.False(File.Exists(Path.Combine(devPackage, ".sunder-generated-output")));
                Assert.False(Directory.Exists(Path.Combine(devPackage, "lib")));
                Assert.False(Directory.Exists(Path.Combine(devPackage, "assets")));
                if (shape.Name == "Default") await RetainTemplateSmokeArtifactAsync(devPackage);
                using (var manifest = JsonDocument.Parse(
                           await File.ReadAllTextAsync(Path.Combine(devPackage, "manifest", "sunder-package.json"))))
                {
                    Assert.Equal(1, manifest.RootElement.GetProperty("archiveFormatVersion").GetInt32());
                    Assert.Equal(1, manifest.RootElement.GetProperty("manifestVersion").GetInt32());
                    Assert.Equal("1.0.0", manifest.RootElement.GetProperty("version").GetString());
                    var targets = manifest.RootElement.GetProperty("targets").EnumerateArray().ToArray();
                    Assert.Equal(shape.BuildTargetCount, targets.Length);
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
                        Assert.Contains("rpc.v1", capabilities);
                        if (target.GetProperty("role").GetString() == "runtime")
                        {
                            Assert.Equal("worker", target.GetProperty("kind").GetString());
                            Assert.Contains("worker-protocol.v1", capabilities);
                            Assert.DoesNotContain("worker-protocol.v3", capabilities);
                            Assert.Contains("sdk-baseline-1-1.v1", capabilities);
                        }
                        AssertExactTemplateCapabilities(shape.Name, target);
                    });
                    var contracts = manifest.RootElement.GetProperty("contractBundles").EnumerateArray().ToArray();
                    Assert.Equal(shape.Name == "Stacks" ? 2 : 1, contracts.Length);
                    var sampleContract = Assert.Single(
                        contracts,
                        contract => contract.GetProperty("contractId").GetString() == packageId + ".sample");
                    Assert.Equal("1.0.0", sampleContract.GetProperty("version").GetString());
                    Assert.Equal("package", sampleContract.GetProperty("exposure").GetString());
                    Assert.Equal(descriptor.Sha256, sampleContract.GetProperty("descriptorSha256").GetString());
                    Assert.Equal(descriptor.AbiSha256, sampleContract.GetProperty("abiSha256").GetString());
                    Assert.False(sampleContract.TryGetProperty("sha256", out _));
                    var uses = manifest.RootElement.GetProperty("usesContracts").EnumerateArray().ToArray();
                    if (shape.HasApp)
                    {
                        var use = Assert.Single(uses);
                        Assert.Equal("app", use.GetProperty("role").GetString());
                        Assert.Equal(packageId + ".sample", use.GetProperty("contractId").GetString());
                        Assert.Equal("1.0.0", use.GetProperty("contractVersion").GetString());
                        Assert.Equal(descriptor.AbiSha256, use.GetProperty("abiSha256").GetString());
                        Assert.True(use.GetProperty("required").GetBoolean());
                        Assert.Equal(
                            ["discover", "invoke", "subscribe"],
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
                    Assert.Equal(shape.Name == "Stacks" ? 2 : 1, providers.Length);
                    var sampleProvider = Assert.Single(
                        providers,
                        provider => provider.GetProperty("providerId").GetString() == packageId + ".sample.provider");
                    Assert.Equal("runtime", sampleProvider.GetProperty("role").GetString());
                    Assert.Equal(packageId + ".sample", sampleProvider.GetProperty("contractId").GetString());
                    Assert.Equal("1.0.0", sampleProvider.GetProperty("contractVersion").GetString());
                    Assert.Equal("owner", sampleProvider.GetProperty("audience").GetString());
                    Assert.Equal(descriptor.Sha256, sampleProvider.GetProperty("descriptorSha256").GetString());
                    Assert.Equal(descriptor.AbiSha256, sampleProvider.GetProperty("abiSha256").GetString());
                    Assert.False(sampleProvider.TryGetProperty("contractSha256", out _));
                    Assert.Equal(
                        shape.HasDependency ? 1 : 0,
                        manifest.RootElement.GetProperty("dependsOn").GetArrayLength());
                }

                var pack = await RunDotnetAsync(
                    output,
                    "msbuild",
                    primaryProject,
                    "-t:PackSunderPackage",
                    "-p:Configuration=Debug",
                    "-nr:false",
                    "-nologo");
                AssertSuccess(pack);
                var archivePath = Path.Combine(primaryOutput, $"{projectName}.1.0.0.sunderpkg");
                Assert.True(File.Exists(archivePath), $"Universal package was not created: {archivePath}");
                var firstArchiveBytes = await File.ReadAllBytesAsync(archivePath);
                using var archive = ZipFile.OpenRead(archivePath);
                var archivePaths = archive.Entries
                    .Select(static entry => entry.FullName)
                    .ToHashSet(StringComparer.Ordinal);
                Assert.Contains(archive.Entries, entry => entry.FullName == "manifest/sunder-package.json");
                Assert.Contains(archive.Entries, entry => entry.FullName == "manifest/content-index.json");
                Assert.Contains(archive.Entries, entry => entry.FullName == "payload/shared/contracts/sample.rpc.json");
                Assert.Equal(shape.Name == "Stacks", archivePaths.Contains("payload/shared/contracts/sunder.stack.contributor.rpc.json"));
                Assert.DoesNotContain(archivePaths, path => path.EndsWith(".Protocol.dll", StringComparison.Ordinal));
                Assert.DoesNotContain(archivePaths, path => path.StartsWith("payload/app/", StringComparison.Ordinal)
                    && (path.EndsWith("/Sunder.Sdk.Worker.dll", StringComparison.Ordinal) || path.EndsWith($"/{projectName}.dll", StringComparison.Ordinal)));
                Assert.DoesNotContain(archive.Entries, entry => entry.FullName == "sunder-package.json");
                Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("payload/lib/", StringComparison.Ordinal));
                Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("payload/assets/", StringComparison.Ordinal));
                var packedManifestEntry = Assert.Single(
                    archive.Entries,
                    entry => entry.FullName == "manifest/sunder-package.json");
                using (var packedManifest = JsonDocument.Parse(packedManifestEntry.Open()))
                {
                    var packedTargets = packedManifest.RootElement.GetProperty("targets").EnumerateArray().ToArray();
                    Assert.Equal(shape.PackTargetCount, packedTargets.Length);
                    Assert.Equal(
                        shape.HasApp,
                        packedTargets.Any(target => target.GetProperty("role").GetString() == "app"));
                    Assert.All(packedTargets, target => AssertExactTemplateCapabilities(shape.Name, target));
                    foreach (var runtimeTarget in packedTargets.Where(static target =>
                                 target.GetProperty("role").GetString() == "runtime"))
                    {
                        var rid = runtimeTarget.GetProperty("rid").GetString()!;
                        Assert.True(ContainsRuntimeLogicalPath(archivePaths, rid, $"lib/{projectName}.dll"));
                        Assert.True(ContainsRuntimeLogicalPath(archivePaths, rid, $"lib/{projectName}.runtimeconfig.json"));
                        Assert.True(ContainsRuntimeLogicalPath(archivePaths, rid, $"lib/{projectName}.deps.json"));
                        Assert.True(ContainsRuntimeLogicalPath(archivePaths, rid, "lib/Sunder.Sdk.Worker.dll"));
                    }
                }
                Assert.DoesNotContain(archivePaths, path =>
                    (path.StartsWith("payload/runtime/", StringComparison.Ordinal)
                     || path.StartsWith("payload/shared/", StringComparison.Ordinal))
                    && (path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase)));
                Assert.DoesNotContain(archivePaths, path =>
                    path.EndsWith($"/lib/{projectName}", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith($"/lib/{projectName}.exe", StringComparison.OrdinalIgnoreCase));
                archive.Dispose();

                if (shape.Name == "Default")
                {
                    AssertSuccess(await RunDotnetAsync(
                        output,
                        "msbuild",
                        primaryProject,
                        "-t:PackSunderPackage",
                        "-p:Configuration=Debug",
                        "-nr:false",
                        "-nologo"));
                    Assert.Equal(firstArchiveBytes, await File.ReadAllBytesAsync(archivePath));
                }

                if (shape.HasApp)
                {
                    var prebuiltArchivePath = Path.Combine(primaryOutput, $"{projectName}.prebuilt.sunderpkg");
                    Task<ProcessResult> PackPrebuiltAsync() => RunDotnetAsync(
                        output,
                        "msbuild",
                        primaryProject,
                        "-t:PackSunderPackage",
                        "-p:Configuration=Debug",
                        "-p:SunderPackageUsePrebuiltLeaves=true",
                        "-p:BuildProjectReferences=false",
                        $"-p:SunderPackageOutputPath={prebuiltArchivePath}",
                        "-nr:false",
                        "-nologo");
                    var prebuiltPack = await PackPrebuiltAsync();
                    Assert.NotEqual(0, prebuiltPack.ExitCode);
                    Assert.Contains("prebuilt", prebuiltPack.StandardOutput + prebuiltPack.StandardError, StringComparison.OrdinalIgnoreCase);
                    Assert.False(File.Exists(prebuiltArchivePath));
                    Assert.Equal(firstArchiveBytes, await File.ReadAllBytesAsync(archivePath));
                }
                Assert.False(File.Exists(Path.Combine(output, "sunder-contracts.owned.json")));
                await AssertTemplateInputsAsync(output, authored);
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task<string> PackLocalSunderFeedAsync(string repositoryRoot, string localFeed, string version)
    {
        Directory.CreateDirectory(localFeed);
        var configuration = "TemplateSdk-" + Guid.NewGuid().ToString("N");
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
                configuration,
                "--no-restore",
                "--disable-build-servers",
                "-o",
                localFeed,
                $"-p:SunderDeveloperPackageVersion={version}",
                $"-p:SunderDeveloperPackageVersionRange=[{version}]",
                $"-p:PackageVersion={version}",
                "-p:GeneratePackageOnBuild=false"));
            // Reuse restored third-party nupkgs, never a cached/published Sunder
            // baseline. The consumer restore is then genuinely offline.
            using var assets = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(packageProject)!, "obj", "project.assets.json")));
            foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
            {
                if (library.Value.GetProperty("type").GetString() != "package" || library.Name.StartsWith("Sunder.", StringComparison.OrdinalIgnoreCase)) continue;
                var identity = library.Name.Split('/');
                var filename = identity[0].ToLowerInvariant() + "." + identity[1].ToLowerInvariant() + ".nupkg";
                var destination = Path.Combine(localFeed, filename);
                if (File.Exists(destination)) continue;
                var relative = library.Value.GetProperty("path").GetString()!;
                var package = assets.RootElement.GetProperty("packageFolders").EnumerateObject()
                    .Select(folder => Path.Combine(folder.Name, relative, filename)).First(File.Exists);
                File.Copy(package, destination);
            }
        }

        Assert.All(
            new[] { "Sunder.Sdk", "Sunder.Sdk.Avalonia", "Sunder.Sdk.Stacks", "Sunder.Sdk.Worker", "Sunder.Package.Build" },
            packageId => Assert.True(
                File.Exists(Path.Combine(localFeed, $"{packageId}.{version}.nupkg")),
                $"Local feed package was not created for {packageId}."));

        using var buildPackage = ZipFile.OpenRead(Path.Combine(localFeed, $"Sunder.Package.Build.{version}.nupkg"));
        Assert.DoesNotContain(buildPackage.Entries, entry => entry.FullName.Contains("BouncyCastle", StringComparison.Ordinal));
        return configuration;
    }

    private static async Task AssertPackedInternalRpcBindingConsumerAsync(
        string root,
        string localFeed,
        string packagesPath,
        string version)
    {
        var consumerDirectory = Path.Combine(root, "packed-internal-rpc-consumer");
        Directory.CreateDirectory(consumerDirectory);
        var projectPath = Path.Combine(consumerDirectory, "PackedInternalRpcConsumer.csproj");
        await File.WriteAllTextAsync(projectPath, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <SunderPackageProject>false</SunderPackageProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Sunder.Sdk" Version="{{version}}" />
                <PackageReference Include="Sunder.Package.Build" Version="{{version}}" PrivateAssets="all" />
                <SunderRpcBinding Include="sample.rpc.json"
                                  Namespace="Packed.Internal.Generated"
                                  OutputFile="Binding.g.cs"
                                  Accessibility="Internal" />
              </ItemGroup>
            </Project>
            """);
        var descriptorPath = Path.Combine(consumerDirectory, "sample.rpc.json");
        await File.WriteAllTextAsync(descriptorPath, PackedConsumerDescriptor);
        await File.WriteAllTextAsync(
            Path.Combine(consumerDirectory, "Consumer.cs"),
            """
            namespace Packed.Internal.Generated;

            internal static class Consumer
            {
                internal static global::System.Type RequestType => typeof(Request);
            }
            """);

        AssertSuccess(await RunDotnetAsync(
            consumerDirectory,
            "restore",
            projectPath,
            "--source",
            localFeed,
            "--packages",
            packagesPath,
            "--disable-parallel",
            "--ignore-failed-sources"));
        AssertSuccess(await RunDotnetAsync(
            consumerDirectory,
            "build",
            projectPath,
            "--no-restore",
            "--disable-build-servers",
            "-m:1"));

        var generatedPath = Path.Combine(
            consumerDirectory,
            "obj",
            "Debug",
            "net10.0",
            "sunder-rpc",
            "Binding.g.cs");
        var generated = await File.ReadAllTextAsync(generatedPath);
        var descriptor = SunderRpcContractDescriptor.Parse(await File.ReadAllBytesAsync(descriptorPath));
        Assert.Equal(
            SunderRpcCSharpGenerator.Generate(
                descriptor,
                "Packed.Internal.Generated",
                SunderRpcCSharpAccessibility.Internal),
            generated);
        Assert.Contains("internal sealed class Request", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("public sealed class Request", generated, StringComparison.Ordinal);

        var taskSetMarkers = Directory.GetFiles(
            Path.Combine(consumerDirectory, "obj"),
            ".sunder-task-set",
            SearchOption.AllDirectories);
        Assert.NotEmpty(taskSetMarkers);
        Assert.All(taskSetMarkers, marker =>
        {
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(marker)!, "Sunder.Package.Format.dll")));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(marker)!, "BouncyCastle.Cryptography.dll")));
        });
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
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        await using var process = CapturedProcess.Start(startInfo);
        try
        {
            var result = await process.WaitForExitAsync(TimeSpan.FromMinutes(4));
            return new ProcessResult(result.ExitCode, result.StandardOutput, result.StandardError);
        }
        catch (ProcessFixtureException exception)
        {
            return new ProcessResult(
                -1,
                process.StandardOutput,
                $"dotnet {string.Join(' ', arguments)} failed its process watchdog."
                + Environment.NewLine
                + exception.Message);
        }
    }

    private static void AssertSuccess(ProcessResult result)
        => Assert.True(
            result.ExitCode == 0,
            $"dotnet exited with {result.ExitCode}.{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");

    private static bool ContainsRuntimeLogicalPath(IReadOnlySet<string> archivePaths, string rid, string logicalPath)
        => archivePaths.Contains("payload/shared/" + logicalPath)
           || archivePaths.Contains("payload/runtime/shared/" + logicalPath)
           || archivePaths.Contains($"payload/runtime/{rid}/{logicalPath}");

    private static void AssertExactTemplateCapabilities(string shapeName, JsonElement target)
    {
        if (shapeName is not ("Default" or "Avalonia" or "AvaloniaNoDefaultView"))
        {
            return;
        }

        var role = target.GetProperty("role").GetString();
        string[] expected = role switch
        {
            "runtime" =>
            [
                "contributions.v1",
                "core.v1",
                "execution-runtime.v1",
                "packaging.v1",
                "rpc.v1",
                "sdk-baseline-1-1.v1",
                "worker-protocol.v1",
            ],
            "app" when shapeName == "AvaloniaNoDefaultView" =>
            [
                "contributions.v1",
                "core.v1",
                "packaging.v1",
                "rpc.v1",
                "sdk-baseline-1-1.v1",
            ],
            "app" when shapeName == "Avalonia" =>
            [
                "contributions.v1",
                "core.v1",
                "packaging.v1",
                "rpc.v1",
                "sdk-baseline-1-1.v1",
                "views.v1",
            ],
            _ => throw new InvalidDataException(
                $"Template shape '{shapeName}' emitted unexpected target role '{role}'."),
        };
        Assert.Equal(
            expected,
            target.GetProperty("requiredHostCapabilities")
                .EnumerateArray()
                .Select(static capability => capability.GetString()!)
                .ToArray());
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

    private const string PackedConsumerDescriptor = """
        {
          "descriptorVersion": 1,
          "schemaProfile": "sunder.rpc.values.v1",
          "contractId": "test.packed.internal",
          "version": "1.0.0",
          "errors": {},
          "services": {
            "sample": {
              "methods": {
                "send": {
                  "kind": "unary",
                  "idempotency": "none",
                  "errors": [],
                  "requestSchema": { "$ref": "#/$defs/Request" },
                  "responseSchema": { "$ref": "#/$defs/Response" }
                }
              }
            }
          },
          "$defs": {
            "Request": {
              "type": "object",
              "properties": { "message": { "type": "string", "minLength": 0, "maxLength": 128 } },
              "required": ["message"],
              "additionalProperties": false
            },
            "Response": {
              "type": "object",
              "properties": { "accepted": { "type": "boolean" } },
              "required": ["accepted"],
              "additionalProperties": false
            }
          }
        }
        """;

    private sealed record GeneratedShape(
        string Name,
        IReadOnlyList<string> Options,
        int BuildTargetCount,
        int PackTargetCount,
        bool HasApp,
        bool HasDependency);
}
