namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed partial class JsonPackageKeyValueStore
{
    internal Task ReplaceValuesAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
        => _document.ExecuteAsync(transaction =>
        {
            var state = Load(transaction);
            if (PackageValueDictionary.Equals(state.Values, values)) return true;
            Save(transaction, new(values, StringComparer.Ordinal), checked(state.Revision + 1));
            return true;
        }, cancellationToken);
}

internal sealed partial class JsonPackageSecretsStore
{
    internal Task ReplaceValuesAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
        => _document.ExecuteAsync(transaction =>
        {
            var secrets = Load(transaction);
            if (PackageValueDictionary.Equals(secrets.Values, values)) return true;
            Save(transaction, new(values, StringComparer.Ordinal), checked(secrets.Revision + 1));
            return true;
        }, cancellationToken);
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
