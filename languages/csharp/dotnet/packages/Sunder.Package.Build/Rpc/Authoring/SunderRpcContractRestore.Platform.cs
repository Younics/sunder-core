using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed partial class SunderRpcContractRestore
{
    internal async Task<JsonObject> SelectPlatformAsync(string contractId, string? registryOrigin, CancellationToken token, string? localCatalog = null)
    {
        if (!PackageId.TryParse(contractId, out _)) throw Error("platform-api.import-not-found", "A canonical platform contract ID is required.");
        if (registryOrigin is not null) SunderRpcAuthoringDocuments.RequireCanonicalRegistryOrigin(registryOrigin, "platform selection", AllowDevelopmentRegistryOrigin);
        SunderVerifiedPlatformContractCatalog catalog;
        if (localCatalog is not null)
        {
            token.ThrowIfCancellationRequested();
            catalog = SunderPlatformContractCatalog.Verify(SunderRpcDescriptorFileReader.ReadStableBytes(
                localCatalog, SunderRpcPlatformCatalogCache.MaximumBytes, "local platform catalog"));
            if (!PackageVersionRange.IsSatisfiedBy(catalog.ReleaseVersion, SunderRpcPlatformCatalogCache.ToolRange))
                throw Error("platform-api.catalog-version-unsupported", "The local catalog must be within the coordinated SDK/tooling line.");
        }
        else
        {
            if (registryOrigin is null)
                throw Error("platform-api.local-input-required", "A local platform selection requires an explicit catalog; no Registry is inferred.");
            var origin = new Uri(registryOrigin);
            using var metadata = SunderRpcAuthoringJson.ParseRegistryMetadata(await ReadAsync(new(origin,
                "api/v1/platform-contracts/sunder.core/resolve?versionRange=" + Segment(SunderRpcPlatformCatalogCache.ToolRange)), null, 1_048_576, token,
                notFoundCode: "platform-api.import-not-found"), 1_048_576);
            var release = Text(metadata.RootElement, "releaseVersion");
            if (!SemanticVersion.TryParse(release, out var version) || version.Prerelease is not null
                || !PackageVersionRange.IsSatisfiedBy(release!, SunderRpcPlatformCatalogCache.ToolRange)
                || Text(metadata.RootElement, "lifecycleState") != "Supported")
                throw Error("platform-api.catalog-invalid", "Resolution must select a supported stable catalog within the coordinated SDK/tooling line.");
            catalog = await ReadPlatformMetadataCatalogAsync(metadata.RootElement, origin, release!, [], token);
        }
        var descriptor = catalog.Contracts.SingleOrDefault(contract => contract.ContractId == contractId)
            ?? throw Error("platform-api.import-not-found", "The selected catalog does not contain the requested contract.");
        var selected = new JsonObject
        {
            ["authorityKind"] = "platform", ["authorityId"] = "sunder.core", ["authorityReleaseRange"] = SunderRpcPlatformCatalogCache.ToolRange,
            ["includePrerelease"] = false, ["selectedAuthorityRelease"] = catalog.ReleaseVersion, ["selectedAuthorityContentIdentity"] = catalog.ContentIdentity,
            ["contractId"] = descriptor.ContractId, ["resolvedContractVersion"] = descriptor.Version, ["registryOrigin"] = registryOrigin,
            ["descriptorBytes"] = descriptor.GetCanonicalUtf8().Length, ["descriptorSha256"] = descriptor.Sha256, ["abiSha256"] = descriptor.AbiSha256, ["etag"] = "\"" + descriptor.Sha256 + "\"",
        };
        if (registryOrigin is null) selected.Remove("registryOrigin");
        return selected;
    }

    private async Task<IReadOnlyDictionary<string, SunderVerifiedPlatformContractCatalog>> RestorePlatformCatalogsAsync(
        SunderRpcContractLock contractLock, string cacheRoot, IReadOnlyList<SunderRpcPlatformRequirement> requirements,
        string? localCatalog, bool offlineCandidate, CancellationToken token)
    {
        var result = new Dictionary<string, SunderVerifiedPlatformContractCatalog>(StringComparer.Ordinal);
        if (requirements.Count == 0) return result;
        if (localCatalog is not null)
        {
            var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(localCatalog, SunderRpcPlatformCatalogCache.MaximumBytes, "selected Registry catalog input");
            var catalog = SunderRpcPlatformCatalogCache.Verify(bytes, requirements);
            result.Add(catalog.ContentIdentity, catalog);
            return result;
        }
        if (offlineCandidate) return SunderRpcPlatformCatalogCache.Resolve(contractLock, cacheRoot);
        foreach (var group in requirements.GroupBy(item => (item.ContentIdentity, item.RegistryOrigin)))
        {
            var selected = group.First();
            if (selected.RegistryOrigin is null)
            {
                result[selected.ContentIdentity] = SunderRpcPlatformCatalogCache.Resolve(contractLock, cacheRoot)[selected.ContentIdentity];
                continue;
            }
            var metadataUri = new Uri(new Uri(selected.RegistryOrigin), $"api/v1/platform-contracts/sunder.core/releases/{Segment(selected.ReleaseVersion)}");
            using var metadata = SunderRpcAuthoringJson.ParseRegistryMetadata(await ReadAsync(metadataUri, null, 1_048_576, token,
                notFoundCode: "platform-api.import-not-found"), 1_048_576);
            var catalog = await ReadPlatformMetadataCatalogAsync(metadata.RootElement, new Uri(selected.RegistryOrigin), selected.ReleaseVersion, group.ToArray(), token);
            var bytes = catalog.GetCanonicalUtf8();
            if (result.TryGetValue(catalog.ContentIdentity, out var previous) && !previous.GetCanonicalUtf8().AsSpan().SequenceEqual(bytes))
                throw Error("platform-api.catalog-invalid", "Registries disagree on immutable catalog bytes.");
            result[catalog.ContentIdentity] = catalog;
        }
        return result;
    }

    private async Task<SunderVerifiedPlatformContractCatalog> ReadPlatformMetadataCatalogAsync(JsonElement value, Uri origin, string release,
        IReadOnlyList<SunderRpcPlatformRequirement> pins, CancellationToken token)
    {
        var catalogUri = new Uri(origin, $"api/v1/platform-contracts/sunder.core/releases/{Segment(release)}/catalog");
        var hash = Text(value, "catalogSha256");
        if (Text(value, "authorityId") != "sunder.core" || Text(value, "releaseVersion") != release
            || hash is not { Length: 64 } || hash.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9'))
            || Text(value, "catalogDownloadUrl") != catalogUri.AbsoluteUri
            || !value.TryGetProperty("catalogBytes", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt32(out var length)
            || length is < 2 or > SunderRpcPlatformCatalogCache.MaximumBytes)
            throw Error("platform-api.catalog-invalid", "Catalog metadata does not reproduce the immutable release and canonical URL.");
        var bytes = await ReadAsync(catalogUri, "\"" + hash + "\"", length, token,
            validateHeaders: response =>
            {
                if (response.Content.Headers.ContentLength != length) throw Error("platform-api.catalog-invalid", "The catalog response requires its exact Content-Length.");
            });
        var catalog = SunderRpcPlatformCatalogCache.Verify(bytes, pins);
        if (bytes.Length != length || catalog.ContentIdentity != hash || catalog.ReleaseVersion != release)
            throw Error("platform-api.catalog-invalid", "Registry catalog identity differs from release metadata.");
        ValidatePlatformMetadata(value, catalog);
        return catalog;
    }

    private static void ValidatePlatformMetadata(JsonElement metadata, SunderVerifiedPlatformContractCatalog catalog)
    {
        var descriptors = JsonSerializer.SerializeToElement(catalog.Contracts.Select(descriptor => new
        {
            contractId = descriptor.ContractId, contractVersion = descriptor.Version, descriptorBytes = descriptor.GetCanonicalUtf8().Length,
            descriptorSha256 = descriptor.Sha256, abiSha256 = descriptor.AbiSha256,
        }));
        if (!metadata.TryGetProperty("contracts", out var contracts) || !JsonElement.DeepEquals(descriptors, contracts))
            throw Error("platform-api.catalog-invalid", "Registry catalog projection differs from the complete canonical catalog.");
        // Exact immutable evidence remains readable after revocation. Restore does
        // not resolve a new release or confer publication/installation authority.
        if (Text(metadata, "lifecycleState") is not ("Supported" or "Revoked"))
            throw Error("platform-api.catalog-invalid", "Catalog lifecycle metadata is unsupported.");
    }
}
