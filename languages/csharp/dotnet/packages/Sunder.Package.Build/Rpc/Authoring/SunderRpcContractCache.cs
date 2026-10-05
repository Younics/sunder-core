using Sunder.Package.Build.Tasks;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed record SunderRpcContractCacheRequirement(
    string ContractId,
    string ContractVersion,
    int DescriptorBytes,
    string DescriptorSha256,
    string AbiSha256);

internal sealed record SunderRpcContractCacheEntryReceipt(
    string ContractId,
    string ContractVersion,
    int DescriptorBytes,
    string DescriptorSha256,
    string AbiSha256);

internal sealed record SunderRpcResolvedContractCacheEntry(
    SunderRpcContractCacheRequirement Requirement,
    SunderRpcContractDescriptor Descriptor,
    byte[] CanonicalUtf8);

internal sealed record SunderRpcResolvedContractCache(
    IReadOnlyDictionary<string, SunderRpcResolvedContractCacheEntry> EntriesByDescriptorSha256);

internal static class SunderRpcContractCache
{
    internal const string ReceiptSuffix = ".receipt.json";

    public static string DefaultRoot
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".sunder",
            "contracts",
            "v1",
            "sha256");

    public static SunderRpcResolvedContractCache Resolve(SunderRpcContractLock contractLock, string cacheRoot)
    {
        ArgumentNullException.ThrowIfNull(contractLock);
        return Resolve(GetRequirements(contractLock), cacheRoot);
    }

    internal static SunderRpcResolvedContractCache Resolve(IReadOnlyDictionary<string, SunderRpcContractCacheRequirement> requirements, string cacheRoot)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        if (requirements.Count == 0)
        {
            return new(new Dictionary<string, SunderRpcResolvedContractCacheEntry>(StringComparer.Ordinal));
        }

        string root;
        try
        {
            root = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(cacheRoot);
            if (!GeneratedOutputPathSafety.PathExists(root))
            {
                throw CacheError(
                    "contract-cache.entry-missing",
                    "The contract descriptor cache is absent. Run 'sunder-package contracts restore'.");
            }
            GeneratedOutputPathSafety.EnsureRegularDirectory(root, "Sunder contract descriptor cache");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            throw CacheError(
                "contract-cache.entry-corrupt",
                "The contract descriptor cache root is not a regular link-free directory.",
                exception);
        }

        // Copy complete verified values while holding the same cross-language lease
        // as cache publication. Compilation uses detached bytes after this lease ends.
        using var lease = GeneratedOutputLock.Acquire([root], TimeSpan.FromSeconds(30));
        SunderRpcContractCacheStore.ValidateRoot(root, requireComplete: true);
        var entries = new Dictionary<string, SunderRpcResolvedContractCacheEntry>(StringComparer.Ordinal);
        foreach (var requirement in requirements.Values.OrderBy(static item => item.DescriptorSha256, StringComparer.Ordinal))
        {
            var descriptorPath = GetDescriptorPath(root, requirement.DescriptorSha256);
            var receiptPath = GetReceiptPath(root, requirement.DescriptorSha256);
            bool descriptorExists;
            bool receiptExists;
            try
            {
                descriptorExists = GeneratedOutputPathSafety.PathExists(descriptorPath);
                receiptExists = GeneratedOutputPathSafety.PathExists(receiptPath);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or NotSupportedException)
            {
                throw CacheError(
                    "contract-cache.entry-corrupt",
                    $"Contract '{requirement.ContractId}' version '{requirement.ContractVersion}' has unreadable cache evidence.",
                    exception);
            }
            if (!descriptorExists || !receiptExists)
            {
                throw CacheError(
                    "contract-cache.entry-missing",
                    $"Contract '{requirement.ContractId}' version '{requirement.ContractVersion}' is missing from the descriptor cache. "
                    + "Run 'sunder-package contracts restore'.");
            }

            try
            {
                var snapshot = SunderRpcDescriptorFileReader.Read(
                    descriptorPath,
                    $"cached RPC descriptor {requirement.DescriptorSha256}",
                    requireCanonical: true);
                var receipt = SunderRpcAuthoringDocuments.ReadContractCacheEntryReceipt(receiptPath);
                ValidateEntry(requirement, snapshot, receipt);
                entries.Add(requirement.DescriptorSha256, new(requirement, snapshot.Descriptor, snapshot.CanonicalUtf8));
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or SunderRpcDescriptorException)
            {
                throw CacheError(
                    "contract-cache.entry-corrupt",
                    $"Contract '{requirement.ContractId}' version '{requirement.ContractVersion}' has corrupt cache evidence. "
                    + "Run 'sunder-package contracts restore'.",
                    exception);
            }
        }
        return new(entries);
    }

    public static string GetDescriptorPath(string cacheRoot, string descriptorSha256)
    {
        RequireSha256PathSegment(descriptorSha256);
        return Path.Combine(cacheRoot, descriptorSha256 + ".json");
    }

    public static string GetReceiptPath(string cacheRoot, string descriptorSha256)
    {
        RequireSha256PathSegment(descriptorSha256);
        return Path.Combine(cacheRoot, descriptorSha256 + ReceiptSuffix);
    }

    internal static IReadOnlyDictionary<string, SunderRpcContractCacheRequirement> GetRequirements(
        SunderRpcContractLock contractLock)
    {
        var requirements = new Dictionary<string, SunderRpcContractCacheRequirement>(StringComparer.Ordinal);
        foreach (var import in contractLock.PackageImports.Values)
        {
            var artifact = contractLock.PackageArtifacts[import.PackageId];
            foreach (var contract in artifact.Contracts.Values.Where(static contract =>
                         string.Equals(contract.AuthorityKind, "package", StringComparison.Ordinal)))
            {
                AddRequirement(requirements, From(contract));
            }
        }
        foreach (var import in contractLock.ProviderImports.Values)
        {
            AddRequirement(requirements, From(contractLock.PackageArtifacts[import.PackageId].Contracts[import.ContractId]));
        }
        foreach (var import in contractLock.PlatformImports.Values)
        {
            AddRequirement(requirements, From(import));
        }
        return requirements;
    }

    internal static void ValidateEntry(
        SunderRpcContractCacheRequirement requirement,
        SunderRpcDescriptorFileSnapshot snapshot,
        SunderRpcContractCacheEntryReceipt receipt)
    {
        if (snapshot.CanonicalUtf8.Length != requirement.DescriptorBytes
            || !string.Equals(snapshot.Descriptor.ContractId, requirement.ContractId, StringComparison.Ordinal)
            || !string.Equals(snapshot.Descriptor.Version, requirement.ContractVersion, StringComparison.Ordinal)
            || !string.Equals(snapshot.Descriptor.Sha256, requirement.DescriptorSha256, StringComparison.Ordinal)
            || !string.Equals(snapshot.Descriptor.AbiSha256, requirement.AbiSha256, StringComparison.Ordinal)
            || !string.Equals(receipt.ContractId, requirement.ContractId, StringComparison.Ordinal)
            || !string.Equals(receipt.ContractVersion, requirement.ContractVersion, StringComparison.Ordinal)
            || receipt.DescriptorBytes != requirement.DescriptorBytes
            || !string.Equals(receipt.DescriptorSha256, requirement.DescriptorSha256, StringComparison.Ordinal)
            || !string.Equals(receipt.AbiSha256, requirement.AbiSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The cached descriptor, receipt, and lock evidence do not agree.");
        }
    }

    private static void AddRequirement(
        IDictionary<string, SunderRpcContractCacheRequirement> requirements,
        SunderRpcContractCacheRequirement requirement)
    {
        if (requirements.TryGetValue(requirement.DescriptorSha256, out var existing) && existing != requirement)
        {
            throw new InvalidDataException(
                $"rpc-authoring.evidence-mismatch: Descriptor hash '{requirement.DescriptorSha256}' has conflicting lock evidence.");
        }
        requirements[requirement.DescriptorSha256] = requirement;
    }

    private static SunderRpcContractCacheRequirement From(SunderRpcLockedArtifactContract contract)
        => new(
            contract.ContractId,
            contract.ResolvedContractVersion,
            contract.DescriptorBytes,
            contract.DescriptorSha256,
            contract.AbiSha256);

    private static SunderRpcContractCacheRequirement From(SunderRpcLockedPlatformImport import)
        => new(
            import.ContractId,
            import.ResolvedContractVersion,
            import.DescriptorBytes,
            import.DescriptorSha256,
            import.AbiSha256);

    private static void RequireSha256PathSegment(string value)
    {
        if (value.Length != 64
            || value.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("Descriptor cache paths require lowercase SHA-256 text.", nameof(value));
        }
    }

    private static InvalidDataException CacheError(string diagnosticId, string message, Exception? inner = null)
        => new($"{diagnosticId}: {message}", inner);
}
