using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Rpc.Authoring;

// Build and explicit readiness checks share the same verified, read-only readers.
// Missing-input diagnostics describe the whole selection, not only the first hash
// encountered by one of those readers. Restore alone publishes selected bytes.
internal static class SunderRpcContractCacheSelection
{
    internal static (SunderRpcResolvedContractCache Cache,
        IReadOnlyDictionary<string, SunderVerifiedPlatformContractCatalog> PlatformCatalogs) Resolve(
        SunderRpcContractLock contractLock, string cacheRoot, CancellationToken token = default)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var catalogs = SunderRpcPlatformCatalogCache.Resolve(contractLock, cacheRoot, token);
            return (SunderRpcContractCache.Resolve(contractLock, cacheRoot, token), catalogs);
        }
        catch (InvalidDataException error) when (error.Message.StartsWith("contract-cache.entry-missing:", StringComparison.Ordinal))
        {
            throw DescribeMissingSelection(contractLock, cacheRoot, error, token);
        }
    }

    private static InvalidDataException DescribeMissingSelection(
        SunderRpcContractLock contractLock, string cacheRoot, InvalidDataException original, CancellationToken token)
    {
        var root = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(cacheRoot);
        var missing = new List<string>();
        var exists = GeneratedOutputPathSafety.PathExists(root);
        using var lease = exists ? GeneratedOutputLock.Acquire([root], TimeSpan.FromSeconds(30), token) : null;
        if (exists) SunderRpcContractCacheStore.ValidateRoot(root, requireComplete: true);

        foreach (var requirement in SunderRpcContractCache.GetVerificationRequirements(contractLock).Values
                     .OrderBy(value => value.ContractId, StringComparer.Ordinal).ThenBy(value => value.ContractVersion, StringComparer.Ordinal))
        {
            var subject = $"contract '{requirement.ContractId}' version '{requirement.ContractVersion}'";
            Add(SunderRpcContractCache.GetDescriptorPath(root, requirement.DescriptorSha256), $"descriptor for {subject}");
            Add(SunderRpcContractCache.GetReceiptPath(root, requirement.DescriptorSha256), $"descriptor receipt for {subject}");
        }
        foreach (var artifact in contractLock.PackageArtifacts.Values.OrderBy(value => value.PackageId, StringComparer.Ordinal))
        {
            var path = SunderRpcContractCache.GetModelProjectionPath(root, artifact.ModelProjectionSha256);
            Add(path, $"model projection for package '{artifact.PackageId}'");
            Add(Path.ChangeExtension(path, "receipt.json"), $"model projection receipt for package '{artifact.PackageId}'");
        }
        foreach (var identity in SunderRpcPlatformCatalogCache.Requirements(contractLock)
                     .Select(value => value.ContentIdentity).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var path = SunderRpcPlatformCatalogCache.CatalogPath(root, identity);
            Add(path, "selected platform catalog");
            Add(Path.ChangeExtension(path, "receipt.json"), "selected platform catalog receipt");
        }

        var details = missing.Count == 0
            ? "The selection changed while checking it; retry the read-only check."
            : "Missing selected inputs:" + Environment.NewLine + string.Join(Environment.NewLine, missing);
        return new InvalidDataException($"contract-cache.entry-missing: Contract cache '{root}' is not ready. {details}"
            + Environment.NewLine + "Run 'dotnet restore <canonical-project>' with the Sunder.Package.Build SDK (or source targets) against this same SunderContractCacheRoot. "
            + "For local selections, supply the exact locked SunderPackageCandidatePath and SunderPlatformCatalogPath; no Registry is inferred. "
            + "Build with no-restore, compilation, Pack and design-time builds do not acquire missing inputs. RestoreSunderContracts remains available for explicit recovery.", original);

        void Add(string path, string description)
        {
            if (!exists || !GeneratedOutputPathSafety.PathExists(path)) missing.Add($"- {description}: '{path}'");
        }
    }
}
