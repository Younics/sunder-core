using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

// This binding is created only by the App's development-owner backend. Package
// code receives DTOs, never Controls, peers, factories, shell roots or this guard.
internal sealed record AppDevelopmentViewBinding(string PackageId, string ViewId, PackageDevelopmentRevision Revision,
    Control Root, IUiDispatcher Dispatcher, Action RequireCurrent);

internal sealed partial class AppDevelopmentViewOperations(TimeProvider? timeProvider = null)
{
    private const int MaximumElements = 128, MaximumVisited = 512, MaximumSnapshotBytes = 48 * 1024;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly Dictionary<string, Snapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Receipt> _receipts = new(StringComparer.Ordinal);

    internal Task<PackageDevelopmentViewSnapshot> InspectAsync(AppDevelopmentViewBinding binding, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Cancellation fences the callback, but cannot abandon queued/accepted UI
        // work. The development owner must await its actual settlement on Unload.
        return binding.Dispatcher.InvokeAsync(() => InspectCoreAsync(binding, token));
    }

    private async Task<PackageDevelopmentViewSnapshot> InspectCoreAsync(AppDevelopmentViewBinding binding, CancellationToken token, PackageDevelopmentViewQuery? query = null)
    {
        RequireView(binding, token);
        var selected = query?.SnapshotId is not null ? SelectedElement(binding, query.SnapshotId, query.ElementReference ?? query.Within!) : null;
        var root = selected?.Control ?? binding.Root;
        var id = Guid.NewGuid().ToString("N");
        var nodes = new Dictionary<string, Element>(StringComparer.Ordinal);
        List<PackageDevelopmentViewElement> visible = [];
        var bytes = 0; var visited = 0; var documents = 0; var truncated = false;
        foreach (var control in (query?.ElementReference is not null ? new[] { root } : root.GetSelfAndVisualDescendants().OfType<Control>()))
        {
            token.ThrowIfCancellationRequested();
            if (++visited > MaximumVisited || visible.Count >= MaximumElements) { truncated = true; break; }
            if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) continue;
            if (HasPrivateAncestor(control)) continue;
            if (control is IAppDevelopmentDocumentOwner browser)
            {
                if (++documents > 8) { truncated = true; break; }
                var documentQuery = query;
                if (selected?.Document is { } target) documentQuery = query! with { Within = query!.Within is null ? null : target.ElementReference, ElementReference = query.ElementReference is null ? null : target.ElementReference };
                var document = query is null ? await browser.DevelopmentDocument.InspectAsync(id, token)
                    : await browser.DevelopmentDocument.QueryAsync(id, documentQuery!, token);
                RequireView(binding, token);
                truncated |= document.WasTruncated;
                foreach (var source in document.Elements)
                {
                    var documentReference = Guid.NewGuid().ToString("N");
                    var documentItem = source with { Reference = documentReference };
                    bytes += JsonSerializer.SerializeToUtf8Bytes(documentItem).Length;
                    if (bytes > MaximumSnapshotBytes || visible.Count >= MaximumElements) { truncated = true; break; }
                    nodes.Add(documentReference, new(control, null, documentItem.Actions,
                        new(browser.DevelopmentDocument, document.DocumentId, source.Reference)));
                    visible.Add(documentItem);
                }
                continue;
            }
            var peer = ControlAutomationPeer.CreatePeerForElement(control);
            if (peer is null) continue;
            var protectedInput = control is TextBox { PasswordChar: not '\0' };
            var name = protectedInput ? "Protected input"
                : Bound(control is TextBox ? AutomationProperties.GetName(control) : peer.GetName(), 128, ref truncated);
            var actions = Actions(peer, protectedInput);
            if (name.Length == 0 && actions.Count == 0) continue;
            var reference = Guid.NewGuid().ToString("N");
            var item = new PackageDevelopmentViewElement(reference, peer.GetAutomationControlType().ToString(), name,
                control.IsEffectivelyEnabled && peer.IsEnabled(), actions);
            if (query is not null)
            {
                item = item with { Role = item.Role.ToLowerInvariant(), State = ReadState(peer, protectedInput, query.IncludeValue, ref truncated) };
                if (!MatchesQuery(item, query)) continue;
            }
            bytes += JsonSerializer.SerializeToUtf8Bytes(item).Length;
            if (bytes > MaximumSnapshotBytes) { truncated = true; break; }
            nodes.Add(reference, new(control, peer, actions));
            visible.Add(item);
        }
        RequireView(binding, token);
        lock (_sync)
        {
            // Only read-only element capabilities expire/evict. Mutation receipts
            // are retained separately and can never turn into replay authority.
            while (_snapshots.Count >= 8) _snapshots.Remove(_snapshots.First().Key);
            _snapshots.Add(id, new(binding, _clock.GetTimestamp(), nodes));
        }
        return new(id, binding.PackageId, binding.ViewId, binding.Revision, visible, truncated);
    }

    internal Task<PackageDevelopmentViewInputResult> InteractAsync(PackageDevelopmentViewInput request, CancellationToken token)
    {
        Validate(request);
        token.ThrowIfCancellationRequested();
        Snapshot snapshot;
        lock (_sync)
        {
            if (FindReceipt(request) is { } receipt) return Task.FromResult(receipt);
            if (!_snapshots.TryGetValue(request.SnapshotId, out snapshot!)) throw Invalid("The view snapshot is unknown or expired. Inspect the view again; do not replay an input.");
        }
        return snapshot.Binding.Dispatcher.InvokeAsync(() => InteractCoreAsync(request, snapshot, token));
    }

    private async Task<PackageDevelopmentViewInputResult> InteractCoreAsync(PackageDevelopmentViewInput request, Snapshot snapshot, CancellationToken token)
    {
        Element element;
        lock (_sync)
        {
            if (FindReceipt(request) is { } recorded) return recorded;
            RequireView(snapshot.Binding, token);
            token.ThrowIfCancellationRequested();
            if (!_snapshots.TryGetValue(request.SnapshotId, out var current) || !ReferenceEquals(current, snapshot)
                || _clock.GetElapsedTime(snapshot.Created) >= TimeSpan.FromSeconds(30)
                || !snapshot.Elements.TryGetValue(request.ElementReference, out element!))
                throw Invalid("The selected element reference is stale. Inspect the exact view again before requesting input.");
            var root = snapshot.Binding.Root;
            if (!element.Control.IsEffectivelyVisible || !element.Control.IsEffectivelyEnabled
                || HasPrivateAncestor(element.Control)
                || element.Control.Bounds.Width <= 0 || element.Control.Bounds.Height <= 0
                || !ReferenceEquals(element.Control, root) && !element.Control.GetVisualAncestors().Contains(root)
                || element.Peer is ControlAutomationPeer controlPeer && !ReferenceEquals(controlPeer.Owner, element.Control)
                || element.Peer is { } peer && (!peer.IsEnabled()
                    || !Actions(peer, element.Control is TextBox { PasswordChar: not '\0' }).Contains(request.Action, StringComparer.Ordinal))
                || !element.Actions.Contains(request.Action, StringComparer.Ordinal))
                throw Invalid("This element no longer permits the selected action inside the original view.");
            element.Document?.Source.RequireCurrent(element.Document.DocumentId);
            if (_receipts.Count >= 64) throw Invalid("The bounded UI operation capacity is full. Retained input receipts cannot be evicted.");
            _receipts.Add(request.OperationId, new(request, new(request.OperationId, "unknown", true)));
        }
        var result = new PackageDevelopmentViewInputResult(request.OperationId, "unknown", true);
        // From this point, a package handler may have changed external state.
        // Do not hold a monitor across native evaluation, and never abandon or
        // retry that evaluation when the caller stops or its reply is lost.
        try
        {
            if (element.Document is { } document)
            {
                if (!await document.Source.InteractAsync(document.DocumentId, request, document.ElementReference, token))
                    throw Invalid("The native document did not confirm input dispatch. Inspect again; do not replay this operation.");
            }
            else
            {
                switch (request.Action)
                {
                    case "invoke": element.Peer!.GetProvider<IInvokeProvider>()!.Invoke(); break;
                    case "set-text": element.Peer!.GetProvider<IValueProvider>()!.SetValue(request.Text!); break;
                    case "toggle": element.Peer!.GetProvider<IToggleProvider>()!.Toggle(); break;
                    case "scroll-up": element.Peer!.GetProvider<IScrollProvider>()!.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeDecrement); break;
                    case "scroll-down": element.Peer!.GetProvider<IScrollProvider>()!.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement); break;
                }
            }
            snapshot.Binding.RequireCurrent();
            element.Document?.Source.RequireCurrent(element.Document.DocumentId);
            token.ThrowIfCancellationRequested();
            result = new(request.OperationId, "dispatched", false);
        }
        catch (Exception error) when (AppPackageUiFaultPolicy.IsRecoverable(error))
        {
            AppSessionLog.WriteError("A development-view input has an unconfirmed outcome; it will not be replayed.", error);
        }
        lock (_sync) _receipts[request.OperationId] = new(request, result);
        return result;
    }

    private PackageDevelopmentViewInputResult? FindReceipt(PackageDevelopmentViewInput request)
    {
        if (!_receipts.TryGetValue(request.OperationId, out var existing)) return null;
        if (existing.Request != request) throw Invalid("An input operation identity cannot be reused with different arguments.");
        return existing.Result;
    }

    private static IReadOnlyList<string> Actions(AutomationPeer peer, bool protectedInput)
    {
        if (protectedInput) return [];
        List<string> actions = [];
        if (peer.GetProvider<IInvokeProvider>() is not null) actions.Add("invoke");
        if (peer.GetProvider<IValueProvider>() is { IsReadOnly: false }) actions.Add("set-text");
        if (peer.GetProvider<IToggleProvider>() is not null) actions.Add("toggle");
        if (peer.GetProvider<IScrollProvider>() is { VerticallyScrollable: true }) { actions.Add("scroll-up"); actions.Add("scroll-down"); }
        return actions;
    }
    private static bool HasPrivateAncestor(Control control)
        => control.GetVisualAncestors().Any(parent => parent is TextBox or IAppDevelopmentDocumentOwner);
    private static string Bound(string? value, int maximumRunes, ref bool truncated)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var count = 0; var length = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (++count > maximumRunes) { truncated = true; break; }
            length += rune.Utf16SequenceLength;
        }
        return value[..length];
    }
    private static void RequireView(AppDevelopmentViewBinding binding, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        binding.RequireCurrent();
        if (!binding.Root.IsEffectivelyVisible || binding.Root.Bounds.Width <= 0 || binding.Root.Bounds.Height <= 0)
            throw Invalid("Open the selected development view before inspecting or interacting with it.");
    }
    private static void Validate(PackageDevelopmentViewInput request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Guid.TryParseExact(request.OperationId, "N", out _) || !Guid.TryParseExact(request.SnapshotId, "N", out _)
            || !Guid.TryParseExact(request.ElementReference, "N", out _)
            || request.Action is not ("invoke" or "set-text" or "toggle" or "scroll-up" or "scroll-down")
            || (request.Action == "set-text" ? request.Text is null || request.Text.Length > 4096 : request.Text is not null))
            throw Invalid("A correlated input requires an inspected element, one advertised action and bounded text only for set-text.");
    }
    private static InvalidOperationException Invalid(string message) => new(message);
    private sealed record Element(Control Control, AutomationPeer? Peer, IReadOnlyList<string> Actions, DocumentElement? Document = null);
    private sealed record DocumentElement(IAppDevelopmentDocument Source, string DocumentId, string ElementReference);
    private sealed record Snapshot(AppDevelopmentViewBinding Binding, long Created, IReadOnlyDictionary<string, Element> Elements);
    private sealed record Receipt(PackageDevelopmentViewInput Request, PackageDevelopmentViewInputResult Result);
}
