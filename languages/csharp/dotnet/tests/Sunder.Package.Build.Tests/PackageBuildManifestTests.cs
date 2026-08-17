using System.Collections;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Microsoft.Build.Framework;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Avalonia.Theming;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Storage;
using Xunit;
using MSBuildTaskItem = Microsoft.Build.Utilities.TaskItem;

[assembly: SunderPackage(
    Id = "test.package.build.fixture",
    Name = "Package Build Fixture",
    Summary = "Exercises generated manifest compatibility metadata.")]

[assembly: SunderPackageDependency(
    PackageId = "test.dependency",
    VersionRange = ">=$(PackageVersion) <$(NextPackageMajorVersion).0.0")]

namespace Sunder.Package.Build.Tests;

public sealed class PackageBuildManifestTests
{
    [Fact]
    public void BuildTaskAssembly_UsesStable11BinaryVersion()
        => Assert.Equal(new Version(1, 1, 0, 0), typeof(GenerateSunderPackageManifestTask).Assembly.GetName().Version);

    [Fact]
    public void GenerateManifest_InfersSdkCompatibilityMetadata()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);

        var success = task.Execute();

        Assert.True(success, string.Join(Environment.NewLine, buildEngine.Errors));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("archiveFormatVersion").GetInt32());
        Assert.Equal(3, root.GetProperty("manifestVersion").GetInt32());
        var targets = root.GetProperty("targets").EnumerateArray().ToArray();
        Assert.Equal(6, targets.Length);
        Assert.All(targets, target => Assert.Equal("app", target.GetProperty("role").GetString()));
        Assert.All(targets, target =>
        {
            var role = target.GetProperty("role").GetString();
            Assert.Contains(target.GetProperty("rid").GetString(), SunderPackageFormat.SupportedRuntimeIdentifiers);
            Assert.Equal("avalonia", target.GetProperty("kind").GetString());
            Assert.Equal($"lib/{Path.GetFileName(Assembly.GetExecutingAssembly().Location)}", target.GetProperty("entryPoint").GetString());
            Assert.Equal("net10.0", target.GetProperty("targetFramework").GetString());
            Assert.False(string.IsNullOrWhiteSpace(target.GetProperty("sdkVersion").GetString()));
        });
        Assert.Empty(root.GetProperty("contractBundles").EnumerateArray());
        Assert.Empty(root.GetProperty("usesContracts").EnumerateArray());
        Assert.Empty(root.GetProperty("provides").EnumerateArray());
        Assert.False(root.TryGetProperty("entryAssembly", out _));
        Assert.False(root.TryGetProperty("hostRoles", out _));
        Assert.False(root.TryGetProperty("sdkApiVersion", out _));
        Assert.False(root.TryGetProperty("sdkPackageVersion", out _));
        Assert.False(root.TryGetProperty("requiredSdkCapabilities", out _));
        var dependency = Assert.Single(root.GetProperty("dependsOn").EnumerateArray());
        Assert.Equal(">=1.2.3 <2.0.0", dependency.GetProperty("versionRange").GetString());
        var capabilities = ReadCapabilities(root);
        Assert.Equal(new[]
        {
            SunderSdkCapabilities.Baseline11V1,
            SunderSdkCapabilities.CoreV1,
            SunderSdkCapabilities.PackagingV1,
            SunderSdkCapabilities.ContributionsV1,
            SunderSdkCapabilities.ViewsV1,
            SunderSdkCapabilities.SettingsViewsV1,
            SunderSdkCapabilities.SettingsSchemaV1,
            SunderSdkCapabilities.SettingsV1,
            SunderSdkCapabilities.StorageV1,
            SunderSdkCapabilities.StorageKeyMigrationV1,
            SunderSdkCapabilities.RoleLocalWorkspaceV1,
            SunderSdkCapabilities.SecretsV1,
            SunderSdkCapabilities.LoggingV1,
            SunderSdkCapabilities.NotificationsV1,
            SunderSdkCapabilities.ShellViewV1,
            SunderSdkCapabilities.ViewNavigationPreparationV1,
            SunderSdkCapabilities.CallbacksV1,
            SunderSdkCapabilities.AuthV1,
            SunderSdkCapabilities.StacksV1,
            SunderSdkCapabilities.StacksRpcV1,
            SunderSdkCapabilities.ThemingV1,
        }.Order(StringComparer.OrdinalIgnoreCase), capabilities.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateManifest_ReadsMetadataFromIncompatibleMachineAssembly()
    {
        var assemblyPath = CopyAssemblyWithIncompatibleMachine(Assembly.GetExecutingAssembly().Location);
        var manifestPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateFixtureTask(assemblyPath, manifestPath, buildEngine);
        task.SdkCapabilities =
        [
            new MSBuildTaskItem(SunderSdkCapabilities.CallbacksV1),
            new MSBuildTaskItem("worker-protocol.v3"),
        ];
        task.ReferencePaths =
        [
            new MSBuildTaskItem(typeof(SunderPackageAttribute).Assembly.Location),
            new MSBuildTaskItem(typeof(IAvaloniaPackageContributionRegistry).Assembly.Location),
            new MSBuildTaskItem(typeof(IPackageStackExporter).Assembly.Location),
        ];

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Equal("test.package.build.fixture", document.RootElement.GetProperty("id").GetString());
        Assert.Equal("test.dependency", Assert.Single(document.RootElement.GetProperty("dependsOn").EnumerateArray())
            .GetProperty("packageId").GetString());
    }

    [Fact]
    public void GenerateManifest_InfersStackCapabilitiesFromSeparateSdkAssembly()
    {
        Assert.Equal("Sunder.Sdk.Stacks", typeof(IPackageStackExporter).Assembly.GetName().Name);
        Assert.NotEqual(typeof(SunderPackageAttribute).Assembly, typeof(IPackageStackExporter).Assembly);

        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);

        var success = task.Execute();

        Assert.True(success, string.Join(Environment.NewLine, buildEngine.Errors));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var capabilities = ReadCapabilities(document.RootElement);
        AssertContainsCapabilities(
            capabilities,
            SunderSdkCapabilities.StacksV1,
            SunderSdkCapabilities.StacksRpcV1);
    }

    [Fact]
    public void GenerateManifest_InfersStackRpcCapabilityFromClientOnlyUse()
    {
        var assemblyPath = CopyFixtureAssemblyToIsolatedDirectory("MissingSdkDependency");
        var manifestPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateFixtureTask(assemblyPath, manifestPath, buildEngine);
        task.ReferencePaths =
        [
            new MSBuildTaskItem(typeof(SunderPackageAttribute).Assembly.Location),
            new MSBuildTaskItem(typeof(StackContributorRpcClient).Assembly.Location),
        ];

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        AssertContainsCapabilities(
            ReadCapabilities(document.RootElement),
            SunderSdkCapabilities.StacksV1,
            SunderSdkCapabilities.StacksRpcV1);
    }

    [Fact]
    public void GenerateManifest_IncludesManualSdkCapabilityOverrides()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        task.SdkCapabilities = [new MSBuildTaskItem("custom.dynamic.v1")];

        var success = task.Execute();

        Assert.True(success, string.Join(Environment.NewLine, buildEngine.Errors));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var capabilities = ReadCapabilities(document.RootElement);
        Assert.Contains("custom.dynamic.v1", capabilities);
    }

    [Fact]
    public void GenerateManifest_RejectsManagedRuntimeTargetAuthoring()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        var target = new MSBuildTaskItem("runtime/linux-arm64");
        target.SetMetadata("Role", "runtime");
        target.SetMetadata("Rid", "linux-arm64");
        target.SetMetadata("Kind", "dotnet");
        target.SetMetadata("EntryPoint", "lib/custom-entry.dll");
        task.PackageTargets = [target];

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("unsupported Runtime target kind 'dotnet'", StringComparison.Ordinal)
            && error.Contains("must use 'worker'", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_AuthorsOneExactSelfContainedWorkerTarget()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        var target = new MSBuildTaskItem("runtime/linux-arm64");
        target.SetMetadata("Kind", SunderPackageFormat.WorkerTargetKind);
        task.PackageTargets = [target];
        task.RuntimeIdentifier = "linux-arm64";
        task.OutputType = "Exe";
        task.SelfContained = true;
        task.UseAppHost = true;
        task.SdkCapabilities = [new MSBuildTaskItem("worker-protocol.v3")];

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        Assert.True(task.HasWorkerTarget);
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var generated = Assert.Single(document.RootElement.GetProperty("targets").EnumerateArray());
        Assert.Equal(SunderPackageFormat.WorkerTargetKind, generated.GetProperty("kind").GetString());
        Assert.Equal("lib/Sunder.Package.Build.Tests", generated.GetProperty("entryPoint").GetString());
        Assert.Equal("Sunder.Package.Build.Tests", task.WorkerEntryPointRelativePath);
    }

    [Fact]
    public void GenerateManifest_RejectsCustomCSharpWorkerAppHostName()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        var target = new MSBuildTaskItem("runtime/linux-x64");
        target.SetMetadata("EntryPoint", "lib/custom-worker");
        ConfigureWorkerTask(task, "linux-x64", target);

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("official self-contained apphost 'lib/Sunder.Package.Build.Tests'", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_RejectsWorkerV1Capability()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        ConfigureWorkerTask(task, "linux-x64");
        task.SdkCapabilities =
        [
            new MSBuildTaskItem("worker-protocol.v3"),
            new MSBuildTaskItem("worker-protocol.v1"),
        ];

        Assert.False(task.Execute());
        Assert.Contains(
            buildEngine.Errors,
            error => error.Contains(
                "cannot use unsupported worker protocol capability 'worker-protocol.v1'",
                StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_RejectsFrameworkDependentWorkerTarget()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        var target = new MSBuildTaskItem("runtime/linux-x64");
        target.SetMetadata("Kind", SunderPackageFormat.WorkerTargetKind);
        task.PackageTargets = [target];
        task.RuntimeIdentifier = "linux-x64";
        task.OutputType = "Exe";
        task.UseAppHost = true;

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains("SelfContained", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_RejectsFreshLegacyProcessTargetWithWorkerGuidance()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        var target = new MSBuildTaskItem("runtime/linux-x64");
        target.SetMetadata("Kind", "process");
        task.PackageTargets = [target];

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("unsupported Runtime target kind 'process'", StringComparison.Ordinal)
            && error.Contains("must use 'worker'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkerV3ExecutableFixture_InfersExactProtocolCapability()
    {
        var root = Path.Combine(
            FindRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "tests",
            "Sunder.Package.Build.TestFixtures",
            "WorkerV3Executable",
            "bin",
            ResolveConfiguration(),
            "net10.0",
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            "sunder-dev");

        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(root);

        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        var target = Assert.Single(validation.Manifest!.Targets!)!;
        Assert.Equal(SunderPackageFormat.WorkerTargetKind, target.Kind);
        Assert.Equal(
            [
                SunderSdkCapabilities.AuthV1,
                SunderSdkCapabilities.BackgroundServicesV1,
                SunderSdkCapabilities.CallbacksV1,
                SunderSdkCapabilities.ContributionsV1,
                SunderSdkCapabilities.CoreV1,
                SunderSdkCapabilities.LoggingV1,
                SunderSdkCapabilities.PackagingV1,
                SunderSdkCapabilities.RpcV2,
                SunderSdkCapabilities.RuntimeGenerationsV1,
                SunderSdkCapabilities.Baseline11V1,
                SunderSdkCapabilities.SecretsV1,
                SunderSdkCapabilities.SettingsSchemaV1,
                SunderSdkCapabilities.SettingsV1,
                SunderSdkCapabilities.StorageKeyMigrationV1,
                SunderSdkCapabilities.StorageV1,
                "worker-protocol.v3",
            ],
            target.RequiredHostCapabilities);
        var executableName = OperatingSystem.IsWindows() ? "WorkerV3Executable.exe" : "WorkerV3Executable";
        Assert.Equal($"lib/{executableName}", target.EntryPoint);
        Assert.True(File.Exists(Path.Combine(root, "payload", "runtime", target.Rid!, "lib", executableName)));
    }

    [Fact]
    public async Task GenerateManifest_AuthorsAndEmitsStrictRpcContracts()
    {
        var root = CreateTempDirectory();
        var descriptorPath = Path.Combine(root, "chat.rpc.json");
        File.WriteAllText(descriptorPath, MinimalV2Descriptor);
        var manifestPath = Path.Combine(root, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        ConfigureWorkerTask(task, "linux-x64");
        var bundle = new MSBuildTaskItem(descriptorPath);
        bundle.SetMetadata("ContractId", "example.rpc.v2.minimal");
        bundle.SetMetadata("Version", "2.0.0");
        bundle.SetMetadata("DescriptorPath", "contracts/chat.rpc.json");
        bundle.SetMetadata("Exposure", "public");
        var use = new MSBuildTaskItem("example.rpc.v2.minimal");
        use.SetMetadata("Role", "runtime");
        use.SetMetadata("ContractVersion", "2.0.0");
        use.SetMetadata("Required", "false");
        use.SetMetadata("Actions", "discover;invoke");
        var provider = new MSBuildTaskItem("test.package.chat");
        provider.SetMetadata("ContractId", "example.rpc.v2.minimal");
        provider.SetMetadata("ContractVersion", "2.0.0");
        provider.SetMetadata("Role", "runtime");
        provider.SetMetadata("Audience", "public");
        task.ContractBundles = [bundle];
        task.UsesContracts = [use];
        task.RpcProviders = [provider];

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Equal(1, document.RootElement.GetProperty("archiveFormatVersion").GetInt32());
        Assert.Equal(3, document.RootElement.GetProperty("manifestVersion").GetInt32());
        var generatedBundle = Assert.Single(document.RootElement.GetProperty("contractBundles").EnumerateArray());
        Assert.Equal("example.rpc.v2.minimal", generatedBundle.GetProperty("contractId").GetString());
        Assert.Equal("2.0.0", generatedBundle.GetProperty("version").GetString());
        Assert.Equal("contracts/chat.rpc.json", generatedBundle.GetProperty("descriptorPath").GetString());
        Assert.Equal(MinimalV2DescriptorSha256, generatedBundle.GetProperty("descriptorSha256").GetString());
        Assert.Equal(MinimalV2AbiSha256, generatedBundle.GetProperty("abiSha256").GetString());
        Assert.Equal("public", generatedBundle.GetProperty("exposure").GetString());
        Assert.False(generatedBundle.TryGetProperty("sha256", out _));
        var generatedUse = Assert.Single(document.RootElement.GetProperty("usesContracts").EnumerateArray());
        Assert.Equal("runtime", generatedUse.GetProperty("role").GetString());
        Assert.Equal("example.rpc.v2.minimal", generatedUse.GetProperty("contractId").GetString());
        Assert.Equal("2.0.0", generatedUse.GetProperty("contractVersion").GetString());
        Assert.Equal(MinimalV2AbiSha256, generatedUse.GetProperty("abiSha256").GetString());
        Assert.False(generatedUse.GetProperty("required").GetBoolean());
        Assert.Equal(
            ["discover", "invoke"],
            generatedUse.GetProperty("actions").EnumerateArray().Select(static value => value.GetString()!).ToArray());
        Assert.False(generatedUse.TryGetProperty("versionRange", out _));
        var generatedProvider = Assert.Single(document.RootElement.GetProperty("provides").EnumerateArray());
        Assert.Equal("test.package.chat", generatedProvider.GetProperty("providerId").GetString());
        Assert.Equal("runtime", generatedProvider.GetProperty("role").GetString());
        Assert.Equal("example.rpc.v2.minimal", generatedProvider.GetProperty("contractId").GetString());
        Assert.Equal("2.0.0", generatedProvider.GetProperty("contractVersion").GetString());
        Assert.Equal(MinimalV2DescriptorSha256, generatedProvider.GetProperty("descriptorSha256").GetString());
        Assert.Equal(MinimalV2AbiSha256, generatedProvider.GetProperty("abiSha256").GetString());
        Assert.Equal("public", generatedProvider.GetProperty("audience").GetString());
        Assert.False(generatedProvider.TryGetProperty("contractSha256", out _));
        Assert.All(document.RootElement.GetProperty("targets").EnumerateArray(), target =>
        {
            var capabilities = target.GetProperty("requiredHostCapabilities")
                .EnumerateArray()
                .Select(static value => value.GetString()!)
                .ToArray();
            Assert.Contains("rpc.v2", capabilities);
            Assert.DoesNotContain("rpc.v1", capabilities);
        });

        var targetDirectory = Path.Combine(root, "target");
        var devPackage = Path.Combine(root, "sunder-dev");
        Directory.CreateDirectory(targetDirectory);
        var assemblyPath = Path.Combine(targetDirectory, Path.GetFileName(Assembly.GetExecutingAssembly().Location));
        File.Copy(Assembly.GetExecutingAssembly().Location, assemblyPath);
        var executablePath = Path.Combine(targetDirectory, Path.GetFileNameWithoutExtension(assemblyPath));
        File.WriteAllText(executablePath, "worker");
        var emitEngine = new TestBuildEngine();
        Assert.True(new EmitSunderDevOutputTask
        {
            BuildEngine = emitEngine,
            ManifestPath = manifestPath,
            DevPackagePath = devPackage,
            DiscoveryRoot = root,
            TargetDirectory = targetDirectory,
            WorkerFiles = [new MSBuildTaskItem(executablePath)],
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
        Assert.Equal("2", snapshot.GetMetadata("DescriptorVersion"));
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
        Assert.Equal(
            snapshotHash,
            Assert.Single(manifest.RootElement.GetProperty("contractBundles").EnumerateArray())
                .GetProperty("descriptorSha256")
                .GetString());
        Assert.Equal(
            snapshotAbiHash,
            Assert.Single(manifest.RootElement.GetProperty("contractBundles").EnumerateArray())
                .GetProperty("abiSha256")
                .GetString());
        Assert.Equal(File.ReadAllBytes(snapshot.ItemSpec), File.ReadAllBytes(Assert.Single(task.ResolvedContractFiles).ItemSpec));
    }

    [Fact]
    public void GenerateManifest_RejectsV1RpcContractDescriptor()
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
            error.Contains("descriptorVersion 2", StringComparison.Ordinal)
            && error.Contains("does not accept V1 RPC descriptors", StringComparison.Ordinal));
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

    [Fact]
    public void GenerateManifest_RejectsLegacyAndAuthoredHashMetadata()
    {
        var root = CreateTempDirectory();
        var descriptorPath = WriteMinimalV2Descriptor(root);
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(Path.Combine(root, "sunder-package.json"), buildEngine);
        task.ProjectDirectory = root;
        var bundle = ContractBundle(descriptorPath);
        bundle.SetMetadata("DescriptorSha256", new string('0', 64));
        var use = ContractUse("app");
        use.SetMetadata("VersionRange", ">=2.0.0 <3.0.0");
        use.SetMetadata("AbiSha256", new string('1', 64));
        var provider = RpcProvider("runtime");
        provider.SetMetadata("ContractSha256", new string('2', 64));
        task.ContractBundles = [bundle];
        task.UsesContracts = [use];
        task.RpcProviders = [provider];

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains("'DescriptorSha256' metadata", StringComparison.Ordinal));
        Assert.Contains(buildEngine.Errors, error => error.Contains("'VersionRange' metadata", StringComparison.Ordinal));
        Assert.Contains(buildEngine.Errors, error => error.Contains("'AbiSha256' metadata", StringComparison.Ordinal));
        Assert.Contains(buildEngine.Errors, error => error.Contains("'ContractSha256' metadata", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_RequiresRoleAndAudienceMetadata()
    {
        var root = CreateTempDirectory();
        var descriptorPath = WriteMinimalV2Descriptor(root);
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
    public void GenerateManifest_RejectsUnsupportedAppRoleProvider()
    {
        var root = CreateTempDirectory();
        var descriptorPath = WriteMinimalV2Descriptor(root);
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(Path.Combine(root, "sunder-package.json"), buildEngine);
        task.ProjectDirectory = root;
        task.ContractBundles = [ContractBundle(descriptorPath)];
        task.RpcProviders = [RpcProvider("app")];

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("App-role providers are not supported", StringComparison.Ordinal)
            && error.Contains("registration and serving are not available", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_RejectsRpcRolesWithoutTargets()
    {
        var root = CreateTempDirectory();
        var descriptorPath = WriteMinimalV2Descriptor(root);
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
        var descriptorPath = WriteMinimalV2Descriptor(root);
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
    public void GenerateManifest_RejectsPublicProviderForPackageExposedContract()
    {
        var root = CreateTempDirectory();
        var descriptorPath = WriteMinimalV2Descriptor(root);
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(Path.Combine(root, "sunder-package.json"), buildEngine);
        task.ProjectDirectory = root;
        ConfigureWorkerTask(task, "linux-x64");
        task.ContractBundles = [ContractBundle(descriptorPath, exposure: "package")];
        task.RpcProviders = [RpcProvider("runtime", audience: "public")];

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains("public Audience", StringComparison.Ordinal)
                                                    && error.Contains("package exposure permits only owner", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_PreservesAndBindsExactSemVerBuildMetadata()
    {
        const string version = "2.0.0+build.7";
        var root = CreateTempDirectory();
        var descriptorPath = WriteMinimalV2Descriptor(root, version);
        var manifestPath = Path.Combine(root, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        task.ProjectDirectory = root;
        task.SdkCapabilities =
        [
            new MSBuildTaskItem(SunderSdkCapabilities.CallbacksV1),
            new MSBuildTaskItem(SunderSdkCapabilities.RpcV2),
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
                Assert.Contains(SunderSdkCapabilities.RpcV2, capabilities);
                if (target.GetProperty("role").GetString() == "runtime")
                {
                    Assert.Contains("rpc.v2", capabilities);
                }
                else
                {
                    Assert.DoesNotContain("rpc.v2", capabilities);
                }
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
        var descriptorPath = WriteMinimalV2Descriptor(root);
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
    public void GenerateManifest_FailsWhenDynamicSdkAccessHasNoExplicitCapabilityDeclaration()
    {
        var assemblyPath = CopyFixtureAssemblyToIsolatedDirectory("AuthoredDynamicReflection");
        var manifestPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateFixtureTask(assemblyPath, manifestPath, buildEngine);
        task.ReferencePaths = SdkReferencePaths();
        task.PackageTargets = [];
        task.SdkCapabilities = [];

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("dynamic/reflection access", StringComparison.Ordinal)
            && error.Contains("SunderSdkCapability", StringComparison.Ordinal));
        Assert.False(File.Exists(manifestPath));
    }

    [Fact]
    public void GenerateManifest_AcceptsExplicitCapabilitiesForUnresolvedDynamicSdkAccess()
    {
        var assemblyPath = CopyFixtureAssemblyToIsolatedDirectory("AuthoredDynamicReflection");
        var manifestPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateFixtureTask(assemblyPath, manifestPath, buildEngine);
        task.ReferencePaths = SdkReferencePaths();
        task.SdkCapabilities =
        [
            new MSBuildTaskItem(SunderSdkCapabilities.CallbacksV1),
            new MSBuildTaskItem("worker-protocol.v3"),
        ];
        task.DynamicAccessAcknowledgements = DynamicAccessAcknowledgements();

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Contains(SunderSdkCapabilities.CallbacksV1, ReadCapabilities(document.RootElement));
    }

    [Fact]
    public void GenerateManifest_RequiresAcknowledgementForEveryGeneratedDynamicAccessSite()
    {
        var assemblyPath = CopyFixtureAssemblyToIsolatedDirectory("AuthoredDynamicReflection");
        var manifestPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateFixtureTask(assemblyPath, manifestPath, buildEngine);
        task.ReferencePaths = SdkReferencePaths();
        task.SdkCapabilities =
        [
            new MSBuildTaskItem(SunderSdkCapabilities.CallbacksV1),
            new MSBuildTaskItem("worker-protocol.v3"),
        ];
        task.DynamicAccessAcknowledgements = [DynamicAccessAcknowledgements()[0]];

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains("ResolveSdkContractAsync", StringComparison.Ordinal));
        Assert.Contains(buildEngine.Errors, error => error.Contains("CreateSdkContractResolver", StringComparison.Ordinal));
        Assert.Contains(buildEngine.Errors, error => error.Contains("ResolveSdkContracts", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_InfersGeneratedAvaloniaXamlThemingAndIgnoresOtherGeneratedMethods()
    {
        var assemblyPath = CopyFixtureAssemblyToIsolatedDirectory("GeneratedAvalonia");
        var manifestPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateFixtureTask(assemblyPath, manifestPath, buildEngine);
        task.ReferencePaths = SdkReferencePaths(includeAvalonia: true);
        task.PackageTargets = [];
        task.SdkCapabilities = [];

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        Assert.Empty(buildEngine.Errors);
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Equal(
            ["app"],
            document.RootElement.GetProperty("targets").EnumerateArray()
                .Select(static target => target.GetProperty("role").GetString()!)
                .Distinct()
                .ToArray());
        Assert.Contains(SunderSdkCapabilities.ThemingV1, ReadCapabilities(document.RootElement));
    }

    [Fact]
    public void GenerateManifest_IgnoresMissingIrrelevantExternalTypeAndExternalCallToken()
    {
        var assemblyPath = CopyFixtureAssemblyToIsolatedDirectory("MissingExternal");
        var manifestPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateFixtureTask(assemblyPath, manifestPath, buildEngine);
        task.ReferencePaths = SdkReferencePaths();

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        Assert.Empty(buildEngine.Errors);
    }

    [Fact]
    public void GenerateManifest_FailsClosedWhenReferencedSdkAssemblyIsMissing()
    {
        var assemblyPath = CopyFixtureAssemblyToIsolatedDirectory("MissingExternal");
        var manifestPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateFixtureTask(assemblyPath, manifestPath, buildEngine);

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("resolved Sunder.Sdk reference", StringComparison.Ordinal));
        Assert.False(File.Exists(manifestPath));
    }

    [Fact]
    public void GenerateManifest_InfersConstantPackageAuthoredSdkReflection()
    {
        var assemblyPath = CopyFixtureAssemblyToIsolatedDirectory("AuthoredReflection");
        var manifestPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateFixtureTask(assemblyPath, manifestPath, buildEngine);
        task.ReferencePaths = SdkReferencePaths();

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Contains(SunderSdkCapabilities.CoreV1, ReadCapabilities(document.RootElement));
    }

    [Fact]
    public void GenerateManifest_InspectsPackageOwnedCopyLocalAssemblies()
    {
        var assemblyPath = CopyFixtureAssemblyToIsolatedDirectory("MissingExternal");
        var dependencyPath = GetFixtureAssemblyPath("ExternalDependency");
        var manifestPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateFixtureTask(assemblyPath, manifestPath, buildEngine);
        task.ReferencePaths = SdkReferencePaths();
        task.RuntimeCopyLocalPaths = [new MSBuildTaskItem(dependencyPath)];
        task.AuthoredAssemblyPaths = [new MSBuildTaskItem(dependencyPath)];

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Contains(SunderSdkCapabilities.NotificationsV1, ReadCapabilities(document.RootElement));
    }

    [Fact]
    public void GenerateManifest_DoesNotTreatArbitraryCopyLocalDependencyAsAuthored()
    {
        var assemblyPath = CopyFixtureAssemblyToIsolatedDirectory("MissingExternal");
        var dependencyPath = GetFixtureAssemblyPath("ExternalDependency");
        var manifestPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateFixtureTask(assemblyPath, manifestPath, buildEngine);
        task.ReferencePaths = SdkReferencePaths();
        task.RuntimeCopyLocalPaths = [new MSBuildTaskItem(dependencyPath)];

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.DoesNotContain(SunderSdkCapabilities.NotificationsV1, ReadCapabilities(document.RootElement));
    }

    [Fact]
    public void GenerateManifest_RejectsSdkPackageVersionOverrideThatDiffersFromResolvedReference()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        task.SdkPackageVersion = "9.9.9";

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains("does not match resolved", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_IgnoresSdkPdbAndXmlCompanionsInAssemblyItemLists()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        var companions = GetAssemblyCompanionPaths(typeof(SunderPackageAttribute).Assembly.Location);
        task.ReferencePaths = task.ReferencePaths
            .Concat(companions.Select(static path => new MSBuildTaskItem(path)))
            .ToArray();
        task.RuntimeCopyLocalPaths = companions.Select(static path => new MSBuildTaskItem(path)).ToArray();

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        Assert.Empty(buildEngine.Errors);
    }

    [Fact]
    public void GenerateManifest_AcceptsEquivalentSdkVersionWithoutInformationalBuildMetadata()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        var informationalVersion = typeof(SunderPackageAttribute).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;
        var packageVersion = informationalVersion.Split('+')[0];
        task.SdkPackageVersion = packageVersion;

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.All(
            document.RootElement.GetProperty("targets").EnumerateArray(),
            target => Assert.Equal(packageVersion, target.GetProperty("sdkVersion").GetString()));
    }

    [Fact]
    public void GenerateManifest_RejectsSdkVersionWithDifferentPrereleasePrecedence()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        task.SdkPackageVersion = "1.1.0-preview.1";

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains("does not match resolved", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_DoesNotTreatSdkCompanionFilesAsResolvedDependencies()
    {
        var assemblyPath = CopyFixtureAssemblyToIsolatedDirectory("MissingSdkDependency");
        var manifestPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateFixtureTask(assemblyPath, manifestPath, buildEngine);
        task.ReferencePaths = SdkReferencePaths();
        task.RuntimeCopyLocalPaths = GetAssemblyCompanionPaths(typeof(IPackageStackExporter).Assembly.Location)
            .Select(static path => new MSBuildTaskItem(path))
            .ToArray();

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains(
            "Referenced Sunder SDK assembly 'Sunder.Sdk.Stacks' could not be resolved",
            StringComparison.Ordinal));
    }

    [Fact]
    public void SourceReferenceFixture_BuildsWithSdkCompanionsAndInformationalMetadata()
    {
        var root = FindRepositoryRoot();
        var configuration = ResolveConfiguration();
        var sdkOutput = Path.Combine(
            root,
            "languages",
            "csharp",
            "dotnet",
            "tests",
            "Sunder.Package.Build.TestFixtures",
            "SourceReferenceSdk",
            "bin",
            configuration,
            "net10.0");
        var sdkAssemblyPath = Path.Combine(sdkOutput, "Sunder.Sdk.dll");
        Assert.True(File.Exists(sdkAssemblyPath));
        Assert.True(File.Exists(Path.Combine(sdkOutput, "Sunder.Sdk.pdb")));
        Assert.True(File.Exists(Path.Combine(sdkOutput, "Sunder.Sdk.xml")));
        Assert.StartsWith("1.1.0+source.fixture", FileVersionInfo.GetVersionInfo(sdkAssemblyPath).ProductVersion, StringComparison.Ordinal);

        var manifestPath = Path.Combine(
            root,
            "languages",
            "csharp",
            "dotnet",
            "tests",
            "Sunder.Package.Build.TestFixtures",
            "SourceReferencePackage",
            "obj",
            configuration,
            "net10.0",
            "sunder-package.json");
        Assert.True(File.Exists(manifestPath), $"Source-reference fixture manifest was not generated: {manifestPath}");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.All(
            document.RootElement.GetProperty("targets").EnumerateArray(),
            target => Assert.Equal("1.1.0", target.GetProperty("sdkVersion").GetString()));
        AssertContainsCapabilities(
            ReadCapabilities(document.RootElement),
            SunderSdkCapabilities.StorageV1,
            SunderSdkCapabilities.StorageKeyMigrationV1,
            SunderSdkCapabilities.ShellViewV1,
            SunderSdkCapabilities.ViewNavigationPreparationV1);
    }

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

    [Fact]
    public async Task PackPackage_PreservesIndexedAssetNamedContentIndexJson()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var devPackage = Path.Combine(root, "sunder-dev");
        var manifestEngine = new TestBuildEngine();
        CreateValidDevPackage(devPackage, manifestEngine);
        Directory.CreateDirectory(Path.Combine(devPackage, "payload", "shared", "assets"));
        File.WriteAllText(Path.Combine(devPackage, "payload", "shared", "assets", "content-index.json"), "asset");
        PackageContentIndexer.Write(devPackage);
        var output = Path.Combine(project, "bin", "package.sunderpkg");
        var buildEngine = new TestBuildEngine();

        Assert.True(new PackSunderPackageTask
        {
            BuildEngine = buildEngine,
            DevPackagePath = devPackage,
            DevPackageDiscoveryRoot = root,
            PackageOutputPath = output,
            ProjectDirectory = project,
        }.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));

        using var archive = ZipFile.OpenRead(output);
        Assert.Contains(archive.Entries, entry => entry.FullName == "payload/shared/assets/content-index.json");
        var indexEntry = Assert.Single(archive.Entries, entry => entry.FullName == "manifest/content-index.json");
        using var reader = new StreamReader(indexEntry.Open());
        using var document = JsonDocument.Parse(reader.ReadToEnd());
        Assert.Contains(document.RootElement.GetProperty("files").EnumerateArray(), entry =>
            entry.GetProperty("path").GetString() == "payload/shared/assets/content-index.json");
        Assert.All(
            document.RootElement.GetProperty("files").EnumerateArray(),
            entry => Assert.False(entry.TryGetProperty("role", out _)));

        var validation = await SunderPackageArchiveInspector
            .ExtractAndValidateAsync(output, Path.Combine(root, "validated"));
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
    }

    [Fact]
    public void PackPackage_RejectsContentChangedAfterIndexing()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var devPackage = Path.Combine(root, "sunder-dev");
        CreateValidDevPackage(devPackage, new TestBuildEngine());
        var assemblyPath = Path.Combine(
            devPackage,
            "payload",
            "shared",
            "lib",
            Path.GetFileName(Assembly.GetExecutingAssembly().Location));
        File.AppendAllText(assemblyPath, "tampered-after-signing-and-indexing");
        var output = Path.Combine(project, "bin", "package.sunderpkg");
        var buildEngine = new TestBuildEngine();

        Assert.False(new PackSunderPackageTask
        {
            BuildEngine = buildEngine,
            DevPackagePath = devPackage,
            DevPackageDiscoveryRoot = root,
            PackageOutputPath = output,
            ProjectDirectory = project,
        }.Execute());

        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("SHA-256 mismatch", StringComparison.Ordinal));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void PackPackage_RecoversAdjacentLockOwnedByDeadProcess()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var devPackage = Path.Combine(root, "sunder-dev");
        var output = Path.Combine(project, "bin", "recovered.sunderpkg");
        CreateValidDevPackage(devPackage, new TestBuildEngine());
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var lockPath = output + ".sunder-output.lock";
        using var deadOwnerMarker = CreateOwnerDirectory(
            lockPath,
            int.MaxValue,
            Environment.MachineName,
            DateTime.UtcNow.AddMinutes(-1));
        var buildEngine = new TestBuildEngine();

        Assert.True(new PackSunderPackageTask
        {
            BuildEngine = buildEngine,
            DevPackagePath = devPackage,
            DevPackageDiscoveryRoot = root,
            PackageOutputPath = output,
            ProjectDirectory = project,
        }.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        Assert.True(File.Exists(output));
        Assert.False(Directory.Exists(lockPath));
    }

    [Fact]
    public void PackPackage_RejectsPackageThatFailsConsumerFormatValidation()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var devPackage = Path.Combine(root, "sunder-dev");
        Directory.CreateDirectory(Path.Combine(devPackage, "manifest"));
        Directory.CreateDirectory(Path.Combine(devPackage, "payload", "shared", "lib"));
        File.WriteAllText(Path.Combine(devPackage, "manifest", "sunder-package.json"), "{}");
        File.WriteAllText(Path.Combine(devPackage, "payload", "shared", "lib", "Package.dll"), "assembly");
        PackageContentIndexer.Write(devPackage);
        var output = Path.Combine(project, "bin", "package.sunderpkg");
        var buildEngine = new TestBuildEngine();

        Assert.False(new PackSunderPackageTask
        {
            BuildEngine = buildEngine,
            DevPackagePath = devPackage,
            DevPackageDiscoveryRoot = root,
            PackageOutputPath = output,
            ProjectDirectory = project,
        }.Execute());

        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("manifestVersion", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void PackPackage_RejectsNonPackageExtensionWithoutOverwritingFile()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var output = Path.Combine(project, "bin", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, "keep");
        var buildEngine = new TestBuildEngine();

        Assert.False(new PackSunderPackageTask
        {
            BuildEngine = buildEngine,
            DevPackagePath = Path.Combine(root, "missing-dev-package"),
            DevPackageDiscoveryRoot = root,
            PackageOutputPath = output,
            ProjectDirectory = project,
        }.Execute());

        Assert.Contains(buildEngine.Errors, error => error.Contains(".sunderpkg", StringComparison.Ordinal));
        Assert.Equal("keep", File.ReadAllText(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("src")]
    [InlineData("Assets")]
    public void PackPackage_RejectsSourceTreeArchiveWithoutOverwritingFile(string sourceDirectory)
    {
        var project = CreateTempDirectory();
        var output = Path.Combine(project, sourceDirectory, "keep.sunderpkg");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, "keep");
        var buildEngine = new TestBuildEngine();

        Assert.False(new PackSunderPackageTask
        {
            BuildEngine = buildEngine,
            DevPackagePath = Path.Combine(project, "missing-dev-package"),
            DevPackageDiscoveryRoot = project,
            PackageOutputPath = output,
            ProjectDirectory = project,
        }.Execute());

        Assert.Contains(buildEngine.Errors, error => error.Contains("source", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("keep", File.ReadAllText(output));
    }

    [Fact]
    public void PackTask_ValidatesTheWrittenArchiveAfterDeterministicCreation()
    {
        var source = File.ReadAllText(Path.Combine(FindTaskDirectory(), "PackSunderPackageTask.cs"));
        var writeIndex = source.IndexOf(
            "DeterministicPackageArchiveWriter.Write(devPackagePath, stagedPackagePath)",
            StringComparison.Ordinal);
        var archiveValidationIndex = source.IndexOf(
            "ExtractAndValidateAsync(stagedPackagePath, archiveValidationPath)",
            StringComparison.Ordinal);

        Assert.True(writeIndex >= 0, "The deterministic package archive write was not found.");
        Assert.True(
            archiveValidationIndex > writeIndex,
            "The exact written archive must be consumer-validated after deterministic creation.");
    }

    [Fact]
    public void GenerateManifest_IsByteStableAcrossEquivalentExecutions()
    {
        var directory = CreateTempDirectory();
        var firstPath = Path.Combine(directory, "first.json");
        var secondPath = Path.Combine(directory, "second.json");
        var firstEngine = new TestBuildEngine();
        var secondEngine = new TestBuildEngine();

        Assert.True(CreateTask(firstPath, firstEngine).Execute(), string.Join(Environment.NewLine, firstEngine.Errors));
        Assert.True(CreateTask(secondPath, secondEngine).Execute(), string.Join(Environment.NewLine, secondEngine.Errors));

        Assert.Equal(File.ReadAllBytes(firstPath), File.ReadAllBytes(secondPath));
    }

    [Fact]
    public async Task EmitDevOutput_CreatesValidatedCanonicalTreeAndIsolatesNativeRids()
    {
        var root = CreateTempDirectory();
        var targetDirectory = Path.Combine(root, "target");
        var devPackage = Path.Combine(root, "sunder-dev");
        Directory.CreateDirectory(targetDirectory);
        var assemblyPath = Path.Combine(targetDirectory, Path.GetFileName(Assembly.GetExecutingAssembly().Location));
        File.Copy(Assembly.GetExecutingAssembly().Location, assemblyPath);
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
        var workerName = CSharpAppHostName("win-x64");
        var workerPath = Path.Combine(targetDirectory, workerName);
        File.WriteAllText(workerPath, "worker");
        ConfigureWorkerTask(manifestTask, "win-x64", target);
        Assert.True(manifestTask.Execute(), string.Join(Environment.NewLine, manifestEngine.Errors));

        var emitEngine = new TestBuildEngine();
        Assert.True(new EmitSunderDevOutputTask
        {
            BuildEngine = emitEngine,
            ManifestPath = manifestPath,
            DevPackagePath = devPackage,
            DiscoveryRoot = root,
            TargetDirectory = targetDirectory,
            WorkerFiles = [new MSBuildTaskItem(workerPath), new MSBuildTaskItem(winNative)],
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
    }

    [Fact]
    public async Task EmitDevOutput_IndexesPostSigningWorkerBytesAndDetectsLaterTampering()
    {
        var root = CreateTempDirectory();
        var targetDirectory = Path.Combine(root, "target");
        var devPackage = Path.Combine(root, "sunder-dev");
        Directory.CreateDirectory(targetDirectory);
        var rid = RuntimeInformation.RuntimeIdentifier;
        var appHostName = CSharpAppHostName(rid);
        var appHostPath = Path.Combine(targetDirectory, appHostName);
        await File.WriteAllBytesAsync(appHostPath, "apphost::platform-signature"u8.ToArray());
        var manifestPath = Path.Combine(root, "sunder-package.json");
        var manifestEngine = new TestBuildEngine();
        var manifestTask = CreateTask(manifestPath, manifestEngine);
        ConfigureWorkerTask(manifestTask, rid);
        Assert.True(manifestTask.Execute(), string.Join(Environment.NewLine, manifestEngine.Errors));

        var emitEngine = new TestBuildEngine();
        Assert.True(new EmitSunderDevOutputTask
        {
            BuildEngine = emitEngine,
            ManifestPath = manifestPath,
            DevPackagePath = devPackage,
            DiscoveryRoot = root,
            TargetDirectory = targetDirectory,
            WorkerFiles = [new MSBuildTaskItem(appHostPath)],
        }.Execute(), string.Join(Environment.NewLine, emitEngine.Errors));

        var packagedAppHost = Path.Combine(devPackage, "payload", "runtime", rid, "lib", appHostName);
        var index = JsonSerializer.Deserialize<SunderPackageContentIndex>(await File.ReadAllTextAsync(
            Path.Combine(devPackage, "manifest", "content-index.json")))!;
        var entry = Assert.Single(index.Files!, item => item!.Path == $"payload/runtime/{rid}/lib/{appHostName}")!;
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(appHostPath))).ToLowerInvariant(),
            entry.Sha256);

        await File.AppendAllTextAsync(packagedAppHost, "::tampered");
        var tampered = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(devPackage);
        Assert.False(tampered.Success);
        Assert.Contains(tampered.Errors, error =>
            error.Contains($"payload/runtime/{rid}/lib/{appHostName}", StringComparison.Ordinal)
            && error.Contains("mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PackageTargets_ExposeWorkerSigningExtensionAfterPublishCollectionAndBeforeIndexing()
    {
        var targets = File.ReadAllText(Path.Combine(
            FindTaskDirectory(),
            "..",
            "build",
            "Sunder.Package.Build.targets"));

        Assert.Contains("SunderPackageWorkerSigningTargets", targets, StringComparison.Ordinal);
        Assert.Contains("<_SunderWorkerSourceFile Include=\"@(ResolvedFileToPublish)\"", targets, StringComparison.Ordinal);
        Assert.Contains("sunder-worker-signing", targets, StringComparison.Ordinal);
        Assert.Contains("<RemoveDir Directories=\"$(_SunderPackageWorkerSigningDirectory)\"", targets, StringComparison.Ordinal);
        Assert.Contains("DestinationFiles=\"@(_SunderWorkerSourceFile->'$(_SunderPackageWorkerSigningDirectory)%(RelativePath)')\"", targets, StringComparison.Ordinal);
        Assert.Contains("SunderPackageWorkerSigningFile Include=\"@(_SunderWorkerFile)\"", targets, StringComparison.Ordinal);
        Assert.Contains("DependsOnTargets=\"_CollectSunderWorkerFiles;$(SunderPackageWorkerSigningTargets)\"", targets, StringComparison.Ordinal);
        Assert.Contains("GenerateSunderPackageManifest;_RunSunderPackageWorkerSigning;_ValidateSunderDevOutputPath", targets, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageTargets_WorkerSigningMutatesPrivateClosureWithoutChangingSources()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(cache);
        var appHost = Path.Combine(cache, "Worker");
        var runtimeLibrary = Path.Combine(cache, "libSystem.Native.dylib");
        await File.WriteAllTextAsync(appHost, "source-apphost");
        await File.WriteAllTextAsync(runtimeLibrary, "source-runtime");
        var targetsPath = Path.GetFullPath(Path.Combine(
            FindTaskDirectory(),
            "..",
            "build",
            "Sunder.Package.Build.targets"));
        var projectFile = Path.Combine(project, "Signing.proj");
        await File.WriteAllTextAsync(projectFile, $$"""
            <Project>
              <PropertyGroup>
                <IntermediateOutputPath>obj/</IntermediateOutputPath>
                <_SunderHasWorkerTarget>true</_SunderHasWorkerTarget>
                <SunderPackageWorkerSigningTargets>TestSignWorker</SunderPackageWorkerSigningTargets>
                <SunderPackageBuildProject>{{Path.Combine(root, "missing.csproj")}}</SunderPackageBuildProject>
                <SunderPackageBuildTaskAssembly>{{typeof(PackSunderPackageTask).Assembly.Location}}</SunderPackageBuildTaskAssembly>
              </PropertyGroup>
              <Target Name="ComputeFilesToPublish">
                <ItemGroup>
                  <ResolvedFileToPublish Include="{{appHost}}" RelativePath="Worker" />
                  <ResolvedFileToPublish Include="{{runtimeLibrary}}" RelativePath="runtimes/osx-arm64/native/libSystem.Native.dylib" />
                </ItemGroup>
              </Target>
              <Target Name="TestSignWorker">
                <WriteLinesToFile File="%(SunderPackageWorkerSigningFile.Identity)"
                                  Lines="signed"
                                  Overwrite="false"
                                  Encoding="UTF-8" />
              </Target>
              <Target Name="Run" DependsOnTargets="_RunSunderPackageWorkerSigning" />
              <Import Project="{{targetsPath}}" />
            </Project>
            """);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = project,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "msbuild", projectFile, "-t:Run", "-nologo" },
        })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, output + error);
        Assert.Equal("source-apphost", await File.ReadAllTextAsync(appHost));
        Assert.Equal("source-runtime", await File.ReadAllTextAsync(runtimeLibrary));
        var signingRoot = Path.Combine(project, "obj", "sunder-worker-signing");
        Assert.EndsWith("signed" + Environment.NewLine, await File.ReadAllTextAsync(Path.Combine(signingRoot, "Worker")), StringComparison.Ordinal);
        Assert.EndsWith(
            "signed" + Environment.NewLine,
            await File.ReadAllTextAsync(Path.Combine(signingRoot, "runtimes", "osx-arm64", "native", "libSystem.Native.dylib")),
            StringComparison.Ordinal);
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
            var assemblyPath = Path.Combine(target, Path.GetFileName(Assembly.GetExecutingAssembly().Location));
            File.Copy(Assembly.GetExecutingAssembly().Location, assemblyPath);
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

    [Fact]
    public void SharedOutputLock_ReclaimsSameHostPidWithDifferentProcessStart()
    {
        var root = CreateTempDirectory();
        var output = Path.Combine(root, "shared-output");
        using var ownerMarker = CreateOwnerDirectory(
            output + GeneratedOutputLock.LockSuffix,
            Environment.ProcessId,
            Environment.MachineName,
            DateTime.UtcNow.AddYears(-10));

        using var outputLock = GeneratedOutputLock.Acquire([output], TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void SharedOutputLock_SameHostOwnerWithoutProcessMarkerTimesOutWithoutReclamation()
    {
        var root = CreateTempDirectory();
        var output = Path.Combine(root, "shared-output");
        var lockPath = output + GeneratedOutputLock.LockSuffix;
        using var ownerMarker = CreateOwnerDirectory(
            lockPath,
            int.MaxValue,
            Environment.MachineName,
            DateTime.UtcNow.AddYears(-10));
        ownerMarker.Delete();

        Assert.Throws<TimeoutException>(() => GeneratedOutputLock.Acquire([output], TimeSpan.FromMilliseconds(250)));
        Assert.True(Directory.Exists(lockPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SharedOutputLock_LinuxBootOrPidNamespaceMismatchTimesOutWithoutReclamation(bool mismatchBoot)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = CreateTempDirectory();
        var output = Path.Combine(root, "shared-output");
        var lockPath = output + GeneratedOutputLock.LockSuffix;
        using var ownerMarker = CreateOwnerDirectory(
            lockPath,
            int.MaxValue,
            Environment.MachineName,
            DateTime.UtcNow.AddYears(-10),
            linuxBootIdOverride: mismatchBoot ? Guid.NewGuid().ToString("D") : null,
            linuxPidNamespaceOverride: mismatchBoot ? null : "pid:[0]");

        Assert.Throws<TimeoutException>(() => GeneratedOutputLock.Acquire([output], TimeSpan.FromMilliseconds(250)));
        Assert.True(Directory.Exists(lockPath));
    }

    [Fact]
    public void SharedOutputLock_UnknownHostOwnerAndGuardTimeOutWithoutReclamation()
    {
        var root = CreateTempDirectory();
        var output = Path.Combine(root, "shared-output");
        var lockPath = output + GeneratedOutputLock.LockSuffix;
        var unknownHost = Environment.MachineName + ".unknown-host";
        using var ownerMarker = CreateOwnerDirectory(lockPath, Environment.ProcessId, unknownHost, DateTime.UtcNow.AddYears(-10));
        File.SetLastWriteTimeUtc(Path.Combine(lockPath, GeneratedOutputLock.OwnerFileName), DateTime.UtcNow.AddDays(-1));

        Assert.Throws<TimeoutException>(() => GeneratedOutputLock.Acquire([output], TimeSpan.FromMilliseconds(250)));
        Assert.True(Directory.Exists(lockPath));

        Directory.Delete(lockPath, recursive: true);
        var guardPath = lockPath + ".transition";
        using var guardMarker = CreateOwnerDirectory(guardPath, Environment.ProcessId, unknownHost, DateTime.UtcNow.AddYears(-10));
        File.SetLastWriteTimeUtc(Path.Combine(guardPath, GeneratedOutputLock.OwnerFileName), DateTime.UtcNow.AddDays(-1));
        Assert.Throws<TimeoutException>(() => GeneratedOutputLock.Acquire([output], TimeSpan.FromMilliseconds(250)));
        Assert.True(Directory.Exists(guardPath));
    }

    [Fact]
    public async Task SharedOutputLock_RetainsLiveStaleHeartbeatAndSerializesTwoReclaimers()
    {
        var root = CreateTempDirectory();
        var output = Path.Combine(root, "shared-output");
        var holder = AcquireGeneratedOutputLock(output);
        var heartbeatPath = Path.Combine(output + ".sunder-output.lock", "owner.json");
        File.SetLastWriteTimeUtc(heartbeatPath, DateTime.UtcNow.AddMinutes(-10));
        var waiterTask = Task.Run(() => AcquireGeneratedOutputLock(output));
        await Task.Delay(300);
        Assert.False(waiterTask.IsCompleted, "A stale heartbeat must not override a live same-host PID.");
        holder.Dispose();
        using (await waiterTask.WaitAsync(TimeSpan.FromSeconds(5)))
        {
        }

        using var deadOwnerMarker = CreateDeadOwnerLock(output);
        using var guardMarker = CreateOwnerDirectory(
            output + GeneratedOutputLock.LockSuffix + ".transition",
            int.MaxValue,
            Environment.MachineName,
            DateTime.UtcNow.AddMinutes(-1));
        var acquired = new SemaphoreSlim(0, 2);
        var order = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var release = new[] { new ManualResetEventSlim(), new ManualResetEventSlim() };
        var reclaimers = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
        {
            using var outputLock = AcquireGeneratedOutputLock(output);
            order.Enqueue(index);
            acquired.Release();
            release[index].Wait(TimeSpan.FromSeconds(10));
        })).ToArray();
        Assert.True(await acquired.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.Delay(250);
        Assert.Single(order);
        Assert.True(order.TryPeek(out var first));
        release[first].Set();
        Assert.True(await acquired.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, order.Count);
        release[first == 0 ? 1 : 0].Set();
        await Task.WhenAll(reclaimers).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Directory.Exists(output + ".sunder-output.lock"));
        Assert.False(Directory.Exists(output + ".sunder-output.lock.transition"));
        Assert.Single(Directory.EnumerateDirectories(root, "shared-output.sunder-output.lock.transition.stale-*"));
    }

    [Fact]
    public async Task SharedOutputLock_InteroperatesWithNodeProducerWhenAvailable()
    {
        var helper = Path.Combine(
            FindRepositoryRoot(),
            "languages",
            "typescript",
            "node",
            "packages",
            "package-tool",
            "dist-test",
            "test",
            "output-child.js");
        if (!File.Exists(helper) || !CanStartNode()) return;
        var root = CreateTempDirectory();
        var output = Path.Combine(root, "shared-output");
        var ready = Path.Combine(root, "node.ready");
        var release = Path.Combine(root, "node.release");
        var holder = AcquireGeneratedOutputLock(output);
        var node = StartNode(helper, "hold", output, ready, release);
        await Task.Delay(300);
        Assert.False(File.Exists(ready));
        holder.Dispose();
        await WaitForFileAsync(ready, TimeSpan.FromSeconds(5));
        File.WriteAllText(release, "release");
        var result = await ReadProcessAsync(node);
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(Directory.Exists(output + ".sunder-output.lock"));

        using (CreateDeadOwnerLock(output))
        {
            var nodeRecovery = StartNode(helper, "recover", output);
            var recoveryResult = await ReadProcessAsync(nodeRecovery);
            Assert.True(recoveryResult.ExitCode == 0, recoveryResult.Output);
            Assert.False(Directory.Exists(output + ".sunder-output.lock"));
        }

        var crashedReady = Path.Combine(root, "crashed-node.ready");
        var crashedRelease = Path.Combine(root, "crashed-node.release");
        var crashedNode = StartNode(helper, "hold", output, crashedReady, crashedRelease);
        await WaitForFileAsync(crashedReady, TimeSpan.FromSeconds(5));
        using var ownerDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            output + GeneratedOutputLock.LockSuffix,
            GeneratedOutputLock.OwnerFileName)));
        using var crashedNodeMarker = new TemporaryProcessMarker(
            ownerDocument.RootElement.GetProperty("processMarkerPath").GetString()
            ?? throw new InvalidDataException("Node lock owner omitted its process marker path."));
        var reclaimerTask = Task.Run(() => GeneratedOutputLock.Acquire([output], TimeSpan.FromSeconds(5)));
        await Task.Delay(250);
        Assert.False(reclaimerTask.IsCompleted, "A live Node owner must remain ambiguous on inexact process identity.");
        crashedNode.Kill();
        var crashedResult = await ReadProcessAsync(crashedNode);
        Assert.NotEqual(0, crashedResult.ExitCode);
        Assert.True(File.Exists(crashedNodeMarker.Path));
        using var reclaimed = await reclaimerTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AggregateTask_FactorsIdenticalFilesAndProducesDeterministicUniversalTree()
    {
        var root = CreateTempDirectory();
        var appLeaf = CreateCanonicalLeaf(root, "app", "win-x64", "app-native");
        var runtimeLeaf = CreateCanonicalLeaf(root, "runtime", "win-x64", "runtime-native");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var buildEngine = new TestBuildEngine();
        var task = new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(appLeaf), TargetLeafItem(runtimeLeaf)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        };

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        var firstIndex = await File.ReadAllBytesAsync(Path.Combine(output, "manifest", "content-index.json"));
        Assert.True(File.Exists(Path.Combine(output, "payload", "app", "shared", "lib", Path.GetFileName(Assembly.GetExecutingAssembly().Location))));
        Assert.True(File.Exists(Path.Combine(
            output,
            "payload",
            "runtime",
            "win-x64",
            "lib",
            CSharpAppHostName("win-x64"))));
        Assert.True(File.Exists(Path.Combine(output, "payload", "app", "shared", "lib", "runtimes", "win-x64", "native", "fixture.bin")));
        Assert.True(File.Exists(Path.Combine(output, "payload", "runtime", "shared", "lib", "runtimes", "win-x64", "native", "fixture.bin")));
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(output);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        Assert.Equal(2, validation.Manifest!.Targets!.Count);

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        Assert.Equal(firstIndex, await File.ReadAllBytesAsync(Path.Combine(output, "manifest", "content-index.json")));
    }

    [Fact]
    public async Task AggregateTask_UnionsRoleScopedRpcDeclarationsAndDeduplicatesRidLeaves()
    {
        var root = CreateTempDirectory();
        var appWin = CreateRpcLeaf(root, "app", "win-x64", required: false, includeProvider: false);
        var appLinux = CreateRpcLeaf(root, "app", "linux-x64", required: false, includeProvider: false);
        var runtime = CreateRpcLeaf(root, "runtime", "linux-arm64", required: true);
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var buildEngine = new TestBuildEngine();
        var task = new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(runtime), TargetLeafItem(appLinux), TargetLeafItem(appWin)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        };

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(output);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        Assert.Single(validation.Manifest!.ContractBundles!);
        var uses = validation.Manifest.UsesContracts!;
        Assert.Equal(
            ["app", "runtime"],
            uses.Select(static use => use!.Role!).ToArray());
        Assert.False(uses[0]!.Required ?? true);
        Assert.True(uses[1]!.Required == true);
        Assert.Equal("runtime", Assert.Single(validation.Manifest.Provides!)!.Role);
        Assert.All(validation.Manifest.Targets!, targetManifest =>
        {
            Assert.Contains("rpc.v2", targetManifest!.RequiredHostCapabilities!);
            Assert.Contains(SunderSdkCapabilities.CallbacksV1, targetManifest.RequiredHostCapabilities!);
            Assert.DoesNotContain("rpc.v1", targetManifest.RequiredHostCapabilities!);
        });
        Assert.True(File.Exists(Path.Combine(output, "payload", "shared", "contracts", "minimal.rpc.json")));
    }

    [Theory]
    [InlineData("use")]
    [InlineData("provider")]
    public void AggregateTask_RejectsConflictingRoleScopedRpcDeclarations(string declaration)
    {
        var root = CreateTempDirectory();
        var first = declaration == "use"
            ? CreateRpcLeaf(root, "runtime", "linux-x64", required: true)
            : CreateRpcLeaf(root, "runtime", "linux-x64", audience: "owner");
        var second = declaration == "use"
            ? CreateRpcLeaf(root, "runtime", "linux-arm64", required: false)
            : CreateRpcLeaf(root, "runtime", "linux-arm64", audience: "public");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var buildEngine = new TestBuildEngine();

        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(first), TargetLeafItem(second)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        }.Execute());
        Assert.Contains(buildEngine.Errors, error =>
            error.Contains($"conflicting {(declaration == "use" ? "contract use" : "provider")}", StringComparison.Ordinal));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("use")]
    [InlineData("provider")]
    public void AggregateTask_RejectsSameRoleRidRpcDeclarationOmissions(string declaration)
    {
        var root = CreateTempDirectory();
        var complete = CreateRpcLeaf(root, "runtime", "linux-x64");
        var omitted = CreateRpcLeaf(
            root,
            "runtime",
            "linux-arm64",
            includeUse: declaration != "use",
            includeProvider: declaration != "provider");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var buildEngine = new TestBuildEngine();

        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(complete), TargetLeafItem(omitted)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        }.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains(
            $"identical {(declaration == "use" ? "contract use" : "provider")} sets for role 'runtime'",
            StringComparison.Ordinal));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void AggregateTask_RequiresIdenticalPackageWideContractBundles()
    {
        var root = CreateTempDirectory();
        var app = CreateRpcLeaf(root, "app", "win-x64", exposure: "public", includeProvider: false);
        var runtime = CreateRpcLeaf(root, "runtime", "linux-x64", exposure: "package");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var buildEngine = new TestBuildEngine();

        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(app), TargetLeafItem(runtime)],
            OutputPath = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName),
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        }.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains("package-wide manifest metadata", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AggregateTask_WaitsForConfiguredAncestorDiscoveryRootAcrossNestedSwapGap()
    {
        var root = CreateTempDirectory();
        var discoveryRoot = Path.Combine(root, "inputs");
        var leaf = CreateCanonicalLeaf(discoveryRoot, "runtime", "linux-x64", "native");
        var backup = leaf + ".manual-backup-" + Guid.NewGuid().ToString("N");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var buildEngine = new TestBuildEngine();
        var aggregate = new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(leaf, discoveryRoot)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        };
        using var gapLock = GeneratedOutputLock.Acquire(
            [GeneratedOutputLock.TargetLeafDiscoveryKey(discoveryRoot, leaf)],
            []);
        Directory.Move(leaf, backup);
        var aggregateTask = Task.Run(aggregate.Execute);
        try
        {
            await Task.Delay(200);
            Assert.False(aggregateTask.IsCompleted, "Aggregation must not inspect a nested leaf during its discovery-root swap gap.");
            Directory.Move(backup, leaf);
            gapLock.Dispose();
            Assert.True(await aggregateTask.WaitAsync(TimeSpan.FromSeconds(5)), string.Join(Environment.NewLine, buildEngine.Errors));
        }
        finally
        {
            if (Directory.Exists(backup) && !Directory.Exists(leaf)) Directory.Move(backup, leaf);
            gapLock.Dispose();
            try
            {
                await aggregateTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Preserve the primary assertion failure.
            }
        }
    }

    [Fact]
    public async Task AggregateAndPackTasks_SerializeConcurrentMsbuildProcesses()
    {
        var root = CreateTempDirectory();
        var appLeaf = CreateCanonicalLeaf(root, "app", "win-x64", "app-native");
        var runtimeLeaf = CreateCanonicalLeaf(root, "runtime", "win-x64", "runtime-native");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var archive = Path.Combine(project, "bin", "concurrent.sunderpkg");
        Directory.CreateDirectory(project);
        var projectFile = Path.Combine(project, "Concurrent.proj");
        File.WriteAllText(projectFile, $$"""
            <Project>
              <UsingTask TaskName="Sunder.Package.Build.Tasks.AggregateSunderPackageTask"
                         AssemblyFile="{{typeof(AggregateSunderPackageTask).Assembly.Location}}" />
              <UsingTask TaskName="Sunder.Package.Build.Tasks.PackSunderPackageTask"
                         AssemblyFile="{{typeof(PackSunderPackageTask).Assembly.Location}}" />
              <ItemGroup>
                <Leaf Include="{{appLeaf}}" DiscoveryRoot="{{Path.GetDirectoryName(appLeaf)}}" />
                <Leaf Include="{{runtimeLeaf}}" DiscoveryRoot="{{Path.GetDirectoryName(runtimeLeaf)}}" />
              </ItemGroup>
              <Target Name="Run">
                <AggregateSunderPackageTask TargetLeaves="@(Leaf)"
                                             OutputPath="{{output}}"
                                             OutputDiscoveryRoot="{{target}}"
                                             ProjectDirectory="{{project}}"
                                             TargetDirectory="{{target}}"
                                             ExpectedPackageVersion="1.2.3" />
                <PackSunderPackageTask DevPackagePath="{{output}}"
                                       DevPackageDiscoveryRoot="{{target}}"
                                       PackageOutputPath="{{archive}}"
                                       ProjectDirectory="{{project}}" />
              </Target>
            </Project>
            """);

        var first = StartMsbuild(projectFile, project);
        var second = StartMsbuild(projectFile, project);
        var results = await Task.WhenAll(ReadProcessAsync(first), ReadProcessAsync(second));

        Assert.All(results, result => Assert.True(result.ExitCode == 0, result.Output));
        var aggregateValidation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(output);
        Assert.True(aggregateValidation.Success, string.Join(Environment.NewLine, aggregateValidation.Errors));
        var archiveValidation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archive,
            Path.Combine(root, "concurrent-validation"));
        Assert.True(archiveValidation.Success, string.Join(Environment.NewLine, archiveValidation.Errors));
        Assert.Equal(2, archiveValidation.Manifest!.Targets!.Count);
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(target),
            path => Path.GetFileName(path).Contains(".stage-", StringComparison.Ordinal)
                    || Path.GetFileName(path).Contains(".backup-", StringComparison.Ordinal)
                    || Path.GetFileName(path).Contains("transaction", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AggregateTask_RollsBackInterruptedCrossProcessReplacementBeforeReadingInputs()
    {
        var root = CreateTempDirectory();
        var leaf = CreateCanonicalLeaf(root, "runtime", "linux-x64", "native");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var marker = output + AggregateSunderPackageTask.MarkerFileSuffix;
        var firstEngine = new TestBuildEngine();
        Assert.True(new AggregateSunderPackageTask
        {
            BuildEngine = firstEngine,
            TargetLeaves = [TargetLeafItem(leaf)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        }.Execute(), string.Join(Environment.NewLine, firstEngine.Errors));
        var priorIndex = await File.ReadAllBytesAsync(Path.Combine(output, "manifest", "content-index.json"));

        var token = Guid.NewGuid().ToString("D");
        var outputStage = output + ".stage-" + Guid.NewGuid().ToString("N");
        var markerStage = marker + ".stage-" + Guid.NewGuid().ToString("N");
        var outputBackup = output + ".backup-" + token;
        var markerBackup = marker + ".backup-" + token;
        Directory.CreateDirectory(outputStage);
        File.WriteAllText(Path.Combine(outputStage, "replacement.txt"), "replacement");
        File.WriteAllText(markerStage, "replacement marker");
        Directory.Move(output, outputBackup);
        File.Move(marker, markerBackup);
        File.WriteAllText(output + ".sunder-output-transaction.json", JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            token,
            coordinatorPath = output,
            outputs = new[]
            {
                new { finalPath = output, stagedPath = outputStage, backupPath = outputBackup, existed = true },
                new { finalPath = marker, stagedPath = markerStage, backupPath = markerBackup, existed = true },
            },
        }));

        var recoveryEngine = new TestBuildEngine();
        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = recoveryEngine,
            TargetLeaves = [TargetLeafItem(Path.Combine(root, "missing-leaf"))],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        }.Execute());

        Assert.Equal(priorIndex, await File.ReadAllBytesAsync(Path.Combine(output, "manifest", "content-index.json")));
        Assert.True(File.Exists(marker));
        Assert.False(Directory.Exists(outputStage));
        Assert.False(Directory.Exists(outputBackup));
        Assert.False(File.Exists(output + ".sunder-output-transaction.json"));
    }

    [Fact]
    public async Task PackTask_PreservesPriorValidatedArchiveWhenReplacementFails()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var devPackage = Path.Combine(root, "sunder-dev");
        var output = Path.Combine(project, "bin", "preserved.sunderpkg");
        var firstEngine = new TestBuildEngine();
        CreateValidDevPackage(devPackage, firstEngine);
        var first = new PackSunderPackageTask
        {
            BuildEngine = firstEngine,
            DevPackagePath = devPackage,
            DevPackageDiscoveryRoot = root,
            PackageOutputPath = output,
            ProjectDirectory = project,
        };
        Assert.True(first.Execute(), string.Join(Environment.NewLine, firstEngine.Errors));
        var priorBytes = await File.ReadAllBytesAsync(output);

        File.WriteAllText(Path.Combine(devPackage, "manifest", "sunder-package.json"), "{}");
        var secondEngine = new TestBuildEngine();
        Assert.False(new PackSunderPackageTask
        {
            BuildEngine = secondEngine,
            DevPackagePath = devPackage,
            DevPackageDiscoveryRoot = root,
            PackageOutputPath = output,
            ProjectDirectory = project,
        }.Execute());

        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(output));
        var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            output,
            Path.Combine(root, "preserved-validation"));
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
    }

    [Fact]
    public void AggregateTask_RejectsDuplicateExactTargets()
    {
        var root = CreateTempDirectory();
        var leaf = CreateCanonicalLeaf(root, "runtime", "linux-x64", "native");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var buildEngine = new TestBuildEngine();

        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(leaf), TargetLeafItem(leaf)],
            OutputPath = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName),
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        }.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AggregateTask_PreservesWorkerTargetLeavesWithoutForeignRidLeakage()
    {
        var root = CreateTempDirectory();
        var first = CreateProcessLeaf(root, "linux-x64", "linux-sea", "worker");
        var second = CreateProcessLeaf(root, "osx-arm64", "mac-sea", "worker");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var buildEngine = new TestBuildEngine();

        Assert.True(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(first), TargetLeafItem(second)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.0.0",
        }.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));

        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(output);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        Assert.All(validation.Manifest!.Targets!, target => Assert.Equal("worker", target!.Kind));
        var linux = SunderPackageTargetResolver.CreateProjectionPlan(
            validation.Manifest,
            validation.ContentIndex!,
            new SunderPackageTargetKey("runtime", "linux-x64"));
        Assert.Contains(linux.Files, file => file.LogicalPath.ToString() == "bin/worker");
        Assert.DoesNotContain(linux.Files, file => file.PhysicalPath.ToString().Contains("osx-arm64", StringComparison.Ordinal));
    }

    [Fact]
    public void AggregateTask_RejectsLegacyProcessTargetLeaf()
    {
        var root = CreateTempDirectory();
        var leaf = CreateProcessLeaf(root, "linux-x64", "legacy", "process", validate: false);
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var buildEngine = new TestBuildEngine();

        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(leaf)],
            OutputPath = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName),
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.0.0",
        }.Execute());
        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("kind 'process'", StringComparison.Ordinal)
            && error.Contains("invalid for role 'runtime'", StringComparison.Ordinal));
    }

    [Fact]
    public void AggregateTask_RejectsWorkerV1CapabilityLeaf()
    {
        var root = CreateTempDirectory();
        var leaf = CreateProcessLeaf(
            root,
            "linux-x64",
            "legacy",
            "worker",
            validate: false,
            requiredHostCapabilities:
            [
                "rpc.v2",
                "sdk-baseline-1-1.v1",
                "worker-protocol.v1",
                "worker-protocol.v3",
            ]);
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var buildEngine = new TestBuildEngine();

        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(leaf)],
            OutputPath = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName),
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.0.0",
        }.Execute());
        Assert.Contains(
            buildEngine.Errors,
            error => error.Contains(
                "cannot require unsupported worker protocol capability 'worker-protocol.v1'",
                StringComparison.Ordinal));
    }

    [Fact]
    public void AggregateTask_RejectsLeafVersionThatDiffersFromAggregateVersion()
    {
        var root = CreateTempDirectory();
        var leaf = CreateCanonicalLeaf(root, "runtime", "linux-x64", "native");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var buildEngine = new TestBuildEngine();

        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(leaf)],
            OutputPath = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName),
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "9.9.9",
        }.Execute());

        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("aggregate expected version '9.9.9'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("wrong-name")]
    [InlineData("nested")]
    [InlineData("source")]
    public void AggregateTask_RejectsUnsafeGeneratedOutputPath(string kind)
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "aggregate-project");
        var target = kind == "source"
            ? Path.Combine(project, "src", "generated")
            : Path.Combine(project, "bin", "Debug", "net10.0");
        var output = kind switch
        {
            "outside" => Path.Combine(root, ValidateSunderDevOutputPathTask.GeneratedDirectoryName),
            "wrong-name" => Path.Combine(target, "universal"),
            "nested" => Path.Combine(target, "nested", ValidateSunderDevOutputPathTask.GeneratedDirectoryName),
            _ => Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName),
        };
        var buildEngine = new TestBuildEngine();

        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(Path.Combine(root, "unused-leaf"))],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.0.0",
        }.Execute());

        Assert.Contains(buildEngine.Errors, error => error.Contains("unsafe", StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void ManifestGeneration_UsesFocusedCollaboratorsAndSizeRatchets()
    {
        var taskDirectory = FindTaskDirectory();
        var taskPath = Path.Combine(taskDirectory, "GenerateSunderPackageManifestTask.cs");
        var taskSource = File.ReadAllText(taskPath);
        var targetsSource = File.ReadAllText(Path.GetFullPath(Path.Combine(
            taskDirectory,
            "..",
            "build",
            "Sunder.Package.Build.targets")));
        var expectedFiles = new[]
        {
            "PackageMetadataDecoder.cs",
            "PackageDependencyExtractor.cs",
            "PackageCapabilityInference.cs",
            "ManagedAssemblyPath.cs",
            "PackageAuthoredCallSite.cs",
            "SdkCapabilityCatalog.cs",
            "ResolvedSdkPackageVersion.cs",
            "PackageAssetDiscovery.cs",
            "PackageManifestValidator.cs",
            "PackageTargetBuilder.cs",
            "PackageContractBuilder.cs",
        };

        Assert.True(File.ReadLines(taskPath).Count() < 180);
        Assert.False(File.Exists(Path.Combine(taskDirectory, "GenerateSunderPackageManifestTaskAdapter.cs")));
        Assert.DoesNotContain("SdkApiVersion { get; set; }", taskSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SdkVersion { get; set; }", taskSource, StringComparison.Ordinal);
        Assert.Contains(
            "GlobalPropertiesToRemove=\"Version;VersionPrefix;VersionSuffix;PackageVersion;InformationalVersion\"",
            targetsSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("'@(SunderRpcBinding)' != ''", targetsSource, StringComparison.Ordinal);
        Assert.Contains(
            "<Output TaskParameter=\"BindingSnapshots\" ItemName=\"_SunderRpcBindingSnapshot\" />",
            targetsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "BindingSnapshots=\"@(_SunderRpcBindingSnapshot)\"",
            targetsSource,
            StringComparison.Ordinal);
        foreach (var file in expectedFiles)
        {
            Assert.True(File.Exists(Path.Combine(taskDirectory, file)), $"Missing manifest-generation collaborator {file}.");
            Assert.True(File.ReadLines(Path.Combine(taskDirectory, file)).Count() < 950, $"{file} exceeded its size ratchet.");
        }
    }

    private static GenerateSunderPackageManifestTask CreateTask(string manifestPath, IBuildEngine buildEngine)
        => new()
        {
            BuildEngine = buildEngine,
            TargetAssemblyPath = Assembly.GetExecutingAssembly().Location,
            ManifestOutputPath = manifestPath,
            EntryAssembly = Path.GetFileName(Assembly.GetExecutingAssembly().Location),
            PackageVersion = "1.2.3",
            ProjectDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            TargetFramework = "net10.0",
            SdkCapabilities = [new MSBuildTaskItem(SunderSdkCapabilities.CallbacksV1)],
            ReferencePaths = SdkReferencePaths(includeAvalonia: true),
        };

    private static Process StartMsbuild(string projectFile, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectFile);
        startInfo.ArgumentList.Add("-t:Run");
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-nodeReuse:false");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start concurrent MSBuild process.");
    }

    private static async Task<ProcessResult> ReadProcessAsync(Process process)
    {
        using (process)
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new ProcessResult(
                process.ExitCode,
                await standardOutput + Environment.NewLine + await standardError);
        }
    }

    private static IDisposable AcquireGeneratedOutputLock(params string[] paths)
        => GeneratedOutputLock.Acquire(paths);

    private static TemporaryProcessMarker CreateDeadOwnerLock(string output)
        => CreateOwnerDirectory(
            output + GeneratedOutputLock.LockSuffix,
            int.MaxValue,
            Environment.MachineName,
            DateTime.UtcNow.AddMinutes(-1));

    private static TemporaryProcessMarker CreateOwnerDirectory(
        string directory,
        int pid,
        string hostname,
        DateTime processStartedAt,
        string? linuxBootIdOverride = null,
        string? linuxPidNamespaceOverride = null)
    {
        var platform = OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsWindows() ? "win32"
            : OperatingSystem.IsMacOS() ? "darwin"
            : throw new PlatformNotSupportedException();
        string? linuxBootId = null;
        string? linuxPidNamespace = null;
        if (OperatingSystem.IsLinux())
        {
            linuxBootId = linuxBootIdOverride
                ?? File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim().ToLowerInvariant();
            linuxPidNamespace = linuxPidNamespaceOverride
                ?? new FileInfo("/proc/self/ns/pid").LinkTarget
                ?? throw new InvalidDataException("Linux PID namespace identity is unavailable.");
        }
        var processStartedAtValue = processStartedAt.ToUniversalTime().ToString("O");
        var processMarkerToken = Guid.NewGuid().ToString("D");
        var processMarkerRoot = Path.Combine(Path.GetTempPath(), "sunder-generated-output", "processes");
        Directory.CreateDirectory(processMarkerRoot);
        var processMarkerPath = Path.Combine(processMarkerRoot, processMarkerToken + ".json");
        File.WriteAllText(processMarkerPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            token = processMarkerToken,
            pid,
            hostname,
            processStartedAt = processStartedAtValue,
            platform,
            linuxBootId,
            linuxPidNamespace,
            createdAt = DateTime.UtcNow,
        }));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, GeneratedOutputLock.OwnerFileName), JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            token = Guid.NewGuid().ToString("D"),
            pid,
            hostname,
            processStartedAt = processStartedAtValue,
            platform,
            processMarkerPath,
            processMarkerToken,
            linuxBootId,
            linuxPidNamespace,
            acquiredAt = DateTime.UtcNow.AddMinutes(-1),
        }));
        return new TemporaryProcessMarker(processMarkerPath);
    }

    private static bool CanStartNode()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "--version" },
            });
            if (process is null) return false;
            process.WaitForExit(5_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Process StartNode(string helper, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = FindRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(helper);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Node lock helper.");
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Timed out waiting for '{path}'.");
            await Task.Delay(25);
        }
    }

    private static void CreateValidDevPackage(string devPackage, TestBuildEngine buildEngine)
    {
        Directory.CreateDirectory(Path.Combine(devPackage, "manifest"));
        Directory.CreateDirectory(Path.Combine(devPackage, "payload", "shared", "lib"));
        var manifestPath = Path.Combine(devPackage, "manifest", "sunder-package.json");
        Assert.True(
            CreateTask(manifestPath, buildEngine).Execute(),
            string.Join(Environment.NewLine, buildEngine.Errors));
        File.Copy(
            Assembly.GetExecutingAssembly().Location,
            Path.Combine(devPackage, "payload", "shared", "lib", Path.GetFileName(Assembly.GetExecutingAssembly().Location)));
        PackageContentIndexer.Write(devPackage);
    }

    private static string CreateCanonicalLeaf(string root, string role, string rid, string nativeContent)
    {
        var leafRoot = Path.Combine(root, role + "-" + rid + "-leaf");
        var targetDirectory = Path.Combine(leafRoot, "target");
        var devPackage = Path.Combine(leafRoot, "sunder-dev");
        Directory.CreateDirectory(targetDirectory);
        var assemblyPath = Path.Combine(targetDirectory, Path.GetFileName(Assembly.GetExecutingAssembly().Location));
        File.Copy(Assembly.GetExecutingAssembly().Location, assemblyPath);
        var nativePath = Path.Combine(targetDirectory, "runtimes", rid, "native", "fixture.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(nativePath)!);
        File.WriteAllText(nativePath, nativeContent);
        var manifestPath = Path.Combine(leafRoot, "sunder-package.json");
        var manifestEngine = new TestBuildEngine();
        var manifestTask = CreateTask(manifestPath, manifestEngine);
        var target = new MSBuildTaskItem(role + "/" + rid);
        target.SetMetadata("Role", role);
        target.SetMetadata("Rid", rid);
        manifestTask.PackageTargets = [target];
        MSBuildTaskItem[] workerFiles = [];
        if (role == SunderPackageFormat.RuntimeHostRole)
        {
            var executableName = CSharpAppHostName(rid);
            var executablePath = Path.Combine(targetDirectory, executableName);
            File.WriteAllText(executablePath, "worker");
            target.SetMetadata("Kind", SunderPackageFormat.WorkerTargetKind);
            ConfigureWorkerTask(manifestTask, rid, target);
            workerFiles = [new MSBuildTaskItem(executablePath), new MSBuildTaskItem(nativePath)];
        }
        Assert.True(manifestTask.Execute(), string.Join(Environment.NewLine, manifestEngine.Errors));
        var emitEngine = new TestBuildEngine();
        Assert.True(new EmitSunderDevOutputTask
        {
            BuildEngine = emitEngine,
            ManifestPath = manifestPath,
            DevPackagePath = devPackage,
            DiscoveryRoot = leafRoot,
            TargetDirectory = targetDirectory,
            ManagedFiles = role == SunderPackageFormat.AppHostRole ? [new MSBuildTaskItem(assemblyPath)] : [],
            NativeRuntimeFiles = role == SunderPackageFormat.AppHostRole ? [new MSBuildTaskItem(nativePath)] : [],
            WorkerFiles = workerFiles,
        }.Execute(), string.Join(Environment.NewLine, emitEngine.Errors));
        return devPackage;
    }

    private static string CreateRpcLeaf(
        string root,
        string role,
        string rid,
        bool required = true,
        string audience = "owner",
        string exposure = "public",
        bool includeUse = true,
        bool includeProvider = true)
    {
        var leafRoot = Path.Combine(root, $"rpc-{role}-{rid}-leaf");
        var targetDirectory = Path.Combine(leafRoot, "target");
        var devPackage = Path.Combine(leafRoot, "sunder-dev");
        Directory.CreateDirectory(targetDirectory);
        var assemblyPath = Path.Combine(targetDirectory, Path.GetFileName(Assembly.GetExecutingAssembly().Location));
        File.Copy(Assembly.GetExecutingAssembly().Location, assemblyPath);
        var descriptorPath = WriteMinimalV2Descriptor(leafRoot);
        var manifestPath = Path.Combine(leafRoot, "sunder-package.json");
        var manifestEngine = new TestBuildEngine();
        var manifestTask = CreateTask(manifestPath, manifestEngine);
        manifestTask.ProjectDirectory = leafRoot;
        var target = new MSBuildTaskItem(role + "/" + rid);
        manifestTask.PackageTargets = [target];
        MSBuildTaskItem[] workerFiles = [];
        if (role == SunderPackageFormat.RuntimeHostRole)
        {
            var executableName = CSharpAppHostName(rid);
            var executablePath = Path.Combine(targetDirectory, executableName);
            File.WriteAllText(executablePath, "worker");
            target.SetMetadata("Kind", SunderPackageFormat.WorkerTargetKind);
            ConfigureWorkerTask(manifestTask, rid, target);
            workerFiles = [new MSBuildTaskItem(executablePath)];
        }
        manifestTask.ContractBundles = [ContractBundle(descriptorPath, exposure)];
        manifestTask.UsesContracts = includeUse ? [ContractUse(role, required: required)] : [];
        manifestTask.RpcProviders = includeProvider ? [RpcProvider(role, audience: audience)] : [];
        Assert.True(manifestTask.Execute(), string.Join(Environment.NewLine, manifestEngine.Errors));
        var emitEngine = new TestBuildEngine();
        Assert.True(new EmitSunderDevOutputTask
        {
            BuildEngine = emitEngine,
            ManifestPath = manifestPath,
            DevPackagePath = devPackage,
            DiscoveryRoot = leafRoot,
            TargetDirectory = targetDirectory,
            ManagedFiles = role == SunderPackageFormat.AppHostRole ? [new MSBuildTaskItem(assemblyPath)] : [],
            WorkerFiles = workerFiles,
            ContractFiles = manifestTask.ResolvedContractFiles,
        }.Execute(), string.Join(Environment.NewLine, emitEngine.Errors));
        return devPackage;
    }

    private static MSBuildTaskItem TargetLeafItem(string path, string? discoveryRoot = null)
    {
        var item = new MSBuildTaskItem(path);
        item.SetMetadata("DiscoveryRoot", discoveryRoot ?? Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Target leaf '{path}' has no discovery root."));
        return item;
    }

    private static string CreateProcessLeaf(
        string root,
        string rid,
        string executableContent,
        string kind,
        bool validate = true,
        IReadOnlyList<string?>? requiredHostCapabilities = null)
    {
        var leaf = Path.Combine(root, "process-" + rid);
        var manifestPath = Path.Combine(leaf, "manifest", "sunder-package.json");
        var executablePath = Path.Combine(leaf, "payload", "runtime", rid, "bin", "worker");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllText(executablePath, executableContent);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new SunderPackageManifest
        {
            ArchiveFormatVersion = 1,
            ManifestVersion = 3,
            Id = "test.process",
            Name = "Test Process",
            Version = "1.0.0",
            DependsOn = [],
            Targets =
            [
                new SunderPackageTargetManifest
                {
                    Role = "runtime",
                    Rid = rid,
                    Kind = kind,
                    EntryPoint = "bin/worker",
                    TargetFramework = "node24",
                    SdkVersion = "1.1.0",
                    RequiredHostCapabilities = requiredHostCapabilities
                        ?? ["rpc.v2", "sdk-baseline-1-1.v1", "worker-protocol.v3"],
                },
            ],
            ContractBundles = [],
            UsesContracts = [],
            Provides = [],
        }));
        PackageContentIndexer.Write(leaf);
        if (validate)
        {
            var validation = SunderPackageArchiveInspector.ValidateExtractedPackageAsync(leaf).GetAwaiter().GetResult();
            Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        }
        return leaf;
    }

    private static GenerateSunderPackageManifestTask CreateFixtureTask(
        string assemblyPath,
        string manifestPath,
        IBuildEngine buildEngine)
    {
        var task = new GenerateSunderPackageManifestTask
        {
            BuildEngine = buildEngine,
            TargetAssemblyPath = assemblyPath,
            ManifestOutputPath = manifestPath,
            EntryAssembly = Path.GetFileName(assemblyPath),
            PackageVersion = "1.2.3",
            ProjectDirectory = Path.GetDirectoryName(assemblyPath)!,
            TargetFramework = "net10.0",
        };
        ConfigureWorkerTask(task, "linux-x64");
        return task;
    }

    private static void ConfigureWorkerTask(
        GenerateSunderPackageManifestTask task,
        string rid,
        MSBuildTaskItem? target = null)
    {
        target ??= new MSBuildTaskItem("runtime/" + rid);
        target.SetMetadata("Role", SunderPackageFormat.RuntimeHostRole);
        target.SetMetadata("Rid", rid);
        target.SetMetadata("Kind", SunderPackageFormat.WorkerTargetKind);
        task.PackageTargets = [target];
        task.RuntimeIdentifier = rid;
        task.OutputType = "Exe";
        task.SelfContained = true;
        task.UseAppHost = true;
        task.SdkCapabilities = task.SdkCapabilities
            .Append(new MSBuildTaskItem("worker-protocol.v3"))
            .ToArray();
    }

    private static string CSharpAppHostName(string rid)
        => Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location)
           + (rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty);

    private static MSBuildTaskItem[] SdkReferencePaths(bool includeAvalonia = false)
        => (includeAvalonia
                ? new[] { typeof(SunderPackageAttribute).Assembly.Location, typeof(IAvaloniaPackageContributionRegistry).Assembly.Location }
                : new[] { typeof(SunderPackageAttribute).Assembly.Location })
            .Select(static path => new MSBuildTaskItem(path))
            .ToArray();

    private static string[] GetAssemblyCompanionPaths(string assemblyPath)
    {
        var paths = new[] { Path.ChangeExtension(assemblyPath, ".pdb"), Path.ChangeExtension(assemblyPath, ".xml") };
        Assert.All(paths, path => Assert.True(File.Exists(path), $"Assembly companion was not built: {path}"));
        return paths;
    }

    private static MSBuildTaskItem[] DynamicAccessAcknowledgements()
        =>
        [
            new("Sunder.Package.Build.Tests.Fixtures.AuthoredDynamicReflection.AuthoredSdkDynamicReflection.ResolveSdkContract"),
            new("Sunder.Package.Build.Tests.Fixtures.AuthoredDynamicReflection.AuthoredSdkDynamicReflection.ResolveSdkContractAsync"),
            new("Sunder.Package.Build.Tests.Fixtures.AuthoredDynamicReflection.AuthoredSdkDynamicReflection.CreateSdkContractResolver"),
            new("Sunder.Package.Build.Tests.Fixtures.AuthoredDynamicReflection.AuthoredSdkDynamicReflection.ResolveSdkContracts"),
        ];

    private static string CopyFixtureAssemblyToIsolatedDirectory(string fixtureName)
    {
        var sourcePath = GetFixtureAssemblyPath(fixtureName);
        Assert.True(File.Exists(sourcePath), $"Fixture assembly was not built: {sourcePath}");
        var destinationPath = Path.Combine(CreateTempDirectory(), Path.GetFileName(sourcePath));
        File.Copy(sourcePath, destinationPath);
        return destinationPath;
    }

    private static string CopyAssemblyWithIncompatibleMachine(string sourcePath)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var peHeaderOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3c, sizeof(int)));
        var machine = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? (ushort)0x8664
            : (ushort)0xaa64;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peHeaderOffset + 4, sizeof(ushort)), machine);
        var destinationPath = Path.Combine(CreateTempDirectory(), Path.GetFileName(sourcePath));
        File.WriteAllBytes(destinationPath, bytes);
        return destinationPath;
    }

    private static string GetFixtureAssemblyPath(string fixtureName)
        => Path.Combine(
            FindRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "tests",
            "Sunder.Package.Build.TestFixtures",
            fixtureName,
            "bin",
            ResolveConfiguration(),
            "net10.0",
            fixtureName + ".dll");

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

    private static HashSet<string> ReadCapabilities(JsonElement root)
        => root.GetProperty("targets")
            .EnumerateArray()
            .SelectMany(static target => target.GetProperty("requiredHostCapabilities").EnumerateArray())
            .Select(static capability => capability.GetString()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void AssertContainsCapabilities(HashSet<string> actual, params string[] expected)
    {
        foreach (var capability in expected)
        {
            Assert.Contains(capability, actual);
        }
    }

    private static string WriteMinimalV2Descriptor(string root, string version = "2.0.0")
    {
        var path = Path.Combine(root, "minimal.rpc.json");
        File.WriteAllText(
            path,
            MinimalV2Descriptor.Replace(
                "\"version\": \"2.0.0\"",
                $"\"version\": \"{version}\"",
                StringComparison.Ordinal));
        return path;
    }

    private static MSBuildTaskItem ContractBundle(
        string descriptorPath,
        string exposure = "public",
        string version = "2.0.0")
    {
        var item = new MSBuildTaskItem(descriptorPath);
        item.SetMetadata("ContractId", "example.rpc.v2.minimal");
        item.SetMetadata("Version", version);
        item.SetMetadata("DescriptorPath", "contracts/minimal.rpc.json");
        item.SetMetadata("Exposure", exposure);
        return item;
    }

    private static MSBuildTaskItem ContractUse(
        string role,
        string version = "2.0.0",
        bool required = true)
    {
        var item = new MSBuildTaskItem("example.rpc.v2.minimal");
        item.SetMetadata("Role", role);
        item.SetMetadata("ContractVersion", version);
        item.SetMetadata("Required", required ? "true" : "false");
        item.SetMetadata("Actions", "discover;invoke");
        return item;
    }

    private static MSBuildTaskItem RpcProvider(
        string role,
        string version = "2.0.0",
        string audience = "owner")
    {
        var item = new MSBuildTaskItem("test.rpc.provider");
        item.SetMetadata("Role", role);
        item.SetMetadata("ContractId", "example.rpc.v2.minimal");
        item.SetMetadata("ContractVersion", version);
        item.SetMetadata("Audience", audience);
        return item;
    }

    private static void WriteSnapshotDescriptor(string path, string propertyName)
        => File.WriteAllText(path, """
            {
              "descriptorVersion":2,
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

    private const string MinimalV2DescriptorSha256 = "9455eede5aa70c5e06cd83954dfd16a51910e2a84b00f57fbe3a40c48a1f799a";
    private const string MinimalV2AbiSha256 = "c9bc1aff363a952397d09a842443805a50f7873d6a56d5521de78bd1f479fc65";
    private const string MinimalV2Descriptor = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "descriptorVersion": 2,
          "schemaProfile": "sunder.rpc.values.v1",
          "contractId": "example.rpc.v2.minimal",
          "version": "2.0.0",
          "errors": {},
          "services": {
            "example": {
              "methods": {
                "ping": {
                  "kind": "unary",
                  "idempotency": "idempotent",
                  "errors": [],
                  "requestSchema": { "$ref": "#/$defs/Empty" },
                  "responseSchema": { "$ref": "#/$defs/Empty" }
                }
              }
            }
          },
          "$defs": {
            "Empty": {
              "type": "object",
              "properties": {},
              "required": [],
              "additionalProperties": false,
              "title": "Empty value"
            }
          }
        }
        """;

    private static string CreateTempDirectory()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (OperatingSystem.IsMacOS() && tempRoot.StartsWith("/var/", StringComparison.Ordinal))
        {
            tempRoot = "/private" + tempRoot;
        }
        var path = Path.Combine(tempRoot, "sunder-package-build-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindTaskDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "languages",
                "csharp",
                "dotnet",
                "packages",
                "Sunder.Package.Build",
                "Tasks");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate Sunder.Package.Build tasks.");
    }

    private sealed class TestBuildEngine : IBuildEngine
    {
        public List<string> Errors { get; } = [];

        public bool ContinueOnError => false;

        public int LineNumberOfTaskNode => 0;

        public int ColumnNumberOfTaskNode => 0;

        public string ProjectFileOfTaskNode => string.Empty;

        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs)
            => true;

        public void LogCustomEvent(CustomBuildEventArgs e)
        {
        }

        public void LogErrorEvent(BuildErrorEventArgs e)
            => Errors.Add(e.Message ?? string.Empty);

        public void LogMessageEvent(BuildMessageEventArgs e)
        {
        }

        public void LogWarningEvent(BuildWarningEventArgs e)
        {
        }
    }

    private sealed class TemporaryProcessMarker(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Delete()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }

        public void Dispose() => Delete();
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}

