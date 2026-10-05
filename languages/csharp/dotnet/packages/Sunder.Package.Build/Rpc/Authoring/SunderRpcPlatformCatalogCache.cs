using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Packaging;
using System.Globalization;
using System.Numerics;
using System.Reflection;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed record SunderRpcPlatformRequirement(
    string? RegistryOrigin, string ReleaseVersion, string ContentIdentity, SunderRpcContractCacheRequirement Descriptor);

// Registry selections establish authority; every reader verifies immutable content
// against the lock. Cached bytes never choose a Registry or create a selection.
internal static partial class SunderRpcPlatformCatalogCache
{
    internal const string DirectoryName = "platform-catalogs";
    internal const int MaximumBytes = 67_108_864;
    internal static string ToolRange
    {
        get
        {
            var (lower, upper) = ToolBounds();
            return $">={lower} <{upper}";
        }
    }

    private static (string Lower, string Upper) ToolBounds()
    {
        // CLR assembly identity may stay stable across SDK releases; use the
        // semantic version of the actual SDK shipped with this tool instead.
        var version = typeof(PackageVersionRange).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!SemanticVersion.TryParse(version, out _))
            throw new InvalidDataException("platform-api.catalog-version-unsupported: The installed SDK has no valid coordinated release identity.");
        var parts = version!.Split('.');
        var minor = BigInteger.Parse(parts[1], CultureInfo.InvariantCulture);
        return ($"{parts[0]}.{parts[1]}.0", $"{parts[0]}.{(minor + 1).ToString(CultureInfo.InvariantCulture)}.0");
    }

    internal static string DefaultKey(string contractId)
    {
        var parts = contractId.Split('.');
        if (parts[0] == "sunder" && parts.Length > 1) parts = parts[1..];
        return parts[0] + string.Concat(parts.Skip(1).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    internal static void ValidateToolRange(SunderRpcContractLock contractLock)
    {
        var (lower, upper) = ToolBounds();
        foreach (var requirement in Requirements(contractLock))
            if (!PackageVersionRange.IsSatisfiedBy(requirement.ReleaseVersion, $">={lower} <{upper}"))
                throw new InvalidDataException("platform-api.catalog-version-unsupported: The platform catalog is outside the coordinated tooling minor line. Upgrade Core tooling.");
        foreach (var import in contractLock.PlatformImports.Values)
        {
            var boundedBelow = false; var boundedAbove = false;
            foreach (var token in import.AuthorityReleaseRange.Split(' '))
            {
                var value = token.TrimStart('<', '>', '=');
                _ = PackageVersionRange.TryCompare(value, lower, out var fromLower);
                _ = PackageVersionRange.TryCompare(value, upper, out var fromUpper);
                var exact = token[0] is not ('<' or '>');
                boundedBelow |= (exact || token[0] == '>') && fromLower >= 0;
                boundedAbove |= (exact || token[0] == '<') && (fromUpper < 0 || fromUpper == 0 && token.StartsWith('<') && !token.StartsWith("<=", StringComparison.Ordinal));
            }
            if (!boundedBelow || !boundedAbove)
                throw new InvalidDataException("rpc-authoring.authority-mismatch: Platform ranges must be contained by the coordinated tooling minor line.");
        }
    }

    internal static SunderRpcLockedArtifactContract AsArtifactContract(SunderRpcLockedPlatformImport value)
        => new(value.ContractId, "imported", "platform", value.AuthorityId, value.SelectedAuthorityRelease,
            value.SelectedAuthorityContentIdentity,
            value.ResolvedContractVersion, value.DescriptorBytes, value.DescriptorSha256, value.AbiSha256, value.Etag);

    internal static IReadOnlyList<SunderRpcPlatformRequirement> Requirements(SunderRpcContractLock value)
    {
        var result = new List<SunderRpcPlatformRequirement>();
        foreach (var artifact in value.PackageArtifacts.Values)
            foreach (var contract in artifact.Contracts.Values.Where(contract => contract.AuthorityKind == "platform"))
                result.Add(new(artifact.RegistryOrigin, contract.SelectedAuthorityRelease, contract.SelectedAuthorityContentIdentity,
                    new(contract.ContractId, contract.ResolvedContractVersion, contract.DescriptorBytes, contract.DescriptorSha256, contract.AbiSha256)));
        foreach (var contract in value.PlatformImports.Values)
            result.Add(new(contract.RegistryOrigin, contract.SelectedAuthorityRelease, contract.SelectedAuthorityContentIdentity,
                new(contract.ContractId, contract.ResolvedContractVersion, contract.DescriptorBytes, contract.DescriptorSha256, contract.AbiSha256)));
        return result.Distinct().OrderBy(item => item.ContentIdentity, StringComparer.Ordinal)
            .ThenBy(item => item.Descriptor.ContractId, StringComparer.Ordinal).ThenBy(item => item.RegistryOrigin, StringComparer.Ordinal).ToArray();
    }

    internal static SunderVerifiedPlatformContractCatalog Verify(byte[] bytes,
        IReadOnlyList<SunderRpcPlatformRequirement> requirements)
    {
        SunderVerifiedPlatformContractCatalog catalog;
        try { catalog = SunderPlatformContractCatalog.Verify(bytes); }
        catch (InvalidDataException error) { throw new InvalidDataException("platform-api.catalog-invalid: The Registry catalog failed canonical verification.", error); }
        foreach (var requirement in requirements)
        {
            Sunder.Sdk.Rpc.SunderRpcContractDescriptor descriptor;
            try { descriptor = catalog.VerifyPinned(requirement.ReleaseVersion, requirement.ContentIdentity, requirement.Descriptor.ContractId,
                requirement.Descriptor.ContractVersion, requirement.Descriptor.DescriptorSha256, requirement.Descriptor.AbiSha256); }
            catch (InvalidDataException error) { throw new InvalidDataException("rpc-authoring.authority-mismatch: Registry catalog differs from the exact lock pins.", error); }
            if (descriptor.GetCanonicalUtf8().Length != requirement.Descriptor.DescriptorBytes)
                throw new InvalidDataException("rpc-authoring.authority-mismatch: Catalog descriptor length differs from the lock.");
        }
        return catalog;
    }

    internal static IReadOnlyDictionary<string, SunderVerifiedPlatformContractCatalog> Resolve(
        SunderRpcContractLock contractLock, string cacheRoot)
    {
        var requirements = Requirements(contractLock);
        var result = new Dictionary<string, SunderVerifiedPlatformContractCatalog>(StringComparer.Ordinal);
        if (requirements.Count == 0) return result;
        var root = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(cacheRoot);
        if (!GeneratedOutputPathSafety.PathExists(root)) throw Missing();
        using var lease = GeneratedOutputLock.Acquire([root], TimeSpan.FromSeconds(30));
        SunderRpcContractCacheStore.ValidateRoot(root, requireComplete: true);
        foreach (var group in requirements.GroupBy(item => item.ContentIdentity, StringComparer.Ordinal))
        {
            var path = CatalogPath(root, group.Key);
            if (!GeneratedOutputPathSafety.PathExists(path)) throw Missing();
            var bytes = ReadOwnedPair(Path.GetDirectoryName(path)!, group.Key);
            result.Add(group.Key, Verify(bytes, group.ToArray()));
        }
        return result;
    }

    internal static void Publish(string cacheRoot, IReadOnlyDictionary<string, byte[]> envelopes,
        IReadOnlyList<SunderRpcPlatformRequirement> requirements,
        CancellationToken token = default, Action<string>? checkpoint = null)
    {
        if (envelopes.Count is < 1 or > 128) throw new InvalidDataException("platform-api.catalog-invalid: Catalog publication exceeds its bound.");
        // Detach and verify the whole set before writing any evidence.
        var selected = envelopes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            var bytes = pair.Value.ToArray();
            var pins = requirements.Where(item => item.ContentIdentity == pair.Key).ToArray();
            if (pins.Length == 0) throw new InvalidDataException("platform-api.catalog-invalid: Unrequested catalog publication.");
            _ = Verify(bytes, pins);
            return (Hash: pair.Key, Bytes: bytes);
        }).ToArray();
        var root = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(cacheRoot);
        using var lease = GeneratedOutputLock.Acquire([root], TimeSpan.FromSeconds(30), token);
        SunderRpcContractCacheStore.InitializeRoot(root);
        SunderRpcContractCacheStore.ValidateRoot(root, requireComplete: true);
        var directory = Path.Combine(root, DirectoryName);
        GeneratedOutputPathSafety.EnsureSafeAncestors(directory, includeLeaf: true, "catalog cache");
        Directory.CreateDirectory(directory);
        GeneratedOutputPathSafety.EnsureRegularDirectory(directory, "catalog cache");
        foreach (var item in selected)
        {
            PublishPair(directory, item.Hash, item.Bytes, token, checkpoint);
        }
    }

    internal static string CatalogPath(string root, string hash)
    {
        _ = SunderRpcContractCache.GetDescriptorPath(root, hash); // identical lowercase content-address grammar
        return Path.Combine(root, DirectoryName, hash + ".json");
    }
    private static byte[]? ReadExisting(string path) => GeneratedOutputPathSafety.PathExists(path)
        ? SunderRpcDescriptorFileReader.ReadStableBytes(path, MaximumBytes, "existing Registry catalog") : null;
    private static InvalidDataException Missing() => new("contract-cache.entry-missing: The exact selected platform catalog is absent. Supply SunderPlatformCatalogPath to RestoreSunderContracts for local selections.");
}
