using System.Security.Cryptography;
using System.Text.Json;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed class MasterKeyStore
{
    private const string KeyFormat = "sunder.package-master-key";
    private const int KeyDocumentVersion = 1;
    private const int MasterKeySize = 32;

    private readonly AtomicFileDocument _document;
    private readonly IMasterKeyProtection _protection;

    internal MasterKeyStore(
        string filePath,
        AtomicFileSystem fileSystem,
        IMasterKeyProtection protection)
    {
        _document = new AtomicFileDocument(
            filePath,
            sensitive: true,
            StorageCommitPhase.MasterKey,
            fileSystem);
        _protection = protection;
    }

    internal MasterKeyMaterial GetExisting() => _document.Execute(transaction =>
    {
        var contents = transaction.Read()
            ?? throw new PackageStorageKeyUnavailableException(
                "The encrypted package secrets master key is missing; ciphertext was preserved for recovery.");
        try
        {
            return Parse(contents, transaction.FilePath);
        }
        catch (Exception exception) when (PackageStorageExceptionClassifier.IsStructural(exception))
        {
            throw FailClosed(transaction, contents, exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contents);
        }
    });

    internal MasterKeyMaterial GetOrCreate() => _document.Execute(transaction =>
    {
        var contents = transaction.Read();
        if (contents is not null)
        {
            try
            {
                return Parse(contents, transaction.FilePath);
            }
            catch (Exception exception) when (PackageStorageExceptionClassifier.IsStructural(exception))
            {
                throw FailClosed(transaction, contents, exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(contents);
            }
        }

        var key = RandomNumberGenerator.GetBytes(MasterKeySize);
        try
        {
            var protectedKey = _protection.Protect(key);
            try
            {
                try
                {
                    Write(transaction, protectedKey);
                }
                catch
                {
                    TryRollbackProtection(transaction, protectedKey);
                    throw;
                }

                return new MasterKeyMaterial(
                    key.ToArray(),
                    protectedKey.Scheme,
                    protectedKey.Version,
                    NeedsReprotection: false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedKey.Payload);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    });

    internal void Reprotect(byte[] expectedKey)
    {
        try
        {
            ReprotectCore(expectedKey);
        }
        catch (Exception exception) when (IsOptionalProtectionFailure(exception))
        {
            // Reprotection is opportunistic and must not make an already-readable key unavailable.
        }
    }

    private void ReprotectCore(byte[] expectedKey) => _document.Execute(transaction =>
    {
        var contents = transaction.Read()
            ?? throw new PackageStorageKeyUnavailableException(
                "The package secrets master key disappeared before it could be reprotected.");
        MasterKeyMaterial? existing = null;
        try
        {
            existing = Parse(contents, transaction.FilePath);
            if (!CryptographicOperations.FixedTimeEquals(existing.Key, expectedKey))
            {
                throw new PackageStorageKeyUnavailableException(
                    "The package secrets master key changed before it could be reprotected; ciphertext was preserved.");
            }

            if (!existing.NeedsReprotection)
            {
                return true;
            }

            ProtectedMasterKey protectedKey;
            try
            {
                protectedKey = _protection.Protect(existing.Key);
            }
            catch (Exception exception) when (IsOptionalProtectionFailure(exception))
            {
                return false;
            }

            try
            {
                if (!IsSamePersistedProtection(contents, protectedKey))
                {
                    try
                    {
                        Write(transaction, protectedKey);
                    }
                    catch (Exception exception) when (IsOptionalProtectionFailure(exception))
                    {
                        TryRollbackProtection(transaction, protectedKey);
                        return false;
                    }

                    TryDeletePersistedProtection(contents);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedKey.Payload);
            }

            return true;
        }
        catch (Exception exception) when (PackageStorageExceptionClassifier.IsStructural(exception))
        {
            throw FailClosed(transaction, contents, exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contents);
            if (existing is not null)
            {
                CryptographicOperations.ZeroMemory(existing.Key);
            }
        }
    });

    internal void ResetAfterFailure() => _document.Execute(transaction => transaction.ResetFailureMarker());

    private MasterKeyMaterial Parse(byte[] contents, string canonicalPath)
    {
        using var json = JsonDocument.Parse(contents);
        var root = PackageStorageJson.RequireObject(json, "master key document");
        if (StorageFailureMarker.IsMarker(root))
        {
            throw StorageFailureMarker.CreateException(root, canonicalPath);
        }

        var format = PackageStorageJson.ReadString(root, "format", "master key document");
        if (!string.Equals(format, KeyFormat, StringComparison.Ordinal))
        {
            throw new PackageStorageNotSupportedException($"Unsupported master key format '{format}'.");
        }

        var documentVersion = PackageStorageJson.ReadInt32(root, "version", "master key document");
        PackageStorageJson.RequireVersion(documentVersion, KeyDocumentVersion, "Master key document version");

        if (!root.TryGetProperty("protection", out var protection)
            || protection.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The master key protection metadata is invalid.");
        }

        var scheme = PackageStorageJson.ReadString(protection, "scheme", "master key protection metadata");
        var version = PackageStorageJson.ReadInt32(protection, "version", "master key protection metadata");
        if (!MasterKeyProtectionSchemes.IsKnown(scheme) || version != MasterKeyProtectionSchemes.CurrentVersion)
        {
            throw new PackageStorageNotSupportedException(
                $"Master key protection scheme '{scheme}' version {version} is not supported.");
        }

        var payload = Convert.FromBase64String(
            PackageStorageJson.ReadString(root, "payload", "master key document"));
        byte[] key;
        try
        {
            key = _protection.Unprotect(scheme, version, payload);
            if (ReferenceEquals(key, payload))
            {
                key = key.ToArray();
            }
        }
        catch (PackageStorageException)
        {
            throw;
        }
        catch (PackageStorageNotSupportedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is CryptographicException or PlatformNotSupportedException)
        {
            throw new PackageStorageKeyUnavailableException(
                "The package secrets master key provider is unavailable; key material and ciphertext were preserved.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }

        if (key.Length != MasterKeySize)
        {
            CryptographicOperations.ZeroMemory(key);
            if (!string.Equals(scheme, MasterKeyProtectionSchemes.RestrictedFile, StringComparison.Ordinal))
            {
                throw new PackageStorageKeyUnavailableException(
                    "The package secrets master-key provider returned key material with an invalid length; "
                        + "key metadata and ciphertext were preserved.");
            }

            throw new InvalidDataException("The decrypted master key has an invalid length.");
        }

        bool needsReprotection;
        try
        {
            needsReprotection = _protection.ShouldReprotect(scheme);
        }
        catch (Exception exception) when (IsOptionalProtectionFailure(exception))
        {
            needsReprotection = false;
        }

        return new MasterKeyMaterial(
            key,
            scheme,
            version,
            needsReprotection);
    }

    private static void Write(AtomicFileTransaction transaction, ProtectedMasterKey protectedKey)
    {
        var envelope = new MasterKeyEnvelope(
            KeyFormat,
            KeyDocumentVersion,
            new MasterKeyProtectionEnvelope(protectedKey.Scheme, protectedKey.Version),
            Convert.ToBase64String(protectedKey.Payload));
        var serializedEnvelope = JsonSerializer.SerializeToUtf8Bytes(envelope, PackageStorageJson.Options);
        try
        {
            transaction.Write(serializedEnvelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serializedEnvelope);
        }
    }

    private static PackageStorageException FailClosed(
        AtomicFileTransaction transaction,
        byte[] contents,
        Exception cause)
    {
        try
        {
            var quarantinePath = transaction.QuarantineAndReplaceWithFailure(
                contents,
                "restricted-raw",
                "structural-corruption");
            return new PackageStorageRecoveryRequiredException(
                $"The corrupt package secrets master key was quarantined at '{quarantinePath}'. "
                    + "Explicit recovery or reset is required.",
                quarantinePath,
                cause);
        }
        catch (Exception quarantineException)
        {
            return new PackageStorageCorruptionException(
                "The package secrets master key is corrupt, and a durable recovery marker could not be committed. "
                    + "The restricted original key document was not overwritten.",
                new AggregateException(cause, quarantineException));
        }
    }

    private void TryDeletePersistedProtection(byte[] contents)
    {
        byte[]? payload = null;
        try
        {
            using var json = JsonDocument.Parse(contents);
            var protection = json.RootElement.GetProperty("protection");
            var scheme = protection.GetProperty("scheme").GetString()!;
            var version = protection.GetProperty("version").GetInt32();
            payload = Convert.FromBase64String(json.RootElement.GetProperty("payload").GetString()!);
            _protection.TryDelete(scheme, version, payload);
        }
        catch (Exception exception) when (IsOptionalProtectionFailure(exception))
        {
            // The newly committed reference is authoritative; stale-provider cleanup is best effort.
        }
        finally
        {
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    private static bool IsSamePersistedProtection(byte[] contents, ProtectedMasterKey protectedKey) =>
        TryComparePersistedProtection(contents, protectedKey, out var isSame) && isSame;

    private static bool TryComparePersistedProtection(
        byte[] contents,
        ProtectedMasterKey protectedKey,
        out bool isSame)
    {
        isSame = false;
        byte[]? payload = null;
        try
        {
            using var json = JsonDocument.Parse(contents);
            var protection = json.RootElement.GetProperty("protection");
            payload = Convert.FromBase64String(json.RootElement.GetProperty("payload").GetString()!);
            isSame = string.Equals(
                    protection.GetProperty("scheme").GetString(),
                    protectedKey.Scheme,
                    StringComparison.Ordinal)
                && protection.GetProperty("version").GetInt32() == protectedKey.Version
                && CryptographicOperations.FixedTimeEquals(payload, protectedKey.Payload);
            return true;
        }
        catch (Exception exception) when (IsOptionalProtectionFailure(exception))
        {
            return false;
        }
        finally
        {
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    private void TryRollbackProtection(AtomicFileTransaction transaction, ProtectedMasterKey protectedKey)
    {
        byte[]? currentContents = null;
        try
        {
            currentContents = transaction.Read();
            if (currentContents is not null
                && (!TryComparePersistedProtection(currentContents, protectedKey, out var isCommitted)
                    || isCommitted))
            {
                return;
            }

            _protection.TryDelete(protectedKey.Scheme, protectedKey.Version, protectedKey.Payload);
        }
        catch (Exception exception) when (IsOptionalProtectionFailure(exception))
        {
            // Rollback cleanup must not mask the local commit failure.
        }
        finally
        {
            if (currentContents is not null)
            {
                CryptographicOperations.ZeroMemory(currentContents);
            }
        }
    }

    private static bool IsOptionalProtectionFailure(Exception exception) =>
        PackageStorageExceptionClassifier.IsProviderFailure(exception);

    private sealed record MasterKeyEnvelope(
        string Format,
        int Version,
        MasterKeyProtectionEnvelope Protection,
        string Payload);

    private sealed record MasterKeyProtectionEnvelope(string Scheme, int Version);
}

internal sealed record MasterKeyMaterial(
    byte[] Key,
    string ProtectionScheme,
    int ProtectionVersion,
    bool NeedsReprotection);

internal sealed record ProtectedMasterKey(string Scheme, int Version, byte[] Payload);
