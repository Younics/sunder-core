using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Tasks;

// Transfer the exact, already selected local import metadata from the offline
// compiler to development loading. This is not publication or a permission grant.
// Registry selections deliberately retain explicit Host preparation and policy.
internal static class DevelopmentImportHandoff
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static byte[] Create(string manifestPath, string? contractLockPath, string cacheRoot, bool allowDevelopmentRegistryOrigin,
        string? projectDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(projectDirectory))
        {
            contractLockPath = Path.GetFullPath(string.IsNullOrWhiteSpace(contractLockPath)
                ? "sunder-contracts.lock.json" : contractLockPath, Path.GetFullPath(projectDirectory));
            if (!string.IsNullOrWhiteSpace(cacheRoot)) cacheRoot = Path.GetFullPath(cacheRoot, Path.GetFullPath(projectDirectory));
        }
        if (string.IsNullOrWhiteSpace(cacheRoot)) cacheRoot = SunderRpcContractCache.DefaultRoot;
        // A tombstone replaces any previous local handoff when the producer has
        // no reviewed lock or now uses Registry authority. Never leave stale data.
        if (string.IsNullOrWhiteSpace(contractLockPath) || !File.Exists(contractLockPath)) return "null\n"u8.ToArray();
        var selection = SunderRpcAuthoringDocuments.ReadContractLock(contractLockPath, allowDevelopmentRegistryOrigin);
        if (selection.PackageArtifacts.Values.Any(artifact => artifact.RegistryOrigin is not null)
            || selection.PlatformImports.Values.Any(import => import.RegistryOrigin is not null)) return "null\n"u8.ToArray();

        _ = SunderRpcContractCache.Resolve(selection, cacheRoot);
        var catalogs = SunderRpcPlatformCatalogCache.Resolve(selection, cacheRoot);
        var manifestBytes = SunderRpcDescriptorFileReader.ReadStableBytes(manifestPath, 4 * 1024 * 1024, "development manifest");
        var manifest = StrictJsonSerializer.Deserialize<SunderPackageManifest>(manifestBytes, new JsonSerializerOptions())
            ?? throw new InvalidDataException("Development manifest is empty.");
        var artifacts = (manifest.PackageArtifactImports ?? []).OfType<SunderPackageArtifactImportManifest>()
            .OrderBy(artifact => artifact.PackageId, StringComparer.Ordinal).ToArray();
        var inventories = artifacts.Select(import =>
        {
            if (!selection.PackageArtifacts.TryGetValue(import.PackageId!, out var artifact)
                || artifact.SelectedPackageVersion != import.SelectedPackageVersion
                || artifact.SelectedPackageContentIdentity != import.SelectedPackageContentIdentity
                || artifact.PublicInventorySha256 != import.PublicInventorySha256
                || artifact.PublicContractCount != import.PublicContractCount
                || artifact.RequestedVersionRange != import.RequestedVersionRange
                || artifact.IncludePrerelease != import.IncludePrerelease)
                throw new InvalidDataException("Development manifest imports differ from the reviewed contract lock.");
            var inventory = SunderRpcAuthoringDocuments.ValidatePackagePublicInventory(artifact);
            var authorities = artifact.Contracts.Values.Where(contract => contract.AuthorityKind == "package" && contract.AuthorityId != artifact.PackageId)
                .Select(contract => contract.AuthorityId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(id => selection.PackageArtifacts.TryGetValue(id, out var authority) && artifacts.Any(value => value.PackageId == id)
                    ? new { packageId = id, versionRange = authority.RequestedVersionRange, includePrerelease = authority.IncludePrerelease }
                    : throw new InvalidDataException("Development imports require an explicit ultimate-authority selection.")).ToArray();
            return new
            {
                artifact = new { inventory = inventory.Payload, publicInventorySha256 = inventory.PublicInventorySha256 },
                requestedVersionRange = artifact.RequestedVersionRange,
                includePrerelease = artifact.IncludePrerelease,
                authorityRequirements = authorities,
            };
        }).ToArray();
        var catalogIds = (manifest.ContractBundles ?? []).Where(bundle => bundle?.AuthorityKind == "platform")
            .Select(bundle => bundle!.SelectedAuthorityContentIdentity!)
            .Concat(artifacts.SelectMany(artifact => artifact.AuthorityConstraints ?? [])
                .Where(authority => authority?.AuthorityKind == "platform").Select(authority => authority!.SelectedAuthorityContentIdentity!))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            sourceKind = "local",
            packageId = manifest.Id,
            version = manifest.Version,
            manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
            registryOrigin = (string?)null,
            inventories,
            catalogs = catalogIds.Select(id => catalogs.TryGetValue(id, out var catalog)
                ? Convert.ToBase64String(catalog.GetCanonicalUtf8())
                : throw new InvalidDataException("Development imports require the exact selected platform catalog.")).ToArray(),
        }, Json);
        if (bytes.Length >= SunderPackageFormat.MaxDevelopmentImportsBytes)
            throw new InvalidDataException("Development import handoff exceeds its byte limit.");
        if (SunderRpcAuthoringDocuments.ReadContractLock(contractLockPath, allowDevelopmentRegistryOrigin).Sha256 != selection.Sha256)
            throw new InvalidDataException("The contract selection changed while preparing development imports. Rebuild the package.");
        return bytes;
    }
}
