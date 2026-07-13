namespace Sunder.App.ViewModels;

internal enum PackageCatalogSort
{
    SourceOrder,
    DisplayName,
    PackageId,
}

internal sealed record PackageCatalogSearchDocument(
    string PackageId,
    string DisplayName,
    string? Version = null,
    string? Summary = null,
    string? SourceLabel = null);

internal static class PackageCatalogProjection
{
    public static IReadOnlyList<TResult> Project<TSource, TResult>(
        IEnumerable<TSource> source,
        Func<TSource, PackageCatalogSearchDocument> describe,
        Func<TSource, TResult> createItem,
        string? searchText = null,
        PackageCatalogSort sort = PackageCatalogSort.SourceOrder)
    {
        var query = searchText?.Trim();
        var items = source.Select(item => new CatalogProjectionItem<TSource>(item, describe(item)));
        if (!string.IsNullOrWhiteSpace(query))
        {
            items = items.Where(item => Matches(item.Document, query));
        }

        items = sort switch
        {
            PackageCatalogSort.DisplayName => items
                .OrderBy(item => item.Document.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Document.PackageId, StringComparer.OrdinalIgnoreCase),
            PackageCatalogSort.PackageId => items.OrderBy(item => item.Document.PackageId, StringComparer.OrdinalIgnoreCase),
            _ => items,
        };

        return items.Select(item => createItem(item.Source)).ToArray();
    }

    private static bool Matches(PackageCatalogSearchDocument document, string query)
        => Contains(document.PackageId, query)
           || Contains(document.DisplayName, query)
           || Contains(document.Version, query)
           || Contains(document.Summary, query)
           || Contains(document.SourceLabel, query);

    private static bool Contains(string? value, string query)
        => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private sealed record CatalogProjectionItem<TSource>(TSource Source, PackageCatalogSearchDocument Document);
}
