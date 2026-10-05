using System.Net;
using System.Text.Json;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed partial class SunderRpcContractRestore(HttpClient http, bool allowDevelopmentRegistryOrigin = false)
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    internal bool AllowDevelopmentRegistryOrigin { get; } = allowDevelopmentRegistryOrigin;

    internal async Task<int> RestoreAsync(string lockPath, string cacheRoot, IReadOnlyList<string> candidates, CancellationToken token = default,
        string? platformCatalog = null)
    {
        var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(lockPath, AllowDevelopmentRegistryOrigin);
        if (platformCatalog is not null && string.IsNullOrWhiteSpace(platformCatalog))
            throw Error("platform-api.catalog-invalid", "An offline catalog input must be nonempty; no network fallback is permitted.");
        if (candidates.Count > 128) throw Error("package-api.public-inventory-invalid", "Candidate input count exceeds the restore limit.");
        var required = SunderRpcContractCache.GetRequirements(contractLock);
        var restored = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var platformRequirements = SunderRpcPlatformCatalogCache.Requirements(contractLock);
        if (platformCatalog is not null && platformRequirements.Count == 0)
            throw Error("platform-api.import-not-found", "Offline catalog restore requires existing platform constraints.");
        var catalogs = await RestorePlatformCatalogsAsync(contractLock, cacheRoot, platformRequirements,
            platformCatalog, candidates.Count != 0, token);
        foreach (var catalog in catalogs.Values)
            foreach (var descriptor in catalog.Contracts)
                if (required.TryGetValue(descriptor.Sha256, out var requested))
                {
                    var bytes = descriptor.GetCanonicalUtf8();
                    if (bytes.Length != requested.DescriptorBytes || descriptor.ContractId != requested.ContractId
                        || descriptor.Version != requested.ContractVersion || descriptor.AbiSha256 != requested.AbiSha256)
                        throw Error("rpc-authoring.authority-mismatch", "Registry catalog differs from the descriptor requirement.");
                    restored.Add(descriptor.Sha256, bytes);
                }
        if (candidates.Count > 0)
        {
            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                var extracted = Path.Combine(Path.GetTempPath(), "sunder-contract-restore-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(candidate, extracted, token);
                    if (!validation.Success || validation.Manifest is not { } manifest)
                        throw Error("package-api.public-inventory-invalid", "The candidate is not a complete canonical package archive.");
                    if (!selected.Add(manifest.Id!) || !contractLock.PackageArtifacts.TryGetValue(manifest.Id!, out var artifact))
                        throw Error("package-api.import-not-found", "Each candidate must identify one distinct existing locked package artifact.");
                    var inventory = CandidateInventory(extracted, validation.Manifest);
                    RequireInventory(artifact, inventory);
                    foreach (var contract in artifact.Contracts.Values.Where(contract => required.ContainsKey(contract.DescriptorSha256)))
                    {
                        var bundle = manifest.ContractBundles!.Single(bundle => bundle!.ContractId == contract.ContractId && bundle.Version == contract.ResolvedContractVersion)!;
                        var path = ArchiveRelativePath.Parse(SunderPackageFormat.SharedPayloadRoot + bundle.DescriptorPath).ToPlatformPath(extracted);
                        var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, MaximumResponseBytes, "restored candidate descriptor");
                        AddDescriptor(restored, contract, SunderRpcContractDescriptor.Parse(bytes).GetCanonicalUtf8());
                    }
                }
                finally { if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true); }
            }
        }
        else if (platformCatalog is null)
        {
            foreach (var artifact in contractLock.PackageArtifacts.Values.OrderBy(artifact => artifact.PackageId, StringComparer.Ordinal))
            {
                if (artifact.RegistryOrigin is null)
                {
                    // Local selections restore from explicit artifacts or already
                    // verified immutable cache entries, never the default Registry.
                    continue;
                }
                var origin = new Uri(artifact.RegistryOrigin.TrimEnd('/') + "/");
                var inventory = await ReadInventoryAsync(origin, artifact.PackageId, artifact.SelectedPackageVersion, token);
                RequireInventory(artifact, inventory);
                foreach (var contract in artifact.Contracts.Values.Where(contract => contract.AuthorityKind == "package" && required.ContainsKey(contract.DescriptorSha256)))
                {
                    var route = $"api/v1/contracts/{Segment(contract.ContractId)}/versions/{Segment(contract.ResolvedContractVersion)}";
                    var metadata = await ReadAsync(new(origin, route), null, 64 * 1024, token);
                    using var descriptorMetadata = SunderRpcAuthoringJson.ParseRegistryMetadata(metadata, 64 * 1024);
                    var record = descriptorMetadata.RootElement;
                    if (Text(record, "contractId") != contract.ContractId || Text(record, "version") != contract.ResolvedContractVersion
                        || Text(record, "descriptorSha256") != contract.DescriptorSha256 || Text(record, "abiSha256") != contract.AbiSha256
                        || Text(record, "exposure") != "public" || Text(record, "firstPublisherPackageId") != contract.AuthorityId
                        || !record.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number
                        || !size.TryGetInt64(out var length) || length != contract.DescriptorBytes || Text(record, "etag") != contract.Etag)
                        throw Error("package-api.public-inventory-invalid", "Registry descriptor metadata does not reproduce locked public authority.");
                    AddDescriptor(restored, contract, await ReadAsync(new(origin, route + "/descriptor"), contract.Etag, contract.DescriptorBytes, token, "application/schema+json"));
                }
            }
        }
        // Offline inputs may cover only part of a selection. Require verified
        // cached evidence for the rest before publishing any newly acquired bytes.
        try
        {
            _ = SunderRpcContractCache.Resolve(required.Where(pair => !restored.ContainsKey(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), cacheRoot);
        }
        catch (InvalidDataException error) when (error.Message.StartsWith("contract-cache.entry-missing:", StringComparison.Ordinal))
        {
            throw new InvalidDataException("contract-cache.entry-missing: Supply the exact local package candidates and platform catalog, or restore the remaining descriptor cache evidence; no Registry is inferred.", error);
        }
        var unchanged = SunderRpcAuthoringDocuments.ReadContractLock(lockPath, AllowDevelopmentRegistryOrigin);
        if (!unchanged.ExactBytes.AsSpan().SequenceEqual(contractLock.ExactBytes))
            throw Error("rpc-authoring.evidence-mismatch", "The contract lock changed during restore.");
        // Descriptor pairs and catalogs are independently immutable. If a
        // process stops between publications, generation fails until exact restore
        // supplies the missing catalog; descriptor integrity never creates a Registry selection.
        if (restored.Count != 0) SunderRpcContractCacheStore.Publish(cacheRoot, restored, required, token);
        if (catalogs.Count != 0) SunderRpcPlatformCatalogCache.Publish(cacheRoot,
            catalogs.ToDictionary(pair => pair.Key, pair => pair.Value.GetCanonicalUtf8(), StringComparer.Ordinal), platformRequirements, token);
        return restored.Count;
    }

    private async Task<byte[]> ReadAsync(Uri uri, string? etag, int maximumBytes, CancellationToken token, string mediaType = "application/json",
        Action<HttpResponseMessage>? validateHeaders = null, string? notFoundCode = null)
    {
        try { SunderRpcAuthoringDocuments.RequireCanonicalRegistryOrigin(uri.GetLeftPart(UriPartial.Authority) + "/", "restore", AllowDevelopmentRegistryOrigin); }
        catch (InvalidDataException) { throw Error("package-api.registry-origin-invalid", "Restore requires canonical HTTPS, or explicit Development mode for http://localhost/."); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotFound && notFoundCode is not null)
            throw Error(notFoundCode, "The selected Registry package was not found.");
        if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri != uri
            || response.Content.Headers.ContentType?.MediaType != mediaType
            || response.Content.Headers.ContentLength > maximumBytes || etag is not null && response.Headers.ETag?.ToString() != etag)
            throw Error("package-api.public-inventory-invalid", "Registry restore response status, origin, media type, length or immutable ETag is invalid.");
        validateHeaders?.Invoke(response);
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var output = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maximumBytes + 1L - output.Length)), deadline.Token);
            if (count == 0) break;
            output.Write(chunk, 0, count);
            if (output.Length > maximumBytes) throw Error("contract-cache.entry-corrupt", "Restored response exceeds its byte limit.");
        }
        if (response.Content.Headers.ContentLength is { } expectedLength && output.Length != expectedLength)
            throw Error("contract-cache.entry-corrupt", "Restored response differs from its declared byte length.");
        return output.ToArray();
    }

    private static void RequireInventory(SunderRpcLockedPackageArtifact artifact, SunderPackagePublicApiInventory observed)
    {
        var expected = SunderRpcAuthoringDocuments.ValidatePackagePublicInventory(artifact);
        if (!observed.ToCanonicalUtf8().AsSpan().SequenceEqual(expected.ToCanonicalUtf8()))
            throw Error("package-api.public-inventory-invalid", "Restore input does not exactly reproduce the committed complete artifact inventory.");
    }

    private static void AddDescriptor(IDictionary<string, byte[]> restored, SunderRpcLockedArtifactContract contract, byte[] bytes)
    {
        var descriptor = SunderRpcContractDescriptor.Parse(bytes);
        if (bytes.Length != contract.DescriptorBytes || descriptor.ContractId != contract.ContractId || descriptor.Version != contract.ResolvedContractVersion
            || descriptor.Sha256 != contract.DescriptorSha256 || descriptor.AbiSha256 != contract.AbiSha256 || !bytes.AsSpan().SequenceEqual(descriptor.GetCanonicalUtf8()))
            throw Error("contract-cache.entry-corrupt", "Restored descriptor bytes do not reproduce the locked canonical RPC identity.");
        if (restored.TryGetValue(contract.DescriptorSha256, out var previous) && !previous.AsSpan().SequenceEqual(bytes))
            throw Error("contract-cache.entry-corrupt", "Restored sources disagree on one immutable descriptor.");
        restored[contract.DescriptorSha256] = bytes;
    }

    private static SunderPackagePublicApiInventory CandidateInventory(string root, SunderPackageManifest manifest)
        => SunderPackagePublicApiInspection.ReadValidated(root, manifest).Inventory;

    private static string Segment(string value) => Uri.EscapeDataString(value);
    private static string? Text(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static InvalidDataException Error(string code, string message) => new(code + ": " + message);
}
