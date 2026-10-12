using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring;

internal static partial class SunderRpcContractCache
{
    internal const string ModelProjectionDirectoryName = "model-projections";
    internal const string ModelProjectionCacheDirectory = "model-projections/sha256";
    internal const int MaximumModelProjectionBytes = SunderPackageFormat.MaxRpcModelProjectionBytes;

    internal static string GetModelProjectionPath(string root, string modelProjectionSha256)
    {
        RequireSha256PathSegment(modelProjectionSha256);
        return Path.Combine(root, ModelProjectionDirectoryName, "sha256", modelProjectionSha256 + ".json");
    }

    internal static byte[] ModelProjectionReceiptBytes(string hash, int bytes)
        => SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1, modelProjectionSha256 = hash, modelProjectionBytes = bytes,
        }));

    internal static byte[] ReadOwnedModelProjectionPair(string directory, string hash)
    {
        RequireSha256PathSegment(hash);
        var path = Path.Combine(directory, hash + ".json");
        var receipt = Path.Combine(directory, hash + ReceiptSuffix);
        if (!GeneratedOutputPathSafety.PathExists(path) || !GeneratedOutputPathSafety.PathExists(receipt))
            throw CacheError("contract-cache.entry-missing", "The selected model projection and ownership receipt are required; run explicit contracts restore.");
        var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, MaximumModelProjectionBytes, "cached model projection");
        _ = SunderRpcModelProjectionDocument.Parse(bytes);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash
            || !SunderRpcDescriptorFileReader.ReadStableBytes(receipt, 16_384, "model projection ownership receipt").AsSpan()
                .SequenceEqual(ModelProjectionReceiptBytes(hash, bytes.Length)))
            throw CacheError("contract-cache.entry-corrupt", "Model projection ownership evidence differs from its content address.");
        return bytes;
    }

    internal static SunderRpcModelProjectionDocument VerifyModelProjection(SunderRpcLockedPackageArtifact artifact, byte[] bytes,
        IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptors)
    {
        if (artifact.ModelProjectionBytes is < 1 or > MaximumModelProjectionBytes
            || bytes.Length != artifact.ModelProjectionBytes
            || Convert.ToHexStringLower(SHA256.HashData(bytes)) != artifact.ModelProjectionSha256)
            throw CacheError("contract-cache.entry-corrupt", "Model projection bytes do not reproduce the selected artifact pins.");
        var document = SunderRpcModelProjectionDocument.Parse(bytes);
        var manifest = new SunderPackageManifest
        {
            Id = artifact.PackageId, Version = artifact.SelectedPackageVersion,
            ContractBundles = artifact.Contracts.Values.Select(contract => new SunderPackageContractBundleManifest
            {
                ContractId = contract.ContractId, Version = contract.ResolvedContractVersion,
                DescriptorSha256 = contract.DescriptorSha256, AbiSha256 = contract.AbiSha256, Exposure = "public",
                Source = contract.ArtifactSource, AuthorityKind = contract.AuthorityKind, AuthorityId = contract.AuthorityId,
                SelectedAuthorityRelease = contract.SelectedAuthorityRelease, SelectedAuthorityContentIdentity = contract.SelectedAuthorityContentIdentity,
            }).ToArray(),
        };
        document.Validate(manifest, descriptors);
        return document;
    }

    internal static IReadOnlyDictionary<string, SunderRpcModelProjection> ResolveModelProjections(
        IReadOnlyList<SunderRpcLockedPackageArtifact> artifacts, string cacheRoot, SunderRpcResolvedContractCache cache)
        => new ReadOnlyDictionary<string, SunderRpcModelProjection>(ResolveProjectionContracts(artifacts, cacheRoot, cache)
            .ToDictionary(pair => pair.Key, pair => pair.Value.ModelProjection, StringComparer.Ordinal));

    internal static IReadOnlyDictionary<string, SunderRpcModelProjectionContract> ResolveProjectionContracts(
        IReadOnlyList<SunderRpcLockedPackageArtifact> artifacts, string cacheRoot, SunderRpcResolvedContractCache cache)
    {
        var result = new Dictionary<string, SunderRpcModelProjectionContract>(StringComparer.Ordinal);
        if (artifacts.Count == 0) return new ReadOnlyDictionary<string, SunderRpcModelProjectionContract>(result);
        var root = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(cacheRoot);
        if (!GeneratedOutputPathSafety.PathExists(root)) throw CacheError("contract-cache.entry-missing", "The selected model projection cache is absent; run explicit contracts restore.");
        using var lease = GeneratedOutputLock.Acquire([root], TimeSpan.FromSeconds(30));
        SunderRpcContractCacheStore.ValidateRoot(root, requireComplete: true);
        foreach (var artifact in artifacts.OrderBy(value => value.PackageId, StringComparer.Ordinal))
        {
            var path = GetModelProjectionPath(root, artifact.ModelProjectionSha256);
            if (!GeneratedOutputPathSafety.PathExists(path)) throw CacheError("contract-cache.entry-missing", $"The pinned model projection for '{artifact.PackageId}' is absent; supply its exact candidate or explicit online restore.");
            var bytes = ReadOwnedModelProjectionPair(Path.GetDirectoryName(path)!, artifact.ModelProjectionSha256);
            var descriptors = artifact.Contracts.Values.ToDictionary(contract => contract.ContractId,
                contract => cache.EntriesByDescriptorSha256[contract.DescriptorSha256].Descriptor, StringComparer.Ordinal);
            var document = VerifyModelProjection(artifact, bytes, descriptors);
            foreach (var contract in document.Contracts)
            {
                if (result.TryGetValue(contract.ContractId, out var previous)
                    && !SunderRpcAuthoringDocuments.SameProjectionContract(previous, contract))
                    throw CacheError("rpc-authoring.evidence-mismatch", $"Reexport model projection for '{contract.ContractId}' differs from its reviewed origin.");
                result[contract.ContractId] = contract;
            }
        }
        return new ReadOnlyDictionary<string, SunderRpcModelProjectionContract>(result);
    }
}

