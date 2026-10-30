using System.Text.Json;
using System.Text.Encodings.Web;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed record InstalledPackageCompatibilityRecord(RegistryPackageVersionIdentity Package, string RegistryOrigin,
    RegistryPackageResolutionPolicy Resolution)
{
    internal void Validate(InstalledPackageRecord package)
    {
        RegistryActivePolicyClient.Require(Package == InstalledPackagePolicyRecord.Subject(package)
            && RegistryOrigin == RegistryActivePolicyClient.Origin(RegistryOrigin)
            && Resolution.PackagePolicy.Revision > 0 && RegistryActivePolicyClient.Hash(Resolution.PackagePolicy.Fingerprint)
            && Resolution.RestoreProvenance.Count <= 128 && Resolution.Requirements.Count <= 4096 && Resolution.OptionalPackages.Count <= 4096,
            "Installed compatibility evidence is invalid.");
        foreach (var provenance in Resolution.RestoreProvenance)
            PackageCompatibilityAuthority.VerifyInventory(provenance.Artifact);
    }
}

internal static class PackageCompatibilityAuthority
{
    private static readonly JsonSerializerOptions InventoryJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static void VerifyInventory(RegistryPackagePublicApiInventoryResponseV1 value)
        => _ = SunderPackagePublicApiInventory.ParseVerifyHash(JsonSerializer.SerializeToUtf8Bytes(value.Inventory, InventoryJson), value.PublicInventorySha256);
    internal static async Task<RuntimePackageSource> ReadSourceAsync(InstalledPackageRecord package, CancellationToken token)
    {
        PackageStorePathGuard.EnsureSafe(package.ManifestPath, package.InstallPath);
        await using var stream = new FileStream(package.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        Require(stream.Length is > 0 and <= 4 * 1024 * 1024, "Installed compatibility manifest exceeds its bound.");
        var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, token);
        var receipt = package.ContentInventory.SingleOrDefault(value => value.Path == SunderPackageFormat.ManifestPath);
        Require(stream.ReadByte() == -1 && receipt is not null && receipt.Size == bytes.Length
            && receipt.Sha256 == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), "Installed compatibility manifest differs from retained content.");
        var manifest = StrictJsonSerializer.Deserialize<SunderPackageManifest>(bytes, new JsonSerializerOptions()) ?? throw Required(package.PackageId);
        Require(manifest.Id == package.PackageId && manifest.Version == package.Version, "Installed compatibility manifest identity differs.");
        return PackagePolicyAdmission.WithProvenance(new(package.PackageId, PackageSourceKind.Installed, package.InstallPath, ContentIdentity: package.ContentIdentity, Manifest: manifest,
            ContentIndex: new(1, package.ContentInventory.Select(value => new SunderPackageContentIndexEntry(value.Path, value.Sha256, value.Size)).ToArray())), package);
    }

    internal static bool NeedsEvidence(SunderPackageManifest manifest) => manifest.PackageArtifactImports is { Count: > 0 }
        || manifest.PlatformImports is { Count: > 0 } || (manifest.DependsOn ?? []).Any(value => value.Presence == "optional");

    internal static IReadOnlyList<PackageDependencyDescriptor> EffectiveDependencies(RuntimePackageSource source)
        => (source.Manifest?.DependsOn ?? []).Where(dependency => dependency.Presence != "optional" || !Omitted(source, dependency.PackageId!))
            .Select(dependency => new PackageDependencyDescriptor(dependency.PackageId!, dependency.VersionRange!)).ToArray();

    internal static void ValidateGraph(IReadOnlyList<RuntimePackageSource> sources)
    {
        var graph = sources.ToDictionary(source => source.PackageId, StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var manifest = source.Manifest ?? throw new InvalidDataException("Compatibility graph source lacks a manifest.");
            PackageManifestAdmissionPolicy.EnsureSupported(manifest);
            if (!NeedsEvidence(manifest)) continue;
            var development = source.Kind == PackageSourceKind.Dev ? source.DevelopmentImports ?? throw Required(source.PackageId) : null;
            var evidence = development is null ? source.Compatibility ?? throw Required(source.PackageId) : null;
            if (development is not null)
                Require(development.PackageId == source.PackageId && development.Version == manifest.Version && source.ContentIndex is not null
                    && development.ManifestSha256 == RuntimeRpcPackageMetadata.GetManifestSha256(source.ContentIndex), "Development evidence names a different manifest.");
            else
            {
                var expected = new RegistryPackageVersionIdentity(source.PackageId, manifest.Version!, source.RegistryPolicyContentIdentity ?? source.ContentIdentity!);
                Require(evidence!.Package == expected, "Compatibility evidence names different package content.");
            }
            var provenance = development?.Inventories ?? evidence!.Resolution.RestoreProvenance.Select(DevelopmentPackageImport.FromRegistry).ToArray();
            foreach (var dependency in manifest.DependsOn ?? [])
            {
                if (dependency.Presence == "optional" && Omitted(source, dependency.PackageId!)) continue;
                Require(graph.TryGetValue(dependency.PackageId!, out var target), $"Required dependency '{dependency.PackageId}' is absent.");
                Require(PackageVersionRange.IsSatisfiedBy(target!.Manifest!.Version!, dependency.VersionRange!), $"Dependency '{dependency.PackageId}' is outside its reviewed range.");
            }
            var artifacts = (manifest.PackageArtifactImports ?? []).OfType<SunderPackageArtifactImportManifest>().ToDictionary(value => value.PackageId!, StringComparer.Ordinal);
            var inventories = new Dictionary<string, RegistryPackagePublicApiInventoryV1>(StringComparer.Ordinal);
            foreach (var artifact in artifacts.Values)
            {
                var match = provenance.Where(value => value.Artifact.Inventory.PackageId == artifact.PackageId
                    && value.Artifact.Inventory.PackageVersion == artifact.SelectedPackageVersion
                    && value.Artifact.Inventory.PackageContentIdentity == artifact.SelectedPackageContentIdentity
                    && value.RequestedVersionRange == artifact.RequestedVersionRange && value.IncludePrerelease == artifact.IncludePrerelease).ToArray();
                Require(match.Length > 0 && match.All(value => value.Artifact.PublicInventorySha256 == artifact.PublicInventorySha256),
                    $"Complete restore inventory for '{artifact.PackageId}' is missing or conflicting.");
                var inventory = match[0].Artifact.Inventory;
                _ = SunderPackagePublicApiInventory.ParseVerifyHash(JsonSerializer.SerializeToUtf8Bytes(inventory, InventoryJson), artifact.PublicInventorySha256!);
                Require(match[0].Artifact.PublicInventorySha256 == artifact.PublicInventorySha256 && inventory.PublicContractCount == artifact.PublicContractCount,
                    "Restore inventory count/hash differs from the package manifest.");
                var expectedAuthorities = inventory.Contracts.Select(AuthorityKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                var actualAuthorities = (artifact.AuthorityConstraints ?? []).OfType<SunderPackageAuthorityConstraintManifest>().Select(AuthorityKey).Order(StringComparer.Ordinal);
                Require(expectedAuthorities.SequenceEqual(actualAuthorities, StringComparer.Ordinal), "Restore authority constraints are incomplete.");
                inventories.Add(artifact.PackageId!, inventory);
            }
            foreach (var api in (manifest.PackageApiImports ?? []).OfType<SunderPackageApiImportManifest>())
            {
                var inventory = inventories[api.PackageId!];
                var complete = inventory.Contracts.Where(value => value.AuthorityKind == "package").Select(value => Key(value.ContractId, value.ContractVersion)).Order(StringComparer.Ordinal);
                Require(complete.SequenceEqual((api.BundleRefs ?? []).Select(value => Key(value!.ContractId!, value.ContractVersion!)).Order(StringComparer.Ordinal), StringComparer.Ordinal),
                    "Package API imports must cover the complete package-authority public inventory.");
                foreach (var reference in api.BundleRefs ?? []) VerifyTuple(api.PackageId!, reference!, api.Presence == "optional" && Omitted(source, api.PackageId!));
            }
            foreach (var provider in (manifest.ProviderImports ?? []).OfType<SunderPackageProviderImportManifest>())
                VerifyTuple(provider.PackageId!, provider.BundleRef!, provider.Presence == "optional" && Omitted(source, provider.PackageId!));
            foreach (var artifact in artifacts.Values)
            {
                var dependency = (manifest.DependsOn ?? []).Single(value => value.PackageId == artifact.PackageId);
                if (dependency.Presence == "optional" && Omitted(source, artifact.PackageId!)) continue;
                Require(graph.TryGetValue(artifact.PackageId!, out var selected) && InRange(selected!.Manifest!.Version!, artifact),
                    $"Included artifact '{artifact.PackageId}' violates its reviewed range/prerelease policy.");
            }
            foreach (var platform in development is null ? PackagePolicyAdmission.Platforms(manifest) : [])
                Require(evidence!.Resolution.PlatformCatalogs.Any(value => value.Catalog == platform && value.Disposition == "Allowed" && value.LifecycleState == "Supported"),
                    "The resolution lacks exact allowed platform-catalog evidence.");

            void VerifyTuple(string selectedId, SunderPackageContractReferenceManifest reference, bool omitted)
            {
                var imported = (manifest.ContractBundles ?? []).Single(value => value!.ContractId == reference.ContractId && value.Version == reference.ContractVersion)!;
                var original = inventories[selectedId].Contracts.Single(value => value.ContractId == reference.ContractId && value.ContractVersion == reference.ContractVersion);
                Require(imported.Source == "imported" && Matches(imported, original)
                    && imported.SelectedAuthorityRelease == original.SelectedAuthorityRelease && imported.SelectedAuthorityContentIdentity == original.SelectedAuthorityContentIdentity,
                    "Imported contract differs from its complete restore inventory.");
                if (omitted) return;
                Require(graph.TryGetValue(selectedId, out var selected) && InRange(selected!.Manifest!.Version!, artifacts[selectedId]), "Selected package is absent or out of range.");
                Require(SatisfiesImport(source, selected!, original),
                    "The installed selected package does not satisfy the imported consumer surface.");
                if (original.AuthorityKind == "package")
                {
                    Require(artifacts.TryGetValue(original.AuthorityId, out var review) && graph.TryGetValue(original.AuthorityId, out var authority)
                        && InRange(authority!.Manifest!.Version!, review!)
                        && SatisfiesImport(source, authority, original, requireOwned: true),
                        "The ultimate package authority lacks a reviewed in-range compatible owned contract.");
                }
            }
        }
    }

    private static bool Omitted(RuntimePackageSource source, string packageId)
    {
        if (source.Kind == PackageSourceKind.Dev) return source.DevelopmentOmittedPackages.Contains(packageId, StringComparer.Ordinal);
        var evidence = source.Compatibility;
        if (evidence is null) return false;
        var omitted = evidence.Resolution.OptionalPackages.Where(value => value.ConsumerPackageId == source.PackageId && value.Disposition == "omitted")
            .Select(value => value.PackageId).ToHashSet(StringComparer.Ordinal);
        var artifacts = (source.Manifest?.PackageArtifactImports ?? []).OfType<SunderPackageArtifactImportManifest>().ToDictionary(value => value.PackageId!, StringComparer.Ordinal);
        var pending = new Queue<string>(omitted);
        while (pending.TryDequeue(out var id))
        {
            if (!artifacts.TryGetValue(id, out var artifact)) continue;
            foreach (var authority in (artifact.AuthorityConstraints ?? []).OfType<SunderPackageAuthorityConstraintManifest>().Where(value => value.AuthorityKind == "package"))
                if (omitted.Add(authority.AuthorityId!)) pending.Enqueue(authority.AuthorityId!);
        }
        return omitted.Contains(packageId);
    }
    private static bool InRange(string version, SunderPackageArtifactImportManifest review) => SemanticVersion.TryParse(version, out var parsed)
        && (review.IncludePrerelease == true || parsed.Prerelease is null) && PackageVersionRange.IsSatisfiedBy(version, review.RequestedVersionRange!);
    private static bool Matches(SunderPackageContractBundleManifest bundle, RegistryPackagePublicApiContractV1 contract)
        => bundle.ContractId == contract.ContractId && bundle.Version == contract.ContractVersion && bundle.DescriptorSha256 == contract.DescriptorSha256
            && bundle.AbiSha256 == contract.AbiSha256 && bundle.AuthorityKind == contract.AuthorityKind && bundle.AuthorityId == contract.AuthorityId;
    private static string Key(string id, string version) => id + "\0" + version;
    private static string AuthorityKey(RegistryPackagePublicApiContractV1 value) => string.Join('\0', value.AuthorityKind, value.AuthorityId, value.SelectedAuthorityRelease,
        value.SelectedAuthorityContentIdentity);
    private static string AuthorityKey(SunderPackageAuthorityConstraintManifest value) => string.Join('\0', value.AuthorityKind, value.AuthorityId, value.SelectedAuthorityRelease,
        value.SelectedAuthorityContentIdentity);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(PackagePolicyErrorCodes.ContractAuthorityReleaseConflict + ": " + message); }
    private static InvalidDataException Required(string id) => new(PackagePolicyErrorCodes.PolicyRequired + $": package '{id}' needs retained complete Registry resolution evidence for imports or optional dependencies.");

    internal static IReadOnlyList<PreparedRuntimePackage> ResolveDevelopmentGraph(IReadOnlyList<PreparedRuntimePackage> packages)
    {
        var graph = packages.ToDictionary(value => value.PackageId, value => value.Source, StringComparer.Ordinal);
        return packages.Select(package =>
        {
            var source = package.Source;
            if (source.Kind != PackageSourceKind.Dev || source.Manifest is null || !NeedsEvidence(source.Manifest)) return package;
            _ = source.DevelopmentImports ?? throw Required(source.PackageId);
            var omitted = (source.Manifest.DependsOn ?? []).Where(dependency => dependency.Presence == "optional"
                    && !SatisfiesDevelopmentDependency(source, dependency, graph)).Select(value => value.PackageId!).Order(StringComparer.Ordinal).ToArray();
            source = source with { DevelopmentOmittedPackages = Array.AsReadOnly(omitted) };
            source = source with { Dependencies = EffectiveDependencies(source) };
            return package with { Source = source, Dependencies = source.PackageDependencies };
        }).ToArray();
    }

    private static bool SatisfiesDevelopmentDependency(RuntimePackageSource source, SunderPackageDependencyManifest dependency,
        IReadOnlyDictionary<string, RuntimePackageSource> graph)
    {
        if (!graph.TryGetValue(dependency.PackageId!, out var actual) || !PackageVersionRange.IsSatisfiedBy(actual.Manifest!.Version!, dependency.VersionRange!)) return false;
        var artifacts = (source.Manifest!.PackageArtifactImports ?? []).OfType<SunderPackageArtifactImportManifest>().ToDictionary(value => value.PackageId!, StringComparer.Ordinal);
        if (!artifacts.TryGetValue(dependency.PackageId!, out var selected)) return true;
        if (!InRange(actual.Manifest!.Version!, selected)) return false;
        var inventory = source.DevelopmentImports!.Inventories.Single(value => value.Artifact.Inventory.PackageId == selected.PackageId).Artifact.Inventory;
        var required = (source.Manifest.PackageApiImports ?? []).OfType<SunderPackageApiImportManifest>().Where(value => value.PackageId == selected.PackageId)
            .SelectMany(value => value.BundleRefs ?? []).Concat((source.Manifest.ProviderImports ?? []).OfType<SunderPackageProviderImportManifest>()
                .Where(value => value.PackageId == selected.PackageId).Select(value => value.BundleRef));
        foreach (var reference in required)
        {
            var contract = inventory.Contracts.Single(value => value.ContractId == reference!.ContractId && value.ContractVersion == reference.ContractVersion);
            if (!SatisfiesImport(source, actual, contract)) return false;
            if (contract.AuthorityKind == "package" && (!artifacts.TryGetValue(contract.AuthorityId, out var review)
                || !graph.TryGetValue(contract.AuthorityId, out var authority) || !InRange(authority.Manifest!.Version!, review)
                || !SatisfiesImport(source, authority, contract, requireOwned: true))) return false;
        }
        return true;
    }

    private static bool SatisfiesImport(RuntimePackageSource consumer, RuntimePackageSource provider,
        RegistryPackagePublicApiContractV1 original, bool requireOwned = false)
    {
        var uses = (consumer.Manifest!.UsesContracts ?? []).OfType<SunderPackageContractUseManifest>()
            .Where(use => use.ContractId == original.ContractId && use.ContractVersion == original.ContractVersion).ToArray();
        var exact = uses.Length == 0 || uses.Any(use => use.ConsumerRequirements is null)
            || (consumer.Manifest.ProviderImports ?? []).Any(import => import?.ContractId == original.ContractId);
        // Complete restore inventory is retained, but unused APIs are not runtime requirements.
        if (!exact && uses.All(use => use.Required != true && use.ConsumerRequirements!.Value.GetProperty("methods").GetArrayLength() == 0)) return true;
        var candidate = (provider.Manifest!.ContractBundles ?? []).SingleOrDefault(bundle => bundle?.ContractId == original.ContractId
            && bundle.Exposure == "public" && bundle.AuthorityKind == original.AuthorityKind && bundle.AuthorityId == original.AuthorityId
            && (!requireOwned || bundle.Source == "owned"));
        if (candidate is null) return false;
        if (exact) return Matches(candidate, original);
        var sourceBundle = consumer.Manifest.ContractBundles!.Single(bundle => bundle?.ContractId == original.ContractId)!;
        var source = ReadContract(consumer, sourceBundle);
        var current = ReadContract(provider, candidate);
        return uses.All(use => SunderRpcConsumerCompatibility.Matches(source, current, use.ConsumerRequirements)
            && SunderRpcConsumerCompatibility.MatchesProjection(SunderRpcConsumerRequirements.Parse(use.ConsumerRequirements!.Value, source),
                ReadProjection(consumer, original.ContractId), ReadProjection(provider, original.ContractId)));
    }

    internal static SunderRpcModelProjection ReadProjection(RuntimePackageSource source, string id)
        => ReadProjections(source).Contracts.Single(value => value.ContractId == id).ModelProjection;

    internal static SunderRpcModelProjectionDocument ReadProjections(RuntimePackageSource source)
    {
        if (source.RpcModelProjection is { } cached) return cached;
        var path = Path.Combine(source.EffectiveSnapshotFolder, SunderPackageFormat.RpcModelProjectionPath);
        PackageStorePathGuard.EnsureSafe(path, source.EffectiveSnapshotFolder);
        using var stream = File.OpenRead(path);
        Require(stream.Length is > 0 and <= SunderPackageFormat.MaxRpcModelProjectionBytes, "Model projection exceeds its bound.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        Require(stream.ReadByte() == -1, "Model projection changed size while being read.");
        if (source.ContentIndex is { } index)
        {
            var entry = index.Files!.Single(value => value?.Path == SunderPackageFormat.RpcModelProjectionPath)!;
            Require(entry.Size == bytes.Length && entry.Sha256 == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
                "Model projection differs from the authenticated content inventory.");
        }
        var projection = SunderRpcModelProjectionDocument.Parse(bytes);
        Require(projection.PackageId == source.PackageId && projection.PackageVersion == source.Manifest!.Version, "Model projection names different package content.");
        return projection;
    }

    internal static SunderRpcContractDescriptor ReadContract(RuntimePackageSource source, SunderPackageContractBundleManifest bundle)
    {
        if (source.RpcContracts?.TryGetValue(PackageSessionPreparer.ContractKey(bundle.ContractId!, bundle.Version!), out var cached) == true)
            return cached;
        var relative = ArchiveRelativePath.Parse(SunderPackageFormat.SharedPayloadRoot + bundle.DescriptorPath!,
            SunderPackageFormat.MaxLogicalPathLength, SunderPackageFormat.MaxLogicalPathDepth);
        var path = relative.ToPlatformPath(source.EffectiveSnapshotFolder);
        PackageStorePathGuard.EnsureSafe(path, source.EffectiveSnapshotFolder);
        using var stream = File.OpenRead(path);
        Require(stream.Length is > 0 and <= SunderPackageFormat.MaxContractDescriptorBytes, "Contract descriptor exceeds its bound.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        var descriptor = SunderRpcContractDescriptor.Parse(bytes);
        Require(stream.ReadByte() == -1 && descriptor.ContractId == bundle.ContractId && descriptor.Version == bundle.Version
            && descriptor.Sha256 == bundle.DescriptorSha256 && descriptor.AbiSha256 == bundle.AbiSha256
            && bytes.AsSpan().SequenceEqual(descriptor.GetCanonicalUtf8()), "Contract descriptor differs from authenticated import provenance.");
        return descriptor;
    }

    internal static bool DevelopmentUseEnabled(RuntimePackageSource source, string role, string contractId, string version)
    {
        if (source.Kind != PackageSourceKind.Dev) return true;
        var selections = (source.Manifest?.PackageApiImports ?? []).OfType<SunderPackageApiImportManifest>()
            .Where(value => (value.UseRefs ?? []).Any(reference => reference?.Role == role && reference.ContractId == contractId && reference.ContractVersion == version)).ToArray();
        return selections.Length == 0 || selections.Any(value => !Omitted(source, value.PackageId!));
    }
}
