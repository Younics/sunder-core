using Sunder.App.Services;

namespace Sunder.App.ViewModels;

internal sealed class StackLibraryCoordinator(LocalStackLibraryService library)
{
    private IReadOnlyList<LocalStackLibraryItem> _items = [];

    public int Count => _items.Count;

    public async Task RefreshAsync(CancellationToken cancellationToken)
        => _items = await library.ListAsync(cancellationToken);

    public async Task<LocalStackLibraryItem> ImportAsync(string path, CancellationToken cancellationToken)
    {
        var item = await library.ImportAsync(path, cancellationToken);
        await RefreshAsync(cancellationToken);
        return item;
    }

    public async Task DeleteAsync(LocalStackLibraryItem item, CancellationToken cancellationToken)
    {
        await library.DeleteAsync(item, cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    public Task ExportAsync(LocalStackLibraryItem item, string path, CancellationToken cancellationToken)
        => library.ExportAsync(item, path, cancellationToken);

    public IReadOnlyList<LocalStackLibraryItemViewModel> Project(string? query)
        => _items
            .Where(item => MatchesSearch(item, query?.Trim() ?? string.Empty))
            .Select(item => new LocalStackLibraryItemViewModel(item))
            .ToArray();

    private static bool MatchesSearch(LocalStackLibraryItem item, string query)
        => string.IsNullOrWhiteSpace(query)
           || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
           || item.StackId.Contains(query, StringComparison.OrdinalIgnoreCase)
           || (item.Summary?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
}
