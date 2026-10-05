using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Rpc;

/// <summary>Preserves whether an optional RPC member was absent or carried a value, including null.</summary>
[JsonConverter(typeof(SunderRpcJsonConverterFactory))]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public readonly struct SunderRpcOptional<T>
{
    private readonly T? _value;

    /// <summary>Creates a present optional member containing <paramref name="value"/>.</summary>
    public SunderRpcOptional(T value)
    {
        _value = value;
        IsPresent = true;
    }

    /// <summary>Gets whether the member was present on the wire.</summary>
    public bool IsPresent { get; }

    /// <summary>Gets the present value.</summary>
    /// <exception cref="InvalidOperationException">The member is absent.</exception>
    public T Value => IsPresent
        ? _value!
        : throw new InvalidOperationException("The optional RPC member is absent.");

    /// <summary>Gets the value when the member is present.</summary>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value!;
        return IsPresent;
    }

    /// <summary>Creates a present optional member from a value.</summary>
    public static implicit operator SunderRpcOptional<T>(T value) => new(value);
}

/// <summary>Provides the converters required by generated RPC bindings.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert == typeof(Guid)
           || typeToConvert == typeof(DateOnly)
           || typeToConvert == typeof(DateTimeOffset)
           || typeToConvert == typeof(long)
           || typeToConvert == typeof(float)
           || typeToConvert == typeof(double)
           || typeToConvert == typeof(BigInteger)
           || typeToConvert == typeof(byte[])
           || typeToConvert == typeof(SunderRpcOffsetDateTime)
           || typeToConvert == typeof(SunderRpcDuration)
           || typeToConvert == typeof(SunderRpcUri)
           || typeToConvert == typeof(SunderRpcDecimal)
           || typeToConvert == typeof(SunderRpcContentReference)
           || typeToConvert.IsGenericType
           && typeToConvert.GetGenericTypeDefinition() == typeof(SunderRpcOptional<>);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (typeToConvert == typeof(Guid)) return new SunderRpcUuidJsonConverter();
        if (typeToConvert == typeof(DateOnly)) return new SunderRpcDateJsonConverter();
        if (typeToConvert == typeof(DateTimeOffset)) return new SunderRpcUtcDateTimeJsonConverter();
        if (typeToConvert == typeof(long)) return new SunderRpcSafeIntegerJsonConverter();
        if (typeToConvert == typeof(float)) return new SunderRpcFloat32JsonConverter();
        if (typeToConvert == typeof(double)) return new SunderRpcFloat64JsonConverter();
        if (typeToConvert == typeof(BigInteger)) return new SunderRpcBigIntegerJsonConverter();
        if (typeToConvert == typeof(byte[])) return new SunderRpcBase64JsonConverter();
        if (typeToConvert == typeof(SunderRpcOffsetDateTime)) return new SunderRpcOffsetDateTimeJsonConverter();
        if (typeToConvert == typeof(SunderRpcDuration)) return new SunderRpcDurationJsonConverter();
        if (typeToConvert == typeof(SunderRpcUri)) return new SunderRpcUriJsonConverter();
        if (typeToConvert == typeof(SunderRpcDecimal)) return new SunderRpcDecimalJsonConverter();
        if (typeToConvert == typeof(SunderRpcContentReference)) return new SunderRpcContentReferenceJsonConverter();
        return (JsonConverter)Activator.CreateInstance(
            typeof(SunderRpcOptionalJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
    }

    private sealed class SunderRpcOptionalJsonConverter<T> : JsonConverter<SunderRpcOptional<T>>
    {
        public override SunderRpcOptional<T> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
            => new(JsonSerializer.Deserialize<T>(ref reader, options)!);

        public override void Write(
            Utf8JsonWriter writer,
            SunderRpcOptional<T> value,
            JsonSerializerOptions options)
        {
            if (!value.IsPresent)
            {
                throw new JsonException("An absent optional RPC member cannot be written as a JSON value.");
            }
            JsonSerializer.Serialize(writer, value.Value, options);
        }
    }

    private sealed class SunderRpcUuidJsonConverter : JsonConverter<Guid>
    {
        public override Guid Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            return value is not null
                   && SunderRpcSchemaProfile.IsUuid(value)
                   && Guid.TryParseExact(value, "D", out var result)
                ? result
                : throw new JsonException("An RPC UUID must use canonical lowercase hyphenated text.");
        }

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString("D"));
    }

    private sealed class SunderRpcUtcDateTimeJsonConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            return value is not null
                   && SunderRpcSchemaProfile.IsUtcDateTime(value)
                   && FractionalSecondDigits(value) <= 7
                   && DateTimeOffset.TryParse(
                       value,
                       CultureInfo.InvariantCulture,
                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                       out var result)
                ? result
                : throw new JsonException(
                    "An RPC UTC date-time mapped to DateTimeOffset must use canonical UTC text with at most seven fractional digits.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                CultureInfo.InvariantCulture));
    }

    private sealed class SunderRpcContentReferenceJsonConverter : JsonConverter<SunderRpcContentReference>
    {
        private static readonly HashSet<string> PropertyNames = new(StringComparer.Ordinal)
        {
            "id",
            "length",
            "sha256",
            "mediaType",
            "fileName",
            "expiresAtUtc",
            "repeatability",
        };

        public override SunderRpcContentReference Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("An RPC content reference must be an object.");
            }

            var encountered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!PropertyNames.Contains(property.Name) || !encountered.Add(property.Name))
                {
                    throw new JsonException("An RPC content reference contains an unknown or duplicate member.");
                }
            }
            if (encountered.Count != PropertyNames.Count)
            {
                throw new JsonException("An RPC content reference is missing a required member.");
            }

            var id = RequiredString(value, "id");
            var sha256 = RequiredString(value, "sha256");
            var mediaType = RequiredString(value, "mediaType");
            var fileName = RequiredString(value, "fileName");
            var expiresAt = RequiredString(value, "expiresAtUtc");
            var repeatability = RequiredString(value, "repeatability") switch
            {
                "single-use" => SunderRpcContentRepeatability.SingleUse,
                "repeatable" => SunderRpcContentRepeatability.Repeatable,
                _ => throw new JsonException("An RPC content reference has invalid repeatability."),
            };
            var lengthElement = value.GetProperty("length");
            if (lengthElement.ValueKind != JsonValueKind.Number
                || !SunderRpcSafeIntegerJsonConverter.TryParseSafeInteger(lengthElement.GetRawText(), out var length)
                || length is < 0 or > 9_007_199_254_740_991)
            {
                throw new JsonException("An RPC content reference has invalid length.");
            }
            if (!IsValidContentMetadata(id, sha256, mediaType, fileName)
                || !SunderRpcSchemaProfile.IsUtcDateTime(expiresAt)
                || !DateTimeOffset.TryParse(
                    expiresAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var expiresAtUtc))
            {
                throw new JsonException("An RPC content reference contains invalid metadata.");
            }

            return new SunderRpcContentReference(
                id,
                length,
                sha256,
                mediaType,
                fileName,
                expiresAtUtc,
                repeatability)
            {
                ExactExpiresAtUtcText = expiresAt,
            };
        }

        public override void Write(
            Utf8JsonWriter writer,
            SunderRpcContentReference value,
            JsonSerializerOptions options)
        {
            if (value.Length is < 0 or > 9_007_199_254_740_991
                || !IsValidContentMetadata(value.Id, value.Sha256, value.MediaType, value.FileName))
            {
                throw new JsonException("The RPC content reference contains invalid metadata.");
            }

            writer.WriteStartObject();
            writer.WriteString("id", value.Id);
            writer.WriteNumber("length", value.Length);
            writer.WriteString("sha256", value.Sha256);
            writer.WriteString("mediaType", value.MediaType);
            writer.WriteString("fileName", value.FileName);
            writer.WriteString("expiresAtUtc", ExactOrRepresentableExpiry(value));
            writer.WriteString(
                "repeatability",
                value.Repeatability switch
                {
                    SunderRpcContentRepeatability.SingleUse => "single-use",
                    SunderRpcContentRepeatability.Repeatable => "repeatable",
                    _ => throw new JsonException("The RPC content reference has invalid repeatability."),
                });
            writer.WriteEndObject();
        }

        private static string RequiredString(JsonElement value, string propertyName)
        {
            var property = value.GetProperty(propertyName);
            return property.ValueKind == JsonValueKind.String
                ? property.GetString()!
                : throw new JsonException($"RPC content-reference member '{propertyName}' must be a string.");
        }

        private static bool IsValidContentMetadata(
            string id,
            string sha256,
            string mediaType,
            string fileName)
            => id.EnumerateRunes().Count() is >= 1 and <= 256
               && sha256.Length == 64
               && sha256.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
               && mediaType.Length is >= 1 and <= 256
               && mediaType.All(static character => character <= 0x7f)
               && fileName.EnumerateRunes().Count() <= 1024;

        private static string ExactOrRepresentableExpiry(SunderRpcContentReference value)
        {
            if (value.ExactExpiresAtUtcText is { } exact
                && SunderRpcSchemaProfile.IsUtcDateTime(exact)
                && DateTimeOffset.TryParse(
                    exact,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed)
                && parsed == value.ExpiresAtUtc.ToUniversalTime())
            {
                return exact;
            }
            return value.ExpiresAtUtc.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                CultureInfo.InvariantCulture);
        }
    }

    private static int FractionalSecondDigits(string value)
        => value.Length == 20 ? 0 : value.Length - 21;
}
