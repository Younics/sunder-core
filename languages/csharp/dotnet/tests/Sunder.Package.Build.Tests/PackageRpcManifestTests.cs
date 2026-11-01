using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Worker;
using Xunit;
using MSBuildTaskItem = Microsoft.Build.Utilities.TaskItem;
using static Sunder.Package.Build.Tests.PackageBuildProcessFixture;
using static Sunder.Package.Build.Tests.PackageManifestFixture;
using static Sunder.Package.Build.Tests.PackageOutputFixture;

namespace Sunder.Package.Build.Tests;

[Collection("Package build artifacts")]
public sealed class PackageRpcManifestTests
{
    [Fact]
    public void GenerateManifest_InfersStackRpcCapabilityFromDomainAdapterUse()
    {
        var assemblyPath = CopyFixtureAssemblyToIsolatedDirectory("MissingSdkDependency");
        var manifestPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateFixtureTask(assemblyPath, manifestPath, buildEngine);
        task.ReferencePaths =
        [
            new MSBuildTaskItem(typeof(SunderPackageAttribute).Assembly.Location),
            new MSBuildTaskItem(typeof(StackContributorServiceAdapter).Assembly.Location),
        ];

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        AssertContainsCapabilities(
            ReadCapabilities(document.RootElement),
            SunderSdkCapabilities.StacksV1,
            SunderSdkCapabilities.StacksRpcV1);
    }

