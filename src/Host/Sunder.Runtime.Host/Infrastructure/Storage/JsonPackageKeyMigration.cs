using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Sdk.Storage;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed partial class JsonPackageKeyValueStore
{
    public Task MigrateKeysAsync(
        IReadOnlyList<PackageStorageKeyMigration> migrations,
        CancellationToken cancellationToken = default)
    {
        PackageStorageKeyMigrationEngine.ValidateRules(migrations);
        return _document.ExecuteAsync(transaction =>
        {
            var canonicalContents = transaction.Read();
            if (canonicalContents is null)
            {
                return true;
            }

            var sourceContents = canonicalContents;
            StorageFailureMarker? marker = null;
            try
            {
                marker = TryReadMarker(canonicalContents);
                if (marker is not null)
                {
                    if (!string.Equals(marker.FailureKind, "structural-corruption", StringComparison.Ordinal)
                        || !string.Equals(marker.QuarantineProtection, "opaque-copy", StringComparison.Ordinal))
                    {
                        throw CreateRecoveryException(canonicalContents, transaction.FilePath);
                    }
                    sourceContents = transaction.ReadRecoveryQuarantine(marker);
                }

                var state = _serializer.Deserialize(
                    sourceContents,
                    transaction.FilePath,
                    enforcePackageKeyValidation: false);
                var result = PackageStorageKeyMigrationEngine.Apply(state.Values, migrations);
                if (!result.Changed)
                {
                    if (marker is not null)
                    {
                        throw CreateRecoveryException(canonicalContents, transaction.FilePath);
                    }
                    return true;
                }

                if (marker is null)
                {
                    transaction.RetainMigrationBackup(canonicalContents);
                }
                Save(transaction, result.Values, checked(state.Revision + 1));
                return true;
            }
            catch (PackageStorageNotSupportedException)
            {
                throw;
            }
            catch (PackageStorageRecoveryRequiredException)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception exception) when (PackageStorageExceptionClassifier.IsStructural(exception))
            {
                if (marker is not null)
                {
                    throw CreateRecoveryException(canonicalContents, transaction.FilePath);
                }
                throw FailClosed(transaction, canonicalContents, "structural-corruption", exception);
            }
        }, cancellationToken);
    }

    private static StorageFailureMarker? TryReadMarker(byte[] contents)
    {
        using var json = JsonDocument.Parse(contents);
        return StorageFailureMarker.IsMarker(json.RootElement)
            ? StorageFailureMarker.Read(json.RootElement)
            : null;
    }

    private static PackageStorageRecoveryRequiredException CreateRecoveryException(
        byte[] markerContents,
        string canonicalPath)
    {
        using var json = JsonDocument.Parse(markerContents);
        return StorageFailureMarker.CreateException(json.RootElement, canonicalPath);
    }
}

internal sealed partial class JsonPackageSecretsStore
{
    public Task MigrateKeysAsync(
        IReadOnlyList<PackageStorageKeyMigration> migrations,
        CancellationToken cancellationToken = default)
    {
        PackageStorageKeyMigrationEngine.ValidateRules(migrations);
        return _document.ExecuteAsync(transaction =>
        {
            var canonicalContents = transaction.Read();
            if (canonicalContents is null)
            {
                return true;
            }

            byte[]? retainedContents = null;
            byte[]? encryptedDocumentContents = null;
            EncryptedPackageSecrets? retainedEncrypted = null;
            EncryptedPackageSecrets? encrypted = null;
            byte[]? plaintext = null;
            StorageFailureMarker? marker = null;
            try
            {
                marker = TryReadSecretsMarker(canonicalContents);
                encryptedDocumentContents = canonicalContents;
                if (marker is not null)
                {
                    if (!string.Equals(marker.FailureKind, "structural-corruption", StringComparison.Ordinal)
                        || marker.QuarantineProtection is not ("aes-gcm-master-key" or "restricted-raw"))
                    {
                        throw CreateSecretsRecoveryException(canonicalContents, transaction.FilePath);
                    }

                    retainedContents = transaction.ReadRecoveryQuarantine(marker);
                    if (string.Equals(marker.QuarantineProtection, "aes-gcm-master-key", StringComparison.Ordinal))
                    {
                        retainedEncrypted = _serializer.DeserializeQuarantine(retainedContents);
                        encryptedDocumentContents = _encryption.DecryptQuarantine(retainedEncrypted);
                    }
                    else
                    {
                        encryptedDocumentContents = retainedContents;
                    }
                }

                encrypted = _serializer.DeserializeEncrypted(encryptedDocumentContents, transaction.FilePath);
                plaintext = _encryption.Decrypt(encrypted);
                var unvalidatedSerializer = new PackageSecretsSerializer(enforcePackageKeyValidation: false);
                var secrets = unvalidatedSerializer.DeserializePlaintext(plaintext);
                var result = PackageStorageKeyMigrationEngine.Apply(secrets.Values, migrations);
                if (!result.Changed)
                {
                    if (marker is not null)
                    {
                        throw CreateSecretsRecoveryException(canonicalContents, transaction.FilePath);
                    }
                    return true;
                }

                if (marker is null)
                {
                    transaction.RetainMigrationBackup(canonicalContents);
                }
                Save(transaction, result.Values, checked(secrets.Revision + 1));
                return true;
            }
            catch (PackageStorageNotSupportedException)
            {
                throw;
            }
            catch (PackageStorageRecoveryRequiredException)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (CryptographicException exception)
            {
                if (marker is not null)
                {
                    throw CreateSecretsRecoveryException(canonicalContents, transaction.FilePath);
                }
                throw FailClosed(transaction, canonicalContents, "authentication-failure", exception);
            }
            catch (Exception exception) when (PackageStorageExceptionClassifier.IsStructural(exception))
            {
                if (marker is not null)
                {
                    throw CreateSecretsRecoveryException(canonicalContents, transaction.FilePath);
                }
                throw FailClosed(transaction, canonicalContents, "structural-corruption", exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonicalContents);
                if (retainedContents is not null)
                {
                    CryptographicOperations.ZeroMemory(retainedContents);
                }
                if (encryptedDocumentContents is not null
                    && !ReferenceEquals(encryptedDocumentContents, canonicalContents)
                    && !ReferenceEquals(encryptedDocumentContents, retainedContents))
                {
                    CryptographicOperations.ZeroMemory(encryptedDocumentContents);
                }
                if (retainedEncrypted is not null)
                {
                    PackageSecretsEncryption.Zero(retainedEncrypted);
                }
                if (encrypted is not null)
                {
                    PackageSecretsEncryption.Zero(encrypted);
                }
                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }, cancellationToken);
    }

    private static StorageFailureMarker? TryReadSecretsMarker(byte[] contents)
    {
        using var json = JsonDocument.Parse(contents);
        return StorageFailureMarker.IsMarker(json.RootElement)
            ? StorageFailureMarker.Read(json.RootElement)
            : null;
    }

    private static PackageStorageRecoveryRequiredException CreateSecretsRecoveryException(
        byte[] markerContents,
        string canonicalPath)
    {
        using var json = JsonDocument.Parse(markerContents);
        return StorageFailureMarker.CreateException(json.RootElement, canonicalPath);
    }
}

internal static class PackageStorageKeyMigrationEngine
{
    internal static void ValidateRules(IReadOnlyList<PackageStorageKeyMigration>? migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        if (migrations.Count == 0 || migrations.Any(static migration => migration is null))
        {
            throw new ArgumentException("At least one non-null package storage key migration is required.", nameof(migrations));
        }
    }

