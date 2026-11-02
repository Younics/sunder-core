using System.Reflection;
using System.Text.Json;
using Microsoft.Build.Framework;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Worker;
using Xunit;
using MSBuildTaskItem = Microsoft.Build.Utilities.TaskItem;

namespace Sunder.Package.Build.Tests;

internal static class PackageManifestFixture
{
    internal static GenerateSunderPackageManifestTask CreateTask(string manifestPath, IBuildEngine buildEngine)
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

    internal static GenerateSunderPackageManifestTask CreateFixtureTask(
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

    internal static void ConfigureWorkerTask(
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

    internal static string CSharpWorkerModuleName()
        => Path.GetFileName(Assembly.GetExecutingAssembly().Location);

    internal static MSBuildTaskItem[] CreateDotNetWorkerFiles(
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

    internal static MSBuildTaskItem[] SdkReferencePaths(bool includeAvalonia = false, bool includeStacks = false)
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

    internal static MSBuildTaskItem[] TestAssemblyDynamicAccessAcknowledgements()
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
            new("Sunder.Package.Build.Tests.SunderRpcCSharpImportPrepassTests.UnchangedGeneratedClient_UsesAdmittedCurrentProviderWithoutRelabelingItsIdentity"),
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

    internal static MSBuildTaskItem[] DynamicAccessAcknowledgements()
        =>
        [
            new("Sunder.Package.Build.Tests.Fixtures.AuthoredDynamicReflection.AuthoredSdkDynamicReflection.ResolveSdkContract"),
            new("Sunder.Package.Build.Tests.Fixtures.AuthoredDynamicReflection.AuthoredSdkDynamicReflection.ResolveSdkContractAsync"),
            new("Sunder.Package.Build.Tests.Fixtures.AuthoredDynamicReflection.AuthoredSdkDynamicReflection.CreateSdkContractResolver"),
            new("Sunder.Package.Build.Tests.Fixtures.AuthoredDynamicReflection.AuthoredSdkDynamicReflection.ResolveSdkContracts"),
        ];

    internal static string CopyFixtureAssemblyToIsolatedDirectory(string fixtureName)
    {
        var sourcePath = GetFixtureAssemblyPath(fixtureName);
        Assert.True(File.Exists(sourcePath), $"Fixture assembly was not built: {sourcePath}");
        var destinationPath = Path.Combine(CreateTempDirectory(), Path.GetFileName(sourcePath));
        File.Copy(sourcePath, destinationPath);
        return destinationPath;
    }

    internal static string GetFixtureAssemblyPath(string fixtureName, string? assemblyName = null)
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

    internal static string ResolveConfiguration()
        => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent?.Name
           ?? "Debug";

    internal static string FindRepositoryRoot()
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

    internal static HashSet<string> ReadCapabilities(JsonElement root)
        => root.GetProperty("targets")
            .EnumerateArray()
            .SelectMany(static target => target.GetProperty("requiredHostCapabilities").EnumerateArray())
            .Select(static capability => capability.GetString()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static void AssertContainsCapabilities(HashSet<string> actual, params string[] expected)
    {
        foreach (var capability in expected)
        {
            Assert.Contains(capability, actual);
        }
    }

    internal const string MinimalV1Descriptor = """
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

    internal static string CreateTempDirectory()
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
}
