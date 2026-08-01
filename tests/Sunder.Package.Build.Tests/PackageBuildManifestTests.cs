using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
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
using Sunder.Sdk.Runtime;
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
        Assert.Equal(1, root.GetProperty("manifestVersion").GetInt32());
        var targets = root.GetProperty("targets").EnumerateArray().ToArray();
        Assert.Equal(12, targets.Length);
        Assert.Equal(
            ["app", "runtime"],
            targets.Select(static target => target.GetProperty("role").GetString()!).Distinct().ToArray());
        Assert.All(targets, target =>
        {
            var role = target.GetProperty("role").GetString();
            Assert.Contains(target.GetProperty("rid").GetString(), SunderPackageFormat.SupportedRuntimeIdentifiers);
            Assert.Equal(role == "app" ? "avalonia" : "dotnet", target.GetProperty("kind").GetString());
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
            SunderSdkCapabilities.BackgroundServicesV1,
            SunderSdkCapabilities.RuntimeGenerationsV1,
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
            SunderSdkCapabilities.RuntimeOperationsV1,
            SunderSdkCapabilities.RuntimeInvocationErrorsV1,
            SunderSdkCapabilities.ThemingV1,
        }.Order(StringComparer.OrdinalIgnoreCase), capabilities.Order(StringComparer.OrdinalIgnoreCase));
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
    public void GenerateManifest_ExplicitTargetDeclarationsOverrideInferredMatrix()
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

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var generated = Assert.Single(document.RootElement.GetProperty("targets").EnumerateArray());
        Assert.Equal("runtime", generated.GetProperty("role").GetString());
        Assert.Equal("linux-arm64", generated.GetProperty("rid").GetString());
        Assert.Equal("dotnet", generated.GetProperty("kind").GetString());
        Assert.Equal("lib/custom-entry.dll", generated.GetProperty("entryPoint").GetString());
    }

    [Fact]
    public async Task GenerateManifest_AuthorsAndEmitsStrictRpcContracts()
    {
        var root = CreateTempDirectory();
        var descriptorPath = Path.Combine(root, "chat.rpc.json");
        File.WriteAllText(descriptorPath, """
            {
              "descriptorVersion": 1,
              "contractId": "test.agent.chat",
              "version": "1.0.0",
              "services": [{
                "serviceId": "chat-provider",
                "methods": [{
                  "methodId": "describe",
                  "kind": "unary",
                  "requestSchema": { "$ref": "#/$defs/Empty" },
                  "responseSchema": { "$ref": "#/$defs/Description" }
                }]
              }],
              "$defs": {
                "Empty": { "type": "object", "properties": {}, "required": [], "additionalProperties": false },
                "Description": {
                  "type": "object",
                  "properties": { "name": { "type": "string", "minLength": 1, "maxLength": 128 } },
                  "required": ["name"],
                  "additionalProperties": false
                }
              }
            }
            """);
        var manifestPath = Path.Combine(root, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        var bundle = new MSBuildTaskItem(descriptorPath);
        bundle.SetMetadata("ContractId", "test.agent.chat");
        bundle.SetMetadata("Version", "1.0.0");
        bundle.SetMetadata("DescriptorPath", "contracts/chat.rpc.json");
        var use = new MSBuildTaskItem("test.agent.chat");
        use.SetMetadata("VersionRange", ">=1.0.0 <2.0.0");
        use.SetMetadata("Required", "false");
        use.SetMetadata("Actions", "discover;invoke");
        var provider = new MSBuildTaskItem("test.package.chat");
        provider.SetMetadata("ContractId", "test.agent.chat");
        provider.SetMetadata("ContractVersion", "1.0.0");
        provider.SetMetadata("Role", "runtime");
        task.ContractBundles = [bundle];
        task.UsesContracts = [use];
        task.RpcProviders = [provider];

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var generatedBundle = Assert.Single(document.RootElement.GetProperty("contractBundles").EnumerateArray());
        Assert.Equal("contracts/chat.rpc.json", generatedBundle.GetProperty("descriptorPath").GetString());
        Assert.Equal(64, generatedBundle.GetProperty("sha256").GetString()!.Length);
        var generatedUse = Assert.Single(document.RootElement.GetProperty("usesContracts").EnumerateArray());
        Assert.False(generatedUse.GetProperty("required").GetBoolean());
        Assert.Equal(
            ["discover", "invoke"],
            generatedUse.GetProperty("actions").EnumerateArray().Select(static value => value.GetString()!).ToArray());
        var generatedProvider = Assert.Single(document.RootElement.GetProperty("provides").EnumerateArray());
        Assert.Equal(generatedBundle.GetProperty("sha256").GetString(), generatedProvider.GetProperty("contractSha256").GetString());

        var targetDirectory = Path.Combine(root, "target");
        var devPackage = Path.Combine(root, "sunder-dev");
        Directory.CreateDirectory(targetDirectory);
        var assemblyPath = Path.Combine(targetDirectory, Path.GetFileName(Assembly.GetExecutingAssembly().Location));
        File.Copy(Assembly.GetExecutingAssembly().Location, assemblyPath);
        var emitEngine = new TestBuildEngine();
        Assert.True(new EmitSunderDevOutputTask
        {
            BuildEngine = emitEngine,
            ManifestPath = manifestPath,
            DevPackagePath = devPackage,
            TargetDirectory = targetDirectory,
            ManagedFiles = [new MSBuildTaskItem(assemblyPath)],
            ContractFiles = task.ResolvedContractFiles,
        }.Execute(), string.Join(Environment.NewLine, emitEngine.Errors));
        Assert.True(File.Exists(Path.Combine(devPackage, "payload", "shared", "contracts", "chat.rpc.json")));
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(devPackage);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
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
    }

    [Fact]
    public void GenerateManifest_FailsWhenDynamicSdkAccessHasNoExplicitCapabilityDeclaration()
    {
        var assemblyPath = CopyFixtureAssemblyToIsolatedDirectory("AuthoredDynamicReflection");
        var manifestPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateFixtureTask(assemblyPath, manifestPath, buildEngine);
        task.ReferencePaths = SdkReferencePaths();

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
        task.SdkCapabilities = [new MSBuildTaskItem(SunderSdkCapabilities.CallbacksV1)];
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
        task.SdkCapabilities = [new MSBuildTaskItem(SunderSdkCapabilities.CallbacksV1)];
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
            SunderSdkCapabilities.ViewNavigationPreparationV1,
            SunderSdkCapabilities.RuntimeOperationsV1,
            SunderSdkCapabilities.RuntimeInvocationErrorsV1);
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
        };

        Assert.True(task.Execute());
        Assert.True(Path.EndsInDirectorySeparator(task.NormalizedDevOutputPath));
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
        var targetFile = Path.Combine(FindRepositoryRoot(), "src", "Sdk", "Sunder.Package.Build", "build", "Sunder.Package.Build.targets");
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
    public async Task PackPackage_IndexesAssetNamedContentIndexJson()
    {
        var root = CreateTempDirectory();
        var devPackage = Path.Combine(root, "sunder-dev");
        var manifestEngine = new TestBuildEngine();
        CreateValidDevPackage(devPackage, manifestEngine);
        Directory.CreateDirectory(Path.Combine(devPackage, "payload", "shared", "assets"));
        File.WriteAllText(Path.Combine(devPackage, "payload", "shared", "assets", "content-index.json"), "asset");
        var output = Path.Combine(root, "package.sunderpkg");
        var buildEngine = new TestBuildEngine();

        Assert.True(new PackSunderPackageTask
        {
            BuildEngine = buildEngine,
            DevPackagePath = devPackage,
            PackageOutputPath = output,
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
    public void PackPackage_RejectsPackageThatFailsConsumerFormatValidation()
    {
        var root = CreateTempDirectory();
        var devPackage = Path.Combine(root, "sunder-dev");
        Directory.CreateDirectory(Path.Combine(devPackage, "manifest"));
        Directory.CreateDirectory(Path.Combine(devPackage, "payload", "shared", "lib"));
        File.WriteAllText(Path.Combine(devPackage, "manifest", "sunder-package.json"), "{}");
        File.WriteAllText(Path.Combine(devPackage, "payload", "shared", "lib", "Package.dll"), "assembly");
        var output = Path.Combine(root, "package.sunderpkg");
        var buildEngine = new TestBuildEngine();

        Assert.False(new PackSunderPackageTask
        {
            BuildEngine = buildEngine,
            DevPackagePath = devPackage,
            PackageOutputPath = output,
        }.Execute());

        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("manifestVersion", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void PackTask_ValidatesTheWrittenArchiveAfterDeterministicCreation()
    {
        var source = File.ReadAllText(Path.Combine(FindTaskDirectory(), "PackSunderPackageTask.cs"));
        var writeIndex = source.IndexOf(
            "DeterministicPackageArchiveWriter.Write(DevPackagePath, PackageOutputPath)",
            StringComparison.Ordinal);
        var archiveValidationIndex = source.IndexOf(
            "ExtractAndValidateAsync(PackageOutputPath, archiveValidationPath)",
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
        target.SetMetadata("Role", "runtime");
        target.SetMetadata("Rid", "win-x64");
        manifestTask.PackageTargets = [target];
        Assert.True(manifestTask.Execute(), string.Join(Environment.NewLine, manifestEngine.Errors));

        var emitEngine = new TestBuildEngine();
        Assert.True(new EmitSunderDevOutputTask
        {
            BuildEngine = emitEngine,
            ManifestPath = manifestPath,
            DevPackagePath = devPackage,
            TargetDirectory = targetDirectory,
            ManagedFiles = [new MSBuildTaskItem(assemblyPath)],
            NativeRuntimeFiles = [new MSBuildTaskItem(winNative), new MSBuildTaskItem(linuxNative)],
        }.Execute(), string.Join(Environment.NewLine, emitEngine.Errors));

        Assert.True(File.Exists(Path.Combine(devPackage, "manifest", "sunder-package.json")));
        Assert.True(File.Exists(Path.Combine(devPackage, "manifest", "content-index.json")));
        Assert.True(File.Exists(Path.Combine(devPackage, "payload", "shared", "lib", Path.GetFileName(assemblyPath))));
        Assert.True(File.Exists(Path.Combine(devPackage, "payload", "runtime", "win-x64", "lib", "runtimes", "win-x64", "native", "fixture.bin")));
        Assert.False(Directory.Exists(Path.Combine(devPackage, "payload", "runtime", "linux-x64")));
        Assert.False(Directory.Exists(Path.Combine(devPackage, "lib")));
        Assert.False(Directory.Exists(Path.Combine(devPackage, "assets")));
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(devPackage);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
    }

    [Fact]
    public async Task AggregateTask_FactorsIdenticalFilesAndProducesDeterministicUniversalTree()
    {
        var root = CreateTempDirectory();
        var appLeaf = CreateCanonicalLeaf(root, "app", "win-x64", "app-native");
        var runtimeLeaf = CreateCanonicalLeaf(root, "runtime", "win-x64", "runtime-native");
        var output = Path.Combine(root, "universal");
        var buildEngine = new TestBuildEngine();
        var task = new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [new MSBuildTaskItem(appLeaf), new MSBuildTaskItem(runtimeLeaf)],
            OutputPath = output,
        };

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        var firstIndex = await File.ReadAllBytesAsync(Path.Combine(output, "manifest", "content-index.json"));
        Assert.True(File.Exists(Path.Combine(output, "payload", "shared", "lib", Path.GetFileName(Assembly.GetExecutingAssembly().Location))));
        Assert.True(File.Exists(Path.Combine(output, "payload", "app", "shared", "lib", "runtimes", "win-x64", "native", "fixture.bin")));
        Assert.True(File.Exists(Path.Combine(output, "payload", "runtime", "shared", "lib", "runtimes", "win-x64", "native", "fixture.bin")));
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(output);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        Assert.Equal(2, validation.Manifest!.Targets!.Count);

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        Assert.Equal(firstIndex, await File.ReadAllBytesAsync(Path.Combine(output, "manifest", "content-index.json")));
    }

    [Fact]
    public void AggregateTask_RejectsDuplicateExactTargets()
    {
        var root = CreateTempDirectory();
        var leaf = CreateCanonicalLeaf(root, "runtime", "linux-x64", "native");
        var buildEngine = new TestBuildEngine();

        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [new MSBuildTaskItem(leaf), new MSBuildTaskItem(leaf)],
            OutputPath = Path.Combine(root, "universal"),
        }.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AggregateTask_AcceptsProcessTargetLeavesWithoutForeignRidLeakage()
    {
        var root = CreateTempDirectory();
        var first = CreateProcessLeaf(root, "linux-x64", "linux-sea");
        var second = CreateProcessLeaf(root, "osx-arm64", "mac-sea");
        var output = Path.Combine(root, "universal");
        var buildEngine = new TestBuildEngine();

        Assert.True(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [new MSBuildTaskItem(first), new MSBuildTaskItem(second)],
            OutputPath = output,
        }.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));

        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(output);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        Assert.All(validation.Manifest!.Targets!, target => Assert.Equal(SunderPackageFormat.ProcessTargetKind, target!.Kind));
        var linux = SunderPackageTargetResolver.CreateProjectionPlan(
            validation.Manifest,
            validation.ContentIndex!,
            new SunderPackageTargetKey("runtime", "linux-x64"));
        Assert.Contains(linux.Files, file => file.LogicalPath.ToString() == "bin/worker");
        Assert.DoesNotContain(linux.Files, file => file.PhysicalPath.ToString().Contains("osx-arm64", StringComparison.Ordinal));
    }

    [Fact]
    public void ManifestGeneration_UsesFocusedCollaboratorsAndSizeRatchets()
    {
        var taskDirectory = FindTaskDirectory();
        var taskPath = Path.Combine(taskDirectory, "GenerateSunderPackageManifestTask.cs");
        var taskSource = File.ReadAllText(taskPath);
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

        Assert.True(File.ReadLines(taskPath).Count() < 150);
        Assert.False(File.Exists(Path.Combine(taskDirectory, "GenerateSunderPackageManifestTaskAdapter.cs")));
        Assert.DoesNotContain("SdkApiVersion { get; set; }", taskSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SdkVersion { get; set; }", taskSource, StringComparison.Ordinal);
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
        Assert.True(manifestTask.Execute(), string.Join(Environment.NewLine, manifestEngine.Errors));
        var emitEngine = new TestBuildEngine();
        Assert.True(new EmitSunderDevOutputTask
        {
            BuildEngine = emitEngine,
            ManifestPath = manifestPath,
            DevPackagePath = devPackage,
            TargetDirectory = targetDirectory,
            ManagedFiles = [new MSBuildTaskItem(assemblyPath)],
            NativeRuntimeFiles = [new MSBuildTaskItem(nativePath)],
        }.Execute(), string.Join(Environment.NewLine, emitEngine.Errors));
        return devPackage;
    }

    private static string CreateProcessLeaf(string root, string rid, string executableContent)
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
            ManifestVersion = 1,
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
                    Kind = "process",
                    EntryPoint = "bin/worker",
                    TargetFramework = "node24",
                    SdkVersion = "1.1.0",
                    RequiredHostCapabilities = ["rpc.v1", "sdk-baseline-1-1.v1"],
                },
            ],
            ContractBundles = [],
            UsesContracts = [],
            Provides = [],
        }));
        var buildEngine = new TestBuildEngine();
        Assert.True(new PackSunderPackageTask
        {
            BuildEngine = buildEngine,
            DevPackagePath = leaf,
            PackageOutputPath = Path.Combine(root, $"process-{rid}.sunderpkg"),
        }.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        return leaf;
    }

    private static GenerateSunderPackageManifestTask CreateFixtureTask(
        string assemblyPath,
        string manifestPath,
        IBuildEngine buildEngine)
        => new()
        {
            BuildEngine = buildEngine,
            TargetAssemblyPath = assemblyPath,
            ManifestOutputPath = manifestPath,
            EntryAssembly = Path.GetFileName(assemblyPath),
            PackageVersion = "1.2.3",
            ProjectDirectory = Path.GetDirectoryName(assemblyPath)!,
            TargetFramework = "net10.0",
        };

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

    private static string GetFixtureAssemblyPath(string fixtureName)
        => Path.Combine(
            FindRepositoryRoot(),
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

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-package-build-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindTaskDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Sdk", "Sunder.Package.Build", "Tasks");
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
}

