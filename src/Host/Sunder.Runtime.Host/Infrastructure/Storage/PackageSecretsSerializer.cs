using System.Text.Json;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed record EncryptedPackageSecrets(
    string KeyScheme,
    int KeyVersion,
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] Tag);

internal sealed class PackageSecretsSerializer
{
    internal const string EncryptedFormat = "sunder.package-secrets-encrypted";
    internal const string PlaintextFormat = "sunder.package-secrets";
    internal const string QuarantineFormat = "sunder.package-secrets-quarantine-encrypted";
    internal const string CipherScheme = "aes-gcm";
    internal const int DocumentVersion = 1;
    internal const int CipherVersion = 1;
    private readonly bool _enforcePackageKeyValidation;

    internal PackageSecretsSerializer(bool enforcePackageKeyValidation = true)
        => _enforcePackageKeyValidation = enforcePackageKeyValidation;

    internal EncryptedPackageSecrets DeserializeEncrypted(byte[] contents, string canonicalPath)
        => DeserializeCiphertext(contents, canonicalPath, EncryptedFormat, allowFailureMarker: true);

    internal EncryptedPackageSecrets DeserializeQuarantine(byte[] contents)
        => DeserializeCiphertext(contents, canonicalPath: null, QuarantineFormat, allowFailureMarker: false);

    private static EncryptedPackageSecrets DeserializeCiphertext(
        byte[] contents,
        string? canonicalPath,
        string expectedFormat,
        bool allowFailureMarker)
    {
        using var json = JsonDocument.Parse(contents);
        var root = PackageStorageJson.RequireObject(json, "secrets document");
        if (PackageStorageJson.TryReadStringDictionary(root, out _))
        {
            throw new PackageStorageNotSupportedException(
                "Legacy plaintext package secrets are not supported in the Runtime V1 state root.");
        }

        if (allowFailureMarker && StorageFailureMarker.IsMarker(root))
        {
            throw StorageFailureMarker.CreateException(root, canonicalPath!);
        }

        var format = PackageStorageJson.ReadString(root, "format", "encrypted secrets document");
        if (!string.Equals(format, expectedFormat, StringComparison.Ordinal))
        {
            throw new PackageStorageNotSupportedException($"Unsupported package secrets format '{format}'.");
        }

        PackageStorageJson.RequireVersion(
            PackageStorageJson.ReadInt32(root, "version", "encrypted secrets document"),
            DocumentVersion,
            "encrypted secrets document version");
        if (!root.TryGetProperty("protection", out var protection)
            || protection.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The encrypted secrets document has invalid protection metadata.");
        }

        var cipherScheme = PackageStorageJson.ReadString(protection, "scheme", "secrets protection metadata");
        var cipherVersion = PackageStorageJson.ReadInt32(protection, "version", "secrets protection metadata");
        if (!string.Equals(cipherScheme, CipherScheme, StringComparison.Ordinal) || cipherVersion != CipherVersion)
        {
            throw new PackageStorageNotSupportedException(
                $"Secrets protection scheme '{cipherScheme}' version {cipherVersion} is not supported.");
        }

        var keyScheme = PackageStorageJson.ReadString(protection, "keyScheme", "secrets protection metadata");
        var keyVersion = PackageStorageJson.ReadInt32(protection, "keyVersion", "secrets protection metadata");
        if (!MasterKeyProtectionSchemes.IsKnown(keyScheme) || keyVersion != MasterKeyProtectionSchemes.CurrentVersion)
        {
            throw new PackageStorageNotSupportedException(
                $"Master-key scheme '{keyScheme}' version {keyVersion} is not supported.");
        }

        var encrypted = new EncryptedPackageSecrets(
            keyScheme,
            keyVersion,
            ReadBase64(root, "nonce"),
            ReadBase64(root, "ciphertext"),
            ReadBase64(root, "tag"));
        if (encrypted.Nonce.Length != AesGcmSecretCipher.NonceSize
            || encrypted.Tag.Length != AesGcmSecretCipher.TagSize)
        {
            throw new InvalidDataException("The encrypted secrets nonce or authentication tag has an invalid length.");
        }

        return encrypted;
    }

    internal PackageValuesDocument DeserializePlaintext(byte[] plaintext)
    {
        using var json = JsonDocument.Parse(plaintext);
        var root = PackageStorageJson.RequireObject(json, "decrypted secrets envelope");
        var format = PackageStorageJson.ReadString(root, "format", "decrypted secrets envelope");
        if (!string.Equals(format, PlaintextFormat, StringComparison.Ordinal))
        {
            throw new PackageStorageNotSupportedException($"Unsupported decrypted secrets format '{format}'.");
        }

        PackageStorageJson.RequireVersion(
            PackageStorageJson.ReadInt32(root, "version", "decrypted secrets envelope"),
            DocumentVersion,
            "decrypted secrets envelope version");
        var revision = PackageStorageJson.ReadInt64(root, "revision", "decrypted secrets envelope");
        if (revision < 0)
        {
            throw new InvalidDataException("The secrets revision is invalid.");
        }

        if (!root.TryGetProperty("values", out var valuesElement))
        {
            throw new InvalidDataException("The decrypted secrets envelope does not contain values.");
        }

        return new PackageValuesDocument(
            PackageStorageJson.ReadStringDictionary(
                valuesElement,
                "Stored secrets must be a JSON object.",
                "Every stored secret must be a string.",
                _enforcePackageKeyValidation),
            revision);
    }

    internal byte[] SerializePlaintext(Dictionary<string, string> values, long revision) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new SecretsEnvelope(PlaintextFormat, DocumentVersion, revision, values),
            PackageStorageJson.Options);

    internal byte[] SerializeEncrypted(EncryptedPackageSecrets encrypted) =>
        SerializeCiphertext(EncryptedFormat, encrypted);

    internal byte[] SerializeQuarantine(EncryptedPackageSecrets encrypted) =>
        SerializeCiphertext(QuarantineFormat, encrypted);

    private static byte[] SerializeCiphertext(string format, EncryptedPackageSecrets encrypted) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new EncryptedSecretsEnvelope(
                format,
                DocumentVersion,
                new ProtectionEnvelope(
                    CipherScheme,
                    CipherVersion,
                    encrypted.KeyScheme,
                    encrypted.KeyVersion),
                Convert.ToBase64String(encrypted.Nonce),
                Convert.ToBase64String(encrypted.Ciphertext),
                Convert.ToBase64String(encrypted.Tag)),
            PackageStorageJson.Options);

    private static byte[] ReadBase64(JsonElement element, string propertyName) => Convert.FromBase64String(
        PackageStorageJson.ReadString(element, propertyName, "encrypted secrets document"));

    private sealed record SecretsEnvelope(
        string Format,
        int Version,
        long Revision,
        Dictionary<string, string> Values);

    private sealed record EncryptedSecretsEnvelope(
        string Format,
        int Version,
        ProtectionEnvelope Protection,
        string Nonce,
        string Ciphertext,
        string Tag);

    private sealed record ProtectionEnvelope(
        string Scheme,
        int Version,
        string KeyScheme,
        int KeyVersion);
}
