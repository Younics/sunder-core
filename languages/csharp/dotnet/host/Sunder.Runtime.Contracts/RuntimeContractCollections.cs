using System.Collections.ObjectModel;

namespace Sunder.Runtime.Contracts;

internal static class RuntimeContractCollections
{
    public static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values)
        => Array.AsReadOnly(values.ToArray());

    public static IReadOnlyList<T>? FreezeNullable<T>(IEnumerable<T>? values)
        => values is null ? null : Freeze(values);

    public static IReadOnlyList<T> FreezeBounded<T>(IReadOnlyList<T> values, int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > maximumCount)
        {
            throw new ArgumentException($"The collection exceeds its {maximumCount}-item bound.", nameof(values));
        }
        var snapshot = values.Take(maximumCount + 1).ToArray();
        if (snapshot.Length > maximumCount || snapshot.Any(static value => value is null))
        {
            throw new ArgumentException("The collection exceeds its bound or contains a null item.", nameof(values));
        }
        return Array.AsReadOnly(snapshot);
    }

    public static IReadOnlyDictionary<string, TValue> Freeze<TValue>(
        IEnumerable<KeyValuePair<string, TValue>> values)
        => new ReadOnlyDictionary<string, TValue>(
            new Dictionary<string, TValue>(values, StringComparer.Ordinal));

    public static IReadOnlyDictionary<string, TValue>? FreezeDictionaryNullable<TValue>(
        IEnumerable<KeyValuePair<string, TValue>>? values)
        => values is null ? null : Freeze(values);
}
