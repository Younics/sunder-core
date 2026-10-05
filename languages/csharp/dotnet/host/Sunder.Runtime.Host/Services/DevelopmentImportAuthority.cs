using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

// Evidence about imports of local code, never a published identity or permission
// decision for the development package itself.
internal sealed record DevelopmentPackageImport(RegistryPackagePublicApiInventoryResponseV1 Artifact, string RequestedVersionRange,
    bool IncludePrerelease, IReadOnlyList<RegistryPackageAuthorityRequirement> AuthorityRequirements)
{
    public IReadOnlyList<RegistryPackageAuthorityRequirement> AuthorityRequirements { get; } = Array.AsReadOnly(AuthorityRequirements.ToArray());
    internal static DevelopmentPackageImport FromRegistry(RegistryPackageRestoreProvenance value)
        => new(value.Artifact, value.RequestedVersionRange, value.IncludePrerelease, value.AuthorityRequirements);
}

internal sealed record DevelopmentImportAuthority(int SchemaVersion, [property: JsonRequired] string SourceKind, string PackageId, string Version,
    string ManifestSha256, string? RegistryOrigin, IReadOnlyList<DevelopmentPackageImport> Inventories,
    IReadOnlyList<string> Catalogs)
{
    internal bool IsLocal => SourceKind == "local";
    public IReadOnlyList<DevelopmentPackageImport> Inventories { get; } = Array.AsReadOnly(Inventories.ToArray());
    public IReadOnlyList<string> Catalogs { get; } = Array.AsReadOnly(Catalogs.ToArray());
}

internal sealed class DevelopmentImportAuthorityStore(RuntimePackagePaths paths, PackageLifecyclePolicyCache policies)
{
    internal const int MaximumReceiptBytes = 96 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(RegistryActivePolicyClient.Json) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal async Task<DevelopmentImportAuthority> ResolveAsync(RuntimePackageSource source, CancellationToken token)
    {
        var hash = source.ContentIndex is null ? null : RuntimeRpcPackageMetadata.GetManifestSha256(source.ContentIndex);
        if (source.Kind != PackageSourceKind.Dev || source.Manifest is null || hash is null) throw Required();
        var path = PathFor(hash); using var lease = await RuntimeFileLock.AcquireExclusiveAsync(path + ".lock", token);
        if (!File.Exists(path)) throw Required();
        var bytes = await ReadAsync(path, MaximumReceiptBytes, token);
        var evidence = JsonSerializer.Deserialize<DevelopmentImportAuthority>(bytes, Json) ?? throw Required();
        Validate(evidence, source.Manifest, hash);
        var platforms = Platforms(source.Manifest);
        if (platforms.Length > 0 && !evidence.IsLocal)
        {
            await policies.CheckpointClockAsync(evidence.RegistryOrigin!, token);
            policies.RequirePlatformActivation(evidence.RegistryOrigin!, platforms);
        }
        return evidence;
    }