public sealed class FixturePackageModule : ISunderRuntimePackageModule, ISunderAppPackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        _ = context.Settings;
        _ = context.Storage.State;
        _ = context.Secrets;
        _ = context.Logging.Events;
        services.AddSingleton<IPackageCallbackHandler, FixtureAuthHandler>();
        services.AddSingleton<IPackageAuthHandler, FixtureAuthHandler>();
        services.AddSingleton<IPackageNotificationService, NullPackageNotificationService>();
        services.AddSingleton<FixtureStackContributor>();
        services.AddSingleton<FixtureRuntimeOperationHandler>();
        services.AddSingleton<FixtureRuntimeStreamHandler>();
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterBackgroundService<FixtureBackgroundService>();
        var stackContributor = services.GetRequiredService<FixtureStackContributor>();
        registry.RegisterStackContributor("fixture.stack", stackContributor, services);
        registry.RegisterSettingsSchema(new PackageSettingsSchema(
            null,
            [new PackageSettingsSection(
                "general",
                "General",
                null,
                [new PackageSettingsField("enabled", "Enabled", PackageSettingsFieldKind.Boolean)])]));
        registry.RegisterRuntimeOperation(
            new PackageRuntimeOperation<FixtureRuntimeRequest, FixtureRuntimeResponse>("fixture.run"),
            services.GetRequiredService<FixtureRuntimeOperationHandler>());
        registry.RegisterRuntimeStream(
            new PackageRuntimeStream<FixtureRuntimeRequest, FixtureRuntimeResponse>("fixture.events"),
            services.GetRequiredService<FixtureRuntimeStreamHandler>());
    }

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        _ = Type.GetType("Sunder.Sdk.Callbacks.PackageCallbackStartContext, Sunder.Sdk");
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterPackageView<FixtureView>(new PackageViewRegistration("fixture.view", "Fixture View"));
        registry.RegisterSettingsView<FixtureSettingsView>();
    }
}

public sealed record FixtureRuntimeRequest(string Value);

public sealed record FixtureRuntimeResponse(string Value);

public sealed class FixtureRuntimeOperationHandler
    : IPackageRuntimeOperationHandler<FixtureRuntimeRequest, FixtureRuntimeResponse>
{
    public ValueTask<FixtureRuntimeResponse> HandleAsync(
        FixtureRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new FixtureRuntimeResponse(request.Value));
    }
}

public sealed class FixtureRuntimeStreamHandler
    : IPackageRuntimeStreamHandler<FixtureRuntimeRequest, FixtureRuntimeResponse>
{
    public async IAsyncEnumerable<FixtureRuntimeResponse> SubscribeAsync(
        FixtureRuntimeRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return new FixtureRuntimeResponse(request.Value);
    }
}

public sealed class FixtureView : UserControl;

public sealed class FixtureSettingsView : UserControl;

public sealed class FixtureBackgroundService : IPackageRuntimeGenerationParticipant
{
    public Task StartAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task CommitGenerationAsync(
        PackageRuntimeGeneration generation,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

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

    public PackageRuntimeInvocationException RuntimeFailure { get; }
        = new("runtime.v1.fixture", isTransient: false);
}
