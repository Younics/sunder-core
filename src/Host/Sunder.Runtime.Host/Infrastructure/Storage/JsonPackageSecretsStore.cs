using System.Security.Cryptography;
using Sunder.Sdk.Abstractions;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed partial class JsonPackageSecretsStore : IPackageSecrets
{
    private readonly AtomicFileDocument _document;
    private readonly PackageSecretsSerializer _serializer;
    private readonly PackageSecretsEncryption _encryption;
    private readonly bool _enforcePackageKeyValidation;

    internal JsonPackageSecretsStore(string filePath)
        : this(filePath, null, null, null, enforcePackageKeyValidation: true)
    {
    }

    internal JsonPackageSecretsStore(string filePath, bool enforcePackageKeyValidation)
        : this(filePath, null, null, null, enforcePackageKeyValidation)
    {
    }

    internal JsonPackageSecretsStore(
        string filePath,
        AtomicFileSystem? fileSystem,
        ISecretCipher? cipher,
        IMasterKeyProtection? masterKeyProtection,
        bool enforcePackageKeyValidation = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _enforcePackageKeyValidation = enforcePackageKeyValidation;
        fileSystem ??= new AtomicFileSystem();
        var canonicalPath = Path.GetFullPath(filePath);
        _document = new AtomicFileDocument(
            canonicalPath,
            sensitive: true,
            StorageCommitPhase.SecretDocument,
            fileSystem);
        var masterKeyStore = new MasterKeyStore(
            $"{canonicalPath}.key",
            fileSystem,
            masterKeyProtection ?? new PlatformMasterKeyProtection());
        _serializer = new PackageSecretsSerializer(enforcePackageKeyValidation);
        _encryption = new PackageSecretsEncryption(
            masterKeyStore,
            cipher ?? new AesGcmSecretCipher(),
            _serializer);
        Activate();
    }

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        return _document.ExecuteAsync(transaction =>
        {
            var secrets = Load(transaction);
            return secrets.Values.TryGetValue(key, out var value) ? value : null;
        }, cancellationToken);
    }

    internal Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default) =>
        _document.ExecuteAsync<IReadOnlyList<string>>(
            transaction => Load(transaction).Values.Keys.Order(StringComparer.Ordinal).ToArray(),
            cancellationToken);

    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        PackageStorageGuards.Value(value, nameof(value));
        return _document.ExecuteAsync(transaction =>
        {
            var secrets = Load(transaction);
            if (secrets.Values.TryGetValue(key, out var current)
                && string.Equals(current, value, StringComparison.Ordinal))
            {
                return true;
            }
            var nextRevision = checked(secrets.Revision + 1);
            secrets.Values[key] = value;
            Save(transaction, secrets.Values, nextRevision);
            return true;
        }, cancellationToken);
    }

    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        return _document.ExecuteAsync(transaction =>
        {
            var secrets = Load(transaction);
            if (secrets.Values.Remove(key))
            {
                Save(transaction, secrets.Values, checked(secrets.Revision + 1));
            }

            return true;
        }, cancellationToken);
    }

    public void ResetAfterFailure() => _document.Execute(transaction => transaction.ResetFailureMarker());

    private void ValidateKey(string key)
    {
        if (_enforcePackageKeyValidation)
        {
            PackageStorageGuards.Key(key, nameof(key));
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(key);
    }

    private void Activate() => _document.Execute(_ => true);

    private PackageValuesDocument Load(AtomicFileTransaction transaction)
    {
        var contents = transaction.Read();
        if (contents is null)
        {
            return new PackageValuesDocument([], 0);
        }

        EncryptedPackageSecrets? encrypted = null;
        byte[]? plaintext = null;
        try
        {
            encrypted = _serializer.DeserializeEncrypted(contents, transaction.FilePath);
            plaintext = _encryption.Decrypt(encrypted);
            return _serializer.DeserializePlaintext(plaintext);
        }
        catch (CryptographicException exception)
        {
            throw FailClosed(transaction, contents, "authentication-failure", exception);
        }
        catch (Exception exception) when (PackageStorageExceptionClassifier.IsStructural(exception))
        {
            throw FailClosed(transaction, contents, "structural-corruption", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contents);
            if (encrypted is not null)
            {
                PackageSecretsEncryption.Zero(encrypted);
            }

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private void Save(AtomicFileTransaction transaction, Dictionary<string, string> values, long revision)
    {
        var plaintext = _serializer.SerializePlaintext(values, revision);
        EncryptedPackageSecrets? encrypted = null;
        byte[]? document = null;
        try
        {
            encrypted = _encryption.Encrypt(plaintext);
            document = _serializer.SerializeEncrypted(encrypted);
            transaction.Write(document);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null)
            {
                PackageSecretsEncryption.Zero(encrypted);
            }

            if (document is not null)
            {
                CryptographicOperations.ZeroMemory(document);
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
            if (_encryption.TryEncryptQuarantine(contents, out var encryptedQuarantine))
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
}
