using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Format;

internal static class SunderPlatformContractCatalog
{
    private const string MandatoryContractId = "sunder.stack.contributor";

    public static byte[] Create(
        string releaseVersion,
        IReadOnlyList<SunderRpcContractDescriptor> contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        RequireStableRelease(releaseVersion);
        if (contracts.Count is < 1 or > 128)
        {
            throw new InvalidDataException("A platform catalog must contain 1..128 contracts.");
        }
        var snapshot = contracts.ToArray();
        if (snapshot.Any(static descriptor => descriptor is null))
        {
            throw new InvalidDataException("A platform catalog cannot contain a null contract.");
        }
        var records = new JsonArray();
        var payload = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["authorityKind"] = "platform",
            ["authorityId"] = "sunder.core",
            ["releaseVersion"] = releaseVersion,
            ["contractCount"] = snapshot.Length,
            ["contracts"] = records,
        };
        long outputBytes = SunderRpcAuthoringJson.WriteCompact(
            JsonSerializer.SerializeToElement(payload), SunderPlatformContractJson.MaximumBytes).Length;
        foreach (var descriptor in snapshot.OrderBy(static descriptor => descriptor.ContractId, StringComparer.Ordinal))
        {
            var descriptorBytes = descriptor.GetCanonicalUtf8();
            if (descriptorBytes.Length is < 2 or > SunderRpcProtocol.MaximumDescriptorBytes)
            {
                throw new InvalidDataException("A platform catalog descriptor exceeds the RPC descriptor byte limit.");
            }
            var record = new JsonObject
            {
                ["contractId"] = descriptor.ContractId,
                ["contractVersion"] = descriptor.Version,
                ["descriptorBytes"] = descriptorBytes.Length,
                ["descriptorSha256"] = descriptor.Sha256,
                ["abiSha256"] = descriptor.AbiSha256,
                ["canonicalDescriptor"] = Encoding.UTF8.GetString(descriptorBytes),
            };
            // Bound the complete model while assembling it, not only after serializing all descriptors.
            outputBytes += SunderRpcAuthoringJson.WriteCompact(
                JsonSerializer.SerializeToElement(record), SunderPlatformContractJson.MaximumBytes).Length
                + (records.Count == 0 ? 0 : 1);
            if (outputBytes > SunderPlatformContractJson.MaximumBytes)
            {
                throw new InvalidDataException("The platform catalog payload exceeds its byte limit.");
            }
            records.Add(record);
        }
        var element = JsonSerializer.SerializeToElement(payload);
        _ = ReadPayload(element);
        return SunderRpcAuthoringJson.WriteCompact(element, SunderPlatformContractJson.MaximumBytes);
    }

    // The selected Registry establishes publication authority. This verifies only
    // the bounded, canonical catalog and its complete descriptor inventory.
    public static SunderVerifiedPlatformContractCatalog Verify(ReadOnlySpan<byte> canonicalCatalog)
    {
        using var document = SunderRpcAuthoringJson.Parse(canonicalCatalog, SunderPlatformContractJson.MaximumBytes);
        var root = document.RootElement;
        var catalog = ReadPayload(root);
        var bytes = SunderRpcAuthoringJson.WriteCompact(root, SunderPlatformContractJson.MaximumBytes);
        if (!canonicalCatalog.SequenceEqual(bytes))
        {
            throw new InvalidDataException("A platform catalog must be exact canonical compact UTF-8 without a terminal newline.");
        }
        return new(catalog.ReleaseVersion, catalog.Contracts, bytes);
    }

    internal static void ValidateCacheOwnership(ReadOnlySpan<byte> canonicalCatalog, string expectedContentIdentity)
    {
        if (Verify(canonicalCatalog).ContentIdentity != expectedContentIdentity)
            throw new InvalidDataException("The cached platform catalog differs from its content address.");
    }

    private static (string ReleaseVersion, SunderRpcContractDescriptor[] Contracts)
        ReadPayload(JsonElement root)
    {
        SunderRpcAuthoringJson.RequireObject(
            root, "schemaVersion", "authorityKind", "authorityId", "releaseVersion", "contractCount", "contracts");
        _ = SunderPlatformContractJson.ReadInteger(root, "schemaVersion", 1, 1);
        SunderPlatformContractJson.RequireString(root, "authorityKind", "platform");
        SunderPlatformContractJson.RequireString(root, "authorityId", "sunder.core");
        var releaseVersion = SunderPlatformContractJson.ReadString(root, "releaseVersion", SemanticVersion.MaximumLength);
        RequireStableRelease(releaseVersion);
        var count = (int)SunderPlatformContractJson.ReadInteger(root, "contractCount", 1, 128);
        var records = root.GetProperty("contracts");
        SunderPlatformContractJson.RequireArray(records, 1, 128);
        if (records.GetArrayLength() != count)
        {
            throw new InvalidDataException("The platform catalog contract count does not match its complete inventory.");
        }
        var contracts = new List<SunderRpcContractDescriptor>(count);
        string? previousContractId = null;
        var mandatoryCount = 0;
        foreach (var record in records.EnumerateArray())
        {
            SunderRpcAuthoringJson.RequireObject(
                record, "contractId", "contractVersion", "descriptorBytes", "descriptorSha256", "abiSha256", "canonicalDescriptor");
            var contractId = SunderPlatformContractJson.ReadString(record, "contractId", 128);
            if (previousContractId is not null && string.CompareOrdinal(previousContractId, contractId) >= 0)
            {
                throw new InvalidDataException("Platform catalog contracts must have unique, ordinally sorted contract IDs.");
            }
            previousContractId = contractId;
            if (contractId == MandatoryContractId) mandatoryCount++;
            var version = SunderPlatformContractJson.ReadString(record, "contractVersion", SemanticVersion.MaximumLength);
            var byteCount = SunderPlatformContractJson.ReadInteger(
                record, "descriptorBytes", 2, SunderRpcProtocol.MaximumDescriptorBytes);
            var descriptorSha256 = SunderPlatformContractJson.ReadDigest(record, "descriptorSha256");
            var abiSha256 = SunderPlatformContractJson.ReadDigest(record, "abiSha256");
            var text = SunderPlatformContractJson.ReadString(record, "canonicalDescriptor", SunderRpcProtocol.MaximumDescriptorBytes);
            if (Encoding.UTF8.GetByteCount(text) != byteCount)
            {
                throw new InvalidDataException("A platform catalog descriptor's exact UTF-8 byte count does not match.");
            }
            var bytes = Encoding.UTF8.GetBytes(text);
            SunderRpcContractDescriptor descriptor;
            try
            {
                descriptor = SunderRpcContractDescriptor.Parse(bytes);
            }
            catch (SunderRpcDescriptorException exception)
            {
                throw new InvalidDataException("A platform catalog contains an invalid RPC descriptor.", exception);
            }
            if (!bytes.AsSpan().SequenceEqual(descriptor.GetCanonicalUtf8())
                || contractId != descriptor.ContractId || version != descriptor.Version
                || descriptorSha256 != descriptor.Sha256 || abiSha256 != descriptor.AbiSha256)
            {
                throw new InvalidDataException("A platform catalog descriptor must match its exact canonical bytes, identity, and descriptor/ABI hashes.");
            }
            contracts.Add(descriptor);
        }
        if (mandatoryCount != 1)
        {
            throw new InvalidDataException("A platform catalog requires exactly one sunder.stack.contributor contract.");
        }
        return (releaseVersion, contracts.ToArray());
    }

    private static void RequireStableRelease(string releaseVersion)
    {
        if (!SemanticVersion.TryParse(releaseVersion, out var version) || version.Prerelease is not null)
        {
            throw new InvalidDataException("A platform catalog release must be a stable strict SemVer version.");
        }
    }

}

