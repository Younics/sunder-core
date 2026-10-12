using Sunder.Package.Format;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed partial class SunderRpcContractRestore
{
    private readonly Dictionary<(string Origin, string Id, string Version), SunderPackagePublicApiSnapshot> _canonicalCandidates = [];

    private static async Task<SunderPackagePublicApiSnapshot> ReadCandidateAsync(string path, CancellationToken token)
    {
        if (!string.Equals(Path.GetExtension(path), SunderPackageFormat.Extension, StringComparison.OrdinalIgnoreCase))
            throw Error("package-api.public-inventory-invalid", "Authoring requires an explicit canonical .sunderpkg candidate.");
        var extracted = Path.Combine(Path.GetTempPath(), "sunder-contract-candidate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(path, extracted, token);
            if (!validation.Success || validation.Manifest is not { } manifest)
                throw Error("package-api.public-inventory-invalid", "Candidate must be a complete validated canonical package archive. " + string.Join(" ", validation.Errors));
            return SunderPackagePublicApiInspection.ReadValidated(extracted, manifest, includeDescriptors: true);
        }
        finally { if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true); }
    }

    private async Task<SunderPackagePublicApiSnapshot> ReadCanonicalCandidateAsync(Uri origin, string packageId, string version, CancellationToken token)
    {
        var key = (origin.AbsoluteUri, packageId, version);
        if (_canonicalCandidates.TryGetValue(key, out var cached)) return cached;
        var bytes = await ReadAsync(new(origin, $"api/v1/packages/{Segment(packageId)}/versions/{Segment(version)}/canonical"),
            null, checked((int)SunderPackageFormat.MaxArchiveBytes), token, "application/octet-stream");
        var directory = Directory.CreateTempSubdirectory("sunder-authoring-canonical-").FullName;
        try
        {
            var path = Path.Combine(directory, "candidate.sunderpkg");
            await File.WriteAllBytesAsync(path, bytes, token);
            var snapshot = await ReadCandidateAsync(path, token);
            var identity = snapshot.Inventory.Payload;
            if (identity.GetProperty("packageId").GetString() != packageId || identity.GetProperty("packageVersion").GetString() != version)
                throw Error("package-api.public-inventory-invalid", "Canonical candidate differs from the pinned Registry package/version.");
            _canonicalCandidates.Add(key, snapshot);
            return snapshot;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal async Task VerifyCandidateSelectionAsync(string candidate, SunderRpcContractLock selection, CancellationToken token)
    {
        var snapshot = await ReadCandidateAsync(candidate, token);
        var id = snapshot.Inventory.Payload.GetProperty("packageId").GetString()!;
        if (!selection.PackageArtifacts.TryGetValue(id, out var artifact))
            throw Error("package-api.import-not-found", "Candidate does not identify a selected artifact.");
        RequireInventory(artifact, snapshot.Inventory);
        _ = SunderRpcContractCache.VerifyModelProjection(artifact, snapshot.ModelProjectionUtf8,
            snapshot.Descriptors.Values.Select(bytes => Sunder.Sdk.Rpc.SunderRpcContractDescriptor.Parse(bytes))
                .ToDictionary(descriptor => descriptor.ContractId, StringComparer.Ordinal));
    }
}
