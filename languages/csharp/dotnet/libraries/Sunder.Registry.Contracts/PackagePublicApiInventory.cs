using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sunder.Registry.Contracts;

/// <summary>Transport metadata for one complete package public API inventory, not publication authorization.</summary>
[JsonConverter(typeof(RegistryPackagePublicApiInventoryJsonConverter<RegistryPackagePublicApiInventoryV1>))]
public sealed record RegistryPackagePublicApiInventoryV1
{
    [JsonConstructor]
    public RegistryPackagePublicApiInventoryV1(
        int schemaVersion,
        string packageId,
        string packageVersion,
        string packageContentIdentity,
        int publicContractCount,
        IReadOnlyList<RegistryPackagePublicApiContractV1> contracts)
    {
        ArgumentNullException.ThrowIfNull(packageId);
        ArgumentNullException.ThrowIfNull(packageVersion);
        ArgumentNullException.ThrowIfNull(packageContentIdentity);
        ArgumentNullException.ThrowIfNull(contracts);
        var snapshot = contracts.ToArray();
        if (snapshot.Any(static contract => contract is null))
        {
            throw new ArgumentException("Inventory contracts must not contain null items.", nameof(contracts));
        }
        SchemaVersion = schemaVersion;
        PackageId = packageId;
        PackageVersion = packageVersion;
        PackageContentIdentity = packageContentIdentity;
        PublicContractCount = publicContractCount;
        Contracts = Array.AsReadOnly(snapshot);
    }

    [JsonPropertyName("schemaVersion"), JsonPropertyOrder(0)]
    public int SchemaVersion { get; }

    [JsonPropertyName("packageId"), JsonPropertyOrder(1)]
    public string PackageId { get; }

    [JsonPropertyName("packageVersion"), JsonPropertyOrder(2)]
    public string PackageVersion { get; }

    [JsonPropertyName("packageContentIdentity"), JsonPropertyOrder(3)]
    public string PackageContentIdentity { get; }

    [JsonPropertyName("publicContractCount"), JsonPropertyOrder(4)]
    public int PublicContractCount { get; }

    [JsonPropertyName("contracts"), JsonPropertyOrder(5)]
    public IReadOnlyList<RegistryPackagePublicApiContractV1> Contracts { get; }
}

[JsonConverter(typeof(RegistryPackagePublicApiInventoryJsonConverter<RegistryPackagePublicApiContractV1>))]
public sealed record RegistryPackagePublicApiContractV1(
    [property: JsonPropertyName("contractId"), JsonPropertyOrder(0)] string ContractId,
    [property: JsonPropertyName("contractVersion"), JsonPropertyOrder(1)] string ContractVersion,
    [property: JsonPropertyName("descriptorBytes"), JsonPropertyOrder(2)] int DescriptorBytes,
    [property: JsonPropertyName("descriptorSha256"), JsonPropertyOrder(3)] string DescriptorSha256,
    [property: JsonPropertyName("abiSha256"), JsonPropertyOrder(4)] string AbiSha256,
    [property: JsonPropertyName("artifactSource"), JsonPropertyOrder(5)] string ArtifactSource,
    [property: JsonPropertyName("authorityKind"), JsonPropertyOrder(6)] string AuthorityKind,
    [property: JsonPropertyName("authorityId"), JsonPropertyOrder(7)] string AuthorityId,
    [property: JsonPropertyName("selectedAuthorityRelease"), JsonPropertyOrder(8)] string SelectedAuthorityRelease,
    [property: JsonPropertyName("selectedAuthorityContentIdentity"), JsonPropertyOrder(9)] string SelectedAuthorityContentIdentity);

/// <summary>The digest covers only the inventory payload, not this transport envelope.</summary>
[JsonConverter(typeof(RegistryPackagePublicApiInventoryJsonConverter<RegistryPackagePublicApiInventoryResponseV1>))]
public sealed record RegistryPackagePublicApiInventoryResponseV1(
    [property: JsonPropertyName("inventory"), JsonPropertyOrder(0)] RegistryPackagePublicApiInventoryV1 Inventory,
    [property: JsonPropertyName("publicInventorySha256"), JsonPropertyOrder(1)] string PublicInventorySha256);

// Enforce wire presence and null/duplicate/unknown-member rules without depending on caller serializer settings.
// Grammar, canonical bytes, hashes and authority relationships remain the Format reader's responsibility.
internal sealed class RegistryPackagePublicApiInventoryJsonConverter<T> : JsonConverter<T> where T : class
{
    private static readonly string[] RequiredMembers = typeof(T) == typeof(RegistryPackagePublicApiInventoryV1)
        ? ["schemaVersion", "packageId", "packageVersion", "packageContentIdentity", "publicContractCount", "contracts"]
        : typeof(T) == typeof(RegistryPackagePublicApiContractV1)
            ? ["contractId", "contractVersion", "descriptorBytes", "descriptorSha256", "abiSha256", "artifactSource",
                "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity"]
            : ["inventory", "publicInventorySha256"];

