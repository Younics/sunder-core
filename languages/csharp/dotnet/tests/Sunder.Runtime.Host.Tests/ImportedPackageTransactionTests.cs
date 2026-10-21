using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ImportedPackageTransactionTests
{
    [Theory]
    [InlineData("compatible")]
    [InlineData("descriptor")]
    [InlineData("range")]
    public async Task RegistryProvenanceSurvivesLocalCompatibleUpdatesWithoutPinningRestoreContent(string change)
    {
        using var fixture = new Fixture();
        var owner = fixture.Archive("example.authority", "1.0.1");
        var consumer = fixture.Archive("example.consumer", "1.0.0", imported: true);
        await fixture.Policy.RefreshAsync(packages: [owner.Identity, consumer.Identity]);
        var stage = await fixture.Coordinator.PrepareStageAsync([
            fixture.RegistryInstall(owner), fixture.RegistryInstall(consumer) with { ResolutionPolicy = fixture.Resolution },
        ]);
        Assert.True(stage.Success, stage.Failure?.Message);
        Assert.True((await fixture.Coordinator.CommitStageAsync(stage.Stage!.StageId)).Success);
        var installedConsumer = (await fixture.Store.ListAsync()).Single(package => package.PackageId == consumer.Identity.PackageId);
        Assert.Equal(fixture.RestoredContent, installedConsumer.Compatibility!.Resolution.RestoreProvenance.Single().Artifact.Inventory.PackageContentIdentity);
        Assert.NotEqual(fixture.RestoredContent, owner.Identity.PackageContentIdentity);
        var before = await File.ReadAllBytesAsync(fixture.Policy.Paths.StateFilePath);

        var replacement = fixture.Archive("example.authority", change == "range" ? "2.0.0" : "1.0.9", descriptorDrift: change == "descriptor");
        await fixture.Policy.RefreshAsync(packages: [replacement.Identity]);
        Task<PackageStoreStagePreparation> PrepareUpdate() => fixture.Coordinator.PrepareStageAsync([new PackageStoreMutation(PackageStoreMutationKind.Upgrade,
            "example.authority", replacement.Path)]);
        if (change != "compatible")
        {
            var failure = await Assert.ThrowsAsync<InvalidDataException>(PrepareUpdate);
            Assert.Contains(PackagePolicyErrorCodes.ContractAuthorityReleaseConflict, failure.Message);
            Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Policy.Paths.StateFilePath));
            Assert.False(fixture.Coordinator.HasPreparedStages);
            return;
        }
        var update = await PrepareUpdate();
        Assert.True(update.Success, update.Failure?.Message);
        Assert.True((await fixture.Coordinator.CommitStageAsync(update.Stage!.StageId)).Success);
        var packages = await fixture.Store.ListAsync();
        Assert.Equal("1.0.9", packages.Single(package => package.PackageId == "example.authority").Version);
        var retained = packages.Single(package => package.PackageId == "example.consumer").Compatibility!;
        Assert.Equal(fixture.RestoredContent, retained.Resolution.RestoreProvenance.Single().Artifact.Inventory.PackageContentIdentity);
        Assert.Equal(installedConsumer.ContentIdentity, packages.Single(package => package.PackageId == "example.consumer").ContentIdentity);
    }

    [Fact]
    public async Task IncompleteRestoreInventoryCannotPrepareAnImportedInstallation()
    {
        using var fixture = new Fixture();
        var owner = fixture.Archive("example.authority", "1.0.1");
        var consumer = fixture.Archive("example.consumer", "1.0.0", imported: true);
        await fixture.Policy.RefreshAsync(packages: [owner.Identity, consumer.Identity]);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Coordinator.PrepareStageAsync([fixture.RegistryInstall(owner), fixture.RegistryInstall(consumer) with
        { ResolutionPolicy = fixture.Resolution with { RestoreProvenance = [] } }]));
        Assert.Empty(await fixture.Store.ListAsync());
        Assert.False(fixture.Coordinator.HasPreparedStages);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectoryScope _temporary = TemporaryDirectoryScope.Create("sunder-import-transactions");
        private readonly SunderRpcContractDescriptor _contract = SunderRpcContractDescriptor.Parse("""
            {"descriptorVersion":1,"schemaProfile":"sunder.rpc.values.v1","contractId":"example.api","version":"1.0.0","errors":{},
             "services":{"service":{"methods":{"call":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Request"}}}}},
             "$defs":{"Request":{"type":"object","properties":{},"required":[],"additionalProperties":false}}}
            """u8.ToArray());
        private readonly RegistryPackagePublicApiInventoryResponseV1 _inventory;
        internal ActivePolicyTestContext Policy { get; } = new();
        internal InstalledPackageStore Store { get; }
        internal PackageStoreCoordinator Coordinator { get; }
        internal string RestoredContent { get; } = new('e', 64);
        internal RegistryPackageResolutionPolicy Resolution { get; }
        internal Fixture()
        {
            RuntimeLocalState.EnsureInitialized(Policy.Paths.RootPath);
            Store = new(Policy.Paths);
            Coordinator = new(Policy.Paths, Store, new(Policy.Paths), packagePolicies: Policy.Cache);
            var inventory = new RegistryPackagePublicApiInventoryV1(1, "example.authority", "1.0.0", RestoredContent, 1,
                [new("example.api", "1.0.0", _contract.GetCanonicalUtf8().Length, _contract.Sha256, _contract.AbiSha256,
                    "owned", "package", "example.authority", "1.0.0", RestoredContent)]);
            var hash = SunderPackagePublicApiInventory.Parse(JsonSerializer.SerializeToUtf8Bytes(inventory,
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })).PublicInventorySha256;
            _inventory = new(inventory, hash);
            Resolution = new(new(1, ActivePolicyTestContext.Hash("package-policy-1")), [],
                [new(ActivePolicyTestContext.Origin, _inventory, ">=1.0.0 <2.0.0", false, [])], [], []);
        }

        internal ArchiveInput Archive(string id, string version, bool imported = false, bool descriptorDrift = false)
        {
            var root = _temporary.GetPath(id + "-" + version);
            var descriptor = _contract;
            if (descriptorDrift)
            {
                var json = JsonNode.Parse(_contract.GetCanonicalUtf8())!;
                json["$defs"]!["Request"]!["title"] = "Only an annotation changed";
                descriptor = SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(json));
                Assert.Equal(_contract.AbiSha256, descriptor.AbiSha256);
                Assert.NotEqual(_contract.Sha256, descriptor.Sha256);
            }
            CanonicalPackageTestBuilder.WriteExplodedPackage(root, id, version, typeof(ImportedPackageTransactionTests).Assembly.Location,
                roles: ["app"], dependencies: imported ? [new("example.authority", ">=1.0.0 <2.0.0")] : [], contractBundles: [new()
                {
                    Source = imported ? "imported" : "owned", AuthorityKind = "package", AuthorityId = imported ? "example.authority" : id,
                    SelectedAuthorityRelease = imported ? "1.0.0" : null, SelectedAuthorityContentIdentity = imported ? RestoredContent : null,
                    ContractId = descriptor.ContractId, Version = descriptor.Version, DescriptorPath = "contracts/api.json",
                    DescriptorSha256 = descriptor.Sha256, AbiSha256 = descriptor.AbiSha256, Exposure = "public",
                }], usesContracts: imported ? [new() { Role = "app", ContractId = descriptor.ContractId, ContractVersion = descriptor.Version,
                    AbiSha256 = descriptor.AbiSha256, Required = false, Actions = ["discover", "invoke"] }] : []);
            if (imported)
            {
                var path = Path.Combine(root, SunderPackageFormat.ManifestPath);
                var json = JsonNode.Parse(File.ReadAllText(path))!;
                var options = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
                json["packageArtifactImports"] = JsonSerializer.SerializeToNode(new[] { new SunderPackageArtifactImportManifest
                {
                    PackageId = "example.authority", RequestedVersionRange = ">=1.0.0 <2.0.0", IncludePrerelease = false,
                    SelectedPackageVersion = "1.0.0", SelectedPackageContentIdentity = RestoredContent, PublicContractCount = 1,
                    PublicInventorySha256 = _inventory.PublicInventorySha256, AuthorityConstraints = [new() { AuthorityKind = "package", AuthorityId = "example.authority",
                        SelectedAuthorityRelease = "1.0.0", SelectedAuthorityContentIdentity = RestoredContent }],
                } }, options);
                json["packageApiImports"] = JsonSerializer.SerializeToNode(new[] { new SunderPackageApiImportManifest
                {
                    PackageId = "example.authority", Presence = "required", BundleRefs = [new() { ContractId = "example.api", ContractVersion = "1.0.0" }],
                    UseRefs = [new() { Role = "app", ContractId = "example.api", ContractVersion = "1.0.0" }],
                } }, options);
                File.WriteAllText(path, json.ToJsonString());
            }
            Directory.CreateDirectory(Path.Combine(root, "payload", "shared", "contracts"));
            File.WriteAllBytes(Path.Combine(root, "payload", "shared", "contracts", "api.json"), descriptor.GetCanonicalUtf8());
            CanonicalPackageTestBuilder.WriteContentIndex(root);
            var archive = root + ".sunderpkg";
            ZipFile.CreateFromDirectory(root, archive);
            return new(archive, new(id, version, PackageSessionPreparer.ComputeContentIdentity(root)));
        }

        internal PackageStoreMutation RegistryInstall(ArchiveInput archive) => new(PackageStoreMutationKind.Install, ArchiveFilePath: archive.Path,
            Provenance: new(InstalledPackageSourceKind.Registry, InstalledPackageVersionPolicy.ExplicitVersion, ActivePolicyTestContext.Origin + "/",
                archive.Identity.PackageId, RequestedVersion: archive.Identity.Version, SourceIdentity: archive.Identity.PackageContentIdentity));
        public void Dispose() { Policy.Dispose(); _temporary.Dispose(); }
    }
    private sealed record ArchiveInput(string Path, RegistryPackageVersionIdentity Identity);
}