    [Fact]
    public async Task GenerateManifest_AuthorsAndEmitsStrictRpcContracts()
    {
        var root = CreateTempDirectory();
        var descriptorPath = Path.Combine(root, "chat.rpc.json");
        File.WriteAllText(descriptorPath, MinimalV1Descriptor);
        var manifestPath = Path.Combine(root, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        ConfigureWorkerTask(task, "linux-x64");
        var bundle = new MSBuildTaskItem(descriptorPath);
        bundle.SetMetadata("ContractId", "example.rpc.v1.minimal");
        bundle.SetMetadata("Version", "2.0.0");
        bundle.SetMetadata("DescriptorPath", "contracts/chat.rpc.json");
        bundle.SetMetadata("Exposure", "public");
        var use = new MSBuildTaskItem("example.rpc.v1.minimal");
        use.SetMetadata("Role", "runtime");
        use.SetMetadata("ContractVersion", "2.0.0");
        use.SetMetadata("Required", "false");
        use.SetMetadata("Actions", "discover;invoke");
        var provider = new MSBuildTaskItem("test.package.chat");
        provider.SetMetadata("ContractId", "example.rpc.v1.minimal");
        provider.SetMetadata("ContractVersion", "2.0.0");
        provider.SetMetadata("Role", "runtime");
        provider.SetMetadata("Audience", "public");
        task.ContractBundles = [bundle];
        task.UsesContracts = [use];
        task.RpcProviders = [provider];

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        var projectionFile = Assert.Single(task.ResolvedContractFiles,
            file => file.GetMetadata("DescriptorPath") == SunderPackageFormat.RpcModelProjectionLogicalPath);
        var sourceClaims = Assert.Single(SunderRpcModelProjectionDocument.Parse(File.ReadAllBytes(projectionFile.ItemSpec)).Contracts).ModelProjection;
        Assert.Empty(sourceClaims.Models);
        Assert.Empty(sourceClaims.ServiceReferences);
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Equal(1, document.RootElement.GetProperty("archiveFormatVersion").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("manifestVersion").GetInt32());
        var generatedBundle = Assert.Single(document.RootElement.GetProperty("contractBundles").EnumerateArray());
        Assert.Equal("example.rpc.v1.minimal", generatedBundle.GetProperty("contractId").GetString());
        Assert.Equal("2.0.0", generatedBundle.GetProperty("version").GetString());
        Assert.Equal("contracts/chat.rpc.json", generatedBundle.GetProperty("descriptorPath").GetString());
        Assert.Equal(MinimalV1DescriptorSha256, generatedBundle.GetProperty("descriptorSha256").GetString());
        Assert.Equal(MinimalV1AbiSha256, generatedBundle.GetProperty("abiSha256").GetString());
        Assert.Equal("public", generatedBundle.GetProperty("exposure").GetString());
        Assert.Equal("owned", generatedBundle.GetProperty("source").GetString());
        Assert.Equal("package", generatedBundle.GetProperty("authorityKind").GetString());
        Assert.Equal("test.package.build.fixture", generatedBundle.GetProperty("authorityId").GetString());
        Assert.Equal(document.RootElement.GetProperty("id").GetString(), generatedBundle.GetProperty("authorityId").GetString());
        Assert.False(generatedBundle.TryGetProperty("selectedAuthorityRelease", out _));
        Assert.False(generatedBundle.TryGetProperty("selectedAuthorityContentIdentity", out _));
        Assert.False(generatedBundle.TryGetProperty("catalogSigningKeyId", out _));
        Assert.False(generatedBundle.TryGetProperty("catalogTrustGeneration", out _));
        Assert.False(generatedBundle.TryGetProperty("catalogTrustFingerprint", out _));
        Assert.False(generatedBundle.TryGetProperty("sha256", out _));
        var generatedUse = Assert.Single(document.RootElement.GetProperty("usesContracts").EnumerateArray());
        Assert.Equal("runtime", generatedUse.GetProperty("role").GetString());
        Assert.Equal("example.rpc.v1.minimal", generatedUse.GetProperty("contractId").GetString());
        Assert.Equal("2.0.0", generatedUse.GetProperty("contractVersion").GetString());
        Assert.Equal(MinimalV1AbiSha256, generatedUse.GetProperty("abiSha256").GetString());
        Assert.False(generatedUse.GetProperty("required").GetBoolean());
        Assert.Equal(
            ["discover", "invoke"],
            generatedUse.GetProperty("actions").EnumerateArray().Select(static value => value.GetString()!).ToArray());
        Assert.False(generatedUse.TryGetProperty("versionRange", out _));
        var generatedProvider = Assert.Single(document.RootElement.GetProperty("provides").EnumerateArray());
        Assert.Equal("test.package.chat", generatedProvider.GetProperty("providerId").GetString());
        Assert.Equal("runtime", generatedProvider.GetProperty("role").GetString());
        Assert.Equal("example.rpc.v1.minimal", generatedProvider.GetProperty("contractId").GetString());
        Assert.Equal("2.0.0", generatedProvider.GetProperty("contractVersion").GetString());
        Assert.Equal(MinimalV1DescriptorSha256, generatedProvider.GetProperty("descriptorSha256").GetString());
        Assert.Equal(MinimalV1AbiSha256, generatedProvider.GetProperty("abiSha256").GetString());
        Assert.Equal("public", generatedProvider.GetProperty("audience").GetString());
        Assert.False(generatedProvider.TryGetProperty("contractSha256", out _));
        Assert.All(document.RootElement.GetProperty("targets").EnumerateArray(), target =>
        {
            var capabilities = target.GetProperty("requiredHostCapabilities")
                .EnumerateArray()
                .Select(static value => value.GetString()!)
                .ToArray();
            Assert.Contains("rpc.v1", capabilities);
        });

        var targetDirectory = Path.Combine(root, "target");
        var devPackage = Path.Combine(root, "sunder-dev");
        Directory.CreateDirectory(targetDirectory);
        var assemblyPath = Path.Combine(targetDirectory, Path.GetFileName(Assembly.GetExecutingAssembly().Location));
        File.Copy(Assembly.GetExecutingAssembly().Location, assemblyPath);
        var workerFiles = CreateDotNetWorkerFiles(targetDirectory, "linux-x64", assemblyPath);
        var emitEngine = new TestBuildEngine();
        Assert.True(new EmitSunderDevOutputTask
        {
            BuildEngine = emitEngine,
            ManifestPath = manifestPath,
            DevPackagePath = devPackage,
            DiscoveryRoot = root,
            TargetDirectory = targetDirectory,
            WorkerFiles = workerFiles,
            ContractFiles = task.ResolvedContractFiles,
        }.Execute(), string.Join(Environment.NewLine, emitEngine.Errors));
        Assert.True(File.Exists(Path.Combine(devPackage, "payload", "shared", "contracts", "chat.rpc.json")));
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(devPackage);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
    }

    [Fact]
    public void GenerateManifest_UsesTheCanonicalBindingSnapshotWhenTheSourceChanges()
    {
        var root = CreateTempDirectory();
        var descriptorPath = Path.Combine(root, "snapshot.rpc.json");
        WriteSnapshotDescriptor(descriptorPath, "original");
        var binding = new MSBuildTaskItem(descriptorPath);
        binding.SetMetadata("Namespace", "Test.Snapshot.Generated");
        var bindingEngine = new TestBuildEngine();
        var bindingTask = new GenerateSunderRpcBindingsTask
        {
            BuildEngine = bindingEngine,
            Bindings = [binding],
            ProjectDirectory = root,
            IntermediateOutputPath = Path.Combine(root, "obj"),
        };
        Assert.True(bindingTask.Execute(), string.Join(Environment.NewLine, bindingEngine.Errors));
        var snapshot = Assert.Single(bindingTask.BindingSnapshots);
        var snapshotHash = snapshot.GetMetadata("DescriptorSha256");
        var snapshotAbiHash = snapshot.GetMetadata("AbiSha256");
        Assert.Equal("1", snapshot.GetMetadata("DescriptorVersion"));
        Assert.Equal(64, snapshotAbiHash.Length);

        WriteSnapshotDescriptor(descriptorPath, "changed");
        Assert.NotEqual(File.ReadAllBytes(snapshot.ItemSpec), File.ReadAllBytes(descriptorPath));

        var manifestPath = Path.Combine(root, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        task.ProjectDirectory = root;
        var bundle = new MSBuildTaskItem(descriptorPath);
        bundle.SetMetadata("ContractId", "test.snapshot");
        bundle.SetMetadata("Version", "1.0.0");
        bundle.SetMetadata("Exposure", "package");
        task.ContractBundles = [bundle];
        task.BindingSnapshots = bindingTask.BindingSnapshots;

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var generatedBundle = Assert.Single(manifest.RootElement.GetProperty("contractBundles").EnumerateArray());
        Assert.Equal(snapshotHash, generatedBundle.GetProperty("descriptorSha256").GetString());
        Assert.Equal(snapshotAbiHash, generatedBundle.GetProperty("abiSha256").GetString());
        Assert.Equal("owned", generatedBundle.GetProperty("source").GetString());
        Assert.Equal("package", generatedBundle.GetProperty("authorityKind").GetString());
        Assert.Equal("test.package.build.fixture", generatedBundle.GetProperty("authorityId").GetString());
        Assert.Equal(File.ReadAllBytes(snapshot.ItemSpec), File.ReadAllBytes(Assert.Single(task.ResolvedContractFiles,
            item => item.GetMetadata("DescriptorPath") != SunderPackageFormat.RpcModelProjectionLogicalPath).ItemSpec));
    }

    [Fact]
    public void GenerateManifest_RejectsRemovedLegacyRpcContractDescriptor()
    {
        var root = CreateTempDirectory();
        var descriptorPath = Path.Combine(root, "contract.rpc.json");
        File.WriteAllText(descriptorPath, """
            {
              "descriptorVersion": 1,
              "contractId": "test.contract",
              "version": "1.0.0",
              "services": [{
                "serviceId": "example",
                "methods": [{
                  "methodId": "invoke",
                  "kind": "unary",
                  "requestSchema": { "$ref": "#/$defs/Empty" },
                  "responseSchema": { "$ref": "#/$defs/Empty" }
                }]
              }],
              "$defs": {
                "Empty": { "type": "object", "properties": {}, "required": [], "additionalProperties": false }
              }
            }
            """);
        var manifestPath = Path.Combine(root, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        task.ProjectDirectory = root;
        var bundle = new MSBuildTaskItem(descriptorPath);
        bundle.SetMetadata("ContractId", "test.contract");
        bundle.SetMetadata("Version", "1.0.0");
        bundle.SetMetadata("Exposure", "package");
        task.ContractBundles = [bundle];

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("is invalid", StringComparison.Ordinal)
            && error.Contains("schemaProfile", StringComparison.Ordinal));
        Assert.False(File.Exists(manifestPath));
    }

    [Fact]
    public void GenerateManifest_RejectsIncompleteRpcAuthoringMetadata()
    {
        var root = CreateTempDirectory();
        var descriptorPath = Path.Combine(root, "contract.json");
        File.WriteAllText(descriptorPath, "{}");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(Path.Combine(root, "sunder-package.json"), buildEngine);
        task.ContractBundles = [new MSBuildTaskItem(descriptorPath)];

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains("requires 'ContractId'", StringComparison.Ordinal));
        Assert.Contains(buildEngine.Errors, error => error.Contains("requires 'Version'", StringComparison.Ordinal));
        Assert.Contains(buildEngine.Errors, error => error.Contains("requires 'Exposure'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Source", "owned")]
    [InlineData("Source", "imported")]
    [InlineData("AuthorityKind", "package")]
    [InlineData("AuthorityKind", "platform")]
    [InlineData("AuthorityId", "test.package.build.fixture")]
    [InlineData("AuthorityId", "test.spoofed.owner")]
    [InlineData("SelectedAuthorityRelease", "1.2.3")]
    [InlineData("SelectedAuthorityContentIdentity", "1111111111111111111111111111111111111111111111111111111111111111")]
    public void GenerateManifest_RejectsAuthoredBundleProvenance(string name, string value)
    {
        var root = CreateTempDirectory();
        var descriptorPath = WriteMinimalV1Descriptor(root);
        var manifestPath = Path.Combine(root, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        task.ProjectDirectory = root;
        var bundle = ContractBundle(descriptorPath);
        bundle.SetMetadata(name, value);
        task.ContractBundles = [bundle];

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("SunderContractBundle", StringComparison.Ordinal)
            && error.Contains($"must not author legacy or derived '{name}' metadata", StringComparison.Ordinal));
        Assert.False(File.Exists(manifestPath));
        Assert.Empty(task.ResolvedContractFiles);
    }

    [Fact]
    public void GenerateManifest_RequiresRoleAndAudienceMetadata()
    {
        var root = CreateTempDirectory();
        var descriptorPath = WriteMinimalV1Descriptor(root);
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(Path.Combine(root, "sunder-package.json"), buildEngine);
        task.ProjectDirectory = root;
        var use = ContractUse("app");
        use.RemoveMetadata("Role");
        var provider = RpcProvider("runtime");
        provider.RemoveMetadata("Audience");
        task.ContractBundles = [ContractBundle(descriptorPath)];
        task.UsesContracts = [use];
        task.RpcProviders = [provider];

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains("SunderUsesContract", StringComparison.Ordinal)
                                                    && error.Contains("requires 'Role'", StringComparison.Ordinal));
        Assert.Contains(buildEngine.Errors, error => error.Contains("SunderRpcProvider", StringComparison.Ordinal)
                                                    && error.Contains("requires 'Audience'", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_RejectsRpcRolesWithoutTargets()
    {
        var root = CreateTempDirectory();
        var descriptorPath = WriteMinimalV1Descriptor(root);
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(Path.Combine(root, "sunder-package.json"), buildEngine);
        task.ProjectDirectory = root;
        task.PackageTargets = [new MSBuildTaskItem("app/win-x64")];
        task.ContractBundles = [ContractBundle(descriptorPath)];
        task.UsesContracts = [ContractUse("runtime")];
        task.RpcProviders = [RpcProvider("runtime")];

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains("SunderUsesContract", StringComparison.Ordinal)
                                                    && error.Contains("existing app or runtime target", StringComparison.Ordinal));
        Assert.Contains(buildEngine.Errors, error => error.Contains("SunderRpcProvider", StringComparison.Ordinal)
                                                    && error.Contains("target Role", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_RejectsDuplicateRoleScopedRpcDeclarations()
    {
        var root = CreateTempDirectory();
        var descriptorPath = WriteMinimalV1Descriptor(root);
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(Path.Combine(root, "sunder-package.json"), buildEngine);
        task.ProjectDirectory = root;
        ConfigureWorkerTask(task, "linux-x64");
        task.ContractBundles = [ContractBundle(descriptorPath)];
        task.UsesContracts = [ContractUse("runtime"), ContractUse("runtime")];
        task.RpcProviders = [RpcProvider("runtime"), RpcProvider("runtime")];

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains("SunderUsesContract", StringComparison.Ordinal)
                                                     && error.Contains("more than once for Role 'runtime'", StringComparison.Ordinal));
        Assert.Contains(buildEngine.Errors, error => error.Contains("SunderRpcProvider", StringComparison.Ordinal)
                                                    && error.Contains("more than once for Role 'runtime'", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_PreservesAndBindsExactSemVerBuildMetadata()
    {
        const string version = "2.0.0+build.7";
        var root = CreateTempDirectory();
        var descriptorPath = WriteMinimalV1Descriptor(root, version);
        var manifestPath = Path.Combine(root, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        task.ProjectDirectory = root;
        task.SdkCapabilities =
        [
            new MSBuildTaskItem(SunderSdkCapabilities.CallbacksV1),
            new MSBuildTaskItem(SunderSdkCapabilities.RpcV1),
        ];
        ConfigureWorkerTask(task, "linux-x64");
        task.ContractBundles = [ContractBundle(descriptorPath, version: version)];
        task.UsesContracts = [ContractUse("runtime", version: version)];
        task.RpcProviders = [RpcProvider("runtime", version: version)];

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        using (var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath)))
        {
            Assert.Equal(version, Assert.Single(manifest.RootElement.GetProperty("contractBundles").EnumerateArray())
                .GetProperty("version").GetString());
            Assert.Equal(version, Assert.Single(manifest.RootElement.GetProperty("usesContracts").EnumerateArray())
                .GetProperty("contractVersion").GetString());
            Assert.Equal(version, Assert.Single(manifest.RootElement.GetProperty("provides").EnumerateArray())
                .GetProperty("contractVersion").GetString());
            Assert.All(manifest.RootElement.GetProperty("targets").EnumerateArray(), target =>
            {
                var capabilities = target.GetProperty("requiredHostCapabilities").EnumerateArray()
                    .Select(static value => value.GetString()!)
                    .ToArray();
                Assert.Contains(SunderSdkCapabilities.CallbacksV1, capabilities);
                Assert.Contains(SunderSdkCapabilities.RpcV1, capabilities);
            });
        }

        var mismatchEngine = new TestBuildEngine();
        var mismatch = CreateTask(Path.Combine(root, "mismatch.json"), mismatchEngine);
        mismatch.ProjectDirectory = root;
        ConfigureWorkerTask(mismatch, "linux-x64");
        mismatch.ContractBundles = [ContractBundle(descriptorPath, version: version)];
        mismatch.UsesContracts = [ContractUse("runtime", version: "2.0.0+build.8")];

        Assert.False(mismatch.Execute());
        Assert.Contains(mismatchEngine.Errors, error => error.Contains("exact matching local", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_RejectsTamperedBindingSnapshotAbiMetadata()
    {
        var root = CreateTempDirectory();
        var descriptorPath = WriteMinimalV1Descriptor(root);
        var bindingEngine = new TestBuildEngine();
        var binding = new MSBuildTaskItem(descriptorPath);
        binding.SetMetadata("Namespace", "Test.Snapshot.Generated");
        var bindingTask = new GenerateSunderRpcBindingsTask
        {
            BuildEngine = bindingEngine,
            Bindings = [binding],
            ProjectDirectory = root,
            IntermediateOutputPath = Path.Combine(root, "obj"),
        };
        Assert.True(bindingTask.Execute(), string.Join(Environment.NewLine, bindingEngine.Errors));
        Assert.Single(bindingTask.BindingSnapshots).SetMetadata("AbiSha256", new string('0', 64));

        var buildEngine = new TestBuildEngine();
        var task = CreateTask(Path.Combine(root, "sunder-package.json"), buildEngine);
        task.ProjectDirectory = root;
        task.ContractBundles = [ContractBundle(descriptorPath)];
        task.BindingSnapshots = bindingTask.BindingSnapshots;

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains("immutable binding snapshot metadata", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_RejectsBundleDescriptorWithUnsafeAncestorOutsideProjectDirectory()
    {
        var root = CreateTempDirectory();
        var projectDirectory = Path.Combine(root, "project");
        var realDirectory = Path.Combine(root, "outside-real");
        var linkDirectory = Path.Combine(root, "outside-link");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(realDirectory);
        var descriptorPath = Path.Combine(realDirectory, "snapshot.rpc.json");
        WriteSnapshotDescriptor(descriptorPath, "value");
        try
        {
            Directory.CreateSymbolicLink(linkDirectory, realDirectory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return;
        }
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(Path.Combine(root, "sunder-package.json"), buildEngine);
        task.ProjectDirectory = projectDirectory;
        var bundle = new MSBuildTaskItem(Path.Combine(linkDirectory, "snapshot.rpc.json"));
        bundle.SetMetadata("ContractId", "test.snapshot");
        bundle.SetMetadata("Version", "1.0.0");
        bundle.SetMetadata("Exposure", "package");
        task.ContractBundles = [bundle];

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("outside-link", StringComparison.Ordinal)
            && error.Contains("symbolic link or reparse point", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManifestGeneration_UsesEvaluatedBindingSnapshotInValidatedOutput()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var targetDirectory = Path.Combine(project, "target");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(targetDirectory);
        var targetAssembly = Path.Combine(targetDirectory, Path.GetFileName(Assembly.GetExecutingAssembly().Location));
        File.Copy(Assembly.GetExecutingAssembly().Location, targetAssembly);
        var descriptorPath = Path.Combine(project, "snapshot.rpc.json");
        WriteSnapshotDescriptor(descriptorPath, "original");

        var targetsPath = Path.GetFullPath(Path.Combine(
            FindTaskDirectory(),
            "..",
            "build",
            "Sunder.Package.Build.targets"));
        var manifestPath = Path.Combine(project, "obj", "sunder-package.json");
        var devPackage = Path.Combine(targetDirectory, "sunder-dev");
        var projectFile = Path.Combine(project, "ManifestPipeline.proj");
        await File.WriteAllTextAsync(projectFile, $$"""
            <Project>
              <PropertyGroup>
                <Configuration>Debug</Configuration>
                <TargetFramework>net10.0</TargetFramework>
                <Version>1.2.3</Version>
                <BaseIntermediateOutputPath>obj/</BaseIntermediateOutputPath>
                <IntermediateOutputPath>obj/</IntermediateOutputPath>
                <TargetPath>{{targetAssembly}}</TargetPath>
                <TargetFileName>{{Path.GetFileName(targetAssembly)}}</TargetFileName>
                <TargetDir>{{targetDirectory}}{{Path.DirectorySeparatorChar}}</TargetDir>
                <SunderGeneratedPackageManifest>{{manifestPath}}</SunderGeneratedPackageManifest>
                <SunderDevOutputPath>{{devPackage}}</SunderDevOutputPath>
                <SunderDevOutputDiscoveryRoot>{{targetDirectory}}</SunderDevOutputDiscoveryRoot>
                <SunderPackageBuildProject>{{Path.Combine(root, "missing.csproj")}}</SunderPackageBuildProject>
                <SunderPackageBuildTaskAssembly>{{typeof(PackSunderPackageTask).Assembly.Location}}</SunderPackageBuildTaskAssembly>
              </PropertyGroup>
              <ItemGroup>
                <ReferencePath Include="{{typeof(SunderPackageAttribute).Assembly.Location}}" />
                <ReferencePath Include="{{typeof(IAvaloniaPackageContributionRegistry).Assembly.Location}}" />
                <ReferencePath Include="{{typeof(IPackageStackExporter).Assembly.Location}}" />
                <ReferencePath Include="{{typeof(SunderWorkerProviderRegistration).Assembly.Location}}" />
                <SunderSdkCapability Include="callbacks.v1" />
                <SunderSdkDynamicAccess Include="{{string.Join(';', TestAssemblyDynamicAccessAcknowledgements().Select(static item => item.ItemSpec))}}" />
                <SunderRpcBinding Include="{{descriptorPath}}"
                                  Namespace="Test.Snapshot.Generated"
                                  OutputFile="Snapshot.g.cs" />
                <SunderContractBundle Include="{{descriptorPath}}"
                                      ContractId="test.snapshot"
                                      Version="1.0.0"
                                      DescriptorPath="contracts/snapshot.rpc.json"
                                      Exposure="package" />
              </ItemGroup>
              <Import Project="{{targetsPath}}" />
              <Target Name="MutateRpcDescriptor" AfterTargets="GenerateSunderRpcBindings">
                <WriteLinesToFile File="{{descriptorPath}}"
                                  Lines="mutated-after-binding-generation"
                                  Overwrite="true"
                                  Encoding="UTF-8" />
              </Target>
              <Target Name="Run" DependsOnTargets="EmitSunderDevOutput" />
            </Project>
            """);

        var result = await ReadProcessAsync(StartMsbuild(projectFile, project));

        Assert.True(result.ExitCode == 0, result.Output);
        var snapshotPath = Assert.Single(Directory.GetFiles(
            Path.Combine(project, "obj", "sunder-rpc", ".snapshots"),
            "*.json",
            SearchOption.TopDirectoryOnly));
        var canonicalDescriptor = await File.ReadAllBytesAsync(snapshotPath);
        var mutatedDescriptor = await File.ReadAllBytesAsync(descriptorPath);
        Assert.False(canonicalDescriptor.SequenceEqual(mutatedDescriptor));
        Assert.True(File.Exists(Path.Combine(project, "obj", "sunder-rpc", "Snapshot.g.cs")));
        var bundledDescriptor = Path.Combine(
            devPackage,
            "payload",
            "shared",
            "contracts",
            "snapshot.rpc.json");
        var bundledDescriptorBytes = await File.ReadAllBytesAsync(bundledDescriptor);
        Assert.Equal(canonicalDescriptor, bundledDescriptorBytes);
        using (var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(
                   Path.Combine(devPackage, "manifest", "sunder-package.json"))))
        {
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(canonicalDescriptor)).ToLowerInvariant(),
                Assert.Single(manifest.RootElement.GetProperty("contractBundles").EnumerateArray())
                    .GetProperty("descriptorSha256")
                    .GetString());
        }
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(devPackage);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
    }

    private static void WriteSnapshotDescriptor(string path, string propertyName)
        => File.WriteAllText(path, """
            {
              "descriptorVersion":1,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"test.snapshot",
              "version":"1.0.0",
              "errors":{},
              "services":{"snapshot":{"methods":{"read":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Empty"},"responseSchema":{"$ref":"#/$defs/Payload"}}}}},
              "$defs":{
                "Empty":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "Payload":{"type":"object","properties":{"PROPERTY_NAME":{"type":"boolean"}},"required":["PROPERTY_NAME"],"additionalProperties":false}
              }
            }
            """.Replace("PROPERTY_NAME", propertyName, StringComparison.Ordinal));

    private const string MinimalV1DescriptorSha256 = "709c513e8ce97618f8a5fefdf3b7de92c8ed673a7d657ba151b279d527c0d371";

    private const string MinimalV1AbiSha256 = "999c92cc5041ca77b2acabeab94a5bf0887117a8005ec3fa709564dc95902a42";
}
