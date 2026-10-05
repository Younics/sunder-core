using System.Text.Encodings.Web;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageCompatibilityAuthorityTests
{
    private static readonly string Descriptor = new('a', 64);
    private static readonly string Abi = new('b', 64);
    private static readonly string ConsumerContent = new('c', 64);
    private const string Api = "example.api";

    [Fact]
    public void CompatiblePatchDoesNotPinRestoreSelectedPackageContent()
    {
        var consumer = Consumer();
        var actual = Authority("1.0.9", new('d', 64));
        PackageCompatibilityAuthority.ValidateGraph([consumer, actual]);
        Assert.NotEqual(consumer.Manifest!.PackageArtifactImports![0]!.SelectedPackageContentIdentity, actual.ContentIdentity);
    }

    [Fact]
    public void DifferentRestoreSnapshotsCanShareOneInRangeInstalledAuthority()
    {
        PackageCompatibilityAuthority.ValidateGraph([Consumer("example.first", new('e', 64)), Consumer("example.second", new('f', 64)), Authority("1.0.9", new('d', 64))]);
    }

    [Theory]
    [InlineData("range")]
    [InlineData("descriptor")]
    [InlineData("abi")]
    [InlineData("private")]
    [InlineData("missing")]
    public void ConflictsRejectTheCompleteCandidateGraph(string mismatch)
    {
        var authority = Authority(mismatch == "range" ? "2.0.0" : "1.0.9", new('d', 64),
            mismatch == "descriptor" ? new('e', 64) : Descriptor, mismatch == "abi" ? new('f', 64) : Abi, mismatch == "private" ? "private" : "public");
        Assert.Throws<InvalidDataException>(() => PackageCompatibilityAuthority.ValidateGraph(mismatch == "missing" ? [Consumer()] : [Consumer(), authority]));
    }

    [Fact]
    public void AHashWithoutTheCompleteRetainedInventoryIsNotAuthority()
    {
        var consumer = Consumer();
        var incomplete = consumer with { Compatibility = consumer.Compatibility! with { Resolution = consumer.Compatibility.Resolution with { RestoreProvenance = [] } } };
        Assert.Throws<InvalidDataException>(() => PackageCompatibilityAuthority.ValidateGraph([incomplete, Authority()]));
    }

    [Fact]
    public void CompleteOptionalOmissionRemovesDependencyButPreservesRequiredUsePredicate()
    {
        var consumer = Consumer(optional: true, omitted: true);
        PackageCompatibilityAuthority.ValidateGraph([consumer]);
        Assert.Empty(PackageCompatibilityAuthority.EffectiveDependencies(consumer));
        Assert.True(Assert.Single(consumer.Manifest!.UsesContracts!)!.Required);
        PackageCompatibilityAuthority.ValidateGraph([consumer, Authority()]);
    }

    [Fact]
    public void OptionalMissingWithoutARegistryDispositionIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => PackageCompatibilityAuthority.ValidateGraph([Consumer(optional: true)]));
    }

    [Fact]
    public void MissingRetainedProofFailsClosedForADevImport()
    {
        var consumer = Consumer() with { Kind = PackageSourceKind.Dev, Compatibility = null };
        PackageManifestAdmissionPolicy.EnsureSupported(consumer.Manifest!);
        Assert.Contains(PackagePolicyErrorCodes.PolicyRequired,
            Assert.Throws<InvalidDataException>(() => PackageCompatibilityAuthority.ValidateGraph([consumer, Authority()])).Message);
    }

    private static RuntimePackageSource Authority(string version = "1.0.1", string? content = null, string? descriptor = null, string? abi = null, string exposure = "public")
        => new(Api, PackageSourceKind.Installed, "/fixture/authority", ContentIdentity: content ?? new('d', 64), Manifest: new()
        {
            Id = Api, Version = version, Targets = [Target()], DependsOn = [],
            ContractBundles = [new() { Source = "owned", AuthorityKind = "package", AuthorityId = Api, ContractId = "example.rpc", Version = "1.0.0",
                DescriptorPath = "contracts/example.json", DescriptorSha256 = descriptor ?? Descriptor, AbiSha256 = abi ?? Abi, Exposure = exposure }],
            UsesContracts = [], Provides = [],
        });

    private static RuntimePackageSource Consumer(string id = "example.consumer", string? restoredContent = null, bool optional = false, bool omitted = false)
    {
        restoredContent ??= new('e', 64);
        var inventory = new RegistryPackagePublicApiInventoryV1(1, Api, "1.0.0", restoredContent, 1,
            [new("example.rpc", "1.0.0", 128, Descriptor, Abi, "owned", "package", Api, "1.0.0", restoredContent)]);
        var hash = SunderPackagePublicApiInventory.Parse(JsonSerializer.SerializeToUtf8Bytes(inventory,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })).PublicInventorySha256;
        var resolution = new RegistryPackageResolutionPolicy(new(1, new('a', 64)), new(1, new('b', 64), 1, new('c', 64)), [],
            [new("https://registry.example", new(inventory, hash), ">=1.0.0 <2.0.0", false, [])],
            omitted ? [new(id, Api, "omitted", null, "package-api.package-unavailable")] : [], []);
        return new(id, PackageSourceKind.Installed, "/fixture/consumer", ContentIdentity: ConsumerContent, Manifest: new()
        {
            Id = id, Version = "1.0.0", Targets = [Target()],
            DependsOn = [new() { PackageId = Api, VersionRange = ">=1.0.0 <2.0.0", Presence = optional ? "optional" : "required" }],
            ContractBundles = [new() { Source = "imported", AuthorityKind = "package", AuthorityId = Api, ContractId = "example.rpc", Version = "1.0.0",
                DescriptorPath = "contracts/example.json", DescriptorSha256 = Descriptor, AbiSha256 = Abi, Exposure = "public", SelectedAuthorityRelease = "1.0.0", SelectedAuthorityContentIdentity = restoredContent }],
            UsesContracts = [new() { Role = "runtime", ContractId = "example.rpc", ContractVersion = "1.0.0", AbiSha256 = Abi, Required = true, Actions = ["discover", "invoke"] }],
            Provides = [],
            PackageArtifactImports = [new() { PackageId = Api, RequestedVersionRange = ">=1.0.0 <2.0.0", IncludePrerelease = false,
                SelectedPackageVersion = "1.0.0", SelectedPackageContentIdentity = restoredContent, PublicContractCount = 1, PublicInventorySha256 = hash,
                AuthorityConstraints = [new() { AuthorityKind = "package", AuthorityId = Api, SelectedAuthorityRelease = "1.0.0", SelectedAuthorityContentIdentity = restoredContent }] }],
            PackageApiImports = [new() { PackageId = Api, Presence = optional ? "optional" : "required", BundleRefs = [new() { ContractId = "example.rpc", ContractVersion = "1.0.0" }],
                UseRefs = [new() { Role = "runtime", ContractId = "example.rpc", ContractVersion = "1.0.0" }] }],
            ProviderImports = [], PlatformImports = [],
        }) { Compatibility = new(new(id, "1.0.0", ConsumerContent), "https://registry.example", resolution) };
    }

    private static SunderPackageTargetManifest Target() => new() { Role = "runtime", Rid = "osx-arm64", Kind = "worker", EntryPoint = "lib/fixture.dll" };
}