    public RegistryPackagePublicApiInventoryJsonConverter() { }

    public override bool HandleNull => true;

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = ReadObject(ref reader);
        var root = document.RootElement;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name)
                || !RequiredMembers.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new JsonException("Public inventory objects must not contain unknown, duplicate or case-colliding members.");
            }
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                throw new JsonException($"Public inventory member '{property.Name}' must not be null.");
            }
        }
        if (RequiredMembers.Any(name => !names.Contains(name)))
        {
            throw new JsonException("Public inventory objects must contain every required member.");
        }

        try
        {
            object value;
            if (typeof(T) == typeof(RegistryPackagePublicApiInventoryV1))
            {
                value = new RegistryPackagePublicApiInventoryV1(
                    root.GetProperty("schemaVersion").GetInt32(),
                    root.GetProperty("packageId").GetString()!,
                    root.GetProperty("packageVersion").GetString()!,
                    root.GetProperty("packageContentIdentity").GetString()!,
                    root.GetProperty("publicContractCount").GetInt32(),
                    root.GetProperty("contracts").Deserialize<RegistryPackagePublicApiContractV1[]>(options)!);
            }
            else if (typeof(T) == typeof(RegistryPackagePublicApiContractV1))
            {
                value = new RegistryPackagePublicApiContractV1(
                    root.GetProperty("contractId").GetString()!,
                    root.GetProperty("contractVersion").GetString()!,
                    root.GetProperty("descriptorBytes").GetInt32(),
                    root.GetProperty("descriptorSha256").GetString()!,
                    root.GetProperty("abiSha256").GetString()!,
                    root.GetProperty("artifactSource").GetString()!,
                    root.GetProperty("authorityKind").GetString()!,
                    root.GetProperty("authorityId").GetString()!,
                    root.GetProperty("selectedAuthorityRelease").GetString()!,
                    root.GetProperty("selectedAuthorityContentIdentity").GetString()!);
            }
            else
            {
                value = new RegistryPackagePublicApiInventoryResponseV1(
                    root.GetProperty("inventory").Deserialize<RegistryPackagePublicApiInventoryV1>(options)!,
                    root.GetProperty("publicInventorySha256").GetString()!);
            }
            return (T)value;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            throw new JsonException("Public inventory members must have their declared JSON types.", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case RegistryPackagePublicApiInventoryV1 inventory:
                writer.WriteNumber("schemaVersion", inventory.SchemaVersion);
                WriteString(writer, "packageId", inventory.PackageId);
                WriteString(writer, "packageVersion", inventory.PackageVersion);
                WriteString(writer, "packageContentIdentity", inventory.PackageContentIdentity);
                writer.WriteNumber("publicContractCount", inventory.PublicContractCount);
                writer.WritePropertyName("contracts");
                JsonSerializer.Serialize(writer, inventory.Contracts, options);
                break;
            case RegistryPackagePublicApiContractV1 contract:
                WriteString(writer, "contractId", contract.ContractId);
                WriteString(writer, "contractVersion", contract.ContractVersion);
                writer.WriteNumber("descriptorBytes", contract.DescriptorBytes);
                WriteString(writer, "descriptorSha256", contract.DescriptorSha256);
                WriteString(writer, "abiSha256", contract.AbiSha256);
                WriteString(writer, "artifactSource", contract.ArtifactSource);
                WriteString(writer, "authorityKind", contract.AuthorityKind);
                WriteString(writer, "authorityId", contract.AuthorityId);
                WriteString(writer, "selectedAuthorityRelease", contract.SelectedAuthorityRelease);
                WriteString(writer, "selectedAuthorityContentIdentity", contract.SelectedAuthorityContentIdentity);
                break;
            case RegistryPackagePublicApiInventoryResponseV1 response:
                writer.WritePropertyName("inventory");
                JsonSerializer.Serialize(writer, response.Inventory, options);
                WriteString(writer, "publicInventorySha256", response.PublicInventorySha256);
                break;
            default:
                throw new JsonException("Public inventory values must be non-null inventory, contract or response objects.");
        }
        writer.WriteEndObject();
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string? value)
        => writer.WriteString(name, value ?? throw new JsonException($"Public inventory member '{name}' must not be null."));

    private static JsonDocument ReadObject(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Public inventory values must be non-null objects.");
        }
        var probe = reader;
        var valuesAndProperties = 1;
        while (probe.Read())
        {
            // The largest V1 payload has 2,701 values/properties; its response envelope adds four.
            if (probe.TokenType is not (JsonTokenType.EndArray or JsonTokenType.EndObject)
                && ++valuesAndProperties > 4_096)
            {
                throw new JsonException("Public inventory JSON exceeds its structural value/property budget.");
            }
            if (probe.TokenType == JsonTokenType.EndObject && probe.CurrentDepth == reader.CurrentDepth)
            {
                return JsonDocument.ParseValue(ref reader);
            }
        }
        throw new JsonException("Public inventory objects must be complete JSON objects.");
    }
}
