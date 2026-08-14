namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed partial class JsonPackageKeyValueStore
{
    internal Task<AtomicFileSnapshot> CaptureMigrationSnapshotAsync(
        CancellationToken cancellationToken = default)
        => _document.CaptureSnapshotAsync(cancellationToken);

    internal Task RestoreMigrationSnapshotAsync(
        AtomicFileSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => _document.RestoreSnapshotAsync(snapshot, cancellationToken);

    internal Task ReplaceValuesAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        PackageStorageGuards.Values(values, nameof(values));
        return _document.ExecuteAsync(transaction =>
        {
            var state = Load(transaction);
            if (PackageValueDictionary.Equals(state.Values, values)) return true;
            Save(transaction, new(values, StringComparer.Ordinal), checked(state.Revision + 1));
            return true;
        }, cancellationToken);
    }

    internal Task ReplacePhysicalValuesForMigrationAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        PackageStorageGuards.PhysicalMigrationValues(values, nameof(values));
        return _document.ExecuteAsync(transaction =>
        {
            var contents = transaction.Read();
            var state = contents is null
                ? new PackageValuesDocument([], 0)
                : _serializer.Deserialize(
                    contents,
                    transaction.FilePath,
                    enforcePackageKeyValidation: false);
            if (PackageValueDictionary.Equals(state.Values, values)) return true;
            Save(transaction, new(values, StringComparer.Ordinal), checked(state.Revision + 1));
            return true;
        }, cancellationToken);
    }
}

internal sealed partial class JsonPackageSecretsStore
{
    internal Task<AtomicFileSnapshot> CaptureMigrationSnapshotAsync(
        CancellationToken cancellationToken = default)
        => _document.CaptureSnapshotAsync(cancellationToken);

    internal Task RestoreMigrationSnapshotAsync(
        AtomicFileSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => _document.RestoreSnapshotAsync(snapshot, cancellationToken);

    internal Task ReplaceValuesAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        PackageStorageGuards.Values(values, nameof(values));
        return _document.ExecuteAsync(transaction =>
        {
            var secrets = Load(transaction);
            if (PackageValueDictionary.Equals(secrets.Values, values)) return true;
            Save(transaction, new(values, StringComparer.Ordinal), checked(secrets.Revision + 1));
            return true;
        }, cancellationToken);
    }

    internal Task ReplacePhysicalValuesForMigrationAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        PackageStorageGuards.PhysicalMigrationValues(values, nameof(values));
        return _document.ExecuteAsync(transaction =>
        {
            var secrets = Load(transaction);
            if (PackageValueDictionary.Equals(secrets.Values, values)) return true;
            Save(transaction, new(values, StringComparer.Ordinal), checked(secrets.Revision + 1));
            return true;
        }, cancellationToken);
    }
}

internal static class PackageValueDictionary
{
    public static bool Equals(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count
           && left.All(pair => right.TryGetValue(pair.Key, out var value)
                               && string.Equals(pair.Value, value, StringComparison.Ordinal));
}
