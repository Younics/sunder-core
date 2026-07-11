using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed class JsonPackageSecretsStore : IPackageSecrets
{
    private const string EncryptedFormat = "sunder.package-secrets-encrypted";
    private const string SecretsFormat = "sunder.package-secrets";
    private const string CipherScheme = "aes-gcm";
    private const string EncryptedQuarantineFormat = "sunder.package-secrets-quarantine-encrypted";
    private const int DocumentVersion = 1;
    private const int CipherVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly AtomicFileDocument _document;
    private readonly MasterKeyStore _masterKeyStore;
    private readonly ISecretCipher _cipher;

    internal JsonPackageSecretsStore(string filePath)
        : this(filePath, null, null, null)
    {
    }

    internal JsonPackageSecretsStore(
        string filePath,
        AtomicFileSystem? fileSystem,
        ISecretCipher? cipher,
        IMasterKeyProtection? masterKeyProtection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        fileSystem ??= new AtomicFileSystem();
        var canonicalPath = Path.GetFullPath(filePath);
        _document = new AtomicFileDocument(
            canonicalPath,
            sensitive: true,
            StorageCommitPhase.SecretDocument,
            fileSystem);
        _masterKeyStore = new MasterKeyStore(
            $"{canonicalPath}.key",
            fileSystem,
            masterKeyProtection ?? new PlatformMasterKeyProtection());
        _cipher = cipher ?? new AesGcmSecretCipher();
        Activate();
    }

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _document.ExecuteAsync(transaction =>
        {
            var secrets = Load(transaction);
            return secrets.Values.TryGetValue(key, out var value) ? value : null;
        }, cancellationToken);
    }

    internal Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default)
        => _document.ExecuteAsync<IReadOnlyList<string>>(transaction =>
    {
        return Load(transaction).Values.Keys
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
    }, cancellationToken);

    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return _document.ExecuteAsync(transaction =>
        {
            var secrets = Load(transaction);
            var nextRevision = checked(secrets.Revision + 1);
            secrets.Values[key] = value;
            Save(transaction, secrets.Values, nextRevision);
            return true;
        }, cancellationToken);
    }

    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _document.ExecuteAsync(transaction =>
        {
            var secrets = Load(transaction);
            if (secrets.Values.ContainsKey(key) || secrets.IsLegacy)
            {
                var nextRevision = checked(secrets.Revision + 1);
                secrets.Values.Remove(key);
                Save(transaction, secrets.Values, nextRevision);
            }

            return true;
        }, cancellationToken);
    }

    public void ResetAfterFailure() => _document.Execute(transaction => transaction.ResetFailureMarker());

    private void Activate() => _document.Execute(_ => true);

    private SecretsDocument Load(AtomicFileTransaction transaction)
    {
        var contents = transaction.Read();
        if (contents is null)
        {
            return new SecretsDocument([], 0, IsLegacy: false);
        }

        SecretsDocument secrets;
        try
        {
            secrets = Parse(contents, transaction.FilePath);
        }
        catch (CryptographicException exception)
        {
            throw FailClosed(transaction, contents, "authentication-failure", exception);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException)
        {
            throw FailClosed(transaction, contents, "structural-corruption", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contents);
        }

        return secrets;
    }

    private SecretsDocument Parse(byte[] contents, string canonicalPath)
    {
        using var json = JsonDocument.Parse(contents);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The secrets document root must be an object.");
        }

        var root = json.RootElement;
        if (TryReadStringDictionary(root, out _))
        {
            throw new PackageStorageNotSupportedException(
                "Legacy plaintext package secrets are not supported in the Runtime V1 state root.");
        }

        if (StorageFailureMarker.IsMarker(root))
        {
            throw StorageFailureMarker.CreateException(root, canonicalPath);
        }

        var format = ReadRequiredString(root, "format", "encrypted secrets document");
        if (!string.Equals(format, EncryptedFormat, StringComparison.Ordinal))
        {
            throw new PackageStorageNotSupportedException($"Unsupported package secrets format '{format}'.");
        }

        var documentVersion = ReadRequiredInt32(root, "version", "encrypted secrets document");
        ThrowIfUnsupported(documentVersion, DocumentVersion, "encrypted secrets document version");

        if (!root.TryGetProperty("protection", out var protection)
            || protection.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The encrypted secrets document has invalid protection metadata.");
        }

        var cipherScheme = ReadRequiredString(protection, "scheme", "secrets protection metadata");
        var cipherVersion = ReadRequiredInt32(protection, "version", "secrets protection metadata");
        if (!string.Equals(cipherScheme, CipherScheme, StringComparison.Ordinal) || cipherVersion != CipherVersion)
        {
            throw new PackageStorageNotSupportedException(
                $"Secrets protection scheme '{cipherScheme}' version {cipherVersion} is not supported.");
        }

        var keyScheme = ReadRequiredString(protection, "keyScheme", "secrets protection metadata");
        var keyVersion = ReadRequiredInt32(protection, "keyVersion", "secrets protection metadata");
        if (!MasterKeyProtectionSchemes.IsKnown(keyScheme) || keyVersion != MasterKeyProtectionSchemes.CurrentVersion)
        {
            throw new PackageStorageNotSupportedException(
                $"Master-key scheme '{keyScheme}' version {keyVersion} is not supported.");
        }

        var nonce = ReadRequiredBase64(root, "nonce");
        var ciphertext = ReadRequiredBase64(root, "ciphertext");
        var tag = ReadRequiredBase64(root, "tag");
        if (nonce.Length != AesGcmSecretCipher.NonceSize || tag.Length != AesGcmSecretCipher.TagSize)
        {
            throw new InvalidDataException("The encrypted secrets nonce or authentication tag has an invalid length.");
        }

        var keyMaterial = _masterKeyStore.GetExisting();
        byte[]? plaintext = null;
        try
        {
            plaintext = _cipher.Decrypt(
                keyMaterial.Key,
                nonce,
                ciphertext,
                tag,
                CreateAssociatedData(keyScheme, keyVersion));
            var parsed = ParsePlaintextEnvelope(plaintext);
            if (keyMaterial.NeedsReprotection)
            {
                _masterKeyStore.Reprotect(keyMaterial.Key);
            }

            return parsed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyMaterial.Key);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static SecretsDocument ParsePlaintextEnvelope(byte[] plaintext)
    {
        using var json = JsonDocument.Parse(plaintext);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The decrypted secrets envelope root must be an object.");
        }

        var root = json.RootElement;
        var format = ReadRequiredString(root, "format", "decrypted secrets envelope");
        if (!string.Equals(format, SecretsFormat, StringComparison.Ordinal))
        {
            throw new PackageStorageNotSupportedException($"Unsupported decrypted secrets format '{format}'.");
        }

        var version = ReadRequiredInt32(root, "version", "decrypted secrets envelope");
        ThrowIfUnsupported(version, DocumentVersion, "decrypted secrets envelope version");
        var revision = ReadRequiredInt64(root, "revision", "decrypted secrets envelope");
        if (revision < 0)
        {
            throw new InvalidDataException("The secrets revision is invalid.");
        }

        if (!root.TryGetProperty("values", out var valuesElement))
        {
            throw new InvalidDataException("The decrypted secrets envelope does not contain values.");
        }

        return new SecretsDocument(ReadStringDictionary(valuesElement), revision, IsLegacy: false);
    }

    private void Save(AtomicFileTransaction transaction, Dictionary<string, string> values, long revision)
    {
        var keyMaterial = GetKeyForWrite();
        byte[]? plaintext = null;
        try
        {
            var envelope = new SecretsEnvelope(SecretsFormat, DocumentVersion, revision, values);
            plaintext = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            var encrypted = _cipher.Encrypt(
                keyMaterial.Key,
                plaintext,
                CreateAssociatedData(keyMaterial.ProtectionScheme, keyMaterial.ProtectionVersion));
            try
            {
                var document = new EncryptedSecretsEnvelope(
                    EncryptedFormat,
                    DocumentVersion,
                    new ProtectionEnvelope(
                        CipherScheme,
                        CipherVersion,
                        keyMaterial.ProtectionScheme,
                        keyMaterial.ProtectionVersion),
                    Convert.ToBase64String(encrypted.Nonce),
                    Convert.ToBase64String(encrypted.Ciphertext),
                    Convert.ToBase64String(encrypted.Tag));

                transaction.Write(JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted.Nonce);
                CryptographicOperations.ZeroMemory(encrypted.Ciphertext);
                CryptographicOperations.ZeroMemory(encrypted.Tag);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyMaterial.Key);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private PackageStorageException FailClosed(
        AtomicFileTransaction transaction,
        byte[] contents,
        string failureKind,
        Exception cause)
    {
        var quarantineContents = contents;
        var quarantineProtection = "restricted-raw";
        try
        {
            if (TryEncryptQuarantine(contents, out var encryptedQuarantine))
            {
                quarantineContents = encryptedQuarantine;
                quarantineProtection = "aes-gcm-master-key";
            }

            var quarantinePath = transaction.QuarantineAndReplaceWithFailure(
                quarantineContents,
                quarantineProtection,
                failureKind);
            return new PackageStorageRecoveryRequiredException(
                $"Corrupt package secrets were quarantined at '{quarantinePath}'. "
                    + "Explicit recovery or reset is required.",
                quarantinePath,
                cause);
        }
        catch (Exception quarantineException)
        {
            return new PackageStorageCorruptionException(
                "Package secrets are corrupt, and a durable recovery marker could not be committed. "
                    + "The restricted original canonical document remains authoritative and was not overwritten.",
                new AggregateException(cause, quarantineException));
        }
        finally
        {
            if (!ReferenceEquals(quarantineContents, contents))
            {
                CryptographicOperations.ZeroMemory(quarantineContents);
            }
        }
    }

    private bool TryEncryptQuarantine(byte[] contents, out byte[] encryptedQuarantine)
    {
        encryptedQuarantine = null!;
        MasterKeyMaterial? keyMaterial = null;
        SecretCiphertext? encrypted = null;
        try
        {
            keyMaterial = GetKeyForWrite();
            var associatedData = CreateQuarantineAssociatedData(
                keyMaterial.ProtectionScheme,
                keyMaterial.ProtectionVersion);
            encrypted = _cipher.Encrypt(keyMaterial.Key, contents, associatedData);
            var envelope = new EncryptedQuarantineEnvelope(
                EncryptedQuarantineFormat,
                DocumentVersion,
                new ProtectionEnvelope(
                    CipherScheme,
                    CipherVersion,
                    keyMaterial.ProtectionScheme,
                    keyMaterial.ProtectionVersion),
                Convert.ToBase64String(encrypted.Nonce),
                Convert.ToBase64String(encrypted.Ciphertext),
                Convert.ToBase64String(encrypted.Tag));
            encryptedQuarantine = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or PackageStorageNotSupportedException
            or CryptographicException
            or PlatformNotSupportedException
            or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (keyMaterial is not null)
            {
                CryptographicOperations.ZeroMemory(keyMaterial.Key);
            }

            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted.Nonce);
                CryptographicOperations.ZeroMemory(encrypted.Ciphertext);
                CryptographicOperations.ZeroMemory(encrypted.Tag);
            }
        }
    }

    private static byte[] CreateAssociatedData(string keyScheme, int keyVersion) => Encoding.UTF8.GetBytes(
        $"{EncryptedFormat}|document={DocumentVersion}|cipher={CipherScheme}:{CipherVersion}|key={keyScheme}:{keyVersion}");

    private MasterKeyMaterial GetKeyForWrite()
    {
        var keyMaterial = _masterKeyStore.GetOrCreate();
        if (!keyMaterial.NeedsReprotection)
        {
            return keyMaterial;
        }

        try
        {
            _masterKeyStore.Reprotect(keyMaterial.Key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyMaterial.Key);
        }

        return _masterKeyStore.GetExisting();
    }

    private static byte[] CreateQuarantineAssociatedData(string keyScheme, int keyVersion) => Encoding.UTF8.GetBytes(
        $"{EncryptedQuarantineFormat}|document={DocumentVersion}|cipher={CipherScheme}:{CipherVersion}|key={keyScheme}:{keyVersion}");

    private static bool TryReadStringDictionary(JsonElement element, out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
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

    private static Dictionary<string, string> ReadStringDictionary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Stored secrets must be a JSON object.");
        }

        if (!TryReadStringDictionary(element, out var values))
        {
            throw new InvalidDataException("Every stored secret must be a string.");
        }

        return values;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName, string documentName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"The {documentName} has an invalid '{propertyName}'.");
        }

        return property.GetString()!;
    }

    private static int ReadRequiredInt32(JsonElement element, string propertyName, string documentName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || !property.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"The {documentName} has an invalid '{propertyName}'.");
        }

        return value;
    }

    private static long ReadRequiredInt64(JsonElement element, string propertyName, string documentName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || !property.TryGetInt64(out var value))
        {
            throw new InvalidDataException($"The {documentName} has an invalid '{propertyName}'.");
        }

        return value;
    }

    private static byte[] ReadRequiredBase64(JsonElement element, string propertyName)
    {
        var encoded = ReadRequiredString(element, propertyName, "encrypted secrets document");
        return Convert.FromBase64String(encoded);
    }

    private static void ThrowIfUnsupported(int actualVersion, int currentVersion, string component)
    {
        if (actualVersion != currentVersion)
        {
            throw new PackageStorageNotSupportedException(
                $"{component} {actualVersion} is not supported; this runtime supports version {currentVersion}.");
        }
    }

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

    private sealed record EncryptedQuarantineEnvelope(
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

    private sealed record SecretsDocument(
        Dictionary<string, string> Values,
        long Revision,
        bool IsLegacy);
}