internal static partial class SunderRpcContractCacheStore
{
    internal static void PublishModelProjections(string cacheRoot, IReadOnlyDictionary<string, byte[]> entries,
        IReadOnlyList<SunderRpcLockedPackageArtifact> artifacts, SunderRpcResolvedContractCache descriptors, CancellationToken token)
    {
        var selected = entries.Select(pair =>
        {
            var artifact = artifacts.Single(value => value.PackageId == pair.Key);
            var bytes = pair.Value.ToArray();
            _ = SunderRpcContractCache.VerifyModelProjection(artifact, bytes, artifact.Contracts.Values.ToDictionary(value => value.ContractId,
                value => descriptors.EntriesByDescriptorSha256[value.DescriptorSha256].Descriptor, StringComparer.Ordinal));
            return (Artifact: artifact, Bytes: bytes);
        }).ToArray();
        if (selected.Length > 128) throw Corrupt("Model projection publication exceeds its artifact bound.");
        var root = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(cacheRoot);
        using var lease = GeneratedOutputLock.Acquire([root], TimeSpan.FromSeconds(30), token);
        InitializeRoot(root);
        ValidateRoot(root, requireComplete: true);
        foreach (var (artifact, bytes) in selected)
        {
            token.ThrowIfCancellationRequested();
            var path = SunderRpcContractCache.GetModelProjectionPath(root, artifact.ModelProjectionSha256);
            GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "model projection cache publication");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var hash = artifact.ModelProjectionSha256;
            foreach (var (target, value) in new[] { (path, bytes), (Path.Combine(Path.GetDirectoryName(path)!, hash + SunderRpcContractCache.ReceiptSuffix),
                         SunderRpcContractCache.ModelProjectionReceiptBytes(hash, bytes.Length)) })
            {
                if (GeneratedOutputPathSafety.PathExists(target))
                {
                    if (SunderRpcDescriptorFileReader.ReadStableBytes(target, MaximumDescriptorBytes, "existing model projection").AsSpan().SequenceEqual(value)) continue;
                    throw Corrupt("Existing model projection content-addressed bytes are corrupt; their evidence was preserved.");
                }
                var stage = Path.Combine(Path.GetDirectoryName(path)!, ".write-" + Guid.NewGuid().ToString("D"));
                try
                {
                    WriteNew(stage, value);
                    token.ThrowIfCancellationRequested();
                    GeneratedOutputPathSafety.EnsureSafeAncestors(target, includeLeaf: true, "model projection cache installation");
                    File.Move(stage, target);
                }
                finally { if (File.Exists(stage)) File.Delete(stage); }
            }
            _ = SunderRpcContractCache.ReadOwnedModelProjectionPair(Path.GetDirectoryName(path)!, hash);
        }
    }
}
