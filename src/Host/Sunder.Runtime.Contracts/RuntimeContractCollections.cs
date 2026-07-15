using System.Collections.ObjectModel;

namespace Sunder.Runtime.Contracts;

internal static class RuntimeContractCollections
{
    public static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values)
        => Array.AsReadOnly(values.ToArray());

    public static IReadOnlyList<T>? FreezeNullable<T>(IEnumerable<T>? values)
        => values is null ? null : Freeze(values);

    public static IReadOnlyDictionary<string, TValue> Freeze<TValue>(
        IEnumerable<KeyValuePair<string, TValue>> values)
        => new ReadOnlyDictionary<string, TValue>(
            new Dictionary<string, TValue>(values, StringComparer.Ordinal));

    public static IReadOnlyDictionary<string, TValue>? FreezeDictionaryNullable<TValue>(
        IEnumerable<KeyValuePair<string, TValue>>? values)
        => values is null ? null : Freeze(values);
}
