using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Format;
using Sunder.Package.Projections;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RegistryPackageArtifactDownloaderTests
{
    [Theory]
    [InlineData("api", "runtime", "app")]
    [InlineData("api", "app", "runtime")]
    [InlineData("provider", "app", "runtime")]
    [InlineData("platform", "app", "runtime")]
    public async Task RemovingTheLastImportRolePreservesTheCompleteSourceGraph(string kind, string selectedRole, string removedRole)
    {
        var root = CreateTempDirectory();
        try
        {
            var fixture = await CreateProjectionFixtureAsync(root, "role", new('a', 64), "role acquisition",
                includeContractUses: true, importContract: true,
                configureManifest: (manifest, contract) => ConfigureRoleImport(manifest, contract, kind, removedRole));
            await using var provider = CreateProvider(root, fixture.ContentByUrl);
            var targetArtifact = selectedRole == "app" ? fixture.AppArtifact! : fixture.RuntimeArtifact;
            var upload = await provider.GetRequiredService<RegistryPackageArtifactDownloader>().DownloadAsync(new(Origin),
                CreatePlanItem(fixture.SharedArtifact, targetArtifact, fixture.Targets), [new(selectedRole, RuntimeInformation.RuntimeIdentifier)], [], default);
            var transfers = provider.GetRequiredService<RuntimeContentTransferStore>();
            var lease = transfers.AcquireUpload(upload.UploadId, RuntimeUploadKind.Package,
                provider.GetRequiredService<PackageSessionLifecycleService>().Generation, consume: false)!;
            var keys = new[] { new SunderPackageTargetKey(selectedRole, RuntimeInformation.RuntimeIdentifier) };
            var provenance = new InstalledPackageProvenanceRecord(InstalledPackageSourceKind.Registry,
                InstalledPackageVersionPolicy.ExplicitVersion, Origin, PackageId, RequestedVersion: Version, SourceIdentity: new('a', 64));
            var paths = new RuntimePackagePaths(Path.Combine(root, "installed"));
            RuntimeLocalState.EnsureInitialized(paths.RootPath);
            var result = await new SunderPackageArchiveInstaller(paths).PrepareAsync(lease.FilePath, true,
                provenance: provenance, consentManifestSha256: fixture.SharedArtifact.ManifestSha256, registryProjectionTargets: keys);
            Assert.True(result.Success, result.Failure?.Message);
            var archive = result.Mutation!;
            Assert.Equal(keys[0], Assert.Single(InstalledPackageProjection.Keys(archive.InstalledRecord)!));
            var sourceRoot = Path.Combine(root, "artifacts", "role", "source");
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(sourceRoot, SunderPackageFormat.ManifestPath)),
                await File.ReadAllBytesAsync(Path.Combine(archive.StagingPath, SunderPackageFormat.ManifestPath)));
            Assert.True(PackageCompatibilityAuthority.NeedsEvidence(archive.Manifest));
            Assert.Equal(2, archive.Manifest.Targets!.Count);
            Assert.Equal("imported", Assert.Single(archive.Manifest.ContractBundles!)!.Source);
            if (kind == "api") Assert.Equal(removedRole, Assert.Single(Assert.Single(archive.Manifest.PackageApiImports!)!.UseRefs!)!.Role);
            else Assert.Single(kind == "provider" ? archive.Manifest.ProviderImports!.Select(import => import!.ProviderRefs) : archive.Manifest.PlatformImports!.Select(import => import!.ProviderRefs));
            await AssertPreparedScopeAsync(paths, archive, selectedRole);
            transfers.DiscardUpload(upload.UploadId);
        }
        finally { await Sunder.Core.TestSupport.TemporaryDirectoryScopeOptions.Default.DeleteDirectory(root, default); }
    }

    [Theory]
    [InlineData("api", "runtime", "app")]
    [InlineData("api", "app", "runtime")]
    [InlineData("provider", "app", "runtime")]
    [InlineData("platform", "app", "runtime")]
    public async Task LocalArchiveCurrentRidSelectionRetainsImportsFromOtherRidRoles(string kind, string selectedRole, string removedRole)
    {
        var root = CreateTempDirectory();
        try
        {
            await CreateProjectionFixtureAsync(root, "local", new('a', 64), "local roles", includeContractUses: true, importContract: true,
                configureManifest: (manifest, contract) => ConfigureRoleImport(manifest, contract, kind, removedRole));
            var source = Path.Combine(root, "artifacts", "local", "source");
            var manifestPath = Path.Combine(source, SunderPackageFormat.ManifestPath);
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
            foreach (var target in manifest["targets"]!.AsArray().Where(target => target!["role"]!.GetValue<string>() == removedRole)) target!["rid"] = AlternateRid();
            if (removedRole == "runtime")
            {
                Directory.Move(Path.Combine(source, "payload", "runtime", RuntimeInformation.RuntimeIdentifier), Path.Combine(source, "payload", "runtime", AlternateRid()));
                TestWorkerFixture.CopyPayload(Path.Combine(source, "payload", "runtime", AlternateRid(), "lib"), AlternateRid());
            }
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
            CanonicalPackageTestBuilder.WriteContentIndex(source);
            var full = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(source);
            Assert.True(full.Success, string.Join(" | ", full.Errors));
            var sourceIdentity = PackageSessionPreparer.ComputeContentIdentity(source);
            var packagePath = Path.Combine(root, "local.sunderpkg");
            ZipFile.CreateFromDirectory(source, packagePath);
            var paths = new RuntimePackagePaths(Path.Combine(root, "installed"));
            RuntimeLocalState.EnsureInitialized(paths.RootPath);
            var result = await new SunderPackageArchiveInstaller(paths).PrepareAsync(packagePath, true);
            Assert.True(result.Success, result.Failure?.Message);
            Assert.Equal(sourceIdentity, result.Mutation!.InstalledRecord.SourcePackageContentIdentity);
            Assert.Equal(await File.ReadAllBytesAsync(manifestPath), await File.ReadAllBytesAsync(Path.Combine(result.Mutation.StagingPath, SunderPackageFormat.ManifestPath)));
            await AssertPreparedScopeAsync(paths, result.Mutation, selectedRole);
        }
        finally { await Sunder.Core.TestSupport.TemporaryDirectoryScopeOptions.Default.DeleteDirectory(root, default); }
    }

    [Fact]
    public async Task PartialProjectionCannotBeReinterpretedAsAnOrdinaryLocalArchive()
    {
        var root = CreateTempDirectory();
        try
        {
            var fixture = await CreateProjectionFixtureAsync(root, "partial", new('a', 64), "partial",
                roles: ["runtime"], runtimeIdentifiers: SunderPackageFormat.SupportedRuntimeIdentifiers);
            await using var provider = CreateProvider(root, fixture.ContentByUrl);
            var upload = await provider.GetRequiredService<RegistryPackageArtifactDownloader>().DownloadAsync(new(Origin),
                CreatePlanItem(fixture.SharedArtifact, fixture.RuntimeArtifact, fixture.Targets), RuntimeSelection(), [], default);
            var transfers = provider.GetRequiredService<RuntimeContentTransferStore>();
            var lease = transfers.AcquireUpload(upload.UploadId, RuntimeUploadKind.Package,
                provider.GetRequiredService<PackageSessionLifecycleService>().Generation, consume: false)!;
            var installer = new SunderPackageArchiveInstaller(new(Path.Combine(root, "installed")));
            Assert.False((await installer.PrepareAsync(lease.FilePath, true)).Success);
            var forgedScope = await installer.PrepareAsync(lease.FilePath, true, registryProjectionTargets: RuntimeKeys());
            Assert.False(forgedScope.Success);
            Assert.Contains("private Registry projection handoff", forgedScope.Failure!.Message);
            transfers.DiscardUpload(upload.UploadId);
        }
        finally { await Sunder.Core.TestSupport.TemporaryDirectoryScopeOptions.Default.DeleteDirectory(root, default); }
    }

    private static async Task AssertPreparedScopeAsync(RuntimePackagePaths paths, PreparedPackageArchiveMutation archive, string role)
    {
        var errors = new List<string>();
        var preparer = new PackageSessionPreparer(ephemeralRuntimeBindingResolver: TestExecutionRuntimeLaunchResolver.Instance,
            projectionCache: new PackageProjectionCache(paths.RootPath));
        var prepared = await preparer.PrepareInstalledPackageAsync(0, archive.InstalledRecord, archive.StagingPath,
            Path.Combine(paths.RootPath, "session"), errors, default);
        Assert.True(prepared is not null, string.Join(" | ", errors));
        Assert.NotNull(prepared);
        try
        {
            Assert.Empty(errors);
            Assert.Equal(role == "app" ? PackageHostRoles.App : PackageHostRoles.Runtime, prepared.HostRoles);
            Assert.Empty(prepared.Activation.RpcContractUses!);
            Assert.Empty(RuntimeRpcReadinessEvaluator.AppPredicates(prepared.Source));
            var descriptor = PackageProtocolMapper.BuildSessionDescriptor(prepared.Activation, true, PackageReadinessState.Ready);
            var session = new ActivePackageSession(null, new Dictionary<string, ActiveLoadedPackage>(),
                new Dictionary<string, SessionPackageDescriptor> { [PackageId] = descriptor }, readySources: [prepared.Source]);
            try
            {
                Assert.Empty(Assert.Single(RuntimeRpcPermissionSubjects.FromSession(session)).ContractUses);
                if (role == "runtime") Assert.Empty(session.GetAppPackageSources());
            }
            finally { await session.DisposeAsync(); }
            Assert.Contains(PackagePolicyErrorCodes.PolicyRequired,
                Assert.Throws<InvalidDataException>(() => PackageCompatibilityAuthority.ValidateGraph([prepared.Source])).Message);
        }
        finally
        {
            if (prepared.PackageProjectionLease is not null) await prepared.PackageProjectionLease.DisposeAsync();
            if (prepared.ExecutionRuntimeRetention is not null) await prepared.ExecutionRuntimeRetention.DisposeAsync();
        }
    }

    private static void ConfigureRoleImport(JsonObject manifest, SunderRpcContractDescriptor contract, string kind, string consumerRole)
    {
        if (kind == "api")
        {
            manifest["usesContracts"] = new JsonArray(manifest["usesContracts"]!.AsArray().Where(use => use!["role"]!.GetValue<string>() == consumerRole).Select(use => use!.DeepClone()).ToArray());
            var api = manifest["packageApiImports"]![0]!;
            api["useRefs"] = new JsonArray(api["useRefs"]!.AsArray().Where(use => use!["role"]!.GetValue<string>() == consumerRole).Select(use => use!.DeepClone()).ToArray());
            return;
        }
        manifest["usesContracts"] = new JsonArray();
        manifest["packageApiImports"] = new JsonArray();
        const string providerId = "test.projection.provider";
        manifest["provides"] = JsonSerializer.SerializeToNode(new[] { new SunderPackageProviderManifest
        {
            Role = "runtime", ProviderId = providerId, ContractId = contract.ContractId, ContractVersion = contract.Version,
            DescriptorSha256 = contract.Sha256, AbiSha256 = contract.AbiSha256, Audience = "public",
        } });
        var reference = new SunderPackageContractReferenceManifest { ContractId = contract.ContractId, ContractVersion = contract.Version };
        var providers = new SunderPackageProviderReferenceManifest[] { new() { Role = "runtime", ProviderId = providerId, ContractId = contract.ContractId, ContractVersion = contract.Version } };
        if (kind == "provider")
        {
            manifest["providerImports"] = JsonSerializer.SerializeToNode(new[] { new SunderPackageProviderImportManifest
            { Key = "provider", PackageId = "other.package", ContractId = contract.ContractId, ContractVersion = contract.Version, BundleRef = reference, ProviderRefs = providers } });
            return;
        }
        manifest["dependsOn"] = new JsonArray();
        manifest["packageArtifactImports"] = new JsonArray();
        var bundle = manifest["contractBundles"]![0]!;
        bundle["authorityKind"] = "platform"; bundle["authorityId"] = "sunder.core";
        manifest["platformImports"] = JsonSerializer.SerializeToNode(new[] { new SunderPackagePlatformImportManifest
        {
            Key = "platform", AuthorityKind = "platform", AuthorityId = "sunder.core", AuthorityReleaseRange = ">=1.0.0", IncludePrerelease = false,
            SelectedAuthorityRelease = "1.0.0", SelectedAuthorityContentIdentity = new('b', 64), ContractId = contract.ContractId,
            ContractVersion = contract.Version, BundleRef = reference, ProviderRefs = providers,
        } });
    }
}
