using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class InstalledPackageStoreTests
{
    [Fact]
    public async Task ListAsync_WhenUnrecordedPayloadExists_DoesNotResurrectPackage()
    {
        var paths = CreateRuntimePackagePaths();
        CreateInstallPath(paths, "orphan.package", "1.0.0");
        var store = new InstalledPackageStore(paths);

        var packages = await store.ListAsync();

        Assert.Empty(packages);
        Assert.False(File.Exists(paths.StateFilePath));
    }

    [Fact]
    public async Task OldSchema_RequiresDataSafeResetBeforeReadOrMutation()
    {
        var paths = CreateRuntimePackagePaths();
        Directory.CreateDirectory(paths.CatalogRootPath);
        var legacyState = JsonSerializer.SerializeToNode(
            new InstalledPackageStateFile(5, [CreatePackage(paths, "legacy.package")]),
            TestJsonOptions)!.AsObject();
        await File.WriteAllTextAsync(paths.StateFilePath, legacyState.ToJsonString(TestJsonOptions));
        var store = new InstalledPackageStore(paths);

        var original = await File.ReadAllTextAsync(paths.StateFilePath);

        var readError = await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAsync());
        var mutationError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.WriteAsync([CreatePackage(paths, "replacement.package")]));

        Assert.Contains("schema version 5", readError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sunder runtime reset --yes", readError.Message, StringComparison.Ordinal);
        Assert.Contains("preserves package data", readError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sunder runtime reset --yes", mutationError.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(paths.StateFilePath));
    }

    [Theory]
    [InlineData("executionRuntimeBindings")]
    [InlineData("acquiredTargets")]
    public async Task ListAsync_RejectsSchemaOneMissingAdmissionStateWithoutRewritingCatalog(string field)
    {
        var paths = CreateRuntimePackagePaths();
        Directory.CreateDirectory(paths.CatalogRootPath);
        var package = CreatePackage(paths, "pinned.package") with
        {
            Provenance = new InstalledPackageProvenanceRecord(
                InstalledPackageSourceKind.Registry,
                InstalledPackageVersionPolicy.ExplicitVersion,
                "https://registry.example/",
                "pinned.package",
                RequestedVersion: "1.0.0",
                SourceIdentity: new string('a', 64)),
        };
        var state = JsonSerializer.SerializeToNode(
            new InstalledPackageStateFile(1, [package]),
            TestJsonOptions)!.AsObject();
        state["packages"]!.AsArray()[0]!.AsObject().Remove(field);
        await File.WriteAllTextAsync(paths.StateFilePath, state.ToJsonString(TestJsonOptions));
        var store = new InstalledPackageStore(paths);

        var original = await File.ReadAllTextAsync(paths.StateFilePath);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAsync());

        Assert.Equal(original, await File.ReadAllTextAsync(paths.StateFilePath));
    }

    [Fact]
    public async Task AcquisitionScopeIsDetachedDurableAndPartOfCatalogBindingIdentity()
    {
        var paths = CreateRuntimePackagePaths();
        var input = new List<InstalledPackageTargetRecord> { new("app", PackageTargetSelection.GetCurrentRuntimeIdentifier()) };
        var package = CreatePackage(paths, "scope.package") with { AcquiredTargets = input };
        input.Clear();
        Assert.Single(package.AcquiredTargets!);
        var store = new InstalledPackageStore(paths);
        await store.WriteAsync([package]);
        var restored = Assert.Single(await new InstalledPackageStore(paths).ListAsync());
        Assert.Equal(package.AcquiredTargets, restored.AcquiredTargets);
        Assert.Throws<NotSupportedException>(() => ((IList<InstalledPackageTargetRecord>)restored.AcquiredTargets!).Clear());
        var differentScope = restored with { AcquiredTargets = [] };
        Assert.Equal(restored.ContentIdentity, differentScope.ContentIdentity);
        Assert.False(PackageStorePolicy.CatalogBindingsEqual([restored], [differentScope]));
        Assert.False(PackageStorePolicy.CatalogBindingsEqual([restored], [restored with { AcquiredTargets = null }]));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("role")]
    [InlineData("rid")]
    [InlineData("null-entry")]
    [InlineData("overflow")]
    public async Task InvalidAcquisitionScopeCannotReplaceTheInstalledCatalog(string failure)
    {
        var paths = CreateRuntimePackagePaths();
        var store = new InstalledPackageStore(paths);
        var package = CreatePackage(paths, "scope.package");
        await store.WriteAsync([package]);
        var before = await File.ReadAllBytesAsync(paths.StateFilePath);
        var key = new InstalledPackageTargetRecord("app", PackageTargetSelection.GetCurrentRuntimeIdentifier());
        InstalledPackageTargetRecord[] invalid = failure switch
        {
            "duplicate" => [key, key],
            "role" => [key with { Role = "unknown" }],
            "rid" => [key with { Rid = "any" }],
            "null-entry" => [null!],
            _ => Enumerable.Repeat(key, SunderPackageFormat.MaxTargets + 1).ToArray(),
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync([package with { AcquiredTargets = invalid }]));
        Assert.Equal(before, await File.ReadAllBytesAsync(paths.StateFilePath));
    }

    [Fact]
    public async Task ListAsync_WhenPersistedPathIsOutsideVersionRoot_RejectsCatalog()
    {
        var paths = CreateRuntimePackagePaths();
        var outsidePath = Path.Combine(Path.GetDirectoryName(paths.RootPath)!, "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsidePath);
        Directory.CreateDirectory(paths.CatalogRootPath);
        await File.WriteAllTextAsync(paths.StateFilePath, JsonSerializer.Serialize(new InstalledPackageStateFile(
            1,
            [CreatePackage(paths, "unsafe.package") with { InstallPath = outsidePath }]), TestJsonOptions));
        var store = new InstalledPackageStore(paths);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAsync());

        Assert.Contains("outside", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(outsidePath));
    }

    [Fact]
    public async Task WriteAsync_RejectsDuplicateIdsWithoutReplacingCatalog()
    {
        var paths = CreateRuntimePackagePaths();
        var store = new InstalledPackageStore(paths);
        var original = CreatePackage(paths, "test.package");
        await store.WriteAsync([original]);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync([original, original]));

        var persisted = Assert.Single(await store.ListAsync());
        Assert.Equal(original.PackageId, persisted.PackageId);
        Assert.Equal(original.Version, persisted.Version);
        Assert.Equal(original.InstallPath, persisted.InstallPath);
    }

    [Fact]
    public async Task WriteAsync_RejectsInvalidVersionAndDependencyRange()
    {
        var paths = CreateRuntimePackagePaths();
        var store = new InstalledPackageStore(paths);
        var invalidVersion = CreatePackage(paths, "test.package") with { Version = "latest" };
        var invalidRange = CreatePackage(paths, "test.package") with
        {
            DependsOn = [new InstalledPackageDependencyRecord("other.package", "banana")],
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync([invalidVersion]));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync([invalidRange]));
    }

    [Fact]
    public async Task WriteAsync_PersistsOneAuthoritativeSortedCatalog()
    {
        var paths = CreateRuntimePackagePaths();
        var store = new InstalledPackageStore(paths);
        var second = CreatePackage(paths, "z.package");
        var first = CreatePackage(paths, "a.package");

        await store.WriteAsync([second, first]);

        Assert.Equal(["a.package", "z.package"], (await store.ListAsync()).Select(package => package.PackageId));
        Assert.DoesNotContain(".tmp-", await File.ReadAllTextAsync(paths.StateFilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacementPolicy_UsesPrecedenceForDowngradesAndFullIdentityForReinstalls()
    {
        var paths = CreateRuntimePackagePaths();
        var current = CreatePackage(paths, "test.package", "1.2.3+first");

        Assert.Null(PackageStorePolicy.ValidateReplacementVersion(
            current,
            CreatePackage(paths, "test.package", "1.2.3+second"),
            allowDowngrade: false,
            reinstall: false));
        var sameIdentityError = PackageStorePolicy.ValidateReplacementVersion(
            current,
            current,
            allowDowngrade: false,
            reinstall: false);
        var downgradeError = PackageStorePolicy.ValidateReplacementVersion(
            current,
            CreatePackage(paths, "test.package", "1.2.2+later-build"),
            allowDowngrade: false,
            reinstall: false);

        Assert.Contains("already installed", Assert.IsType<string>(sameIdentityError), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be downgraded", Assert.IsType<string>(downgradeError), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PackageDescriptors_IncludeManifestDeclaredRpcAccess()
    {
        var paths = CreateRuntimePackagePaths();
        var installPath = paths.GetInstalledPackagePath("test.package", "1.0.0");
        var descriptorBytes = System.Text.Encoding.UTF8.GetBytes("""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "descriptorVersion": 1,
              "schemaProfile": "sunder.rpc.values.v1",
              "contractId": "dev.sunder.execution",
              "version": "1.0.0",
              "errors": {},
              "services": {
                "execution": {
                  "methods": {
                    "run": {
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
                  "properties": {
                    "value": { "type": "string", "minLength": 0, "maxLength": 256 }
                  },
                  "required": ["value"],
                  "additionalProperties": false
                },
                "Response": {
                  "type": "object",
                  "properties": {
                    "accepted": { "type": "boolean" }
                  },
                  "required": ["accepted"],
                  "additionalProperties": false
                }
              }
            }
            """);
        var descriptorHash = SunderRpcContractDescriptor.Parse(descriptorBytes).Sha256;
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            installPath,
            "test.package",
            "1.0.0",
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            roles: [SunderPackageFormat.RuntimeHostRole],
            runtimeRequiredHostCapabilities:
            [
                "sdk-baseline-1-1.v1",
                "core.v1",
                "execution-runtime.v1",
                "rpc.v1",
                "worker-protocol.v1",
            ],
            contractBundles:
            [
                new SunderPackageContractBundleManifest
                {
                    Source = "owned",
                    AuthorityKind = "package",
                    AuthorityId = "test.package",
                    ContractId = "dev.sunder.execution",
                    Version = "1.0.0",
                    DescriptorPath = "contracts/execution.json",
                    DescriptorSha256 = descriptorHash,
                    AbiSha256 = SunderRpcContractDescriptor.Parse(descriptorBytes).AbiSha256,
                    Exposure = SunderPackageFormat.PackageContractExposure,
                },
            ],
            usesContracts:
            [
                new SunderPackageContractUseManifest
                {
                    Role = SunderPackageFormat.RuntimeHostRole,
                    ContractId = "dev.sunder.execution",
                    ContractVersion = "1.0.0",
                    AbiSha256 = SunderRpcContractDescriptor.Parse(descriptorBytes).AbiSha256,
                    Required = true,
                    Actions = ["discover", "invoke"],
                },
            ]);
        AddExecutionRuntimeRequirement(installPath);
        var descriptorPath = Path.Combine(installPath, "payload", "shared", "contracts", "execution.json");
        Directory.CreateDirectory(Path.GetDirectoryName(descriptorPath)!);
        File.WriteAllBytes(descriptorPath, descriptorBytes);
        CanonicalPackageTestBuilder.WriteContentIndex(installPath);
        var package = CanonicalPackageTestBuilder.CreateInstalledRecord(
            installPath,
            "test.package",
            "1.0.0");
        var store = new InstalledPackageStore(paths);

        var descriptor = store.ToDescriptor(package);

        Assert.Equal(InstalledPackageSourceKind.Unknown, descriptor.Provenance.SourceKind);
        var contractUse = Assert.Single(descriptor.RpcContractUses);
        Assert.Equal(SunderPackageFormat.RuntimeHostRole, contractUse.Role);
        Assert.Equal("dev.sunder.execution", contractUse.ContractId);
        Assert.Equal("1.0.0", contractUse.ContractVersion);
        Assert.Equal(SunderRpcContractDescriptor.Parse(descriptorBytes).AbiSha256, contractUse.AbiSha256);
        Assert.True(contractUse.Required);
        Assert.Equal(["discover", "invoke"], contractUse.Actions);

        var errors = new List<string>();
        var activation = await new PackageSessionPreparer().ReadInstalledActivationStateAsync(
            package,
            installPath,
            errors,
            CancellationToken.None);
        Assert.Empty(errors);
        var sessionDescriptor = PackageProtocolMapper.BuildSessionDescriptor(
            Assert.IsType<RuntimePackageActivationState>(activation),
            isEnabled: true,
            PackageReadinessState.Ready);
        var sessionUse = Assert.Single(sessionDescriptor.RpcContractUses);
        Assert.Equal(contractUse.Role, sessionUse.Role);
        Assert.Equal(contractUse.ContractId, sessionUse.ContractId);
        Assert.Equal(contractUse.ContractVersion, sessionUse.ContractVersion);
        Assert.Equal(contractUse.AbiSha256, sessionUse.AbiSha256);
        Assert.Equal(contractUse.Required, sessionUse.Required);
        Assert.Equal(contractUse.Actions, sessionUse.Actions);

        var manifestPath = Path.Combine(installPath, "manifest", "sunder-package.json");
        var manifestJson = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifestJson["usesContracts"] = new JsonArray();
        await File.WriteAllTextAsync(manifestPath, manifestJson.ToJsonString(TestJsonOptions));
        CanonicalPackageTestBuilder.WriteContentIndex(installPath);

        var tampered = Assert.Throws<InvalidDataException>(() => store.ToDescriptor(package));
        Assert.Contains("content identity", tampered.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions TestJsonOptions = new() { WriteIndented = true };

    private static RuntimePackagePaths CreateRuntimePackagePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new RuntimePackagePaths(root);
    }

    internal static InstalledPackageRecord CreatePackage(
        RuntimePackagePaths paths,
        string packageId,
        string version = "1.0.0",
        bool isEnabled = true,
        IReadOnlyList<InstalledPackageDependencyRecord>? dependencies = null)
    {
        var installPath = CreateInstallPath(paths, packageId, version);
        return new InstalledPackageRecord(
            packageId,
            packageId,
            Summary: null,
            version,
            Icon: null,
            installPath,
            Path.Combine(installPath, "manifest", "sunder-package.json"),
            new string('0', 64),
            [new InstalledPackageContentRecord("manifest/sunder-package.json", new string('0', 64), 0)],
            dependencies ?? [],
            isEnabled,
            DateTimeOffset.UtcNow,
            InstalledPackageProvenanceRecord.Unknown,
            []);
    }

    private static string CreateInstallPath(RuntimePackagePaths paths, string packageId, string version)
    {
        var installPath = paths.GetInstalledPackagePath(packageId, version);
        Directory.CreateDirectory(installPath);
        return installPath;
    }

    private static void AddExecutionRuntimeRequirement(string installPath)
    {
        var manifestPath = Path.Combine(installPath, "manifest", "sunder-package.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        foreach (var target in manifest["targets"]!.AsArray().Select(static target => target!.AsObject()))
        {
            target["executionRuntime"] = new JsonObject
            {
                ["id"] = SunderPackageFormat.DotNetExecutionRuntimeId,
                ["versionRange"] = ">=10.0.0 <11.0.0",
                ["moduleAbi"] = SunderPackageFormat.DotNetWorkerModuleAbi,
            };
        }
        File.WriteAllText(manifestPath, manifest.ToJsonString(TestJsonOptions));
        CanonicalPackageTestBuilder.WriteContentIndex(installPath);
    }
}