public sealed class FixturePackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        _ = context.Settings;
        _ = context.Storage.State;
        _ = context.Secrets;
        _ = context.Logging.Events;
        services.AddSingleton<IPackageCallbackHandler, FixtureAuthHandler>();
        services.AddSingleton<IPackageAuthHandler, FixtureAuthHandler>();
        services.AddSingleton<IPackageNotificationService, NullPackageNotificationService>();
        services.AddSingleton<FixtureStackContributor>();
        services.AddSingleton(new PackageSettingsSchema(
            null,
            [new PackageSettingsSection(
                "general",
                "General",
                null,
                [new PackageSettingsField("enabled", "Enabled", PackageSettingsFieldKind.Boolean)])]));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        _ = Type.GetType("Sunder.Sdk.Callbacks.PackageCallbackStartContext, Sunder.Sdk");
        registry.RegisterPackageView<FixtureView>(new PackageViewRegistration("fixture.view", "Fixture View"));
        registry.RegisterSettingsView<FixtureSettingsView>();
    }
}

public sealed class FixtureView : UserControl;

public sealed class FixtureSettingsView : UserControl;

public sealed class FixtureStackContributor : IPackageStackExporter, IPackageStackImporter
{
    public string ContributorId => "test.package.build.fixture.stacks";

    public string DisplayName => "Fixture Stacks";

