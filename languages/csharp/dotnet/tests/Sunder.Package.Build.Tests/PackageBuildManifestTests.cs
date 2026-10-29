using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Microsoft.Build.Framework;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Core.TestSupport;
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
using Sunder.Sdk.Worker;
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

public sealed partial class PackageBuildManifestTests
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
        Assert.Equal(1, root.GetProperty("manifestVersion").GetInt32());
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
        Assert.Empty(root.GetProperty("packageArtifactImports").EnumerateArray());
        Assert.Empty(root.GetProperty("packageApiImports").EnumerateArray());
        Assert.Empty(root.GetProperty("providerImports").EnumerateArray());
        Assert.Empty(root.GetProperty("platformImports").EnumerateArray());
        Assert.False(root.TryGetProperty("entryAssembly", out _));
        Assert.False(root.TryGetProperty("hostRoles", out _));
        Assert.False(root.TryGetProperty("sdkApiVersion", out _));
        Assert.False(root.TryGetProperty("sdkPackageVersion", out _));
        Assert.False(root.TryGetProperty("requiredSdkCapabilities", out _));
        var dependency = Assert.Single(root.GetProperty("dependsOn").EnumerateArray());
        Assert.Equal("test.dependency", dependency.GetProperty("packageId").GetString());
        Assert.Equal(">=1.2.3 <2.0.0", dependency.GetProperty("versionRange").GetString());
        Assert.Equal("required", dependency.GetProperty("presence").GetString());
        var capabilities = ReadCapabilities(root);
        Assert.Equal(new[]
        {
            SunderSdkCapabilities.Baseline11V1,
            SunderSdkCapabilities.CoreV1,
            SunderSdkCapabilities.PackagingV1,
            SunderSdkCapabilities.ContributionsV1,
            SunderSdkCapabilities.ViewsV1,
            SunderSdkCapabilities.SettingsViewsV1,
            SunderSdkCapabilities.SettingsNavigationV1,
            SunderSdkCapabilities.SettingsSchemaV1,
            SunderSdkCapabilities.SettingsV1,
            SunderSdkCapabilities.StorageV1,
            SunderSdkCapabilities.StorageKeyMigrationV1,
            SunderSdkCapabilities.RoleLocalWorkspaceV1,
            SunderSdkCapabilities.SecretsV1,
            SunderSdkCapabilities.LoggingV1,
            SunderSdkCapabilities.NotificationsV1,
            SunderSdkCapabilities.BackgroundProcessesV1,
            SunderSdkCapabilities.RpcV1,
            // The dispatch-evidence fixture calls this member-gated App API.
            SunderSdkCapabilities.WorkerExecutionObservationsV1,
            SunderSdkCapabilities.ShellViewV1,
            SunderSdkCapabilities.ViewNavigationPreparationV1,
            SunderSdkCapabilities.CallbacksV1,
            SunderSdkCapabilities.AuthV1,
            SunderSdkCapabilities.StacksV1,
            SunderSdkCapabilities.StacksRpcV1,
            SunderSdkCapabilities.ThemingV1,
            // The role-projection fixture now exercises owned Worker composition.
            SunderSdkCapabilities.BackgroundServicesV1,
            SunderSdkCapabilities.RuntimeGenerationsV1,
            Sunder.Sdk.Worker.SunderWorkerCapabilities.ProtocolV1,
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
            new MSBuildTaskItem("worker-protocol.v1"),
        ];
        task.ReferencePaths =
        [
            new MSBuildTaskItem(typeof(SunderPackageAttribute).Assembly.Location),
            new MSBuildTaskItem(typeof(IAvaloniaPackageContributionRegistry).Assembly.Location),
            new MSBuildTaskItem(typeof(IPackageStackExporter).Assembly.Location),
            new MSBuildTaskItem(typeof(SunderWorkerProviderRegistration).Assembly.Location),
        ];

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Equal("test.package.build.fixture", document.RootElement.GetProperty("id").GetString());
        var dependency = Assert.Single(document.RootElement.GetProperty("dependsOn").EnumerateArray());
        Assert.Equal("test.dependency", dependency.GetProperty("packageId").GetString());
        Assert.Equal("required", dependency.GetProperty("presence").GetString());
    }

    [Fact]
    public void PackageDependencyExtractor_EmitsExplicitRequiredPresenceForSdkAttributes()
    {
        var dependency = Assert.Single(new PackageDependencyExtractor("1.2.3").Extract(
        [
            new PackageCustomAttribute(
                typeof(SunderPackageDependencyAttribute).FullName!,
                new Dictionary<string, string?>
                {
                    [nameof(SunderPackageDependencyAttribute.PackageId)] = "test.dependency",
                    [nameof(SunderPackageDependencyAttribute.VersionRange)] = ">=$(PackageVersion) <$(NextPackageMajorVersion).0.0",
                }),
        ]));

        Assert.Equal("test.dependency", dependency.PackageId);
        Assert.Equal(">=1.2.3 <2.0.0", dependency.VersionRange);
        Assert.Equal("required", dependency.Presence);
    }

    [Fact]
    public void GenerateManifest_InfersStackCapabilitiesFromSeparateSdkAssembly()
    {
        Assert.Equal("Sunder.Sdk.Stacks", typeof(IPackageStackExporter).Assembly.GetName().Name);
        Assert.NotEqual(typeof(SunderPackageAttribute).Assembly, typeof(IPackageStackExporter).Assembly);

        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        Assert.Contains(
            task.ReferencePaths,
            reference => Path.GetFileNameWithoutExtension(reference.ItemSpec) == "Sunder.Sdk.Stacks");

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
    public void SdkCapabilityCatalog_SeparatesResolutionFromDeclaredCapabilities()
    {
        var diagnostics = new List<string>();
        var catalog = SdkCapabilityCatalog.Create(
            [GetFixtureAssemblyPath("SourceReferenceSdk", "Sunder.Sdk")],
            diagnostics);

        Assert.Empty(diagnostics);
        var annotated = catalog.ResolveType(
            "Sunder.Sdk",
            "Sunder.Sdk.Abstractions",
            "IPackageStorageKeyMigrator");
        Assert.True(annotated.IsResolved);
        Assert.Equal(
            new[] { SunderSdkCapabilities.StorageV1, SunderSdkCapabilities.StorageKeyMigrationV1 }
                .Order(StringComparer.Ordinal),
            annotated.DeclaredCapabilities.Order(StringComparer.Ordinal));

        var unannotatedType = catalog.ResolveType(
            "Sunder.Sdk",
            "Sunder.Sdk.Abstractions",
            "IPackageSettings");
        Assert.True(unannotatedType.IsResolved);
        Assert.Empty(unannotatedType.DeclaredCapabilities);

        var unannotatedMember = catalog.ResolveMember(
            "Sunder.Sdk",
            "Sunder.Sdk.Abstractions",
            "IPackageContext",
            "get_Settings");
        Assert.True(unannotatedMember.IsResolved);
        Assert.Empty(unannotatedMember.DeclaredCapabilities);

        var unresolved = catalog.ResolveType(
            "Sunder.Sdk",
            "Sunder.Sdk.Abstractions",
            "MissingContract");
        Assert.False(unresolved.IsResolved);
        Assert.Empty(unresolved.DeclaredCapabilities);
    }

    [Theory]
    [InlineData("IPackageSettingsNavigationService", SunderSdkCapabilities.SettingsNavigationV1)]
    [InlineData("IPackageDevelopmentService", SunderSdkCapabilities.PackageDevelopmentV1)]
    [InlineData("IPackageLogNavigationService", SunderSdkCapabilities.PackageLogNavigationV1)]
    [InlineData("IPackageAppStateUpdates", SunderSdkCapabilities.AppStateUpdatesV1)]
    [InlineData("IBackgroundTaskQueue", SunderSdkCapabilities.BackgroundProcessesV1)]
    public void SdkCapabilityCatalog_ResolvesCurrentAnnotatedServiceCapabilities(
        string typeName,
        string expectedCapability)
    {
        var diagnostics = new List<string>();
        var catalog = SdkCapabilityCatalog.Create(
            [typeof(SunderPackageAttribute).Assembly.Location],
            diagnostics);

        var resolved = catalog.ResolveType(
            "Sunder.Sdk",
            "Sunder.Sdk.Abstractions",
            typeName);

        Assert.Empty(diagnostics);
        Assert.True(resolved.IsResolved);
        Assert.Contains(expectedCapability, resolved.DeclaredCapabilities);
    }

    [Fact]
    public void CapabilityInference_DoesNotUseNameFallbackForResolvedUnannotatedContracts()
    {
        var assemblyPath = GetFixtureAssemblyPath("SourceReferencePackage");
        var inference = new PackageCapabilityInference(
            [],
            [GetFixtureAssemblyPath("SourceReferenceSdk", "Sunder.Sdk")],
            [],
            [],
            [],
            Path.GetDirectoryName(assemblyPath)!);

        var capabilities = inference.Infer(assemblyPath);

        Assert.True(inference.IsComplete, string.Join(Environment.NewLine, inference.Diagnostics));
        Assert.Empty(inference.Diagnostics);
        Assert.DoesNotContain(SunderSdkCapabilities.SettingsV1, capabilities);
    }

    [Fact]
    public void CapabilityInference_ReportsUnresolvedSdkContractsOnceInDeterministicOrder()
    {
        var assemblyPath = GetFixtureAssemblyPath("SourceReferencePackage");
        var inference = new PackageCapabilityInference(
            [],
            [typeof(SunderPackageAttribute).Assembly.Location],
            [],
            [],
            [],
            Path.GetDirectoryName(assemblyPath)!);

        _ = inference.Infer(assemblyPath);

        Assert.False(inference.IsComplete);
        Assert.Equal(
            [
                "Referenced Sunder SDK type contract 'Sunder.Sdk.Abstractions.UnresolvedSdkContract, Sunder.Sdk' could not be resolved from SDK capability annotations; capability inference cannot safely determine its SDK usage.",
                "Referenced Sunder SDK member contract 'Sunder.Sdk.Abstractions.IPackageContext::get_UnresolvedMember, Sunder.Sdk' could not be resolved from SDK capability annotations; capability inference cannot safely determine its SDK usage.",
            ],
            inference.Diagnostics);
    }

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
    public void GenerateManifest_AuthorsOneExactFrameworkDependentWorkerTarget()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        var target = new MSBuildTaskItem("runtime/linux-arm64");
        target.SetMetadata("Kind", SunderPackageFormat.WorkerTargetKind);
        task.PackageTargets = [target];
        task.RuntimeIdentifier = "linux-arm64";
        task.OutputType = "Exe";
        task.SelfContained = false;
        task.UseAppHost = false;
        task.PublishSingleFile = false;
        task.PublishTrimmed = false;
        task.SdkCapabilities = [new MSBuildTaskItem("worker-protocol.v1")];

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        Assert.True(task.HasWorkerTarget);
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var generated = Assert.Single(document.RootElement.GetProperty("targets").EnumerateArray());
        Assert.Equal(SunderPackageFormat.WorkerTargetKind, generated.GetProperty("kind").GetString());
        Assert.Equal("lib/Sunder.Package.Build.Tests.dll", generated.GetProperty("entryPoint").GetString());
        Assert.Equal("Sunder.Package.Build.Tests.dll", task.WorkerEntryPointRelativePath);
    }

    [Fact]
    public void GenerateManifest_RejectsCustomCSharpWorkerModuleName()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        var target = new MSBuildTaskItem("runtime/linux-x64");
        target.SetMetadata("EntryPoint", "lib/custom-worker");
        ConfigureWorkerTask(task, "linux-x64", target);

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("official managed module 'lib/Sunder.Package.Build.Tests.dll'", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_RejectsOldWorkerV3Capability()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        ConfigureWorkerTask(task, "linux-x64");
        task.SdkCapabilities =
        [
            new MSBuildTaskItem("worker-protocol.v1"),
            new MSBuildTaskItem("worker-protocol.v3"),
        ];

        Assert.False(task.Execute());
        Assert.Contains(
            buildEngine.Errors,
            error => error.Contains(
                "cannot use unsupported worker protocol capability 'worker-protocol.v3'",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SelfContained")]
    [InlineData("UseAppHost")]
    [InlineData("PublishSingleFile")]
    [InlineData("PublishTrimmed")]
    public void GenerateManifest_RejectsUnsupportedWorkerPublishShape(string property)
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        ConfigureWorkerTask(task, "linux-x64");
        switch (property)
        {
            case "SelfContained": task.SelfContained = true; break;
            case "UseAppHost": task.UseAppHost = true; break;
            case "PublishSingleFile": task.PublishSingleFile = true; break;
            case "PublishTrimmed": task.PublishTrimmed = true; break;
        }

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains(property, StringComparison.Ordinal));
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
    public async Task WorkerV1ExecutableFixture_InfersExactProtocolCapability()
    {
        var root = Path.Combine(
            FindRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "tests",
            "Sunder.Package.Build.TestFixtures",
            "WorkerV1Executable",
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
                SunderPackageFormat.ExecutionRuntimeV1Capability,
                SunderSdkCapabilities.LoggingV1,
                SunderSdkCapabilities.PackagingV1,
                SunderSdkCapabilities.RpcV1,
                SunderSdkCapabilities.RuntimeGenerationsV1,
                SunderSdkCapabilities.Baseline11V1,
                SunderSdkCapabilities.SecretsV1,
                SunderSdkCapabilities.SettingsSchemaV1,
                SunderSdkCapabilities.SettingsV1,
                SunderSdkCapabilities.StorageKeyMigrationV1,
                SunderSdkCapabilities.StorageV1,
                "worker-protocol.v1",
            ],
            target.RequiredHostCapabilities);
        const string moduleName = "WorkerV1Executable.dll";
        Assert.Equal($"lib/{moduleName}", target.EntryPoint);
        Assert.True(File.Exists(Path.Combine(root, "payload", "runtime", target.Rid!, "lib", moduleName)));
        Assert.True(File.Exists(Path.Combine(root, "payload", "runtime", target.Rid!, "lib", "WorkerV1Executable.runtimeconfig.json")));
        Assert.True(File.Exists(Path.Combine(root, "payload", "runtime", target.Rid!, "lib", "WorkerV1Executable.deps.json")));
        Assert.False(File.Exists(Path.Combine(root, "payload", "runtime", target.Rid!, "lib", "WorkerV1Executable")));
        Assert.False(File.Exists(Path.Combine(root, "payload", "runtime", target.Rid!, "lib", "WorkerV1Executable.exe")));
        Assert.False(Directory.EnumerateFiles(
            Path.Combine(root, "payload", "runtime", target.Rid!, "lib"),
            "*.pdb",
            SearchOption.AllDirectories).Any());
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

    [Fact]
    public void GenerateManifest_RejectsLegacyAndAuthoredHashMetadata()
    {
        var root = CreateTempDirectory();
        var descriptorPath = WriteMinimalV1Descriptor(root);
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
    public void GenerateManifest_RejectsUnsupportedAppRoleProvider()
    {
        var root = CreateTempDirectory();
        var descriptorPath = WriteMinimalV1Descriptor(root);
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
    public void GenerateManifest_RejectsPublicProviderForPackageExposedContract()
    {
        var root = CreateTempDirectory();
        var descriptorPath = WriteMinimalV1Descriptor(root);
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
            new MSBuildTaskItem("worker-protocol.v1"),
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
            new MSBuildTaskItem("worker-protocol.v1"),
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
        Assert.Equal(
            new[]
            {
                SunderSdkCapabilities.ContributionsV1,
                SunderSdkCapabilities.CoreV1,
                SunderSdkCapabilities.PackagingV1,
                SunderSdkCapabilities.Baseline11V1,
                SunderSdkCapabilities.ShellViewV1,
                SunderSdkCapabilities.StorageKeyMigrationV1,
                SunderSdkCapabilities.StorageV1,
                SunderSdkCapabilities.ViewNavigationPreparationV1,
            }.Order(StringComparer.Ordinal),
            ReadCapabilities(document.RootElement).Order(StringComparer.Ordinal));
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

    [Theory]
    [InlineData("app")]
    [InlineData(" app ")]
    [InlineData("runtime")]
    [InlineData(null)]
    public async Task PackageTargets_QueryCurrentAppUsesWithoutBuildingAndPreserveDuplicates(string? role)
    {
        var root = CreateTempDirectory();
        var targetsPath = Path.GetFullPath(Path.Combine(FindTaskDirectory(), "..", "build", "Sunder.Package.Build.targets"));
        var appProject = Path.Combine(root, "App.proj");
        var declaration = role is null ? string.Empty
            : $"<SunderUsesContract Include=\"test.rpc\" Role=\"{role}\" ContractVersion=\"2.0.0\" Required=\"true\" Actions=\"discover;invoke\" />";
        await File.WriteAllTextAsync(appProject, $$"""
            <Project>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                {{declaration}}
                {{declaration}}
              </ItemGroup>
              <Import Project="{{targetsPath}}" />
              <Target Name="Build"><Error Text="Query must not build the App." /></Target>
              <Target Name="Restore"><Error Text="Query must not restore the App." /></Target>
              <Target Name="CheckQueryContext" BeforeTargets="_GetSunderPackageAppContractUses">
                <Error Condition="'$(RuntimeIdentifier)' != '' or '$(BuildProjectReferences)' != 'false'"
                       Text="App query did not isolate its evaluation context." />
              </Target>
            </Project>
            """);
        var project = Path.Combine(root, "Package.proj");
        await File.WriteAllTextAsync(project, $$"""
            <Project>
              <PropertyGroup>
                <Configuration>Debug</Configuration>
                <TargetFramework>net10.0</TargetFramework>
                <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
                <SunderPackagePrimaryProject>true</SunderPackagePrimaryProject>
                <SunderPackageUsePrebuiltLeaves>true</SunderPackageUsePrebuiltLeaves>
                <BuildProjectReferences>false</BuildProjectReferences>
                <SunderPackageAppProject>{{appProject}}</SunderPackageAppProject>
                <SunderPackageAppTargetFramework>net8.0</SunderPackageAppTargetFramework>
              </PropertyGroup>
              <Import Project="{{targetsPath}}" />
              <Target Name="Run" DependsOnTargets="_ValidateSunderPackagePrimaryProject;_PrepareSunderContractPackInputs">
                <Message Importance="high" Text="queried-count:@(_SunderRpcPackAppUse->Count())" />
                <Message Importance="high" Text="@(_SunderRpcPackAppUse->'%(ContractVersion)|%(Required)|%(Actions)')" />
              </Target>
            </Project>
            """);
        var result = await ReadProcessAsync(StartMsbuild(project, root));
        if (role == "runtime")
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("must explicitly declare Role=app", result.Output, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(result.ExitCode == 0, result.Output);
            Assert.Contains(role is null ? "queried-count:0" : "queried-count:2", result.Output, StringComparison.Ordinal);
            if (role is not null) Assert.Contains("2.0.0|true|discover;invoke", result.Output, StringComparison.Ordinal);
        }
        Assert.False(Directory.Exists(Path.Combine(root, "obj")));
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("app")]
    public async Task PackPackage_DescriptorFirstRpcRequiresExplicitCompleteSourceInputs(string role)
    {
        var root = CreateTempDirectory();
        var devPackage = CreateRpcLeaf(root, role, "linux-x64", includeProvider: role == "runtime");
        var descriptor = Path.Combine(Path.GetDirectoryName(devPackage)!, "minimal.rpc.json");
        var output = Path.Combine(root, "bin", "descriptor-first.sunderpkg");
        var intermediate = Path.Combine(root, "pack-read-only");
        var missing = CreatePack();
        Assert.False(missing.Execute());
        Assert.Contains(((TestBuildEngine)missing.BuildEngine).Errors, error => error.Contains("Artifact-only RPC packing is disabled", StringComparison.Ordinal));

        var explicitInputs = CreatePack();
        explicitInputs.ContractBundles = [ContractBundle(descriptor)];
        explicitInputs.UsesContracts = [ContractUse(role)];
        explicitInputs.RpcProviders = role == "runtime" ? [RpcProvider(role)] : [];
        Assert.True(explicitInputs.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)explicitInputs.BuildEngine).Errors));
        Assert.False(Directory.Exists(intermediate));
        var prior = await File.ReadAllBytesAsync(output);
        var incomplete = CreatePack();
        incomplete.ContractBundles = [ContractBundle(descriptor)];
        incomplete.RpcProviders = explicitInputs.RpcProviders;
        Assert.False(incomplete.Execute());
        Assert.Contains(((TestBuildEngine)incomplete.BuildEngine).Errors, error => error.Contains(
            role == "app" ? "App contract uses" : "runtime contract uses", StringComparison.Ordinal));
        Assert.Equal(prior, await File.ReadAllBytesAsync(output));
        Assert.False(File.Exists(Path.Combine(root, "sunder-contracts.owned.json")));
        var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(output, Path.Combine(root, "descriptor-validation"));
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));

        PackSunderPackageTask CreatePack() => new()
        {
            BuildEngine = new TestBuildEngine(), ProjectDirectory = root, DevPackagePath = devPackage,
            DevPackageDiscoveryRoot = Path.GetDirectoryName(devPackage)!, PackageOutputPath = output, IntermediateOutputPath = intermediate,
        };
    }

    [Fact]
    public async Task PackPackage_DescriptorSourceIsRecheckedAfterWaitingForBaselineLease()
    {
        var root = CreateTempDirectory();
        var devPackage = CreateRpcLeaf(root, "runtime", "linux-x64");
        var descriptor = Path.Combine(Path.GetDirectoryName(devPackage)!, "minimal.rpc.json");
        var baseline = Path.Combine(root, "sunder-contracts.owned.json");
        var task = new PackSunderPackageTask
        {
            BuildEngine = new TestBuildEngine(), ProjectDirectory = root, DevPackagePath = devPackage,
            DevPackageDiscoveryRoot = Path.GetDirectoryName(devPackage)!, PackageOutputPath = Path.Combine(root, "bin", "descriptor-race.sunderpkg"),
            ContractBundles = [ContractBundle(descriptor)], UsesContracts = [ContractUse("runtime")], RpcProviders = [RpcProvider("runtime")],
        };
        using var lease = GeneratedOutputLock.Acquire([baseline]);
        using var contender = OutputLockContenderBarrier<bool>.Start(baseline, task.Execute, () => descriptor);
        try
        {
            await contender.WaitForAttemptAsync(TimeSpan.FromSeconds(30));
            await contender.AssertContendedAsync();
            File.AppendAllText(descriptor, "\n");
        }
        finally
        {
            lease.Dispose();
            await contender.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.False(await contender.Completion);
        Assert.Contains(((TestBuildEngine)task.BuildEngine).Errors, error => error.Contains("changed after it was read", StringComparison.Ordinal));
        Assert.False(File.Exists(task.PackageOutputPath));
        Assert.False(File.Exists(baseline));
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
        var workerName = CSharpWorkerModuleName();
        var workerPath = Path.Combine(targetDirectory, workerName);
        File.WriteAllText(workerPath, "worker");
        var workerFiles = CreateDotNetWorkerFiles(targetDirectory, "win-x64", workerPath);
        ConfigureWorkerTask(manifestTask, "win-x64", target);
        Assert.True(manifestTask.Execute(), string.Join(Environment.NewLine, manifestEngine.Errors));

        var assets = Path.Combine(root, "authored-assets");
        Directory.CreateDirectory(assets);
        var asset = Path.Combine(assets, "icon.png");
        var assetBytes = RandomNumberGenerator.GetBytes(8192);
        await File.WriteAllBytesAsync(asset, assetBytes);
        if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(asset, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var references = Path.Combine(root, "nuget-cache");
        Directory.CreateDirectory(references);
        var reference = Path.Combine(references, "Sunder.Sdk.Worker.dll");
        var referenceBytes = await File.ReadAllBytesAsync(typeof(SunderWorkerProviderRegistration).Assembly.Location);
        await File.WriteAllBytesAsync(reference, referenceBytes); // A fresh file, not an already shared APFS clone.
        var referenceTime = File.GetLastWriteTimeUtc(reference);
        var cachedReference = new MSBuildTaskItem(reference);
        cachedReference.SetMetadata("RelativePath", Path.GetFileName(reference));
        var sourceChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sourceWatcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        void ObserveInput(object sender, FileSystemEventArgs args)
        {
            if (args.FullPath == asset || args.FullPath == reference) sourceChanged.TrySetResult();
        }
        sourceWatcher.Changed += ObserveInput;
        sourceWatcher.Created += ObserveInput;
        sourceWatcher.EnableRaisingEvents = true;
        var emitEngine = new TestBuildEngine();
        Assert.True(new EmitSunderDevOutputTask
        {
            BuildEngine = emitEngine,
            ManifestPath = manifestPath,
            DevPackagePath = devPackage,
            DiscoveryRoot = root,
            TargetDirectory = targetDirectory,
            WorkerFiles = [.. workerFiles, new MSBuildTaskItem(winNative), cachedReference],
            AssetsDirectory = assets,
            AssetFiles = [new MSBuildTaskItem(asset)],
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
        var copiedAsset = Path.Combine(devPackage, "payload", "shared", "assets", "icon.png");
        Assert.Equal(assetBytes, await File.ReadAllBytesAsync(copiedAsset));
        var copiedReference = Path.Combine(devPackage, "payload", "runtime", "win-x64", "lib", "Sunder.Sdk.Worker.dll");
        Assert.Equal(referenceBytes, await File.ReadAllBytesAsync(copiedReference));
        Assert.Equal(referenceBytes, await File.ReadAllBytesAsync(reference));
        Assert.Equal(referenceTime, File.GetLastWriteTimeUtc(reference));
        Assert.Equal(referenceTime, File.GetLastWriteTimeUtc(copiedReference));
        if (OperatingSystem.IsMacOS()) Assert.Equal(File.GetUnixFileMode(asset), File.GetUnixFileMode(copiedAsset));
        await Task.Delay(500);
        Assert.False(sourceChanged.Task.IsCompleted, "Copying assets or cached runtime references must not invalidate Watch's input revision.");
        await File.AppendAllTextAsync(asset, "real authored edit");
        await sourceChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)); // The observer itself is live.
    }

    [Fact]
    public async Task EmitDevOutput_IndexesPostSigningWorkerBytesAndDetectsLaterTampering()
    {
        var root = CreateTempDirectory();
        var targetDirectory = Path.Combine(root, "target");
        var devPackage = Path.Combine(root, "sunder-dev");
        Directory.CreateDirectory(targetDirectory);
        var rid = RuntimeInformation.RuntimeIdentifier;
        var moduleName = CSharpWorkerModuleName();
        var modulePath = Path.Combine(targetDirectory, moduleName);
        await File.WriteAllBytesAsync(modulePath, "module::platform-signature"u8.ToArray());
        var workerFiles = CreateDotNetWorkerFiles(targetDirectory, rid, modulePath);
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
            WorkerFiles = workerFiles,
        }.Execute(), string.Join(Environment.NewLine, emitEngine.Errors));

        var packagedModule = Path.Combine(devPackage, "payload", "runtime", rid, "lib", moduleName);
        var index = JsonSerializer.Deserialize<SunderPackageContentIndex>(await File.ReadAllTextAsync(
            Path.Combine(devPackage, "manifest", "content-index.json")))!;
        var entry = Assert.Single(index.Files!, item => item!.Path == $"payload/runtime/{rid}/lib/{moduleName}")!;
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(modulePath))).ToLowerInvariant(),
            entry.Sha256);

        await File.AppendAllTextAsync(packagedModule, "::tampered");
        var tampered = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(devPackage);
        Assert.False(tampered.Success);
        Assert.Contains(tampered.Errors, error =>
            error.Contains($"payload/runtime/{rid}/lib/{moduleName}", StringComparison.Ordinal)
            && error.Contains("mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PackageTargets_WorkerSigningRunsAfterPublishCollectionAndBeforeContentIndexing()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var targetDirectory = Path.Combine(project, "target");
        var sourceDirectory = Path.Combine(root, "publish");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateDirectory(sourceDirectory);
        var rid = RuntimeInformation.RuntimeIdentifier;
        var moduleName = CSharpWorkerModuleName();
        var sourceModule = Path.Combine(sourceDirectory, moduleName);
        await File.WriteAllTextAsync(sourceModule, "unsigned-worker");
        var workerFiles = CreateDotNetWorkerFiles(sourceDirectory, rid, sourceModule);
        var sourceRuntimeConfig = workerFiles.Single(item => item.ItemSpec.EndsWith(".runtimeconfig.json", StringComparison.Ordinal)).ItemSpec;
        var sourceDeps = workerFiles.Single(item => item.ItemSpec.EndsWith(".deps.json", StringComparison.Ordinal)).ItemSpec;

        var manifestPath = Path.Combine(root, "sunder-package.json");
        var manifestEngine = new TestBuildEngine();
        var manifestTask = CreateTask(manifestPath, manifestEngine);
        ConfigureWorkerTask(manifestTask, rid);
        Assert.True(manifestTask.Execute(), string.Join(Environment.NewLine, manifestEngine.Errors));

        var targetsPath = Path.GetFullPath(Path.Combine(
            FindTaskDirectory(),
            "..",
            "build",
            "Sunder.Package.Build.targets"));
        var devPackage = Path.Combine(targetDirectory, "sunder-dev");
        var projectFile = Path.Combine(project, "SigningOrder.proj");
        await File.WriteAllTextAsync(projectFile, $$"""
            <Project>
              <PropertyGroup>
                <Configuration>Debug</Configuration>
                <TargetFramework>net10.0</TargetFramework>
                <BaseIntermediateOutputPath>obj/</BaseIntermediateOutputPath>
                <IntermediateOutputPath>obj/</IntermediateOutputPath>
                <TargetDir>{{targetDirectory}}{{Path.DirectorySeparatorChar}}</TargetDir>
                <SunderGeneratedPackageManifest>{{manifestPath}}</SunderGeneratedPackageManifest>
                <SunderDevOutputPath>{{devPackage}}</SunderDevOutputPath>
                <SunderDevOutputDiscoveryRoot>{{targetDirectory}}</SunderDevOutputDiscoveryRoot>
                <SunderPackageWorkerSigningTargets>TestSignWorker</SunderPackageWorkerSigningTargets>
                <SunderPackageBuildProject>{{Path.Combine(root, "missing.csproj")}}</SunderPackageBuildProject>
                <SunderPackageBuildTaskAssembly>{{typeof(PackSunderPackageTask).Assembly.Location}}</SunderPackageBuildTaskAssembly>
              </PropertyGroup>
              <Target Name="ComputeFilesToPublish">
                <ItemGroup>
                  <ResolvedFileToPublish Include="{{sourceModule}}" RelativePath="{{moduleName}}" />
                  <ResolvedFileToPublish Include="{{sourceRuntimeConfig}}" RelativePath="{{Path.GetFileName(sourceRuntimeConfig)}}" />
                  <ResolvedFileToPublish Include="{{sourceDeps}}" RelativePath="{{Path.GetFileName(sourceDeps)}}" />
                </ItemGroup>
              </Target>
              <Target Name="TestSignWorker">
                <WriteLinesToFile File="%(SunderPackageWorkerSigningFile.Identity)"
                                  Condition="'%(SunderPackageWorkerSigningFile.Extension)' == '.dll'"
                                  Lines="signed"
                                  Overwrite="false"
                                  Encoding="UTF-8" />
              </Target>
              <Import Project="{{targetsPath}}" />
              <Target Name="GenerateSunderPackageManifest">
                <PropertyGroup>
                  <_SunderHasWorkerTarget>true</_SunderHasWorkerTarget>
                </PropertyGroup>
              </Target>
              <Target Name="Run" DependsOnTargets="EmitSunderDevOutput" />
            </Project>
            """);

        var result = await ReadProcessAsync(StartMsbuild(projectFile, project));

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal("unsigned-worker", await File.ReadAllTextAsync(sourceModule));
        var packagedModule = Path.Combine(devPackage, "payload", "runtime", rid, "lib", moduleName);
        Assert.EndsWith(
            "signed" + Environment.NewLine,
            await File.ReadAllTextAsync(packagedModule),
            StringComparison.Ordinal);
        var contentIndex = JsonSerializer.Deserialize<SunderPackageContentIndex>(await File.ReadAllTextAsync(
            Path.Combine(devPackage, "manifest", "content-index.json")))!;
        var indexedModule = Assert.Single(
            contentIndex.Files!,
            entry => entry!.Path == $"payload/runtime/{rid}/lib/{moduleName}")!;
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(packagedModule))).ToLowerInvariant(),
            indexedModule.Sha256);
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(devPackage);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
    }

    [Fact]
    public async Task PackageTargets_WorkerSigningMutatesPrivateClosureWithoutChangingSources()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(cache);
        var module = Path.Combine(cache, "Worker.dll");
        var managedAssembly = Path.Combine(cache, "Authored.Dependency.dll");
        var documentation = Path.Combine(cache, "Authored.Dependency.xml");
        var runtimeConfiguration = Path.Combine(cache, "runtime.settings.xml");
        var runtimeLibrary = Path.Combine(cache, "libPackage.Native.dylib");
        await File.WriteAllTextAsync(module, "source-module");
        await File.WriteAllTextAsync(managedAssembly, "source-assembly");
        await File.WriteAllTextAsync(documentation, "source-documentation");
        await File.WriteAllTextAsync(runtimeConfiguration, "source-configuration");
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
                  <ResolvedFileToPublish Include="{{module}}" RelativePath="Worker.dll" />
                  <ResolvedFileToPublish Include="{{managedAssembly}}" RelativePath="Authored.Dependency.dll" />
                  <ResolvedFileToPublish Include="{{documentation}}" RelativePath="Authored.Dependency.xml" />
                  <ResolvedFileToPublish Include="{{runtimeConfiguration}}" RelativePath="runtime.settings.xml" />
                  <ResolvedFileToPublish Include="{{runtimeLibrary}}" RelativePath="runtimes/osx-arm64/native/libPackage.Native.dylib" />
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
        Assert.Equal("source-module", await File.ReadAllTextAsync(module));
        Assert.Equal("source-assembly", await File.ReadAllTextAsync(managedAssembly));
        Assert.Equal("source-documentation", await File.ReadAllTextAsync(documentation));
        Assert.Equal("source-configuration", await File.ReadAllTextAsync(runtimeConfiguration));
        Assert.Equal("source-runtime", await File.ReadAllTextAsync(runtimeLibrary));
        var signingRoot = Path.Combine(project, "obj", "sunder-worker-signing");
        Assert.EndsWith("signed" + Environment.NewLine, await File.ReadAllTextAsync(Path.Combine(signingRoot, "Worker.dll")), StringComparison.Ordinal);
        Assert.EndsWith(
            "signed" + Environment.NewLine,
            await File.ReadAllTextAsync(Path.Combine(signingRoot, "Authored.Dependency.dll")),
            StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(signingRoot, "Authored.Dependency.xml")));
        Assert.EndsWith(
            "signed" + Environment.NewLine,
            await File.ReadAllTextAsync(Path.Combine(signingRoot, "runtime.settings.xml")),
            StringComparison.Ordinal);
        Assert.EndsWith(
            "signed" + Environment.NewLine,
            await File.ReadAllTextAsync(Path.Combine(signingRoot, "runtimes", "osx-arm64", "native", "libPackage.Native.dylib")),
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
    public void SharedOutputLock_SchedulerBackoffRunsOutsideTransitionAndFailedCallbacksReleasePartialAcquisition()
    {
        var root = CreateTempDirectory();
        try
        {
            var first = Path.Combine(root, "a-output"); var second = Path.Combine(root, "b-output");
            using var owner = GeneratedOutputLock.Acquire([second]);
            var calls = 0;
            var error = Assert.Throws<InvalidOperationException>(() => GeneratedOutputLock.Acquire([], [first, second],
                TimeSpan.FromSeconds(2), waitBeforeRetry: () =>
                {
                    calls++;
                    Assert.False(Directory.Exists(second + GeneratedOutputLock.LockSuffix + ".transition"));
                    throw new InvalidOperationException("The scheduler wait was cancelled.");
                }));
            Assert.Equal("The scheduler wait was cancelled.", error.Message); Assert.Equal(1, calls);
            using (GeneratedOutputLock.Acquire([first], TimeSpan.FromSeconds(2))) { }
            using var acquired = GeneratedOutputLock.Acquire([], [first, second], TimeSpan.FromSeconds(2),
                waitBeforeRetry: owner.Dispose);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SharedOutputLock_RetainReleasesUnneededPathsWithoutUnlockingRetainedPath()
    {
        var root = CreateTempDirectory();
        try
        {
            var retained = Path.Combine(root, "retained-output");
            var released = Path.Combine(root, "released-output");
            using (var outputLock = GeneratedOutputLock.Acquire([retained, released]))
            {
                outputLock.Retain([retained]);
                using var releasedLock = GeneratedOutputLock.Acquire([released], TimeSpan.FromSeconds(2));
                Assert.Throws<TimeoutException>(() =>
                    GeneratedOutputLock.Acquire([retained], TimeSpan.FromMilliseconds(250)));
            }

            Assert.False(Directory.Exists(retained + GeneratedOutputLock.LockSuffix));
            Assert.False(Directory.Exists(released + GeneratedOutputLock.LockSuffix));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    [LinuxTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void SharedOutputLock_LinuxBootOrPidNamespaceMismatchTimesOutWithoutReclamation(bool mismatchBoot)
    {
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
        using var waiter = OutputLockContenderBarrier<IDisposable>.Start(
            output,
            () => AcquireGeneratedOutputLock(output),
            () => $"lock={output + GeneratedOutputLock.LockSuffix}; owner={File.ReadAllText(heartbeatPath)}");
        await waiter.WaitForAttemptAsync(TimeSpan.FromSeconds(5));
        await waiter.AssertContendedAsync();
        holder.Dispose();
        using (await waiter.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
        {
        }

        using var deadOwnerMarker = CreateDeadOwnerLock(output);
        using var guardMarker = CreateOwnerDirectory(
            output + GeneratedOutputLock.LockSuffix + ".transition",
            int.MaxValue,
            Environment.MachineName,
            DateTime.UtcNow.AddMinutes(-1));
        var acquired = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var order = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var release = new[] { new ManualResetEventSlim(), new ManualResetEventSlim() };
        var reclaimers = Enumerable.Range(0, 2).Select(index =>
            OutputLockContenderBarrier<object?>.Start(output, () =>
            {
                using var outputLock = AcquireGeneratedOutputLock(output);
                order.Enqueue(index);
                acquired[index].TrySetResult();
                release[index].Wait(TimeSpan.FromSeconds(10));
                return null;
            }, () => $"reclaimer={index}; acquired={string.Join(',', order)}; lock={output + GeneratedOutputLock.LockSuffix}"))
            .ToArray();
        await Task.WhenAll(reclaimers.Select(reclaimer => reclaimer.WaitForAttemptAsync(TimeSpan.FromSeconds(5))));
        await Task.WhenAny(acquired.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(order.TryPeek(out var first));
        await Eventually.AssertPendingAsync(
            acquired[first == 0 ? 1 : 0].Task,
            () => $"Both stale-lock reclaimers acquired before release; order={string.Join(',', order)}");
        Assert.Single(order);
        release[first].Set();
        await acquired[first == 0 ? 1 : 0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, order.Count);
        release[first == 0 ? 1 : 0].Set();
        await Task.WhenAll(reclaimers.Select(reclaimer => reclaimer.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Directory.Exists(output + ".sunder-output.lock"));
        Assert.False(Directory.Exists(output + ".sunder-output.lock.transition"));
        Assert.Single(Directory.EnumerateDirectories(root, "shared-output.sunder-output.lock.transition.stale-*"));
    }

    [ExternalArtifactFact("SUNDER_REQUIRE_NODE_OUTPUT_LOCK_INTEROP")]
    [Trait("Category", "ExternalArtifact")]
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
        Assert.True(File.Exists(helper), $"Required Node output-lock helper does not exist: {helper}");
        Assert.True(CanStartNode(), "The selected output-lock interop lane cannot start Node.");
        var scope = TemporaryDirectoryScope.Create("sunder-output-lock-interop");
        var root = scope.RootPath;
        var output = Path.Combine(root, "shared-output");
        var ready = Path.Combine(root, "node.ready");
        var release = Path.Combine(root, "node.release");
        var attempting = Path.Combine(root, "node.attempting");
        IDisposable? holder = null;
        CapturedProcess? node = null;
        CapturedProcess? crashedNode = null;
        OutputLockContenderBarrier<IDisposable>? reclaimer = null;
        IDisposable? reclaimed = null;
        try
        {
            holder = AcquireGeneratedOutputLock(output);
            node = StartCapturedNodeInTempRoot(Path.Combine(root, "node-job-temp"), helper, "hold", output, ready, release, attempting);
            var nodeResult = node.WaitForExitAsync(TimeSpan.FromSeconds(15));
            await FileSignal.WaitForAsync(attempting, TimeSpan.FromSeconds(5), () => $"ready={File.Exists(ready)}; {node.Diagnostics}");
            await Eventually.AssertPendingAsync(nodeResult, () => $"attempting={File.Exists(attempting)}; ready={File.Exists(ready)}; {node.Diagnostics}");
            Assert.False(File.Exists(ready));
            holder.Dispose();
            holder = null;
            await FileSignal.WaitForAsync(ready, TimeSpan.FromSeconds(5), () => node.Diagnostics);
            File.WriteAllText(release, "release");
            var result = await nodeResult;
            Assert.True(result.ExitCode == 0, result.CombinedOutput);
            await node.DisposeAsync();
            node = null;
            Assert.False(Directory.Exists(output + ".sunder-output.lock"));

            using (CreateDeadOwnerLock(output))
            {
                await using var nodeRecovery = StartCapturedNode(helper, "recover", output);
                var recoveryResult = await nodeRecovery.WaitForExitAsync(TimeSpan.FromSeconds(10));
                Assert.True(recoveryResult.ExitCode == 0, recoveryResult.CombinedOutput);
                Assert.False(Directory.Exists(output + ".sunder-output.lock"));
            }

            var crashedReady = Path.Combine(root, "crashed-node.ready");
            var crashedRelease = Path.Combine(root, "crashed-node.release");
            var crashedAttempting = Path.Combine(root, "crashed-node.attempting");
            var crashedTemp = Path.Combine(root, "crashed-job-temp");
            crashedNode = StartCapturedNodeInTempRoot(crashedTemp, helper, "hold", output, crashedReady, crashedRelease, crashedAttempting);
            await FileSignal.WaitForAsync(crashedReady, TimeSpan.FromSeconds(5), () => crashedNode.Diagnostics);
            using var ownerDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                output + GeneratedOutputLock.LockSuffix,
                GeneratedOutputLock.OwnerFileName)));
            using var crashedNodeMarker = new TemporaryProcessMarker(
                ownerDocument.RootElement.GetProperty("processMarkerPath").GetString()
                ?? throw new InvalidDataException("Node lock owner omitted its process marker path."));
            reclaimer = OutputLockContenderBarrier<IDisposable>.Start(
                output,
                () => GeneratedOutputLock.Acquire([output], TimeSpan.FromSeconds(5)),
                () => $"nodePid={crashedNode.Id}; lock={output + GeneratedOutputLock.LockSuffix}; {crashedNode.Diagnostics}");
            await reclaimer.WaitForAttemptAsync(TimeSpan.FromSeconds(5));
            await reclaimer.AssertContendedAsync();
            crashedNode.Process.Kill(entireProcessTree: true);
            var crashedResult = await crashedNode.WaitForExitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(0, crashedResult.ExitCode);
            Directory.Delete(crashedTemp, recursive: true);
            Assert.True(File.Exists(crashedNodeMarker.Path));
            reclaimed = await reclaimer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            TryWriteReleaseSignal(release);
            TryWriteReleaseSignal(Path.Combine(root, "crashed-node.release"));
            CaptureCleanup(cleanupErrors, () => holder?.Dispose());
            if (crashedNode is not null)
            {
                await CaptureCleanupAsync(cleanupErrors, () => crashedNode.DisposeAsync());
            }
            if (node is not null)
            {
                await CaptureCleanupAsync(cleanupErrors, () => node.DisposeAsync());
            }
            if (reclaimed is null && reclaimer is not null)
            {
                try
                {
                    reclaimed = await reclaimer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add(exception);
                }
            }
            CaptureCleanup(cleanupErrors, () => reclaimed?.Dispose());
            CaptureCleanup(cleanupErrors, () => reclaimer?.Dispose());
            await CaptureCleanupAsync(cleanupErrors, () => scope.DisposeAsync());
            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException("Output-lock interop cleanup did not complete.", cleanupErrors);
            }
        }
        Assert.False(Directory.Exists(root), $"Output-lock interop temporary root was retained: {root}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Conformance")]
    [Trait("Capability", "Filesystem")]
    [Trait("Size", "Small")]
    public void SharedOutputDocuments_MatchCrossLanguagePropertyCorpusAndRecoverTransaction(bool developmentHandoff)
    {
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "GeneratedOutputConformance",
            "documents.json")));
        var documents = corpus.RootElement.GetProperty("documents");
        Assert.Equal(1, corpus.RootElement.GetProperty("formatVersion").GetInt32());
        var root = CreateTempDirectory();
        var output = Path.Combine(root, developmentHandoff ? "sunder-dev" : "shared-output");
        using (AcquireGeneratedOutputLock(output))
        {
            using var owner = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
                output + GeneratedOutputLock.LockSuffix,
                GeneratedOutputLock.OwnerFileName)));
            AssertDocumentProperties(owner.RootElement, documents.GetProperty("owner"));
            var markerPath = owner.RootElement.GetProperty("processMarkerPath").GetString()!;
            using var marker = JsonDocument.Parse(File.ReadAllBytes(markerPath));
            AssertDocumentProperties(marker.RootElement, documents.GetProperty("processMarker"));
        }

        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "generation.txt"), "old");
        var token = Guid.NewGuid().ToString("D");
        var staged = output + ".stage-" + Guid.NewGuid().ToString("N");
        var backup = output + ".backup-" + token;
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "generation.txt"), "new");
        var journal = output + GeneratedOutputTransaction.TransactionSuffix;
        var imports = output + SunderPackageFormat.DevelopmentImportsSuffix;
        var stagedImports = imports + ".stage-" + Guid.NewGuid().ToString("N");
        var importsBackup = imports + ".backup-" + token;
        var outputs = new List<object> { new { finalPath = output, stagedPath = staged, backupPath = backup, existed = true } };
        if (developmentHandoff)
        {
            File.WriteAllText(imports, "old selection");
            File.WriteAllText(stagedImports, "new selection");
            outputs.Add(new { finalPath = imports, stagedPath = stagedImports, backupPath = importsBackup, existed = true });
        }
        File.WriteAllText(journal, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            token,
            coordinatorPath = output,
            outputs,
        }));
        using (var transaction = JsonDocument.Parse(File.ReadAllBytes(journal)))
        {
            AssertDocumentProperties(transaction.RootElement, documents.GetProperty("transaction"));
            AssertDocumentProperties(
                transaction.RootElement.GetProperty("outputs")[0],
                documents.GetProperty("transactionOutput"));
        }
        Directory.Move(output, backup);
        Directory.Move(staged, output);
        if (developmentHandoff)
        {
            File.Move(imports, importsBackup);
            File.Move(stagedImports, imports);
        }

        GeneratedOutputTransaction.RecoverAndCleanup(
            GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(output));

        Assert.Equal("old", File.ReadAllText(Path.Combine(output, "generation.txt")));
        if (developmentHandoff) Assert.Equal("old selection", File.ReadAllText(imports));
        Assert.False(File.Exists(journal));

        using var mutated = JsonDocument.Parse("""{"schemaVersion":1,"token":"x","coordinatorPath":"x","outputs":[],"seeded":true}""");
        Assert.ThrowsAny<Exception>(() => AssertDocumentProperties(
            mutated.RootElement,
            documents.GetProperty("transaction")));
    }

    [Fact]
    public void SharedOutputLock_MacOsCaseAliasesFailInsteadOfSelfContending()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = CreateTempDirectory();
        try
        {
            var output = Path.Combine(root, "CaseSensitiveProbe");
            var alias = Path.Combine(root, "casesensitiveprobe");
            Directory.CreateDirectory(output);
            if (!Directory.Exists(alias)) return;

            var exception = Assert.Throws<InvalidDataException>(() =>
                GeneratedOutputLock.Acquire([output], [alias], TimeSpan.FromMilliseconds(250)));
            Assert.Contains("differ only by case", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SharedOutputLock_MacOsSystemAliasAndPhysicalPathDeduplicate()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = CreateTempDirectory();
        try
        {
            string aliasRoot;
            string physicalRoot;
            if (root.StartsWith("/private/var/", StringComparison.Ordinal))
            {
                aliasRoot = root["/private".Length..];
                physicalRoot = root;
            }
            else if (root.StartsWith("/var/", StringComparison.Ordinal))
            {
                aliasRoot = root;
                physicalRoot = "/private" + root;
            }
            else
            {
                return;
            }

            using var outputLock = GeneratedOutputLock.Acquire(
                [Path.Combine(aliasRoot, "shared-output")],
                [Path.Combine(physicalRoot, "shared-output")],
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AggregateTask_FactorsIdenticalFilesAndProducesDeterministicUniversalTree()
    {
        var root = CreateTempDirectory();
        var appLeaf = CreateCanonicalLeaf(root, "app", "win-x64", "app-native");
        var runtimeLeaf = CreateCanonicalLeaf(root, "runtime", "win-x64", "runtime-native");
        var managedRidPath = Path.Combine("lib", "runtimes", "win-x64", "lib", "net10.0", "SharedManaged.dll");
        var nativeExecutablePath = Path.Combine("lib", "package-native");
        foreach (var (leaf, role) in new[] { (appLeaf, "app"), (runtimeLeaf, "runtime") })
        {
            var managedDestination = Path.Combine(leaf, "payload", role, "win-x64", managedRidPath);
            Directory.CreateDirectory(Path.GetDirectoryName(managedDestination)!);
            File.Copy(Assembly.GetExecutingAssembly().Location, managedDestination);
            File.WriteAllBytes(
                Path.Combine(leaf, "payload", role, "win-x64", nativeExecutablePath),
                [0x7f, (byte)'E', (byte)'L', (byte)'F', 1, 2, 3, 4]);
            PackageContentIndexer.Write(leaf);
        }
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
        Assert.True(File.Exists(Path.Combine(output, "payload", "app", "win-x64", "lib", Path.GetFileName(Assembly.GetExecutingAssembly().Location))));
        Assert.True(File.Exists(Path.Combine(
            output,
            "payload",
            "runtime",
            "win-x64",
            "lib",
            CSharpWorkerModuleName())));
        Assert.True(File.Exists(Path.Combine(output, "payload", "app", "win-x64", "lib", "runtimes", "win-x64", "native", "fixture.bin")));
        Assert.True(File.Exists(Path.Combine(output, "payload", "runtime", "win-x64", "lib", "runtimes", "win-x64", "native", "fixture.bin")));
        Assert.False(Directory.Exists(Path.Combine(output, "payload", "app", "shared", "lib", "runtimes")));
        Assert.False(Directory.Exists(Path.Combine(output, "payload", "runtime", "shared", "lib", "runtimes")));
        Assert.True(File.Exists(Path.Combine(output, "payload", "shared", managedRidPath)));
        Assert.False(File.Exists(Path.Combine(output, "payload", "app", "win-x64", managedRidPath)));
        Assert.False(File.Exists(Path.Combine(output, "payload", "runtime", "win-x64", managedRidPath)));
        Assert.True(File.Exists(Path.Combine(output, "payload", "app", "win-x64", nativeExecutablePath)));
        Assert.True(File.Exists(Path.Combine(output, "payload", "runtime", "win-x64", nativeExecutablePath)));
        Assert.False(File.Exists(Path.Combine(output, "payload", "shared", nativeExecutablePath)));
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
        var bundle = Assert.Single(validation.Manifest!.ContractBundles!)!;
        Assert.Equal("owned", bundle.Source);
        Assert.Equal("package", bundle.AuthorityKind);
        Assert.Equal("test.package.build.fixture", bundle.AuthorityId);
        Assert.Null(bundle.SelectedAuthorityRelease);
        Assert.Null(bundle.SelectedAuthorityContentIdentity);
        Assert.Equal("required", Assert.Single(validation.Manifest.DependsOn!).Presence);
        Assert.Empty(validation.Manifest.PackageArtifactImports!);
        Assert.Empty(validation.Manifest.PackageApiImports!);
        Assert.Empty(validation.Manifest.ProviderImports!);
        Assert.Empty(validation.Manifest.PlatformImports!);
        var uses = validation.Manifest.UsesContracts!;
        Assert.Equal(
            ["app", "runtime"],
            uses.Select(static use => use!.Role!).ToArray());
        Assert.False(uses[0]!.Required ?? true);
        Assert.True(uses[1]!.Required == true);
        Assert.Equal("runtime", Assert.Single(validation.Manifest.Provides!)!.Role);
        Assert.All(validation.Manifest.Targets!, targetManifest =>
        {
            Assert.Contains("rpc.v1", targetManifest!.RequiredHostCapabilities!);
            Assert.Contains(SunderSdkCapabilities.CallbacksV1, targetManifest.RequiredHostCapabilities!);
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
        Assert.Contains(buildEngine.Errors, error => error.Contains("conflicting 'contractBundles' evidence", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("packageArtifactImports")]
    [InlineData("packageApiImports")]
    [InlineData("providerImports")]
    [InlineData("platformImports")]
    public async Task AggregateTask_FingerprintsExplicitEmptyImportGroups(string group)
    {
        var root = CreateTempDirectory();
        var first = CreateCanonicalLeaf(root, "app", "win-x64", "native");
        var second = CreateCanonicalLeaf(root, "app", "linux-x64", "native");
        var manifestPath = Path.Combine(second, "manifest", "sunder-package.json");
        var manifest = JsonNode.Parse(await File.ReadAllBytesAsync(manifestPath))!.AsObject();
        Assert.Empty(manifest[group]!.AsArray());
        Assert.True(manifest.Remove(group));
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        PackageContentIndexer.Write(second);
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(second);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));

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

        Assert.Contains(buildEngine.Errors, error => error.Contains("package-wide manifest metadata", StringComparison.Ordinal));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("optional-dependency")]
    [InlineData("package-api")]
    [InlineData("package-provider")]
    [InlineData("platform-provider")]
    public async Task AggregateTask_PreservesImportsAndNonRpcOptionalDependencies(string metadata)
    {
        var root = CreateTempDirectory();
        var leaf = CreateRpcLeaf(
            root,
            "runtime",
            "linux-x64",
            includeUse: metadata is "optional-dependency" or "package-api",
            includeProvider: metadata != "package-api");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var initialEngine = new TestBuildEngine();
        var task = new AggregateSunderPackageTask
        {
            BuildEngine = initialEngine,
            TargetLeaves = [TargetLeafItem(leaf)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        };
        Assert.True(task.Execute(), string.Join(Environment.NewLine, initialEngine.Errors));
        var outputManifestPath = Path.Combine(output, "manifest", "sunder-package.json");
        var outputIndexPath = Path.Combine(output, "manifest", "content-index.json");
        var priorManifest = await File.ReadAllBytesAsync(outputManifestPath);
        var priorIndex = await File.ReadAllBytesAsync(outputIndexPath);

        var manifestPath = Path.Combine(leaf, "manifest", "sunder-package.json");
        var manifest = JsonNode.Parse(await File.ReadAllBytesAsync(manifestPath))!.AsObject();
        if (metadata == "optional-dependency")
        {
            manifest["dependsOn"]![0]!["presence"] = "optional";
        }
        else
        {
            var platform = metadata == "platform-provider";
            var authority = new JsonObject
            {
                ["authorityKind"] = platform ? "platform" : "package",
                ["authorityId"] = platform ? "sunder.core" : "test.dependency",
                ["selectedAuthorityRelease"] = "1.2.3",
                ["selectedAuthorityContentIdentity"] = new string('1', 64),
            };
            var bundle = Assert.Single(manifest["contractBundles"]!.AsArray())!;
            bundle["source"] = "imported";
            foreach (var property in authority)
            {
                bundle[property.Key] = property.Value?.DeepClone();
            }
            var bundleRef = new JsonObject
            {
                ["contractId"] = "example.rpc.v1.minimal",
                ["contractVersion"] = "2.0.0",
            };
            if (!platform)
            {
                manifest["packageArtifactImports"] = new JsonArray(new JsonObject
                {
                    ["packageId"] = "test.dependency",
                    ["requestedVersionRange"] = ">=1.2.3 <2.0.0",
                    ["includePrerelease"] = false,
                    ["selectedPackageVersion"] = "1.2.3",
                    ["selectedPackageContentIdentity"] = new string('1', 64),
                    ["publicContractCount"] = 1,
                    ["publicInventorySha256"] = new string('3', 64),
                    ["authorityConstraints"] = new JsonArray(authority.DeepClone()),
                });
            }
            if (metadata == "package-api")
            {
                var useRef = (JsonObject)bundleRef.DeepClone();
                useRef["role"] = "runtime";
                manifest["packageApiImports"] = new JsonArray(new JsonObject
                {
                    ["packageId"] = "test.dependency",
                    ["presence"] = "required",
                    ["bundleRefs"] = new JsonArray(bundleRef),
                    ["useRefs"] = new JsonArray(useRef),
                });
            }
            else
            {
                var providerRef = (JsonObject)bundleRef.DeepClone();
                providerRef["role"] = "runtime";
                providerRef["providerId"] = "test.rpc.provider";
                var import = platform ? authority : new JsonObject { ["packageId"] = "test.dependency" };
                import["key"] = "minimalProvider";
                import["contractId"] = "example.rpc.v1.minimal";
                import["contractVersion"] = "2.0.0";
                import["bundleRef"] = bundleRef;
                import["providerRefs"] = new JsonArray(providerRef);
                if (platform)
                {
                    import["authorityReleaseRange"] = ">=1.0.0 <2.0.0";
                    import["includePrerelease"] = false;
                }
                manifest[platform ? "platformImports" : "providerImports"] = new JsonArray(import);
            }
        }
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        PackageContentIndexer.Write(leaf);
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(leaf);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));

        var aggregateEngine = new TestBuildEngine();
        task.BuildEngine = aggregateEngine;
        if (metadata is "package-api" or "package-provider" or "platform-provider")
        {
            Assert.True(task.Execute(), string.Join(Environment.NewLine, aggregateEngine.Errors));
            Assert.Empty(aggregateEngine.Errors);
            var aggregateValidation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(output);
            Assert.True(aggregateValidation.Success, string.Join(Environment.NewLine, aggregateValidation.Errors));
            using var sourceDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath));
            using var aggregateDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(outputManifestPath));
            foreach (var name in new[]
                     {
                         "dependsOn", "contractBundles", "usesContracts", "provides",
                         "packageArtifactImports", "packageApiImports", "providerImports", "platformImports",
                     })
            {
                Assert.True(JsonElement.DeepEquals(sourceDocument.RootElement.GetProperty(name), aggregateDocument.RootElement.GetProperty(name)),
                    $"Aggregation changed '{name}' for supported {metadata} metadata.");
            }
            var result = aggregateValidation.Manifest!;
            if (metadata == "platform-provider") Assert.Empty(result.PackageArtifactImports!);
            else Assert.Single(result.PackageArtifactImports!);
            Assert.Equal("imported", Assert.Single(result.ContractBundles!)!.Source);
            if (metadata == "package-api")
            {
                Assert.Equal("runtime", Assert.Single(Assert.Single(result.PackageApiImports!)!.UseRefs!)!.Role);
                Assert.Single(result.UsesContracts!);
                Assert.Empty(result.ProviderImports!);
                Assert.Empty(result.Provides!);
            }
            else
            {
                var references = metadata == "platform-provider" ? Assert.Single(result.PlatformImports!)!.ProviderRefs : Assert.Single(result.ProviderImports!)!.ProviderRefs;
                Assert.Equal("runtime", Assert.Single(references!)!.Role);
                Assert.Single(result.Provides!);
                Assert.Empty(result.PackageApiImports!);
                Assert.Empty(result.UsesContracts!);
            }
            Assert.NotEqual(priorManifest, await File.ReadAllBytesAsync(outputManifestPath));
            return;
        }
        Assert.True(task.Execute(), string.Join(Environment.NewLine, aggregateEngine.Errors));
        var optional = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(output);
        Assert.True(optional.Success, string.Join(Environment.NewLine, optional.Errors));
        Assert.Equal("optional", Assert.Single(optional.Manifest!.DependsOn!).Presence);
        Assert.NotEqual(priorManifest, await File.ReadAllBytesAsync(outputManifestPath));
        Assert.NotEqual(priorIndex, await File.ReadAllBytesAsync(outputIndexPath));
    }

    [Theory]
    [InlineData("contractBundles", "source", "must declare source")]
    [InlineData("contractBundles", "authorityKind", "must declare package authority")]
    [InlineData("contractBundles", "authorityId", "must declare package authority")]
    [InlineData("dependsOn", "presence", "must declare presence")]
    public async Task AggregateTask_RejectsMissingProducerMetadataWithoutInferringDefaults(
        string group,
        string property,
        string diagnostic)
    {
        var root = CreateTempDirectory();
        var leaf = CreateRpcLeaf(root, "runtime", "linux-x64");
        var manifestPath = Path.Combine(leaf, "manifest", "sunder-package.json");
        var manifest = JsonNode.Parse(await File.ReadAllBytesAsync(manifestPath))!.AsObject();
        Assert.True(manifest[group]![0]!.AsObject().Remove(property));
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        PackageContentIndexer.Write(leaf);

        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var buildEngine = new TestBuildEngine();
        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(leaf)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        }.Execute());

        Assert.Contains(buildEngine.Errors, error => error.Contains(diagnostic, StringComparison.Ordinal));
        Assert.False(Directory.Exists(output));
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
        using var aggregateContender = OutputLockContenderBarrier<bool>.Start(
            GeneratedOutputLock.TargetLeafDiscoveryKey(discoveryRoot, leaf),
            aggregate.Execute,
            () => $"discoveryRoot={discoveryRoot}; leafPresent={Directory.Exists(leaf)}; backupPresent={Directory.Exists(backup)}");
        var aggregateTask = aggregateContender.Completion;
        try
        {
            await aggregateContender.WaitForAttemptAsync(TimeSpan.FromSeconds(5));
            await aggregateContender.AssertContendedAsync();
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
        var priorFiles = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .Append(marker).Append(output + SunderPackageFormat.DevelopmentImportsSuffix)
            .ToDictionary(path => path, File.ReadAllBytes);

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
        Assert.All(priorFiles, entry => Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key)));
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
        Assert.Contains(linux.Files, file => file.LogicalPath.ToString() == "lib/worker.cjs");
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
    public void AggregateTask_RejectsOldWorkerV3CapabilityLeaf()
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
                "rpc.v1",
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
                "cannot require unsupported worker protocol capability 'worker-protocol.v3'",
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
            ReferencePaths = SdkReferencePaths(includeAvalonia: true, includeStacks: true),
            DynamicAccessAcknowledgements = TestAssemblyDynamicAccessAcknowledgements(),
        };

    private static void AssertDocumentProperties(JsonElement document, JsonElement specification)
    {
        if (specification.TryGetProperty("schemaVersion", out var schemaVersion))
        {
            Assert.Equal(schemaVersion.GetInt32(), document.GetProperty("schemaVersion").GetInt32());
        }
        var expected = specification.GetProperty("properties").EnumerateArray()
            .Select(static property => property.GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = document.EnumerateObject().Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);
    }

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
        var processMarkerRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sunder", "generated-output", "processes");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(processMarkerRoot);
        else Directory.CreateDirectory(processMarkerRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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

    private static CapturedProcess StartCapturedNode(string helper, params string[] arguments)
        => StartCapturedNodeInTempRoot(null, helper, arguments);

    private static CapturedProcess StartCapturedNodeInTempRoot(string? tempRoot, string helper, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = FindRepositoryRoot(),
            UseShellExecute = false,
        };
        if (tempRoot is not null)
        {
            Directory.CreateDirectory(tempRoot);
            foreach (var name in new[] { "TMPDIR", "TMP", "TEMP" }) startInfo.Environment[name] = tempRoot;
        }
        startInfo.ArgumentList.Add(helper);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return CapturedProcess.Start(startInfo);
    }

    private static void TryWriteReleaseSignal(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "release");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Process-tree cleanup below remains authoritative if the signal cannot be written.
        }
    }

    private static void CaptureCleanup(ICollection<Exception> errors, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }

    private static async Task CaptureCleanupAsync(
        ICollection<Exception> errors,
        Func<ValueTask> cleanup)
    {
        try
        {
            await cleanup();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
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
        var inputs = devPackage + "-inputs";
        Directory.CreateDirectory(inputs);
        var generation = CreateTask(Path.Combine(inputs, "sunder-package.json"), buildEngine);
        Assert.True(
            generation.Execute(),
            string.Join(Environment.NewLine, buildEngine.Errors));
        File.Copy(generation.ManifestOutputPath, manifestPath);
        foreach (var file in generation.ResolvedContractFiles)
        {
            var destination = Path.Combine(devPackage, "payload", "shared", file.GetMetadata("DescriptorPath"));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file.ItemSpec, destination);
        }
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
            var executableName = CSharpWorkerModuleName();
            var executablePath = Path.Combine(targetDirectory, executableName);
            File.WriteAllText(executablePath, "worker");
            target.SetMetadata("Kind", SunderPackageFormat.WorkerTargetKind);
            ConfigureWorkerTask(manifestTask, rid, target);
            workerFiles = [.. CreateDotNetWorkerFiles(targetDirectory, rid, executablePath), new MSBuildTaskItem(nativePath)];
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
        var descriptorPath = WriteMinimalV1Descriptor(leafRoot);
        var manifestPath = Path.Combine(leafRoot, "sunder-package.json");
        var manifestEngine = new TestBuildEngine();
        var manifestTask = CreateTask(manifestPath, manifestEngine);
        manifestTask.ProjectDirectory = leafRoot;
        var target = new MSBuildTaskItem(role + "/" + rid);
        manifestTask.PackageTargets = [target];
        MSBuildTaskItem[] workerFiles = [];
        if (role == SunderPackageFormat.RuntimeHostRole)
        {
            var executableName = CSharpWorkerModuleName();
            var executablePath = Path.Combine(targetDirectory, executableName);
            File.WriteAllText(executablePath, "worker");
            target.SetMetadata("Kind", SunderPackageFormat.WorkerTargetKind);
            ConfigureWorkerTask(manifestTask, rid, target);
            workerFiles = CreateDotNetWorkerFiles(targetDirectory, rid, executablePath);
        }
        Assert.True(manifestTask.Execute(), string.Join(Environment.NewLine, manifestEngine.Errors));
        // This is an archive/aggregation fixture, not retired descriptor-first
        // authoring. Construct its complete current canonical contract evidence.
        var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!;
        var descriptor = Sunder.Sdk.Rpc.SunderRpcContractDescriptor.Parse(File.ReadAllBytes(descriptorPath));
        manifest["contractBundles"] = new JsonArray(new JsonObject
        {
            ["contractId"] = descriptor.ContractId, ["version"] = descriptor.Version, ["descriptorPath"] = "contracts/minimal.rpc.json",
            ["descriptorSha256"] = descriptor.Sha256, ["abiSha256"] = descriptor.AbiSha256, ["exposure"] = exposure,
            ["source"] = "owned", ["authorityKind"] = "package", ["authorityId"] = manifest["id"]!.GetValue<string>(),
        });
        manifest["usesContracts"] = includeUse ? new JsonArray(new JsonObject
        {
            ["role"] = role, ["contractId"] = descriptor.ContractId, ["contractVersion"] = descriptor.Version,
            ["abiSha256"] = descriptor.AbiSha256, ["required"] = required, ["actions"] = new JsonArray("discover", "invoke"),
        }) : new JsonArray();
        manifest["provides"] = includeProvider ? new JsonArray(new JsonObject
        {
            ["providerId"] = "test.rpc.provider", ["role"] = role, ["audience"] = audience,
            ["contractId"] = descriptor.ContractId, ["contractVersion"] = descriptor.Version,
            ["descriptorSha256"] = descriptor.Sha256, ["abiSha256"] = descriptor.AbiSha256,
        }) : new JsonArray();
        if (includeUse || includeProvider)
            foreach (var targetValue in manifest["targets"]!.AsArray())
            {
                var capabilities = targetValue!["requiredHostCapabilities"]!.AsArray();
                if (!capabilities.Any(value => value!.GetValue<string>() == SunderPackageFormat.RpcV1Capability)) capabilities.Add(SunderPackageFormat.RpcV1Capability);
            }
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var projection = new SunderRpcModelProjectionDocument(1, manifest["id"]!.GetValue<string>(), manifest["version"]!.GetValue<string>(), exposure == "public"
            ? [new(descriptor.ContractId, descriptor.Version, descriptor.Sha256, descriptor.AbiSha256, SunderRpcModelProjection.Empty)] : []);
        var projectionPath = Path.Combine(leafRoot, SunderPackageFormat.RpcModelProjectionFileName);
        File.WriteAllBytes(projectionPath, projection.ToCanonicalUtf8());
        var projectionItem = new MSBuildTaskItem(projectionPath); projectionItem.SetMetadata("DescriptorPath", SunderPackageFormat.RpcModelProjectionLogicalPath);
        var descriptorItem = new MSBuildTaskItem(descriptorPath); descriptorItem.SetMetadata("DescriptorPath", "contracts/minimal.rpc.json");
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
            ContractFiles = [descriptorItem, projectionItem],
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
        var executablePath = Path.Combine(leaf, "payload", "runtime", rid, "lib", "worker.cjs");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllText(executablePath, executableContent);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new SunderPackageManifest
        {
            ArchiveFormatVersion = 1,
            ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
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
                    EntryPoint = "lib/worker.cjs",
                    TargetFramework = "node24",
                    SdkVersion = "1.1.0",
                    RequiredHostCapabilities = requiredHostCapabilities
                        ?? ["execution-runtime.v1", "rpc.v1", "sdk-baseline-1-1.v1", "worker-protocol.v1"],
                    ExecutionRuntime = kind == SunderPackageFormat.WorkerTargetKind
                        ? new SunderPackageExecutionRuntimeManifest
                        {
                            Id = SunderPackageFormat.NodeExecutionRuntimeId,
                            VersionRange = ">=24.18.1 <25.0.0",
                            ModuleAbi = SunderPackageFormat.NodeWorkerModuleAbi,
                        }
                        : null,
                },
            ],
            ContractBundles = [],
            UsesContracts = [],
            Provides = [],
            PackageArtifactImports = [],
            PackageApiImports = [],
            ProviderImports = [],
            PlatformImports = [],
        }));
        var projectionPath = Path.Combine(leaf, SunderPackageFormat.RpcModelProjectionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(projectionPath)!);
        File.WriteAllBytes(projectionPath, new SunderRpcModelProjectionDocument(1, "test.process", "1.0.0", []).ToCanonicalUtf8());
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
            DynamicAccessAcknowledgements = Path.GetFileName(assemblyPath) == Path.GetFileName(Assembly.GetExecutingAssembly().Location)
                ? TestAssemblyDynamicAccessAcknowledgements() : [],
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
        task.SelfContained = false;
        task.UseAppHost = false;
        task.PublishSingleFile = false;
        task.PublishTrimmed = false;
        task.SdkCapabilities = task.SdkCapabilities
            .Append(new MSBuildTaskItem("worker-protocol.v1"))
            .ToArray();
    }

    private static string CSharpWorkerModuleName()
        => Path.GetFileName(Assembly.GetExecutingAssembly().Location);

    private static MSBuildTaskItem[] CreateDotNetWorkerFiles(
        string directory,
        string rid,
        string? modulePath = null)
    {
        modulePath ??= Path.Combine(directory, CSharpWorkerModuleName());
        Directory.CreateDirectory(directory);
        if (!File.Exists(modulePath)) File.WriteAllText(modulePath, "worker");
        var moduleName = Path.GetFileName(modulePath);
        var moduleIdentity = Path.GetFileNameWithoutExtension(moduleName);
        var runtimeTarget = $".NETCoreApp,Version=v10.0/{rid}";
        var runtimeConfigPath = Path.ChangeExtension(modulePath, ".runtimeconfig.json");
        var depsPath = Path.ChangeExtension(modulePath, ".deps.json");
        File.WriteAllText(runtimeConfigPath, """
            {"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}
            """);
        File.WriteAllText(depsPath, $$"""
            {
              "runtimeTarget": { "name": "{{runtimeTarget}}", "signature": "" },
              "targets": {
                "{{runtimeTarget}}": {
                  "{{moduleIdentity}}/1.0.0": { "runtime": { "{{moduleName}}": {} } }
                }
              },
              "libraries": {
                "{{moduleIdentity}}/1.0.0": { "type": "project", "serviceable": false, "sha512": "" }
              }
            }
            """);
        return
        [
            new MSBuildTaskItem(modulePath),
            new MSBuildTaskItem(runtimeConfigPath),
            new MSBuildTaskItem(depsPath),
        ];
    }

    private static MSBuildTaskItem[] SdkReferencePaths(bool includeAvalonia = false, bool includeStacks = false)
        => new[]
            {
                typeof(SunderPackageAttribute).Assembly.Location,
                includeAvalonia ? typeof(IAvaloniaPackageContributionRegistry).Assembly.Location : null,
                includeStacks ? typeof(IPackageStackExporter).Assembly.Location : null,
                typeof(SunderWorkerProviderRegistration).Assembly.Location,
            }
            .Where(static path => path is not null)
            .Select(static path => new MSBuildTaskItem(path!))
            .ToArray();

    private static string[] GetAssemblyCompanionPaths(string assemblyPath)
    {
        var paths = new[] { Path.ChangeExtension(assemblyPath, ".pdb"), Path.ChangeExtension(assemblyPath, ".xml") };
        Assert.All(paths, path => Assert.True(File.Exists(path), $"Assembly companion was not built: {path}"));
        return paths;
    }

    private static MSBuildTaskItem[] TestAssemblyDynamicAccessAcknowledgements()
        =>
        [
            new("Sunder.Package.Build.Tests.SunderRpcImportedBuildTests.ImportedProvider_CompilesRegistrationAndCoalescesOnlyExplicitConsumerGrants"),
            new("Sunder.Package.Build.Tests.SunderRpcImportedBuildTests.StackPlatform_GeneratesRealSixMethodProviderAndPreservesSemanticContentBehavior"),
            new("Sunder.Package.Build.Tests.SunderRpcImportedBuildTests.StackPlatform_TemplateUsesSelectedImportAndGeneratedRegistrationWithoutPrivateBootstrap"),
            new("Sunder.Package.Build.Tests.SunderRpcAuthoringReceiptTests.Parse_RejectsCrossArtifactSelectionsEvenWhenAuthorityCountAndFormatGraphMatch"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpImplementationCodecTests+RuntimeProbe..ctor"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpImportPrepassTests.InjectedPackageApiIsLazyOwnsUnaryScopesRejectsDriftAndDoesNotRetry"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpImportPrepassTests.ImportedExample"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpImportPrepassTests.GeneratedCancellation_UsesOriginalCallerAndRetainsRemoteDispatchMetadata"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpImportPrepassTests.GeneratedCatalogBorrowedSelectionProbesFreshPermissionAfterEndpointUnavailable"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpImportPrepassTests.InvokeCollectibleImportedGraph"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpImportPrepassTests.AssertNoCollectibleJsonAccessorEntries"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpImportPrepassTests.InjectedPackageStreamOpensLazilyAndRetirementCancelsAndDrainsItsScope"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpImportPrepassTests.InjectedRoleClientsUseReviewedRangeAndExactTupleWithoutRestoreVersionAuthority"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpUnionCodecTests+RuntimeProbe..ctor"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpUnionCodecTests+RuntimeProbe.CodecRoundTrip"),
            new("Sunder.Package.Build.Tests.StackContributorSourceParityTests.NeutralTransport_EmitsActualServicePartialAndCompilesWithOnlySdkAndCuratedBcl"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpProviderDeclarationTests.Declaration_ProvidesImmutableIdentityAndErrorsWithoutConstructingServiceAndForwardsExactly"),
            new("Sunder.Package.Build.Tests.SunderPackageRoleProjectionTests.Project_CompilesMixedSourceIntoSeparateRoleAssembliesWithoutImplementationLeakage"),
            new("Sunder.Package.Build.Tests.SunderPackageRoleProjectionTests.ConstructorValues_ExecuteTypedAppWorkerRoundTripsWithIndependentDomainRecords"),
            new("Sunder.Package.Build.Tests.SunderPackageRoleProjectionTests.GeneratedTypedStream_PreservesCancellationErrorsRetirementAndEnumeratorOwnership"),
            new("Sunder.Package.Build.Tests.SunderPackageRoleProjectionTests.GeneratedComposition_OwnsLocalServicesOnceAndPreservesStagedLifecycle"),
            new("Sunder.Package.Build.Tests.SunderPackageRoleProjectionTests.GeneratedStorageMigrations_SnapshotPlansAndPrepareInDependencyOrderWithoutLifecycleComposition"),
            new("Sunder.Package.Build.Tests.SunderPackageRoleProjectionTests.GeneratedWorkerMixedApi_KeepsOrdinaryCallsLocalAndRoutesHostContextThroughExplicitBrokerUse"),
            new("Sunder.Package.Build.Tests.SunderPackageRoleProjectionTests.GeneratedActivation_PreservesDependencyOrderCancellationAndCandidateCleanup"),
            new("Sunder.Package.Build.Tests.SunderPackageRoleProjectionTests.GeneratedCallbacks_PreserveSdkRoutingAndOwnServicesOnConstructionFailure"),
            new("Sunder.Package.Build.Tests.SunderPackageRoleProjectionTests.GeneratedAppComposition_UsesTransientModelsAndOneDisposableHelperForInterfaceConstructors"),
            new("Sunder.Package.Build.Tests.SunderPackageRoleProjectionTests.Configure"),
            new("Sunder.Package.Build.Tests.SunderPackageRoleProjectionTests.Stage"),
            new("Sunder.Package.Build.Tests.SunderPackageRoleProjectionTests.ShutdownReason"),
            new("Sunder.Package.Build.Tests.SunderPackageRoleProjectionTests.Number"),
            new("Sunder.Package.Build.Tests.ImplementationFirstRpcBuildTests.SingleProject_NugetAndProjectAssetsStayWithinTheirRole"),
            new("Sunder.Package.Build.Tests.SunderPackageRoleProjectionTests.DeclarationOnlyCatalogPreservesSixNaturalSignaturesAndDispatchesExactForeignProvider"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpImportPrepassTests.ImportedNaturalProviderNeedsNoContextUnitOrAuthoredTransportAdapter"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpImportPrepassTests.NaturalCatalogEntriesAreLazyExactBoundAndRetiredWithTheirOwner"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpImportPrepassTests.NaturalCatalogWatchPreservesBothCursorPartsLifecycleAndReset"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpImportPrepassTests.NaturalProviderContextFreeExplicitAndInheritedBindingsRemainValid"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpImportPrepassTests.NaturalProviderMixedMethodsReceiveTheActualHostContextForAuthorizationContentAndStreams"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpModelProjectionTests.LoadProjectionProbe"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpImportPrepassTests.MultiContractProviderUsesOneInstanceAndExplicitDescribeBindingsWithRealContext"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpImportPrepassTests.PrivateServiceReusesValidatedImportedRecordAndCodecWithoutClaimingItsOwnership"),
            new("Sunder.Package.Build.Tests.SunderRpcCSharpModelProjectionTests.ProjectedNullableRequiredResourcesRetainTypedMembersAndWithClones"),
        ];

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

    private static string GetFixtureAssemblyPath(string fixtureName, string? assemblyName = null)
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
            (assemblyName ?? fixtureName) + ".dll");

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

    private static string WriteMinimalV1Descriptor(string root, string version = "2.0.0")
    {
        var path = Path.Combine(root, "minimal.rpc.json");
        File.WriteAllText(
            path,
            MinimalV1Descriptor.Replace(
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
        item.SetMetadata("ContractId", "example.rpc.v1.minimal");
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
        var item = new MSBuildTaskItem("example.rpc.v1.minimal");
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
        item.SetMetadata("ContractId", "example.rpc.v1.minimal");
        item.SetMetadata("ContractVersion", version);
        item.SetMetadata("Audience", audience);
        return item;
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

    private const string MinimalV1Descriptor = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "descriptorVersion": 1,
          "schemaProfile": "sunder.rpc.values.v1",
          "contractId": "example.rpc.v1.minimal",
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
