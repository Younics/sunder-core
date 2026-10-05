using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Format;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed partial class SunderRpcContractRestore
{
    internal async Task<JsonObject> SelectArtifactAsync(
        string packageId, string range, bool includePrerelease, string? registryOrigin, string? candidate, CancellationToken token)
    {
        if (!PackageId.TryParse(packageId, out _) || !PackageVersionRange.TryParse(range, out var parsedRange)
            || parsedRange.ToString() != range)
            throw Error("package-api.no-compatible-version", "Selection requires a canonical package ID and an explicit canonical version range.");
        if (registryOrigin is null && candidate is null)
            throw Error("package-api.local-input-required", "A local package selection requires an explicit candidate archive; no Registry is inferred.");
        try { if (registryOrigin is not null) SunderRpcAuthoringDocuments.RequireCanonicalRegistryOrigin(registryOrigin, "package selection", AllowDevelopmentRegistryOrigin); }
        catch (InvalidDataException) { throw Error("package-api.registry-origin-invalid", "New selection requires canonical HTTPS, or explicit Development mode for http://localhost/."); }

        SunderPackagePublicApiInventory inventory;
        if (candidate is not null)
        {
            var extracted = Path.Combine(Path.GetTempPath(), "sunder-contract-candidate-" + Guid.NewGuid().ToString("N"));
            try
            {
                var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(candidate, extracted, token);
                if (!validation.Success || validation.Manifest is not { } manifest || manifest.Id != packageId)
                    throw Error("package-api.public-inventory-invalid", "Candidate must be a complete canonical archive for the selected package.");
                inventory = CandidateInventory(extracted, manifest);
            }
            finally { if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true); }
        }
        else
        {
            var origin = new Uri(registryOrigin!);
            var version = await ResolvePackageVersionAsync(origin, packageId, parsedRange, includePrerelease, token);
            inventory = await ReadInventoryAsync(origin, packageId, version, token);
        }

        var value = inventory.Payload;
        var selectedVersion = value.GetProperty("packageVersion").GetString()!;
        if (!parsedRange.IsSatisfiedBy(SemanticVersion.Parse(selectedVersion))
            || !includePrerelease && SemanticVersion.Parse(selectedVersion).Prerelease is not null)
            throw Error("package-api.no-compatible-version", "The selected artifact does not satisfy the reviewed range and prerelease policy.");
        if (value.GetProperty("publicContractCount").GetInt32() == 0)
            throw Error("package-api.public-api-empty", "The selected artifact has no public RPC contracts.");
        var contracts = new JsonObject();
        foreach (var contract in value.GetProperty("contracts").EnumerateArray())
        {
            var record = new JsonObject();
            foreach (var field in new[] { "artifactSource", "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity" })
                record[field] = JsonNode.Parse(contract.GetProperty(field).GetRawText());
            record["resolvedContractVersion"] = contract.GetProperty("contractVersion").GetString();
            foreach (var field in new[] { "descriptorBytes", "descriptorSha256", "abiSha256" })
                record[field] = JsonNode.Parse(contract.GetProperty(field).GetRawText());
            record["etag"] = "\"" + contract.GetProperty("descriptorSha256").GetString() + "\"";
            contracts[contract.GetProperty("contractId").GetString()!] = record;
        }
        var selected = new JsonObject
        {
            ["requestedVersionRange"] = range, ["includePrerelease"] = includePrerelease,
            ["selectedPackageVersion"] = selectedVersion, ["selectedPackageContentIdentity"] = value.GetProperty("packageContentIdentity").GetString(),
            ["registryOrigin"] = registryOrigin, ["publicContractCount"] = contracts.Count,
            ["publicInventorySha256"] = inventory.PublicInventorySha256, ["contracts"] = contracts,
        };
        if (registryOrigin is null) selected.Remove("registryOrigin");
        return selected;
    }

    private async Task<SunderPackagePublicApiInventory> ReadInventoryAsync(Uri origin, string packageId, string version, CancellationToken token)
    {
        var bytes = await ReadAsync(new(origin, $"api/v1/packages/{Segment(packageId)}/versions/{Segment(version)}/public-api"), null, 1024 * 1024, token);
        using var envelope = SunderRpcAuthoringJson.Parse(bytes, 1024 * 1024);
        SunderRpcAuthoringJson.RequireObject(envelope.RootElement, "inventory", "publicInventorySha256");
        var hash = Text(envelope.RootElement, "publicInventorySha256")
            ?? throw Error("package-api.public-inventory-invalid", "The Registry inventory hash is missing.");
        var inventory = SunderPackagePublicApiInventory.ParseVerifyHash(Encoding.UTF8.GetBytes(envelope.RootElement.GetProperty("inventory").GetRawText()), hash);
        if (inventory.Payload.GetProperty("packageId").GetString() != packageId || inventory.Payload.GetProperty("packageVersion").GetString() != version)
            throw Error("package-api.public-inventory-invalid", "Registry inventory identity differs from the exact requested artifact.");
        return inventory;
    }

    private async Task<string> ResolvePackageVersionAsync(Uri origin, string packageId, PackageVersionRange range, bool includePrerelease, CancellationToken token)
    {
        const int pageSize = 100;
        const int maximumVersions = 4096;
        int? total = null;
        var versions = new HashSet<string>(StringComparer.Ordinal);
        SemanticVersion? best = null;
        for (var skip = 0; skip < maximumVersions; skip += pageSize)
        {
            var bytes = await ReadAsync(new(origin, $"api/v1/packages/{Segment(packageId)}/versions?skip={skip}&take={pageSize}"), null, 1024 * 1024, token,
                validateHeaders: response =>
                {
                    var count = HeaderInteger(response, "X-Total-Count");
                    if (count > maximumVersions || total is not null && total != count
                        || HeaderInteger(response, "X-Skip") != skip || HeaderInteger(response, "X-Take") != pageSize)
                        throw Error("package-api.public-inventory-invalid", "Registry version pagination changed or exceeded its bound.");
                    total = count;
                }, notFoundCode: "package-api.package-not-found");
            using var page = SunderRpcAuthoringJson.ParseRegistryMetadata(bytes, 1024 * 1024);
            if (page.RootElement.ValueKind != JsonValueKind.Array || page.RootElement.GetArrayLength() != Math.Min(pageSize, total!.Value - skip))
                throw Error("package-api.public-inventory-invalid", "Registry version page does not agree with its bounded pagination.");
            foreach (var item in page.RootElement.EnumerateArray())
            {
                var text = Text(item, "version");
                if (!SemanticVersion.TryParse(text, out var version) || !versions.Add(text!)
                    || !item.TryGetProperty("isYanked", out var yanked) || yanked.ValueKind is not (JsonValueKind.False or JsonValueKind.True))
                    throw Error("package-api.public-inventory-invalid", "Registry version history has invalid or duplicate identities.");
                if (yanked.GetBoolean() || !includePrerelease && version.Prerelease is not null || !range.IsSatisfiedBy(version)) continue;
                if (best is null || version.ComparePrecedenceTo(best.Value) > 0
                    || version.ComparePrecedenceTo(best.Value) == 0 && string.CompareOrdinal(version.ToString(), best.Value.ToString()) > 0)
                    best = version;
            }
            if (versions.Count == total) break;
        }
        return best?.ToString() ?? throw Error("package-api.no-compatible-version", "No non-yanked package version satisfies the reviewed range and prerelease policy.");
    }

    private static int HeaderInteger(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values)) throw Error("package-api.public-inventory-invalid", "Registry pagination headers are missing.");
        var array = values.ToArray();
        if (array.Length != 1 || !int.TryParse(array[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || array[0] != value.ToString(CultureInfo.InvariantCulture))
            throw Error("package-api.public-inventory-invalid", "Registry pagination headers are not canonical nonnegative integers.");
        return value;
    }
}
