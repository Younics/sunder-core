using System.Text.Json;
using System.Security.Cryptography;
using Sunder.Sdk.Storage;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal static class PackageStorageJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal static JsonElement RequireObject(JsonDocument document, string documentName)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The {documentName} root must be an object.");
        }

        return document.RootElement;
    }

    internal static string ReadString(JsonElement element, string propertyName, string documentName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"The {documentName} has an invalid '{propertyName}'.");
        }

        return property.GetString()!;
    }

    internal static int ReadInt32(JsonElement element, string propertyName, string documentName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || !property.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"The {documentName} has an invalid '{propertyName}'.");
        }

        return value;
    }

    internal static long ReadInt64(JsonElement element, string propertyName, string documentName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || !property.TryGetInt64(out var value))
        {
            throw new InvalidDataException($"The {documentName} has an invalid '{propertyName}'.");
        }

        return value;
    }

    internal static void RequireVersion(int actualVersion, int currentVersion, string component)
    {
        if (actualVersion != currentVersion)
        {
            throw new PackageStorageNotSupportedException(
                $"{component} {actualVersion} is not supported; this runtime supports version {currentVersion}.");
        }
    }

    internal static bool TryReadStringDictionary(JsonElement element, out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
        {
            values = null!;
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                values = null!;
                return false;
            }

            values[property.Name] = property.Value.GetString()!;
        }

        return true;
    }

    internal static Dictionary<string, string> ReadStringDictionary(
        JsonElement element,
        string objectError,
        string valueError,
        bool enforcePackageKeyValidation = true)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(objectError);
        }

        if (!TryReadStringDictionary(element, out var values))
        {
            throw new InvalidDataException(valueError);
        }

        if (values.Any(pair =>
                (enforcePackageKeyValidation && !PackageStorageValidation.IsValidKey(pair.Key))
                || !PackageStorageValidation.IsValidValue(pair.Value)))
        {
            throw new InvalidDataException(
                "Stored package keys or values violate the portable package storage contract.");
        }

        return values;
    }
}

internal static class PackageStorageExceptionClassifier
{
    internal static bool IsStructural(Exception exception) => exception is
        JsonException or InvalidDataException or FormatException;

    internal static bool IsProviderFailure(Exception exception) => exception is not (
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException);

    internal static bool IsQuarantineEncryptionFailure(Exception exception) => exception is
        IOException
        or PackageStorageNotSupportedException
        or CryptographicException
        or PlatformNotSupportedException
        or UnauthorizedAccessException;
}
