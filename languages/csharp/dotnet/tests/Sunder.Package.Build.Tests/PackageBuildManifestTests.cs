using System.Buffers.Binary;
using System.Runtime.InteropServices;
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
using static Sunder.Package.Build.Tests.PackageManifestFixture;
using static Sunder.Package.Build.Tests.PackageOutputFixture;

namespace Sunder.Package.Build.Tests;

[Collection("Package build artifacts")]
public sealed class PackageBuildManifestTests
{
    [Fact]
    public void BuildTaskAssembly_UsesStableOneBinaryVersion()
        => Assert.Equal(new Version(1, 0, 0, 0), typeof(GenerateSunderPackageManifestTask).Assembly.GetName().Version);

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
            Assert.Equal($"lib/{Path.GetFileName(ManifestAssemblyPath)}", target.GetProperty("entryPoint").GetString());
            Assert.Equal("net10.0", target.GetProperty("targetFramework").GetString());
            Assert.False(string.IsNullOrWhiteSpace(target.GetProperty("sdkVersion").GetString()));
            Assert.Equal("12.1.4", target.GetProperty("avaloniaVersion").GetString()!.Split('+')[0]);
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
            "app-bridge.v1",
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
        var assemblyPath = CopyAssemblyWithIncompatibleMachine(ManifestAssemblyPath);
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
}