// Immutable validated content. Publication authority comes from the selected Registry.
internal sealed class SunderVerifiedPlatformContractCatalog
{
    private readonly byte[] _canonicalUtf8;

    internal SunderVerifiedPlatformContractCatalog(
        string releaseVersion,
        IEnumerable<SunderRpcContractDescriptor> contracts,
        byte[] canonicalUtf8)
    {
        ReleaseVersion = releaseVersion;
        Contracts = Array.AsReadOnly(contracts.ToArray());
        _canonicalUtf8 = canonicalUtf8.ToArray();
        ContentIdentity = Convert.ToHexStringLower(SHA256.HashData(_canonicalUtf8));
    }

    public string ReleaseVersion { get; }
    public string ContentIdentity { get; }
    public IReadOnlyList<SunderRpcContractDescriptor> Contracts { get; }

    public byte[] GetCanonicalUtf8() => _canonicalUtf8.ToArray();

    // Exact pins retain the selected Registry's release and immutable content.
    public SunderRpcContractDescriptor VerifyPinned(
        string expectedReleaseVersion,
        string expectedContentIdentity,
        string contractId,
        string contractVersion,
        string descriptorSha256,
        string abiSha256)
    {
        if (ReleaseVersion != expectedReleaseVersion || ContentIdentity != expectedContentIdentity)
        {
            throw new InvalidDataException("The platform catalog does not match the exact release and content pins.");
        }
        var descriptor = Contracts.FirstOrDefault(descriptor => descriptor.ContractId == contractId);
        if (descriptor is null || descriptor.Version != contractVersion
            || descriptor.Sha256 != descriptorSha256 || descriptor.AbiSha256 != abiSha256)
        {
            throw new InvalidDataException("The verified platform catalog does not contain the exact pinned contract identity and descriptor/ABI hashes.");
        }
        return descriptor;
    }
}