    internal static PackageStorageKeyMigrationResult Apply(
        Dictionary<string, string> values,
        IReadOnlyList<PackageStorageKeyMigration> migrations)
    {
        var mutations = new List<PackageStorageKeyMutation>();
        foreach (var pair in values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            (PackageStorageKeyMigrationAction Action, bool IsCleanup)? resolution = null;
            foreach (var migration in migrations)
            {
                var candidate = migration.Resolve(pair.Key, pair.Value);
                if (candidate.Kind == PackageStorageKeyMigrationActionKind.NoMatch)
                {
                    continue;
                }
                if (resolution is not null)
                {
                    throw new InvalidOperationException($"Legacy package storage key '{pair.Key}' matches multiple migration rules.");
                }
                resolution = (candidate, migration.IsDynamicCleanup);
            }

            if (resolution is null)
            {
                if (!PackageStorageValidation.IsValidKey(pair.Key))
                {
                    throw new InvalidDataException($"Stored package key '{pair.Key}' is invalid and has no declared migration.");
                }
                continue;
            }
            if (resolution.Value.Action.Kind == PackageStorageKeyMigrationActionKind.Rewrite
                && !PackageStorageValidation.IsValidKey(resolution.Value.Action.DestinationKey))
            {
                throw new InvalidOperationException("A package storage key migration produced an invalid destination key.");
            }
            if (string.Equals(pair.Key, resolution.Value.Action.DestinationKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A package storage key migration must change the physical key.");
            }
            mutations.Add(new PackageStorageKeyMutation(
                pair.Key,
                resolution.Value.Action.DestinationKey,
                pair.Value,
                resolution.Value.Action.Precedence,
                resolution.Value.IsCleanup));
        }

        if (mutations.Count == 0)
        {
            return new PackageStorageKeyMigrationResult(values, Changed: false);
        }

        var migrated = new Dictionary<string, string>(values, StringComparer.Ordinal);
        foreach (var mutation in mutations)
        {
            migrated.Remove(mutation.Source);
        }
        foreach (var group in mutations
                     .Where(static mutation => mutation.Destination is not null)
                     .GroupBy(static mutation => mutation.Destination!, StringComparer.Ordinal))
        {
            var rewrites = group.ToArray();
            if (rewrites.Any(static rewrite => !rewrite.IsCleanup))
            {
                var expected = rewrites[0].Value;
                if (rewrites.Any(rewrite => !string.Equals(rewrite.Value, expected, StringComparison.Ordinal))
                    || migrated.TryGetValue(group.Key, out var existing)
                    && !string.Equals(existing, expected, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Package storage key migration collision at '{group.Key}' contains nonidentical values.");
                }
                migrated[group.Key] = expected;
                continue;
            }

            // A valid current value is authoritative over any crash remnant.
            if (migrated.ContainsKey(group.Key))
            {
                continue;
            }

            var highestPrecedence = rewrites.Max(static rewrite => rewrite.Precedence);
            var preferred = rewrites
                .Where(rewrite => rewrite.Precedence == highestPrecedence)
                .Select(static rewrite => rewrite.Value)
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            if (preferred.Length == 1)
            {
                migrated[group.Key] = preferred[0];
            }
        }

        if (migrated.Any(static pair => !PackageStorageValidation.IsValidKey(pair.Key)
                                        || !PackageStorageValidation.IsValidValue(pair.Value)))
        {
            throw new InvalidDataException("Migrated package storage does not satisfy the portable storage contract.");
        }
        return new PackageStorageKeyMigrationResult(migrated, Changed: true);
    }
}

internal sealed record PackageStorageKeyMutation(
    string Source,
    string? Destination,
    string Value,
    int Precedence,
    bool IsCleanup);

internal sealed record PackageStorageKeyMigrationResult(
    Dictionary<string, string> Values,
    bool Changed);
