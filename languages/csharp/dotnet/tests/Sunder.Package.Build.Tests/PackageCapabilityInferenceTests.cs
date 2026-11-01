using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Sunder.Package.Build.Tasks;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Stacks;
using Xunit;
using MSBuildTaskItem = Microsoft.Build.Utilities.TaskItem;
using static Sunder.Package.Build.Tests.PackageManifestFixture;

namespace Sunder.Package.Build.Tests;

[Collection("Package build artifacts")]
public sealed class PackageCapabilityInferenceTests
{
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

    private static string[] GetAssemblyCompanionPaths(string assemblyPath)
    {
        var paths = new[] { Path.ChangeExtension(assemblyPath, ".pdb"), Path.ChangeExtension(assemblyPath, ".xml") };
        Assert.All(paths, path => Assert.True(File.Exists(path), $"Assembly companion was not built: {path}"));
        return paths;
    }
}