    internal async Task SaveAsync(DevelopmentImportAuthority evidence, SunderPackageManifest manifest, CancellationToken token)
    {
        Validate(evidence, manifest, evidence.ManifestSha256);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence, Json);
        if (bytes.Length >= MaximumReceiptBytes) throw Invalid("Development import evidence exceeds its byte limit.");
        var path = PathFor(evidence.ManifestSha256);
        using var lease = await RuntimeFileLock.AcquireExclusiveAsync(path + ".lock", token);
        if (File.Exists(path) && (await ReadAsync(path, MaximumReceiptBytes, token)).AsSpan().TrimEnd("\r\n"u8).SequenceEqual(bytes)) return;
        PackageStorePathGuard.EnsureSafe(path, paths.RootPath);
        await DurableJsonDocument.WriteAsync(path, evidence, Json, token);
    }

    internal string PathFor(string hash)
    {
        if (!RegistryActivePolicyClient.Hash(hash)) throw Invalid("Development manifest hash is invalid.");
        var path = Path.Combine(paths.CacheRootPath, "development-import-authority", "v1", hash + ".json");
        PackageStorePathGuard.EnsureSafe(path, paths.RootPath);
        return path;
    }

    internal static async Task<byte[]> ReadAsync(string path, int maximum, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is < 1 || stream.Length > maximum) throw Invalid("Development import input exceeds its byte limit.");
        var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, token);
        if (stream.ReadByte() != -1) throw Invalid("Development import input changed during reading.");
        return bytes;
    }

    internal static void Validate(DevelopmentImportAuthority evidence, SunderPackageManifest manifest, string manifestHash)
    {
        if (evidence.SchemaVersion != 1 || evidence.SourceKind is not ("local" or "registry") || evidence.PackageId != manifest.Id || evidence.Version != manifest.Version
            || evidence.ManifestSha256 != manifestHash || !RegistryActivePolicyClient.Hash(manifestHash)
            || (evidence.IsLocal ? evidence.RegistryOrigin is not null
                : evidence.RegistryOrigin is null || evidence.RegistryOrigin != RegistryActivePolicyClient.Origin(evidence.RegistryOrigin))
            || evidence.Inventories.Count > 128 || evidence.Catalogs.Count > 128) throw Invalid("Development import evidence differs from the local manifest.");
        var artifacts = (manifest.PackageArtifactImports ?? []).OfType<SunderPackageArtifactImportManifest>().ToArray();
        if (artifacts.Length != evidence.Inventories.Count || evidence.Inventories.Select(value => value.Artifact.Inventory.PackageId).Distinct(StringComparer.Ordinal).Count() != artifacts.Length)
            throw Invalid("Development evidence must contain exactly the complete selected artifact inventory set.");
        foreach (var artifact in artifacts)
        {
            var retained = evidence.Inventories.SingleOrDefault(value => value.Artifact.Inventory.PackageId == artifact.PackageId)
                ?? throw Invalid("A selected package inventory is missing.");
            if (retained.RequestedVersionRange != artifact.RequestedVersionRange
                || retained.IncludePrerelease != artifact.IncludePrerelease) throw Invalid("Reviewed development import intent differs.");
            ValidateInventory(artifact, retained.Artifact);
            var authorityIds = retained.Artifact.Inventory.Contracts.Where(value => value.AuthorityKind == "package" && value.AuthorityId != artifact.PackageId)
                .Select(value => value.AuthorityId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var expected = authorityIds.Select(id => artifacts.SingleOrDefault(value => value.PackageId == id)
                ?? throw Invalid("An ultimate authority requires an explicitly reviewed artifact selection."))
                .Select(value => new RegistryPackageAuthorityRequirement(value.PackageId!, value.RequestedVersionRange!, value.IncludePrerelease!.Value)).ToArray();
            if (!retained.AuthorityRequirements.SequenceEqual(expected)) throw Invalid("Reviewed ultimate-authority requirements differ.");
        }
        foreach (var api in (manifest.PackageApiImports ?? []).OfType<SunderPackageApiImportManifest>())
        {
            var inventory = evidence.Inventories.Single(value => value.Artifact.Inventory.PackageId == api.PackageId).Artifact.Inventory;
            var expected = inventory.Contracts.Where(value => value.AuthorityKind == "package").Select(value => value.ContractId + "\0" + value.ContractVersion).Order(StringComparer.Ordinal);
            if (!expected.SequenceEqual((api.BundleRefs ?? []).Select(value => value!.ContractId + "\0" + value.ContractVersion).Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw Invalid("A development package API must cover the complete public package-authority inventory.");
            foreach (var reference in api.BundleRefs ?? []) VerifyBundle(api.PackageId!, reference!);
        }
        foreach (var provider in (manifest.ProviderImports ?? []).OfType<SunderPackageProviderImportManifest>()) VerifyBundle(provider.PackageId!, provider.BundleRef!);
        var subjects = Platforms(manifest);
        if (subjects.Length != evidence.Catalogs.Count) throw Invalid("Development evidence must retain the exact selected catalog set.");
        var catalogs = new Dictionary<string, SunderVerifiedPlatformContractCatalog>(StringComparer.Ordinal);
        foreach (var encoded in evidence.Catalogs)
        {
            if (encoded.Length > 89_478_488) throw Invalid("A Registry catalog exceeds its byte limit.");
            var bytes = Convert.FromBase64String(encoded);
            if (encoded != Convert.ToBase64String(bytes)) throw Invalid("Catalog evidence is not canonical base64.");
            var catalog = SunderPlatformContractCatalog.Verify(bytes);
            if (!catalogs.TryAdd(catalog.ContentIdentity, catalog)) throw Invalid("Catalog evidence is duplicated.");
        }
        foreach (var subject in subjects)
            if (!catalogs.TryGetValue(subject.CatalogSha256, out var catalog) || catalog.ReleaseVersion != subject.ReleaseVersion)
                throw Invalid("The Registry catalog differs from its manifest authority.");
        foreach (var inventory in evidence.Inventories)
            foreach (var contract in inventory.Artifact.Inventory.Contracts.Where(value => value.AuthorityKind == "platform"))
            {
                var descriptor = catalogs[contract.SelectedAuthorityContentIdentity].VerifyPinned(contract.SelectedAuthorityRelease, contract.SelectedAuthorityContentIdentity,
                    contract.ContractId,
                    contract.ContractVersion, contract.DescriptorSha256, contract.AbiSha256);
                if (descriptor.GetCanonicalUtf8().Length != contract.DescriptorBytes) throw Invalid("Catalog descriptor length differs from publication evidence.");
            }
        foreach (var bundle in (manifest.ContractBundles ?? []).Where(value => value?.AuthorityKind == "platform"))
            _ = catalogs[bundle!.SelectedAuthorityContentIdentity!].VerifyPinned(bundle.SelectedAuthorityRelease!, bundle.SelectedAuthorityContentIdentity!,
                bundle.ContractId!, bundle.Version!, bundle.DescriptorSha256!, bundle.AbiSha256!);

        void VerifyBundle(string packageId, SunderPackageContractReferenceManifest reference)
        {
            var inventory = evidence.Inventories.Single(value => value.Artifact.Inventory.PackageId == packageId).Artifact.Inventory;
            var contract = inventory.Contracts.SingleOrDefault(value => value.ContractId == reference.ContractId && value.ContractVersion == reference.ContractVersion)
                ?? throw Invalid("A development import is absent from its Registry inventory.");
            var bundle = (manifest.ContractBundles ?? []).Single(value => value!.ContractId == reference.ContractId && value.Version == reference.ContractVersion)!;
            if (bundle.Source != "imported" || bundle.AuthorityKind != contract.AuthorityKind || bundle.AuthorityId != contract.AuthorityId
                || bundle.SelectedAuthorityRelease != contract.SelectedAuthorityRelease || bundle.SelectedAuthorityContentIdentity != contract.SelectedAuthorityContentIdentity
                || bundle.DescriptorSha256 != contract.DescriptorSha256 || bundle.AbiSha256 != contract.AbiSha256)
                throw Invalid("Development bundle identity or authority differs from Registry publication evidence.");
        }
    }

    internal static void ValidateInventory(SunderPackageArtifactImportManifest expected, RegistryPackagePublicApiInventoryResponseV1 response)
    {
        PackageCompatibilityAuthority.VerifyInventory(response); var inventory = response.Inventory;
        if (inventory.PackageId != expected.PackageId || inventory.PackageVersion != expected.SelectedPackageVersion
            || inventory.PackageContentIdentity != expected.SelectedPackageContentIdentity || inventory.PublicContractCount != expected.PublicContractCount
            || response.PublicInventorySha256 != expected.PublicInventorySha256) throw Invalid("Registry inventory differs from the exact selected package artifact.");
        var actual = inventory.Contracts.Select(value => string.Join('\0', value.AuthorityKind, value.AuthorityId, value.SelectedAuthorityRelease,
            value.SelectedAuthorityContentIdentity))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var wanted = (expected.AuthorityConstraints ?? []).Select(value => string.Join('\0', value!.AuthorityKind, value.AuthorityId,
            value.SelectedAuthorityRelease, value.SelectedAuthorityContentIdentity)).Order(StringComparer.Ordinal);
        if (!actual.SequenceEqual(wanted, StringComparer.Ordinal)) throw Invalid("Registry authority constraints differ from the complete manifest inventory.");
    }

    internal static RegistryPlatformCatalogIdentity[] Platforms(SunderPackageManifest manifest)
        => PackagePolicyAdmission.Platforms(manifest).Concat((manifest.PackageArtifactImports ?? []).OfType<SunderPackageArtifactImportManifest>()
            .SelectMany(value => value.AuthorityConstraints ?? []).Where(value => value?.AuthorityKind == "platform")
            .Select(value => new RegistryPlatformCatalogIdentity(value!.AuthorityId!, value.SelectedAuthorityRelease!, value.SelectedAuthorityContentIdentity!)))
            .Distinct().OrderBy(RegistryActivePolicyClient.PlatformKey, StringComparer.Ordinal).ToArray();

    internal static InvalidDataException Invalid(string message) => new(PackagePolicyErrorCodes.ContractAuthorityReleaseConflict + ": " + message);
    internal static InvalidDataException Required() => new(PackagePolicyErrorCodes.PolicyRequired
        + ": Development imports need exact local or Registry evidence. Run 'sunder dev package prepare-imports <folder>' with local --candidate/--platform-catalog inputs or an explicit --registry before loading.");
}
