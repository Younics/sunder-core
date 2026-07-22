using Sunder.Sdk.Abstractions;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed partial class JsonPackageKeyValueStore : IPackageKeyValueStore
{
    private readonly AtomicFileDocument _document;
    private readonly PackageStateSerializer _serializer = new();

    internal JsonPackageKeyValueStore(string filePath)
        : this(filePath, null)
    {
    }

    internal JsonPackageKeyValueStore(string filePath, AtomicFileSystem? fileSystem)
    {
        _document = new AtomicFileDocument(
            filePath,
            sensitive: false,
            StorageCommitPhase.StateDocument,
            fileSystem);
    }

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        PackageStorageGuards.Key(key, nameof(key));
        return _document.ExecuteAsync(transaction =>
        {
            var state = Load(transaction);
            return state.Values.TryGetValue(key, out var value) ? value : null;
        }, cancellationToken);
    }

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        PackageStorageGuards.Key(key, nameof(key));
        PackageStorageGuards.Value(value, nameof(value));
        return _document.ExecuteAsync(transaction =>
        {
            var state = Load(transaction);
            if (state.Values.TryGetValue(key, out var current)
                && string.Equals(current, value, StringComparison.Ordinal))
            {
                return true;
            }
            var nextRevision = checked(state.Revision + 1);
            state.Values[key] = value;
            Save(transaction, state.Values, nextRevision);
            return true;
        }, cancellationToken);
    }

    public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        PackageStorageGuards.Key(key, nameof(key));
        return _document.ExecuteAsync(
            transaction => Load(transaction).Values.ContainsKey(key),
            cancellationToken);
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        PackageStorageGuards.Key(key, nameof(key));
        return _document.ExecuteAsync(transaction =>
        {
            var state = Load(transaction);
            if (state.Values.ContainsKey(key))
            {
                var nextRevision = checked(state.Revision + 1);
                state.Values.Remove(key);
                Save(transaction, state.Values, nextRevision);
            }

            return true;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(
        string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        PackageStorageGuards.KeyPrefix(prefix, nameof(prefix));
        return _document.ExecuteAsync<IReadOnlyList<string>>(transaction =>
        {
            return Load(transaction).Values.Keys
                .Where(key => string.IsNullOrEmpty(prefix)
                    || key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
        }, cancellationToken);
    }

    public void ResetAfterFailure() => _document.Execute(transaction => transaction.ResetFailureMarker());

    private PackageValuesDocument Load(AtomicFileTransaction transaction)
    {
        var contents = transaction.Read();
        if (contents is null)
        {
            return new PackageValuesDocument([], 0);
        }

        try
        {
            return _serializer.Deserialize(contents, transaction.FilePath);
        }
        catch (Exception exception) when (PackageStorageExceptionClassifier.IsStructural(exception))
        {
            throw FailClosed(transaction, contents, "structural-corruption", exception);
        }
    }

    private void Save(AtomicFileTransaction transaction, Dictionary<string, string> values, long revision) =>
        transaction.Write(_serializer.Serialize(values, revision));

    private static PackageStorageException FailClosed(
        AtomicFileTransaction transaction,
        byte[] contents,
        string failureKind,
        Exception cause)
    {
        try
        {
            var quarantinePath = transaction.QuarantineAndReplaceWithFailure(
                contents,
                "opaque-copy",
                failureKind);
            return new PackageStorageRecoveryRequiredException(
                $"Corrupt package state was quarantined at '{quarantinePath}'. Explicit recovery or reset is required.",
                quarantinePath,
                cause);
        }
        catch (Exception quarantineException)
        {
            return new PackageStorageCorruptionException(
                "Package state is corrupt, and a durable recovery marker could not be committed. "
                    + "The original canonical document remains authoritative and was not overwritten.",
                new AggregateException(cause, quarantineException));
        }
    }
}
