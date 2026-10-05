using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeArchitectureRatchetTests
{
    [Fact]
    public void RuntimeCompatibilityProfile_SupportsEveryPublishedSdkCapability()
    {
        var publishedCapabilities = typeof(SunderSdkCapabilities)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var key = new SunderPackageTargetKey("runtime", "win-x64");
        var target = CreateTarget("1.1.0", publishedCapabilities);

        Assert.Empty(SunderSdkCompatibilityProfile.Validate("test.package", key, target));
    }

    [Fact]
    public void WorkerV1CompatibilityProfile_ClassifiesEveryPublishedSdkCapability()
    {
        var publishedCapabilities = typeof(SunderSdkCapabilities)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!)
            .Append(SunderPackageFormat.ExecutionRuntimeV1Capability)
            .Append(SunderWorkerProtocol.V1Capability)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            publishedCapabilities,
            SunderSdkCompatibilityProfile.WorkerV1CapabilityClassification.Keys
                .Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0-beta.1")]
    [InlineData("1.2.0")]
    public void RuntimeCompatibilityProfile_RejectsPackagesOutsideCoordinated11Baseline(string sdkVersion)
    {
        var errors = SunderSdkCompatibilityProfile.Validate(
            "test.package",
            new SunderPackageTargetKey("runtime", "win-x64"),
            CreateTarget(sdkVersion, [SunderSdkCapabilities.Baseline11V1]));

        Assert.Contains(errors, error => error.Contains("requires >=1.1.0 <1.2.0", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1.1.0")]
    [InlineData("1.1.7")]
    [InlineData("1.1.7+build.42")]
    public void RuntimeCompatibilityProfile_AcceptsCompatible11PackageVersions(string sdkVersion)
    {
        var errors = SunderSdkCompatibilityProfile.Validate(
            "test.package",
            new SunderPackageTargetKey("runtime", "win-x64"),
            CreateTarget(sdkVersion, [SunderSdkCapabilities.Baseline11V1]));

        Assert.Empty(errors);
    }

    private static SunderPackageTargetManifest CreateTarget(
        string sdkVersion,
        IReadOnlyList<string> capabilities)
        => new()
        {
            Role = "runtime",
            Rid = "win-x64",
            Kind = SunderPackageFormat.WorkerTargetKind,
            EntryPoint = "lib/test.dll",
            TargetFramework = "net10.0",
            SdkVersion = sdkVersion,
            RequiredHostCapabilities = capabilities.Cast<string?>().ToArray(),
        };

    [Fact]
    public void SecuritySensitiveStorageOrchestrators_StaySmallAndDelegatePlatformAndPersistenceWork()
    {
        var root = LocateRepositoryRoot();
        var runtimeRoot = Path.Combine(root, "languages", "csharp", "dotnet", "host", "Sunder.Runtime.Host");
        var protection = File.ReadAllText(Path.Combine(
            runtimeRoot, "Infrastructure", "Storage", "MasterKeyProtection.cs"));
        Assert.DoesNotContain("ProtectedData.", protection, StringComparison.Ordinal);
        Assert.DoesNotContain("/usr/bin/security", protection, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-tool", protection, StringComparison.Ordinal);
        Assert.DoesNotContain("CapturedProcessRunner", protection, StringComparison.Ordinal);

        var stores = File.ReadAllText(Path.Combine(
                runtimeRoot, "Infrastructure", "Storage", "JsonPackageSecretsStore.cs"))
            + File.ReadAllText(Path.Combine(
                runtimeRoot, "Infrastructure", "Storage", "JsonPackageKeyValueStore.cs"));
        Assert.DoesNotContain("JsonDocument", stores, StringComparison.Ordinal);
        Assert.DoesNotContain("cipher.Encrypt(", stores, StringComparison.Ordinal);
        Assert.DoesNotContain("cipher.Decrypt(", stores, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateAssociatedData", stores, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer", stores, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Move", stores, StringComparison.Ordinal);

        var coordinator = File.ReadAllText(Path.Combine(runtimeRoot, "Services", "PackageStoreCoordinator.cs"));
        Assert.DoesNotContain("JsonSerializer", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Move", coordinator, StringComparison.Ordinal);
        Assert.Contains(nameof(PackageStoreStageManager), coordinator, StringComparison.Ordinal);
        Assert.Contains(nameof(PackageStoreTransactionManager), coordinator, StringComparison.Ordinal);
        Assert.Contains(nameof(PackageStoreRecovery), coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeTransportFlows_UseCentralBoundedResponseReaders()
    {
        var root = LocateRepositoryRoot();
        var registryFiles = new[]
        {
            "RegistryAuthCoordinator.cs",
            "RegistryAuthenticatedOperations.cs",
            "RegistryPackageChangeOrchestrator.cs",
        };
        var registrySource = string.Join('\n', registryFiles.Select(file => File.ReadAllText(Path.Combine(
            root, "languages", "csharp", "dotnet", "host", "Sunder.Runtime.Host", "Services", file))));
        Assert.DoesNotContain("ReadFromJsonAsync", registrySource, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureSuccessStatusCode", registrySource, StringComparison.Ordinal);
        Assert.Contains(nameof(RegistryHttpClient), registrySource, StringComparison.Ordinal);

        var clientRoot = Path.Combine(root, "languages", "csharp", "dotnet", "host", "Sunder.Runtime.Client");
        var clientSource = string.Join('\n', Directory.EnumerateFiles(clientRoot, "*.cs")
            .Where(path => Path.GetFileName(path) != "RuntimeHttpResponseReader.cs")
            .Select(File.ReadAllText));
        Assert.DoesNotContain("ReadFromJsonAsync", clientSource, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureSuccessStatusCode", clientSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAsByteArrayAsync", clientSource, StringComparison.Ordinal);

        var processSource = File.ReadAllText(Path.Combine(
            root,
            "languages",
            "csharp",
            "dotnet",
            "host",
            "Sunder.Runtime.LocalState",
            "CapturedProcessRunner.cs"));
        Assert.Contains("MaxCapturedCharactersPerStream", processSource, StringComparison.Ordinal);
        Assert.Contains("OutputTruncated", processSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeHost_DoesNotReferenceRuntimeClient()
    {
        var root = LocateRepositoryRoot();
        var hostProject = File.ReadAllText(Path.Combine(
            root, "languages", "csharp", "dotnet", "host", "Sunder.Runtime.Host", "Sunder.Runtime.Host.csproj"));
        var clientProject = File.ReadAllText(Path.Combine(
            root, "languages", "csharp", "dotnet", "host", "Sunder.Runtime.Client", "Sunder.Runtime.Client.csproj"));

        Assert.DoesNotContain("Sunder.Runtime.Client.csproj", hostProject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sunder.Runtime.LocalState.csproj", hostProject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sunder.Runtime.LocalState.csproj", clientProject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeHost_RefreshesExecutionRuntimePolicyOnlyAfterPackageActivationStarts()
    {
        var root = LocateRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(
            root,
            "languages",
            "csharp",
            "dotnet",
            "host",
            "Sunder.Runtime.Host",
            "Program.cs"));

        var bootstrap = program.IndexOf("RuntimeHostBootstrapRunner.StartAsync", StringComparison.Ordinal);
        var maintenance = program.IndexOf("executionRuntimeMaintenance.RunOnceAsync", StringComparison.Ordinal);

        Assert.DoesNotContain("RefreshBeforeActivationAsync", program, StringComparison.Ordinal);
        Assert.True(bootstrap >= 0);
        Assert.True(maintenance > bootstrap);
    }

    [Fact]
    public void RuntimeStacks_DoNotExposeContributorFilesystemPaths()
    {
        var root = LocateRepositoryRoot();
        var stackSources = string.Join('\n', new[]
        {
            "RuntimeStackExportService.cs",
            "RuntimeStackImportService.cs",
            "StackExportArchiveBuilder.cs",
            "StackImportArchiveReader.cs",
        }.Select(file => File.ReadAllText(Path.Combine(
            root, "languages", "csharp", "dotnet", "host", "Sunder.Runtime.Host", "Services", file))));

        Assert.DoesNotContain("SourcePath", stackSources, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtractedPath", stackSources, StringComparison.Ordinal);
        Assert.Contains("OpenReadAsync", stackSources, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeEndpoints_DependOnFocusedServicesInsteadOfSessionFacadeOrStateOwners()
    {
        var endpointRoot = Path.Combine(
            LocateRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "host",
            "Sunder.Runtime.Host",
            "Endpoints");
        var source = string.Join('\n', Directory.EnumerateFiles(endpointRoot, "*.cs").Select(File.ReadAllText));

        Assert.DoesNotContain("RuntimePackageSessionService", source, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(RuntimeSessionOwner), source, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(PackageSessionState), source, StringComparison.Ordinal);
        Assert.Contains(nameof(PackageSessionLifecycleService), source, StringComparison.Ordinal);
        Assert.Contains(nameof(InstalledPackageLifecycleService), source, StringComparison.Ordinal);
        Assert.Contains(nameof(RuntimeStackExportService), source, StringComparison.Ordinal);
        Assert.Contains(nameof(RuntimeStackImportService), source, StringComparison.Ordinal);
        Assert.Contains(nameof(PackageSettingsAccessService), source, StringComparison.Ordinal);
        Assert.Contains(nameof(PackageAuthAccessService), source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeSessionGeneration_HasOneStateOwner()
    {
        var servicesRoot = Path.Combine(
            LocateRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "host",
            "Sunder.Runtime.Host",
            "Services");
        var owners = Directory.EnumerateFiles(servicesRoot, "*.cs")
            .Where(path => File.ReadAllText(path).Contains("private long _generation;", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)!)
            .ToArray();

        Assert.Equal(new[] { "PackageSessionState.cs" }, owners);
        Assert.Single(typeof(PackageSessionState).GetMethods(BindingFlags.Instance | BindingFlags.Public), method => method.Name == nameof(PackageSessionState.PublishSessionAsync));
    }

    [Fact]
    public void RuntimeSessionPublication_ExposesReadOnlyStatusAndImmutableDescriptorSnapshot()
    {
        var sessionEntry = typeof(PackageSessionState).GetNestedType(
            "SessionEntry",
            BindingFlags.NonPublic);
        var mutablePublication = typeof(PackageSessionState).GetNestedType(
            "MutableSessionPublication",
            BindingFlags.NonPublic);
        var publication = typeof(PackageSessionState).GetNestedType(
            "SessionPublication",
            BindingFlags.NonPublic);
        var publicationSnapshot = typeof(PackageSessionState).GetNestedType(
            "SessionPublicationSnapshot",
            BindingFlags.NonPublic);

        Assert.NotNull(sessionEntry);
        Assert.True(sessionEntry.IsNestedPrivate);
        Assert.NotNull(mutablePublication);
        Assert.True(mutablePublication.IsNestedPrivate);
        Assert.NotNull(publication);
        Assert.True(publication.IsNestedAssembly);
        Assert.True(publication.IsInterface);
        var properties = publication.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        Assert.Equal(
            new[]
            {
                "Abandoned",
                "CandidateGeneration",
                "Committed",
                "Completed",
                "ExpectedGeneration",
                "PreviousSession",
                "PreviousWorkersQuiesced",
                "RecoveryCommitted",
                "Session",
            },
            properties.Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.All(properties, property => Assert.Null(property.SetMethod));
        Assert.NotNull(publicationSnapshot);
        Assert.True(publicationSnapshot.IsNestedAssembly);
        Assert.True(publicationSnapshot.IsSealed);
        var snapshotProperties = publicationSnapshot.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        Assert.Equal(
            new[] { "ActivePackages", "Generation", "SessionPackages" },
            snapshotProperties.Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.All(snapshotProperties, property => Assert.Null(property.SetMethod));
        Assert.DoesNotContain(
            snapshotProperties,
            property => property.PropertyType == typeof(ActivePackageSession));
        var snapshotConstructor = Assert.Single(publicationSnapshot.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.True(snapshotConstructor.IsPrivate);
    }

    [Fact]
    public void RuntimeContracts_RemainHostAndSdkNeutral()
    {
        var references = typeof(SystemStatusResponse).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();

        Assert.DoesNotContain(references, name => name is not null
            && (name.StartsWith("Sunder.Sdk", StringComparison.Ordinal)
                || name.StartsWith("Sunder.Package.Format", StringComparison.Ordinal)
                || name.StartsWith("Sunder.Runtime.Host", StringComparison.Ordinal)
                || name.StartsWith("Sunder.Registry.Contracts", StringComparison.Ordinal)));
        Assert.DoesNotContain(typeof(SystemStatusResponse).Assembly.ExportedTypes, type =>
            type.GetProperties().Any(property => property.PropertyType.Assembly == typeof(PackageSessionState).Assembly));
    }

    [Fact]
    public void ExistingRuntimeEndpointRoutes_RemainMapped()
    {
        var endpointRoot = Path.Combine(
            LocateRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "host",
            "Sunder.Runtime.Host",
            "Endpoints");
        var source = string.Join('\n', Directory.EnumerateFiles(endpointRoot, "*.cs").Select(File.ReadAllText));
        var stableRoutes = new[]
        {
            "session/load", "session/stage", "store/stage",
            "snapshot", "ui-snapshots", "auth/status", "settings/values", "export/items", "import/preview",
            "/downloads/{downloadId}", "/dev-package-owners", "runtime-events", "package-logs",
        };

        foreach (var route in stableRoutes) Assert.Contains(route, source, StringComparison.Ordinal);
        Assert.DoesNotContain("config/values", source, StringComparison.Ordinal);
        Assert.DoesNotContain("data/configuration", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeProtocolHandshake_PrecedesVersionedRoutesAndProductSemVerIsNotCompatibility()
    {
        var root = LocateRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(
            root,
            "languages",
            "csharp",
            "dotnet",
            "host",
            "Sunder.Runtime.Host",
            "Program.cs"));
        var handshakeIndex = program.IndexOf("MapRuntimeHandshakeEndpoint", StringComparison.Ordinal);
        var versionedGroupIndex = program.IndexOf("MapGroup(\"/api/v1\")", StringComparison.Ordinal);
        var resourcePolicyIndex = program.IndexOf(
            "RuntimeResourcePolicyLaunch.ReadAndClearEnvironment",
            StringComparison.Ordinal);
        var builderIndex = program.IndexOf("WebApplication.CreateBuilder", StringComparison.Ordinal);
        var appServices = Path.Combine(root, "languages", "csharp", "dotnet", "host", "Sunder.App", "Services");

        Assert.True(handshakeIndex >= 0 && handshakeIndex < versionedGroupIndex);
        Assert.True(resourcePolicyIndex >= 0 && resourcePolicyIndex < builderIndex);
        Assert.False(File.Exists(Path.Combine(appServices, "RuntimeHostVersionComparer.cs")));
        Assert.Contains(
            "RuntimeProtocolCompatibility.IsCompatible",
            File.ReadAllText(Path.Combine(appServices, "RuntimeAvailabilityManager.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasePublishes_UseOneCanonicalInformationalVersionAcrossBundledProcesses()
    {
        var root = LocateRepositoryRoot();
        var appProject = File.ReadAllText(Path.Combine(
            root, "languages", "csharp", "dotnet", "host", "Sunder.App", "Sunder.App.csproj"));
        var supervisorProject = File.ReadAllText(Path.Combine(
            root, "languages", "csharp", "dotnet", "host", "Sunder.Host.Supervisor", "Sunder.Host.Supervisor.csproj"));
        var windowsPackager = File.ReadAllText(Path.Combine(
            root, "scripts", "release", "package-sunder.ps1"));
        var unixPackager = File.ReadAllText(Path.Combine(
            root, "scripts", "release", "package-sunder.sh"));
        var hostWorkflow = File.ReadAllText(Path.Combine(
            root, ".github", "workflows", "sunder-host-release.yml"));

        Assert.Contains("IncludeSourceRevisionInInformationalVersion=false", appProject, StringComparison.Ordinal);
        Assert.Contains("IncludeSourceRevisionInInformationalVersion=false", supervisorProject, StringComparison.Ordinal);
        Assert.Contains("IncludeSourceRevisionInInformationalVersion=false", windowsPackager, StringComparison.Ordinal);
        Assert.Contains("IncludeSourceRevisionInInformationalVersion=false", unixPackager, StringComparison.Ordinal);
        Assert.Contains("IncludeSourceRevisionInInformationalVersion=false", hostWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageSettings_DoNotEnumerateOpaqueState()
    {
        var root = LocateRepositoryRoot();
        var settingsSource = File.ReadAllText(Path.Combine(
            root, "languages", "csharp", "dotnet", "host", "Sunder.Runtime.Host", "Services", "PackageSettingsService.cs"));
        var dataEndpointSource = File.ReadAllText(Path.Combine(
            root, "languages", "csharp", "dotnet", "host", "Sunder.Runtime.Host", "Endpoints", "PackageDataEndpoints.cs"));

        Assert.DoesNotContain("StateStore", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("configuration", dataEndpointSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeShutdown_IsOwnedByResponseCompletion()
    {
        var source = File.ReadAllText(Path.Combine(
            LocateRepositoryRoot(), "languages", "csharp", "dotnet", "host", "Sunder.Runtime.Host", "Endpoints", "SystemEndpoints.cs"));

        Assert.Contains("OnCompleted", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResponseDrainDelay", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeShutdown_CompletesLongLivedStreamsBeforeStoppingWebHost()
    {
        var source = File.ReadAllText(Path.Combine(
            LocateRepositoryRoot(), "languages", "csharp", "dotnet", "host", "Sunder.Runtime.Host", "Program.cs"));
        var retireSessions = source.IndexOf("\"retire package sessions\"", StringComparison.Ordinal);
        var stopPackageLogs = source.IndexOf("\"stop package log streaming\"", StringComparison.Ordinal);
        var completeRuntimeEvents = source.IndexOf("runtimeEventStreamService.Complete()", StringComparison.Ordinal);
        var stopWebHost = source.IndexOf("\"stop the Runtime web host\"", StringComparison.Ordinal);

        Assert.True(retireSessions >= 0);
        Assert.True(stopPackageLogs > retireSessions);
        Assert.True(completeRuntimeEvents > stopPackageLogs);
        Assert.True(stopWebHost > completeRuntimeEvents);
    }

    [Fact]
    public void RuntimeHost_IsWorkerOnlyWhileAppRetainsAppModuleLoading()
    {
        var root = LocateRepositoryRoot();
        var hostRoot = Path.Combine(
            root,
            "languages",
            "csharp",
            "dotnet",
            "host",
            "Sunder.Runtime.Host");
        var hostProject = File.ReadAllText(Path.Combine(hostRoot, "Sunder.Runtime.Host.csproj"));
        var hostSource = string.Join('\n', Directory.EnumerateFiles(hostRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Select(File.ReadAllText));

        Assert.DoesNotContain("Sunder.Package.Hosting", hostProject, StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(PackageSessionState).Assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "Sunder.Package.Hosting", StringComparison.Ordinal));
        foreach (var removed in new[]
                 {
                     "AssemblyLoadContext",
                     "ISunderRuntimePackageModule",
                     "ISunderRuntimeContributionRegistry",
                     "RuntimePackageLoadContext",
                     "RuntimeSharedAssemblyRegistry",
                     "RuntimePackageContributionRegistry",
                     "RuntimePackageOperationService",
                     "PackageRuntimeOperation",
                     "PackageRuntimeStream",
                     "sunder.worker.v3",
                     "worker.content-discard",
                     "UsesStagedLifecycle",
                     "SUNDER_PACKAGE_CONTENT_PATH",
                     "SUNDER_PACKAGE_DATA_PATH",
                     "SUNDER_PACKAGE_STATE_PATH",
                     "SUNDER_PACKAGE_WORKING_PATH",
                 })
        {
            Assert.DoesNotContain(removed, hostSource, StringComparison.Ordinal);
        }
        Assert.Contains(nameof(ProcessRuntimeWorker), hostSource, StringComparison.Ordinal);

        var appServices = Path.Combine(
            root,
            "languages",
            "csharp",
            "dotnet",
            "host",
            "Sunder.App",
            "Services");
        Assert.Contains(
            nameof(Sunder.Sdk.Abstractions.ISunderAppPackageModule),
            File.ReadAllText(Path.Combine(appServices, "AppPackageActivator.cs")),
            StringComparison.Ordinal);
        Assert.Contains(
            nameof(Sunder.Sdk.Abstractions.ISunderAppPackageModule),
            File.ReadAllText(Path.Combine(appServices, "AppSharedAssemblyRegistry.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void V1Sources_DoNotReintroduceRemovedCompatibilityNames()
    {
        var root = LocateRepositoryRoot();
        var productionRoots = new[]
        {
            Path.Combine(root, "languages", "csharp", "dotnet", "host"),
            Path.Combine(root, "languages", "csharp", "dotnet", "packages"),
            Path.Combine(root, "languages", "csharp", "dotnet", "libraries"),
        };
        var paths = productionRoots.SelectMany(path => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories));
        var source = string.Join('\n', paths.Where(path => !IsBuildOutput(path)).Select(File.ReadAllText));

        foreach (var removed in new[]
                 {
                     "SUNDER_REGISTRY_URL", "--registry-url", "DisposeLegacyOwnedInstances",
                     "SnapshotLegacyResources", "config/values", "data/configuration",
                     "PackageLifecycleLoadRequest", "InstalledPackageSessionReloadRequest",
                     "load-batch", "reload-installed", "PackageRuntimeFaultReporter",
                     "ReportPackageFaultAsync", "ReportPackageFaultRequest", "PackageFaultService",
                     "MapPackageFaultEndpoints", "CurrentSdkApiVersion", "ContractOnlyHostRole",
                     "PackageHostRoles.ContractOnly", "PackageHostRoleMetadataValue.ContractOnly",
                     "contract-only", "StackContributionsV1", "stacks.contributions.v1",
                     "IPackageExtensionCatalog", "IPackageExtensionInvocationCatalog",
                     "PackageExtensionPoint", "SharedContractAssembly",
                 })
        {
            Assert.DoesNotContain(removed, source, StringComparison.Ordinal);
        }
    }

    private static bool IsBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Core repository root.");
    }
}

public sealed class RuntimeProblemDetailsTests
{
    [Fact]
    public void ProblemDetailsTaxonomy_MatchesGoldenFixture()
    {
        var root = LocateRepositoryRoot();
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "languages",
            "csharp",
            "dotnet",
            "tests",
            "Sunder.Runtime.Host.Tests",
            "Fixtures",
            "ProblemDetails.golden.json")));
        var expected = fixture.RootElement.EnumerateArray()
            .Select(item => (item.GetProperty("code").GetString(), item.GetProperty("title").GetString(), item.GetProperty("status").GetInt32()))
            .ToArray();
        var exceptions = new RuntimeException[]
        {
            new RuntimeValidationException("safe"),
            new RuntimeAuthenticationException(),
            new RuntimeConflictException("safe"),
            new RuntimeStaleGenerationException("safe"),
            new RuntimeNotFoundException("safe"),
            new RuntimeUnavailableException("safe"),
            new RuntimeCancellationException(),
            new RuntimeUploadLimitException("safe"),
            new RuntimePackageValidationException("safe"),
        };
        var actual = exceptions.Select(exception => ((string?)exception.Code, (string?)exception.Title, exception.StatusCode))
            .Append(((string?)RuntimeErrorCodes.Internal, (string?)"Internal Runtime error", 500))
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ExpectedClientError_WritesCorrelatedProblemWithoutInternalLog()
    {
        var logger = new CollectingLogger<RuntimeProblemDetailsMiddleware>();
        var middleware = new RuntimeProblemDetailsMiddleware(
            _ => throw new RuntimeValidationException("The request is invalid."),
            logger);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Headers[RuntimeProblemDetailsMiddleware.CorrelationHeader] = "test-correlation";

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Assert.Equal(RuntimeErrorCodes.Validation, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("test-correlation", problem.RootElement.GetProperty("correlationId").GetString());
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task InternalError_DoesNotLeakExceptionOrFilesystemPath()
    {
        var logger = new CollectingLogger<RuntimeProblemDetailsMiddleware>();
        var middleware = new RuntimeProblemDetailsMiddleware(
            _ => throw new InvalidOperationException("secret /private/runtime/path"),
            logger);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/private/runtime/path", body, StringComparison.Ordinal);
        Assert.Single(logger.Entries);
    }

    [Fact]
    public async Task InternalRpcError_LogsTypedDiagnosticsWithoutExposingThemInResponse()
    {
        const string rpcCode = "rpc.provider-retired";
        var logger = new CollectingLogger<RuntimeProblemDetailsMiddleware>();
        var middleware = new RuntimeProblemDetailsMiddleware(
            _ => throw new InvalidOperationException(
                "outer failure",
                new AggregateException(
                    new InvalidOperationException("private aggregate sibling"),
                    new SunderRpcException(new SunderRpcError(
                        SunderRpcErrorKind.Unavailable,
                        rpcCode,
                        "private provider detail")))),
            logger);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/packages/test/runtime/operations/test.run";
        context.Request.Headers[RuntimeProblemDetailsMiddleware.CorrelationHeader] = "rpc-correlation";

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.DoesNotContain(rpcCode, body, StringComparison.Ordinal);
        Assert.DoesNotContain("private provider detail", body, StringComparison.Ordinal);
        var message = Assert.Single(logger.Messages);
        Assert.Contains("rpc-correlation", message, StringComparison.Ordinal);
        Assert.Contains(nameof(SunderRpcErrorKind.Unavailable), message, StringComparison.Ordinal);
        Assert.Contains(rpcCode, message, StringComparison.Ordinal);
        Assert.DoesNotContain("outer failure", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private aggregate sibling", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private provider detail", message, StringComparison.Ordinal);
        Assert.Null(Assert.Single(logger.Exceptions));
    }

    [Fact]
    public async Task HostAuthenticatedRpcCancellation_NormalizesDirectWrappedAndCancellationOnlyAggregates()
    {
        Exception[] cancellations =
        [
            CreateHostRpcCancellation(),
            new InvalidOperationException("wrapper", CreateHostRpcCancellation()),
            new AggregateException(
                new InvalidOperationException("wrapped cancellation", CreateHostRpcCancellation()),
                new AggregateException(
                    new OperationCanceledException(),
                    CreateHostRpcCancellation())),
        ];

        foreach (var cancellation in cancellations)
        {
            var logger = new CollectingLogger<RuntimeProblemDetailsMiddleware>();
            var middleware = new RuntimeProblemDetailsMiddleware(_ => throw cancellation, logger);
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context);

            context.Response.Body.Position = 0;
            using var problem = await JsonDocument.ParseAsync(context.Response.Body);
            Assert.Equal(499, context.Response.StatusCode);
            Assert.Equal(RuntimeErrorCodes.Cancellation, problem.RootElement.GetProperty("code").GetString());
            Assert.Empty(logger.Entries);
        }
    }

    [Fact]
    public async Task MixedRpcCancellationAggregate_RemainsInternalFailure()
    {
        var logger = new CollectingLogger<RuntimeProblemDetailsMiddleware>();
        var middleware = new RuntimeProblemDetailsMiddleware(
            _ => throw new AggregateException(
                CreateHostRpcCancellation(),
                new InvalidOperationException("genuine failure")),
            logger);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(RuntimeErrorCodes.Internal, problem.RootElement.GetProperty("code").GetString());
        Assert.Single(logger.Entries);
    }

    [Fact]
    public async Task ForgedRpcCancellation_RemainsInternalFailure()
    {
        var logger = new CollectingLogger<RuntimeProblemDetailsMiddleware>();
        var middleware = new RuntimeProblemDetailsMiddleware(
            _ => throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.Cancelled,
                "rpc.call.cancelled",
                "forged cancellation")),
            logger);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(RuntimeErrorCodes.Internal, problem.RootElement.GetProperty("code").GetString());
        Assert.Single(logger.Entries);
    }

    private static SunderRpcException CreateHostRpcCancellation()
        => SunderRpcException.Infrastructure(
            SunderRpcErrorKind.Cancelled,
            "rpc.call.cancelled",
            "The RPC call was cancelled.");

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Core repository root.");
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Entries { get; } = [];
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(logLevel);
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }
}
