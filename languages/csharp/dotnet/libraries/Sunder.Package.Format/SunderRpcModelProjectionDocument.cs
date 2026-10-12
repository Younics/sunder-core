using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Format;

internal sealed record SunderRpcModelProjectionContract
{
    internal SunderRpcModelProjectionContract(string contractId, string contractVersion, string descriptorSha256,
        string abiSha256, SunderRpcModelProjection modelProjection, SunderRpcMethodPolicies? methodPolicies = null)
    {
        SunderRpcModelProjectionJson.RequireId(contractId);
        SunderRpcModelProjectionJson.RequireVersion(contractVersion);
        SunderRpcModelProjectionJson.RequireHash(descriptorSha256);
        SunderRpcModelProjectionJson.RequireHash(abiSha256);
        ArgumentNullException.ThrowIfNull(modelProjection);
        ContractId = contractId; ContractVersion = contractVersion;
        DescriptorSha256 = descriptorSha256; AbiSha256 = abiSha256; ModelProjection = modelProjection;
        MethodPolicies = methodPolicies ?? SunderRpcMethodPolicies.Empty;
    }

    public string ContractId { get; }
    public string ContractVersion { get; }
    public string DescriptorSha256 { get; }
    public string AbiSha256 { get; }
    public SunderRpcModelProjection ModelProjection { get; }
    public SunderRpcMethodPolicies MethodPolicies { get; }

    internal JsonObject ToJson()
    {
        var value = new JsonObject
        {
            ["contractId"] = ContractId, ["contractVersion"] = ContractVersion,
            ["descriptorSha256"] = DescriptorSha256, ["abiSha256"] = AbiSha256,
            ["modelProjection"] = ModelProjection.ToJson(),
        };
        if (!MethodPolicies.IsEmpty) value.Add("methodPolicies", MethodPolicies.ToJson());
        return value;
    }

    internal byte[] ToCanonicalUtf8() => SunderRpcModelProjectionJson.Write(ToJson());
}

internal sealed record SunderRpcModelProjectionDocument
{
    private readonly byte[] _canonicalUtf8;

