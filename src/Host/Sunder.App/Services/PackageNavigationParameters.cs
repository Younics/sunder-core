using System.Collections.ObjectModel;

namespace Sunder.App.Services;

internal static class PackageNavigationParameters
{
    private static readonly IReadOnlyDictionary<string, string?> Empty =
        new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(StringComparer.Ordinal));

    public static IReadOnlyDictionary<string, string?> Snapshot(
        IReadOnlyDictionary<string, string?>? parameters)
        => parameters is null || parameters.Count == 0
            ? Empty
            : new ReadOnlyDictionary<string, string?>(
                new Dictionary<string, string?>(parameters, StringComparer.Ordinal));
}
