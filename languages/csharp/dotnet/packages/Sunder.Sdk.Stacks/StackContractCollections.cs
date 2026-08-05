using System.Collections.ObjectModel;

namespace Sunder.Sdk.Stacks;

internal static class StackContractCollections
{
    public static IReadOnlyList<T> FreezeList<T>(IEnumerable<T> values)
        => Array.AsReadOnly(values.ToArray());

    public static IReadOnlyList<T>? FreezeListNullable<T>(IEnumerable<T>? values)
        => values is null ? null : FreezeList(values);

    public static IReadOnlyDictionary<string, TValue> FreezeDictionary<TValue>(
        IEnumerable<KeyValuePair<string, TValue>> values)
        => new ReadOnlyDictionary<string, TValue>(
            new Dictionary<string, TValue>(values, StringComparer.OrdinalIgnoreCase));
}