    internal SunderRpcModelProjectionDocument(int schemaVersion, string packageId, string packageVersion,
        IReadOnlyList<SunderRpcModelProjectionContract> contracts)
    {
        if (schemaVersion != 1) throw new InvalidDataException("Model projection schemaVersion must be 1.");
        SunderRpcModelProjectionJson.RequireId(packageId);
        SunderRpcModelProjectionJson.RequireVersion(packageVersion);
        ArgumentNullException.ThrowIfNull(contracts);
        if (contracts.Count > 128) throw new InvalidDataException("Model projection document exceeds 128 contracts.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in contracts)
            if (contract is null || !ids.Add(contract.ContractId))
                throw new InvalidDataException("Model projection document permits one entry/version per contract ID.");
        SchemaVersion = schemaVersion; PackageId = packageId; PackageVersion = packageVersion;
        Contracts = Array.AsReadOnly(contracts.OrderBy(c => c.ContractId, StringComparer.Ordinal)
            .ThenBy(c => c.ContractVersion, StringComparer.Ordinal).ToArray());
        _canonicalUtf8 = SunderRpcModelProjectionJson.Write(ToJson());
        ModelProjectionSha256 = Convert.ToHexStringLower(SHA256.HashData(_canonicalUtf8));
    }

    public int SchemaVersion { get; }
    public string PackageId { get; }
    public string PackageVersion { get; }
    public IReadOnlyList<SunderRpcModelProjectionContract> Contracts { get; }
    internal int ModelProjectionBytes => _canonicalUtf8.Length;
    internal string ModelProjectionSha256 { get; }
    internal byte[] ToCanonicalUtf8() => (byte[])_canonicalUtf8.Clone();

    internal JsonObject ToJson() => new()
    {
        ["schemaVersion"] = SchemaVersion, ["packageId"] = PackageId, ["packageVersion"] = PackageVersion,
        ["contracts"] = new JsonArray(Contracts.Select(c => (JsonNode)c.ToJson()).ToArray()),
    };

    internal static SunderRpcModelProjectionDocument Parse(ReadOnlySpan<byte> bytes)
    {
        try { return ParseCore(bytes); }
        catch (InvalidDataException exception) { throw SunderRpcModelProjectionJson.Error(exception); }
    }

    private static SunderRpcModelProjectionDocument ParseCore(ReadOnlySpan<byte> bytes)
    {
        using var json = SunderRpcAuthoringJson.ParseModelProjection(bytes);
        var root = json.RootElement;
        SunderRpcAuthoringJson.RequireObject(root, "schemaVersion", "packageId", "packageVersion", "contracts");
        var version = root.GetProperty("schemaVersion");
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schemaVersion) || schemaVersion != 1)
            throw new InvalidDataException("Model projection schemaVersion must be 1.");
        var array = root.GetProperty("contracts");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 128)
            throw new InvalidDataException("Model projection contracts must be an array of at most 128 entries.");
        var contracts = new List<SunderRpcModelProjectionContract>();
        foreach (var entry in array.EnumerateArray())
        {
            var hasPolicies = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("methodPolicies", out _);
            SunderRpcAuthoringJson.RequireObject(entry, hasPolicies
                ? ["contractId", "contractVersion", "descriptorSha256", "abiSha256", "modelProjection", "methodPolicies"]
                : ["contractId", "contractVersion", "descriptorSha256", "abiSha256", "modelProjection"]);
            contracts.Add(new(Text(entry, "contractId"), Text(entry, "contractVersion"), Text(entry, "descriptorSha256"),
                Text(entry, "abiSha256"), SunderRpcModelProjection.Parse(entry.GetProperty("modelProjection")),
                hasPolicies ? SunderRpcMethodPolicies.Parse(entry.GetProperty("methodPolicies")) : SunderRpcMethodPolicies.Empty));
        }
        var result = new SunderRpcModelProjectionDocument(schemaVersion, Text(root, "packageId"), Text(root, "packageVersion"), contracts);
        if (!bytes.SequenceEqual(result._canonicalUtf8))
            throw new InvalidDataException("Model projection JSON must use deterministic schema/map order, two-space indentation, LF and one terminal LF.");
        return result;
    }

    // The manifest is the validated artifact's authority evidence. This proves linkage and
    // public closure, not that a producer actually used the claimed source symbols.
    internal void Validate(SunderPackageManifest manifest, IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptorsByContractId)
    {
        try { ValidateCore(manifest, descriptorsByContractId); }
        catch (InvalidDataException exception) { throw SunderRpcModelProjectionJson.Error(exception); }
    }

    private void ValidateCore(SunderPackageManifest manifest, IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptorsByContractId)
    {
        if (PackageId != manifest.Id || PackageVersion != manifest.Version)
            throw new InvalidDataException("Model projection package identity/version does not match its artifact.");
        if (manifest.ContractBundles is { Count: > 128 })
            throw new InvalidDataException("Model projection artifact has too many contract bundles.");
        var bundles = (manifest.ContractBundles ?? []).Where(b => b?.Exposure == SunderPackageFormat.PublicContractExposure)
            .Select(b => b!).OrderBy(b => b.ContractId, StringComparer.Ordinal).ToArray();
        if (!Contracts.Select(c => c.ContractId).SequenceEqual(bundles.Select(b => b.ContractId), StringComparer.Ordinal))
            throw new InvalidDataException("Model projection entries must cover all and only public contract bundles exactly once.");
        var authorities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (entry, bundle) in Contracts.Zip(bundles))
        {
            if (entry.ContractVersion != bundle.Version || entry.DescriptorSha256 != bundle.DescriptorSha256 || entry.AbiSha256 != bundle.AbiSha256)
                throw new InvalidDataException("Model projection entry identity does not match its public bundle.");
            SunderRpcModelProjectionJson.RequireId(bundle.AuthorityId!);
            if (bundle.AuthorityKind is not ("package" or "platform"))
                throw new InvalidDataException("Model projection requires validated bundle authority.");
            if (bundle.AuthorityKind == "platform" && (entry.ModelProjection.Models.Count != 0 || entry.ModelProjection.ServiceReferences.Count != 0
                || entry.ModelProjection.OptionalServiceReferenceMembers.Count != 0))
                throw new InvalidDataException("Platform-only bundles require explicit empty source model projection maps.");
            authorities.Add(entry.ContractId, bundle.AuthorityKind + ":" + bundle.AuthorityId);
        }
        SunderRpcModelProjectionGraph.Validate(Contracts, descriptorsByContractId, authorities);
    }

    private static string Text(JsonElement value, string name) => SunderRpcModelProjectionJson.Text(value.GetProperty(name));
}