    public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
        StackExportDiscoveryContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<StackExportItemDescriptor>>(
            [new StackExportItemDescriptor("fixture", "Fixture", "fixture")]);

    public ValueTask<StackExportContribution> ExportAsync(
        StackExportRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new StackExportContribution([], [], []));

    public ValueTask<StackImportPreview> PreviewImportAsync(
        StackImportPreviewRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new StackImportPreview([], [], [], []));

    public ValueTask<StackImportResult> ImportAsync(
        StackImportRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new StackImportResult(StackImportOutcome.Completed, [], new Dictionary<string, string>(), [], []));
}

public sealed class FixtureAuthHandler : IPackageAuthHandler
{
    public ValueTask<PackageAuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new PackageAuthStatus(
            "test.package.build.fixture",
            PackageAuthStatusKind.NotConnected,
            "Not connected.",
            CanAuthorize: true,
            CanDisconnect: false));

    public Task<PackageAuthSessionStartResult?> StartAuthorizationAsync(PackageAuthSessionStartContext context, CancellationToken cancellationToken = default)
        => Task.FromResult<PackageAuthSessionStartResult?>(new PackageAuthSessionStartResult(
            "test.package.build.fixture",
            context.AuthSessionId,
            PackageAuthFlowKind.Browser,
            "https://example.test/auth",
            "Authorize."));

    public Task<PackageAuthStatus> CompleteAuthorizationAsync(PackageAuthSessionCompletionContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(new PackageAuthStatus(
            "test.package.build.fixture",
            PackageAuthStatusKind.Connected,
            "Connected.",
            CanAuthorize: false,
            CanDisconnect: true));

    public Task<PackageAuthStatus> DisconnectAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new PackageAuthStatus(
            "test.package.build.fixture",
            PackageAuthStatusKind.NotConnected,
            "Disconnected.",
            CanAuthorize: true,
            CanDisconnect: false));
}

public sealed class FixtureShellViewConsumer(IPackageShellViewService shellViewService)
{
    public IPackageShellViewService ShellViewService { get; } = shellViewService;

    public IReadOnlyList<string> ThemeKeys => SunderThemeKeys.BrushKeys;
}

public sealed class FixturePreparedNavigationConsumer(IPackageViewNavigationPreparationTarget target)
{
    public IPackageViewNavigationPreparationTarget Target { get; } = target;
}

public sealed class FixturePackageActivationCapabilities(IPackageRoleLocalWorkspace roleLocalWorkspace)
{
    public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; } = roleLocalWorkspace;
}

public sealed class FixtureNewSdkCapabilities(IPackageStorageKeyMigrator storageKeyMigrator)
{
    public IPackageStorageKeyMigrator StorageKeyMigrator { get; } = storageKeyMigrator;

    public string StorageKey { get; } = PackageStorageKeyFactory.Create("fixture", 1, "key");

    public PackageStorageKeyMigration StorageMigration { get; }
        = PackageStorageKeyMigration.Exact("legacy", "fixture.v1.key");

}
