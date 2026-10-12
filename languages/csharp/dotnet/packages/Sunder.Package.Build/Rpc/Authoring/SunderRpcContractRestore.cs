using System.Net;
using System.Text.Json;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed partial class SunderRpcContractRestore(HttpClient http, bool allowDevelopmentRegistryOrigin = false)
{
    internal bool AllowDevelopmentRegistryOrigin { get; } = allowDevelopmentRegistryOrigin;

    internal async Task<int> RestoreAsync(string lockPath, string cacheRoot, IReadOnlyList<string> candidates, CancellationToken token = default,
        string? platformCatalog = null)
    {
        var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(lockPath, AllowDevelopmentRegistryOrigin);
        if (platformCatalog is not null && string.IsNullOrWhiteSpace(platformCatalog))
            throw Error("platform-api.catalog-invalid", "An offline catalog input must be nonempty; no network fallback is permitted.");
        if (candidates.Count > 128) throw Error("package-api.public-inventory-invalid", "Candidate input count exceeds the restore limit.");
        var required = SunderRpcContractCache.GetVerificationRequirements(contractLock);
        var restored = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var projections = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var candidateSnapshots = new Dictionary<string, SunderPackagePublicApiSnapshot>(StringComparer.Ordinal);
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
                var snapshot = await ReadCandidateAsync(candidate, token);
                var id = snapshot.Inventory.Payload.GetProperty("packageId").GetString()!;
                if (!selected.Add(id) || !contractLock.PackageArtifacts.TryGetValue(id, out var artifact))
                    throw Error("package-api.import-not-found", "Each candidate must identify one distinct existing locked package artifact.");
                Collect(artifact, snapshot);
                candidateSnapshots.Add(candidate, snapshot);
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
                // A cache hit is usable only with every selected pin and descriptor.
                try
                {
                    var entries = SunderRpcContractCache.Resolve(artifact.Contracts.Values.ToDictionary(contract => contract.DescriptorSha256,
                        contract => required[contract.DescriptorSha256], StringComparer.Ordinal), cacheRoot);
                    _ = SunderRpcContractCache.ResolveModelProjections([artifact], cacheRoot, entries);
                    continue;
                }
                catch (InvalidDataException error) when (error.Message.StartsWith("contract-cache.entry-missing:", StringComparison.Ordinal)
                    || error.Message.StartsWith("contract-cache.entry-corrupt:", StringComparison.Ordinal)) { }
                var origin = new Uri(artifact.RegistryOrigin.TrimEnd('/') + "/");
                Collect(artifact, await ReadCanonicalCandidateAsync(origin, artifact.PackageId, artifact.SelectedPackageVersion, token));
            }
        }
        // Offline inputs may cover only part of a selection. Require verified
        // cached evidence for the rest before publishing any newly acquired bytes.
        SunderRpcResolvedContractCache cached;
        try
        {
            cached = SunderRpcContractCache.Resolve(required.Where(pair => !restored.ContainsKey(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), cacheRoot);
        }
        catch (InvalidDataException error) when (error.Message.StartsWith("contract-cache.entry-missing:", StringComparison.Ordinal))
        {
            throw new InvalidDataException("contract-cache.entry-missing: Supply the exact local package candidates and platform catalog, or restore the remaining descriptor cache evidence; no Registry is inferred.", error);
        }
        var complete = new SunderRpcResolvedContractCache(cached.EntriesByDescriptorSha256.Concat(restored.Select(pair =>
            new KeyValuePair<string, SunderRpcResolvedContractCacheEntry>(pair.Key, new(required[pair.Key], SunderRpcContractDescriptor.Parse(pair.Value), pair.Value))))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        var cachedProjections = SunderRpcContractCache.ResolveProjectionContracts(contractLock.PackageArtifacts.Values
            .Where(artifact => !projections.ContainsKey(artifact.PackageId)).ToArray(), cacheRoot, complete);
        var allProjections = new Dictionary<string, SunderRpcModelProjectionContract>(cachedProjections, StringComparer.Ordinal);
        foreach (var (id, bytes) in projections)
            foreach (var entry in SunderRpcContractCache.VerifyModelProjection(contractLock.PackageArtifacts[id], bytes,
                contractLock.PackageArtifacts[id].Contracts.Values.ToDictionary(contract => contract.ContractId,
                    contract => complete.EntriesByDescriptorSha256[contract.DescriptorSha256].Descriptor, StringComparer.Ordinal)).Contracts)
            {
                if (allProjections.TryGetValue(entry.ContractId, out var previous)
                    && !SunderRpcAuthoringDocuments.SameProjectionContract(previous, entry))
                    throw Error("rpc-authoring.evidence-mismatch", "Selected reexport model projections differ from their reviewed origin.");
                allProjections[entry.ContractId] = entry;
            }
        foreach (var (candidate, snapshot) in candidateSnapshots)
        {
            var current = await ReadCandidateAsync(candidate, token);
            if (current.Inventory.PublicInventorySha256 != snapshot.Inventory.PublicInventorySha256
                || !current.ModelProjectionUtf8.AsSpan().SequenceEqual(snapshot.ModelProjectionUtf8))
                throw Error("rpc-authoring.evidence-mismatch", "A candidate changed during restore.");
        }
        var unchanged = SunderRpcAuthoringDocuments.ReadContractLock(lockPath, AllowDevelopmentRegistryOrigin);
        if (!unchanged.ExactBytes.AsSpan().SequenceEqual(contractLock.ExactBytes))
            throw Error("rpc-authoring.evidence-mismatch", "The contract lock changed during restore.");
        // Descriptor pairs and catalogs are independently immutable. If a
        // process stops between publications, generation fails until exact restore
        // supplies the missing catalog; descriptor integrity never creates a Registry selection.
        foreach (var batch in restored.Chunk(128))
            SunderRpcContractCacheStore.Publish(cacheRoot, batch.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), required, token);
        if (projections.Count != 0) SunderRpcContractCacheStore.PublishModelProjections(cacheRoot, projections, contractLock.PackageArtifacts.Values.ToArray(), complete, token);
        if (catalogs.Count != 0) SunderRpcPlatformCatalogCache.Publish(cacheRoot,
            catalogs.ToDictionary(pair => pair.Key, pair => pair.Value.GetCanonicalUtf8(), StringComparer.Ordinal), platformRequirements, token);
        return restored.Count;

        void Collect(SunderRpcLockedPackageArtifact artifact, SunderPackagePublicApiSnapshot snapshot)
        {
            RequireInventory(artifact, snapshot.Inventory);
            _ = SunderRpcContractCache.VerifyModelProjection(artifact, snapshot.ModelProjectionUtf8,
                snapshot.Descriptors.Values.Select(bytes => SunderRpcContractDescriptor.Parse(bytes)).ToDictionary(descriptor => descriptor.ContractId, StringComparer.Ordinal));
            foreach (var contract in artifact.Contracts.Values) AddDescriptor(restored, contract, snapshot.Descriptors[contract.DescriptorSha256]);
            projections.Add(artifact.PackageId, snapshot.ModelProjectionUtf8);
        }
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

    private static string Segment(string value) => Uri.EscapeDataString(value);
    private static string? Text(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static InvalidDataException Error(string code, string message) => new(code + ": " + message);
}
