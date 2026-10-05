using System.Text.Json;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed partial class SunderRpcContractRestore
{
    internal async Task<string> VerifyPlatformPublicationAsync(string registryOrigin, SunderVerifiedPlatformContractCatalog expected, CancellationToken token)
    {
        SunderRpcAuthoringDocuments.RequireCanonicalRegistryOrigin(registryOrigin, "platform publication import", AllowDevelopmentRegistryOrigin);
        var origin = new Uri(registryOrigin);
        using var metadata = SunderRpcAuthoringJson.ParseRegistryMetadata(await ReadAsync(new(origin,
            $"api/v1/platform-contracts/sunder.core/releases/{Segment(expected.ReleaseVersion)}"), null, 1_048_576, token), 1_048_576);
        var actual = await ReadPlatformMetadataCatalogAsync(metadata.RootElement, origin, expected.ReleaseVersion, [], token);
        if (!actual.GetCanonicalUtf8().AsSpan().SequenceEqual(expected.GetCanonicalUtf8()))
            throw SunderRpcPublicationImport.Invalid("Registry catalog differs from the retained canonical catalog.");
        return Text(metadata.RootElement, "publishedAtUtc") ?? throw SunderRpcPublicationImport.Invalid("Registry catalog publication time is missing.");
    }

    internal async Task<SunderRpcVerifiedPackagePublication> VerifyPackagePublicationAsync(string registryOrigin, SunderPackagePublicApiInventory candidate,
        string archiveHash, long archiveBytes, CancellationToken token)
    {
        SunderRpcAuthoringDocuments.RequireCanonicalRegistryOrigin(registryOrigin, "publication import", AllowDevelopmentRegistryOrigin);
        var expected = candidate.Payload; var id = expected.GetProperty("packageId").GetString()!; var version = expected.GetProperty("packageVersion").GetString()!;
        var origin = new Uri(registryOrigin); var route = $"api/v1/packages/{Segment(id)}/versions/{Segment(version)}";
        using var details = SunderRpcAuthoringJson.ParseRegistryMetadata(await ReadAsync(new(origin, route), null, 1_048_576, token), 1_048_576);
        var value = details.RootElement;
        if (Text(value, "packageId") != id || Text(value, "version") != version
            || !value.TryGetProperty("canonicalArtifact", out var archive) || Text(archive, "sha256") != archiveHash
            || !archive.TryGetProperty("size", out var size) || !size.TryGetInt64(out var length) || length != archiveBytes
            || !value.TryGetProperty("publication", out var publication) || publication.ValueKind != JsonValueKind.Object
            || !publication.TryGetProperty("package", out var package) || Text(package, "packageId") != id || Text(package, "version") != version
            || Text(package, "packageContentIdentity") != expected.GetProperty("packageContentIdentity").GetString()
            || Text(publication, "publicInventorySha256") != candidate.PublicInventorySha256
            || Text(publication, "ownerPrincipalId") is not { Length: 32 } owner || owner.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || Text(publication, "initialAvailability") is not ("Listed" or "Yanked"))
            throw SunderRpcPublicationImport.Invalid("Registry did not confirm the exact retained artifact and immutable publication identity.");
        var inventory = await ReadInventoryAsync(origin, id, version, token);
        if (!inventory.ToCanonicalUtf8().AsSpan().SequenceEqual(candidate.ToCanonicalUtf8()))
            throw SunderRpcPublicationImport.Invalid("Registry public inventory differs from the complete validated artifact.");
        if (!value.TryGetProperty("contractBundles", out var bundles) || bundles.ValueKind != JsonValueKind.Array || bundles.GetArrayLength() > 128)
            throw SunderRpcPublicationImport.Invalid("Registry bundle evidence is missing or oversized.");
        var publicContracts = expected.GetProperty("contracts").EnumerateArray().ToDictionary(item => (Text(item, "contractId"), Text(item, "contractVersion")));
        var owned = new List<SunderRpcPublishedContract>(); var identities = new HashSet<(string?, string?)>();
        foreach (var bundle in bundles.EnumerateArray())
        {
            var key = (Text(bundle, "contractId"), Text(bundle, "version"));
            if (key.Item1 is null || key.Item2 is null || !identities.Add(key)) throw SunderRpcPublicationImport.Invalid("Registry bundle identities are invalid or duplicated.");
            var exposure = Text(bundle, "exposure");
            if (exposure == "public")
            {
                if (!publicContracts.TryGetValue(key, out var contract) || Text(bundle, "descriptorSha256") != Text(contract, "descriptorSha256")
                    || Text(bundle, "abiSha256") != Text(contract, "abiSha256")) throw SunderRpcPublicationImport.Invalid("Registry bundle/public inventory identities differ.");
                if (Text(contract, "artifactSource") != "owned") continue;
                if (Text(contract, "authorityKind") != "package" || Text(contract, "authorityId") != id)
                    throw SunderRpcPublicationImport.Invalid("Published source is not the package's owned authority.");
            }
            else if (exposure != "package") throw SunderRpcPublicationImport.Invalid("Registry bundle exposure is invalid.");
            owned.Add(new(key.Item1, key.Item2, Text(bundle, "descriptorSha256")!, Text(bundle, "abiSha256")!, exposure));
        }
        if (publicContracts.Keys.Any(key => !identities.Contains(key))) throw SunderRpcPublicationImport.Invalid("Registry omitted a public bundle.");
        return new(registryOrigin, id, version, expected.GetProperty("packageContentIdentity").GetString()!,
            Text(value, "publishedAtUtc") ?? throw SunderRpcPublicationImport.Invalid("Registry publication time is missing."), owned.OrderBy(item => item.ContractId, StringComparer.Ordinal).ToArray());
    }
}
