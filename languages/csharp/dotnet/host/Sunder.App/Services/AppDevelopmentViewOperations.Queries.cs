using System.Text.Json;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed partial class AppDevelopmentViewOperations
{
    private readonly Dictionary<string, QueryPage> _pages = new(StringComparer.Ordinal);
    internal Task<PackageDevelopmentViewPage> QueryAsync(AppDevelopmentViewBinding binding, PackageDevelopmentViewQuery query, CancellationToken token)
    {
        query.Validate();
        token.ThrowIfCancellationRequested();
        return binding.Dispatcher.InvokeAsync(async () =>
        {
            RequireView(binding, token);
            QueryPage page;
            if (query.Cursor is { } cursor)
            {
                lock (_sync)
                {
                    if (!_pages.TryGetValue(cursor, out page!) || !SameView(page.Binding, binding)
                        || _clock.GetElapsedTime(page.Created) >= TimeSpan.FromSeconds(30)
                        || !_snapshots.TryGetValue(page.Snapshot.SnapshotId, out var retained) || !SameView(retained.Binding, binding)
                        || page.Query != query with { Cursor = null, Limit = page.Query.Limit })
                        throw Invalid("The view page expired or its query changed. Query the current view again.");
                    foreach (var document in retained.Elements.Values.Select(element => element.Document).OfType<DocumentElement>().DistinctBy(document => document.DocumentId))
                        document.Source.RequireCurrent(document.DocumentId);
                }
                page.Binding.RequireCurrent();
            }
            else
            {
                var snapshot = await InspectCoreAsync(binding, token, query);
                page = new(binding, snapshot, query with { Cursor = null }, 0, _clock.GetTimestamp());
            }
            List<PackageDevelopmentViewElement> items = [];
            foreach (var item in page.Snapshot.Elements.Skip(page.Offset).Take(query.Limit))
            {
                items.Add(item);
                if (JsonSerializer.SerializeToUtf8Bytes(items).Length > 5 * 1024) { items.RemoveAt(items.Count - 1); break; }
            }
            string? next = null;
            if (page.Offset + items.Count < page.Snapshot.Elements.Count)
            {
                next = Guid.NewGuid().ToString("N");
                lock (_sync)
                {
                    while (_pages.Count >= 16) _pages.Remove(_pages.First().Key);
                    _pages.Add(next, page with { Offset = page.Offset + items.Count });
                }
            }
            RequireView(binding, token);
            return new PackageDevelopmentViewPage(page.Snapshot with { Elements = items }, page.Snapshot.WasTruncated, next);
        });
    }

    private Element SelectedElement(AppDevelopmentViewBinding binding, string snapshotId, string elementId)
    {
        lock (_sync)
        {
            if (!_snapshots.TryGetValue(snapshotId, out var snapshot) || !SameView(snapshot.Binding, binding)
                || _clock.GetElapsedTime(snapshot.Created) >= TimeSpan.FromSeconds(30) || !snapshot.Elements.TryGetValue(elementId, out var element))
                throw Invalid("The inspected element expired. Query this exact view again.");
            snapshot.Binding.RequireCurrent();
            if (!element.Control.IsEffectivelyVisible || HasPrivateAncestor(element.Control)
                || element.Control != binding.Root && !element.Control.GetVisualAncestors().Contains(binding.Root)) throw Invalid("The inspected element left its original view.");
            element.Document?.Source.RequireCurrent(element.Document.DocumentId);
            return element;
        }
    }

    private static bool MatchesQuery(PackageDevelopmentViewElement item, PackageDevelopmentViewQuery query)
        => (query.Role is null || item.Role.Equals(query.Role, StringComparison.OrdinalIgnoreCase))
            && (query.Name is null || item.Name.Contains(query.Name, StringComparison.OrdinalIgnoreCase))
            && (query.Text is null || item.Name.Contains(query.Text, StringComparison.OrdinalIgnoreCase));

    private static bool SameView(AppDevelopmentViewBinding left, AppDevelopmentViewBinding right)
        => left.Root == right.Root && left.PackageId == right.PackageId && left.ViewId == right.ViewId
            && left.Revision.RuntimeInstanceId == right.Revision.RuntimeInstanceId && left.Revision.SourceContentIdentity == right.Revision.SourceContentIdentity;

    private static IReadOnlyDictionary<string, string> ReadState(AutomationPeer peer, bool protectedInput, bool includeValue, ref bool truncated)
    {
        Dictionary<string, string> state = [];
        if (protectedInput) return state;
        if (peer.GetProvider<IToggleProvider>() is { } toggle) state["checked"] = toggle.ToggleState.ToString().ToLowerInvariant();
        if (peer.GetProvider<IExpandCollapseProvider>() is { } expansion) state["expanded"] = expansion.ExpandCollapseState.ToString().ToLowerInvariant();
        if (peer.GetProvider<ISelectionItemProvider>() is { } selection) state["selected"] = selection.IsSelected ? "true" : "false";
        if (includeValue && peer.GetProvider<IValueProvider>() is { } value) state["value"] = Bound(value.Value, 256, ref truncated);
        return state;
    }

    private sealed record QueryPage(AppDevelopmentViewBinding Binding, PackageDevelopmentViewSnapshot Snapshot, PackageDevelopmentViewQuery Query, int Offset, long Created);
}
