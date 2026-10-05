using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Format;

// A detached, structurally validated payload. Neither its hash nor its claimed provenance grants authority.
// Keep the JSON snapshot internal instead of duplicating the Registry transport DTOs in Format.
internal sealed class SunderPackagePublicApiInventory
{
    internal const int MaximumPayloadBytes = 1_048_576;
    private const int MaximumDepth = 16;
    // Root: 13 values/properties; at most 128 records with 21 each (2,701 total).
    private const int MaximumValuesAndProperties = 4_096;
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] RootMembers =
    [
        "schemaVersion", "packageId", "packageVersion", "packageContentIdentity", "publicContractCount", "contracts",
    ];
    private static readonly string[] ContractMembers =
    [
        "contractId", "contractVersion", "descriptorBytes", "descriptorSha256", "abiSha256", "artifactSource",
        "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity",
    ];
    private readonly byte[] _canonicalUtf8;

    private SunderPackagePublicApiInventory(JsonElement payload, byte[] canonicalUtf8, string publicInventorySha256)
    {
        Payload = payload.Clone();
        _canonicalUtf8 = canonicalUtf8;
        PublicInventorySha256 = publicInventorySha256;
    }

    public JsonElement Payload { get; }
    public string PublicInventorySha256 { get; }
    public byte[] ToCanonicalUtf8() => (byte[])_canonicalUtf8.Clone();

    // Whitespace is accepted for the frozen human-readable fixtures; member, array, string and integer forms are strict.
    public static SunderPackagePublicApiInventory Parse(ReadOnlySpan<byte> utf8)
        => ParseCore(utf8, null);

    public static SunderPackagePublicApiInventory ParseVerifyHash(ReadOnlySpan<byte> utf8, string publicInventorySha256)
    {
        RequireSha256(publicInventorySha256, "publicInventorySha256");
        return ParseCore(utf8, publicInventorySha256);
    }

    private static SunderPackagePublicApiInventory ParseCore(ReadOnlySpan<byte> utf8, string? expectedSha256)
    {
        if (utf8.Length is < 2 or > MaximumPayloadBytes || utf8.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            throw new InvalidDataException($"The public inventory must be 2..{MaximumPayloadBytes} UTF-8 bytes without a BOM.");
        }
        var bytes = utf8.ToArray();
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaximumDepth });
            var objects = new Stack<HashSet<string>>();
            var valuesAndProperties = 0;
            while (reader.Read())
            {
                if (reader.TokenType is not (JsonTokenType.EndArray or JsonTokenType.EndObject)
                    && ++valuesAndProperties > MaximumValuesAndProperties)
                {
                    throw new InvalidDataException("The public inventory exceeds its structural value/property budget.");
                }
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objects.Push(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        break;
                    case JsonTokenType.EndObject:
                        objects.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                    case JsonTokenType.String:
                        // Every string in this closed schema is ASCII and needs no JSON escaping.
                        foreach (var character in reader.ValueSpan)
                        {
                            if (character is < 0x20 or > 0x7f or (byte)'\\')
                            {
                                throw new InvalidDataException("Public inventory strings must use their canonical ASCII form.");
                            }
                        }
                        if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                        {
                            throw new InvalidDataException("The public inventory contains duplicate or ASCII-case-colliding members.");
                        }
                        break;
                    case JsonTokenType.Number:
                        if (!reader.TryGetInt64(out var number) || number is < 0 or > MaximumSafeInteger
                            || reader.ValueSpan[0] == (byte)'-')
                        {
                            throw new InvalidDataException("Public inventory numbers must be canonical nonnegative safe integers.");
                        }
                        break;
                    case JsonTokenType.Null:
                        throw new InvalidDataException("The public inventory must not contain null values.");
                }
            }

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            var root = document.RootElement;
            RequireObject(root, RootMembers, RootMembers.Length);
            _ = RequireInteger(root, "schemaVersion", 1, 1);
            var packageId = RequireId(root, "packageId");
            var packageVersion = RequireVersion(root, "packageVersion");
            var packageContentIdentity = RequireDigest(root, "packageContentIdentity");
            var count = RequireInteger(root, "publicContractCount", 0, 128);
            var contracts = RequireProperty(root, "contracts", JsonValueKind.Array);
            if (contracts.GetArrayLength() > 128 || contracts.GetArrayLength() != count)
            {
                throw Error("count-mismatch", "The public inventory count must equal its complete array of at most 128 contracts.");
            }

            string? previousId = null;
            foreach (var contract in contracts.EnumerateArray())
            {
                var authorityKind = RequireString(contract, "authorityKind");
                RequireObject(contract, ContractMembers, ContractMembers.Length);
                var contractId = RequireId(contract, "contractId");
                if (previousId is not null && string.CompareOrdinal(previousId, contractId) >= 0)
                {
                    throw Error("order-mismatch", "Public contracts must be ordinally sorted with only one version per contract ID.");
                }
                previousId = contractId;
                _ = RequireVersion(contract, "contractVersion");
                _ = RequireInteger(contract, "descriptorBytes", 2, 4_194_304);
                _ = RequireDigest(contract, "descriptorSha256");
                _ = RequireDigest(contract, "abiSha256");
                if (RequireString(contract, "artifactSource") is not ("owned" or "imported")
                    || authorityKind is not ("package" or "platform"))
                {
                    throw new InvalidDataException("The public inventory has an unsupported artifact source or authority kind.");
                }
                var authorityId = RequireId(contract, "authorityId");
                _ = RequireVersion(contract, "selectedAuthorityRelease");
                _ = RequireDigest(contract, "selectedAuthorityContentIdentity");
                if (authorityKind == "platform")
                {
                    if (authorityId != "sunder.core")
                    {
                        throw Error("authority-mismatch", "Platform inventory metadata must name sunder.core.");
                    }
                }
            }

            var output = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                // Shape validation established schema order and ASCII-only values, so this emits the exact preimage.
                root.WriteTo(writer);
            }
            var canonicalUtf8 = output.WrittenSpan.ToArray();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData("sunder.package-public-api-inventory.v1\0"u8);
            hash.AppendData(canonicalUtf8);
            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (expectedSha256 is not null && !string.Equals(expectedSha256, sha256, StringComparison.Ordinal))
            {
                throw Error("hash-mismatch", "The public inventory hash does not match its complete metadata projection.");
            }

            // Check the digest first to retain the lock reader's hash-mismatch diagnostic for tampered source metadata.
            foreach (var contract in contracts.EnumerateArray())
            {
                if (contract.GetProperty("artifactSource").GetString() == "owned"
                    && (contract.GetProperty("authorityKind").GetString() != "package"
                        || contract.GetProperty("authorityId").GetString() != packageId
                        || contract.GetProperty("selectedAuthorityRelease").GetString() != packageVersion
                        || contract.GetProperty("selectedAuthorityContentIdentity").GetString() != packageContentIdentity))
                {
                    throw Error("authority-mismatch", "Owned inventory metadata must match its enclosing package authority, release and content.");
                }
            }
            using var canonicalDocument = JsonDocument.Parse(canonicalUtf8);
            return new(canonicalDocument.RootElement, canonicalUtf8, sha256);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            throw new InvalidDataException("The public inventory is not bounded strict UTF-8 JSON.", exception);
        }
    }

    private static void RequireObject(JsonElement value, IReadOnlyList<string> members, int count)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.EnumerateObject().Select(static property => property.Name).SequenceEqual(members.Take(count), StringComparer.Ordinal))
        {
            throw new InvalidDataException("The public inventory has missing, unsupported or reordered schema members.");
        }
    }

    private static JsonElement RequireProperty(JsonElement value, string name, JsonValueKind kind)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) || property.ValueKind != kind)
        {
            throw new InvalidDataException($"Public inventory member '{name}' must be a {kind}.");
        }
        return property;
    }

    private static string RequireString(JsonElement value, string name)
        => RequireProperty(value, name, JsonValueKind.String).GetString()!;

    private static string RequireId(JsonElement value, string name)
    {
        var text = RequireString(value, name);
        if (!PackageId.TryParse(text, out _))
        {
            throw new InvalidDataException($"Public inventory member '{name}' is not a canonical package identity.");
        }
        return text;
    }

    private static string RequireVersion(JsonElement value, string name)
    {
        var text = RequireString(value, name);
        if (!SemanticVersion.TryParse(text, out _))
        {
            throw new InvalidDataException($"Public inventory member '{name}' is not an exact strict semantic version.");
        }
        return text;
    }

    private static string RequireDigest(JsonElement value, string name)
    {
        var text = RequireString(value, name);
        RequireSha256(text, name);
        return text;
    }

    private static void RequireSha256(string? text, string name)
    {
        if (text is null || text.Length != 64 || text.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException($"Public inventory member '{name}' is not lowercase SHA-256 text.");
        }
    }

    private static long RequireInteger(JsonElement value, string name, long minimum, long maximum)
    {
        var property = RequireProperty(value, name, JsonValueKind.Number);
        if (!property.TryGetInt64(out var number) || number < minimum || number > maximum)
        {
            throw new InvalidDataException($"Public inventory member '{name}' must be an integer in {minimum}..{maximum}.");
        }
        return number;
    }

    private static InvalidDataException Error(string diagnostic, string message)
        => new($"rpc-authoring.{diagnostic}: {message}");
}
