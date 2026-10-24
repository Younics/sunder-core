using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal interface IAppDevelopmentDocumentOwner
{
    IAppDevelopmentDocument DevelopmentDocument { get; }
}

internal interface IAppDevelopmentDocument
{
    Task<AppDevelopmentDocumentSnapshot> InspectAsync(string snapshotId, CancellationToken token);
    async Task<AppDevelopmentDocumentSnapshot> QueryAsync(string snapshotId, PackageDevelopmentViewQuery query, CancellationToken token)
    {
        var snapshot = await InspectAsync(snapshotId, token).ConfigureAwait(false);
        return snapshot with { Elements = snapshot.Elements.Where(item =>
            (query.Role is null || item.Role.Equals(query.Role, StringComparison.OrdinalIgnoreCase))
            && (query.Name is null || item.Name.Contains(query.Name, StringComparison.OrdinalIgnoreCase))
            && (query.Text is null || item.Name.Contains(query.Text, StringComparison.OrdinalIgnoreCase))).ToArray(),
            WasTruncated = snapshot.WasTruncated || query.Within is not null || query.ElementReference is not null || query.IncludeValue };
    }
    void RequireCurrent(string documentId);
    Task<bool> InteractAsync(string documentId, PackageDevelopmentViewInput request, string nativeElementReference, CancellationToken token);
    Task<(byte[] Png, Avalonia.Rect Bounds)> CaptureAsync(string? snapshotId, string? element, int edge, CancellationToken token)
        => throw new NotSupportedException("This native document does not support complete package capture.");
}

internal sealed record AppDevelopmentDocumentSnapshot(string DocumentId, IReadOnlyList<PackageDevelopmentViewElement> Elements, bool WasTruncated);

// Only this host adapter evaluates fixed script. No caller-supplied JavaScript,
// selector, URL, frame, screen coordinate, shortcut or clipboard is accepted.
internal sealed class AppWebDevelopmentDocument(IAppWebView webView, AppWebBridge bridge, Avalonia.Controls.Control? coordinateRoot = null) : IAppDevelopmentDocument
{
    private readonly string _key = "__sunder_development_" + Guid.NewGuid().ToString("N");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task<AppDevelopmentDocumentSnapshot> InspectAsync(string snapshotId, CancellationToken token)
        => await QueryAsync(snapshotId, new(), token).ConfigureAwait(false);

    public async Task<AppDevelopmentDocumentSnapshot> QueryAsync(string snapshotId, PackageDevelopmentViewQuery query, CancellationToken token)
    {
        query.Validate();
        token.ThrowIfCancellationRequested();
        var document = bridge.CaptureDevelopmentDocument();
        var result = await webView.InvokeScriptAsync(AppWebDevelopmentScript.Generate(new(_key, document, "inspect", snapshotId, Query: query)));
        token.ThrowIfCancellationRequested();
        bridge.RequireDevelopmentDocument(document);
        var decoded = Decode(result);
        if (decoded.State != "inspected" || decoded.Elements is null || decoded.Elements.Count > 128)
            throw new InvalidDataException("The native browser did not return a bounded development document snapshot.");
        HashSet<string> references = new(StringComparer.Ordinal);
        foreach (var element in decoded.Elements)
            if (element is null || !Guid.TryParseExact(element.Reference, "N", out _) || !references.Add(element.Reference)
                || element.Role is not { Length: > 0 and <= 32 } || element.Name is null || element.Name.EnumerateRunes().Count() > 128
                || element.Actions is null || element.Actions.Count > 5 || element.Actions.Distinct(StringComparer.Ordinal).Count() != element.Actions.Count
                || element.Actions.Any(action => action is not ("invoke" or "set-text" or "toggle" or "scroll-up" or "scroll-down"))
                || element.State is null || element.State.Count > 4 || element.State.Any(pair => pair.Key is not ("checked" or "expanded" or "selected" or "value") || pair.Value.Length > 512)
                || !query.IncludeValue && element.State.ContainsKey("value"))
                throw new InvalidDataException("The package browser returned invalid element data.");
        return new(document, decoded.Elements, decoded.WasTruncated);
    }

    public void RequireCurrent(string documentId) => bridge.RequireDevelopmentDocument(documentId);

    public async Task<(byte[] Png, Avalonia.Rect Bounds)> CaptureAsync(string? snapshotId, string? element, int edge, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var document = bridge.CaptureDevelopmentDocument();
        var viewport = webView.CaptureViewport;
        var bounds = viewport;
        if (element is not null)
        {
            var result = Decode(await webView.InvokeScriptAsync(AppWebDevelopmentScript.Generate(new(_key, document, "bounds", snapshotId!, element))));
            if (result.State != "bounds" || result.Bounds is not { } region) throw new InvalidOperationException("The capture element expired. Inspect the current view.");
            if (!double.IsFinite(region.ViewportWidth) || !double.IsFinite(region.ViewportHeight) || region.ViewportWidth <= 0 || region.ViewportHeight <= 0
                || !double.IsFinite(region.X) || !double.IsFinite(region.Y) || !double.IsFinite(region.Width) || !double.IsFinite(region.Height))
                throw new InvalidDataException("The native document returned invalid capture geometry.");
            var sx = viewport.Width / region.ViewportWidth; var sy = viewport.Height / region.ViewportHeight;
            bounds = new Avalonia.Rect(viewport.X + region.X * sx, viewport.Y + region.Y * sy, region.Width * sx, region.Height * sy).Intersect(viewport);
        }
        bridge.RequireDevelopmentDocument(document);
        var png = await webView.CaptureAsync(bounds, edge, token);
        bridge.RequireDevelopmentDocument(document);
        token.ThrowIfCancellationRequested();
        if (webView.CaptureViewport != viewport) throw new InvalidOperationException("The native viewport changed during capture.");
        var offset = coordinateRoot is null ? default : webView.Control.TranslatePoint(default, coordinateRoot)
            ?? throw new InvalidOperationException("The native document left its capture owner.");
        return (png, bounds.Translate(new Avalonia.Vector(offset.X, offset.Y)));
    }

    public async Task<bool> InteractAsync(string documentId, PackageDevelopmentViewInput request, string nativeElementReference, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bridge.RequireDevelopmentDocument(documentId);
        var result = await webView.InvokeScriptAsync(AppWebDevelopmentScript.Generate(new(_key, documentId, "input", request.SnapshotId,
            nativeElementReference, request.Action, request.Text)));
        // Do not abandon native evaluation on caller cancellation. Its actual reply
        // or failure settles the owner's in-flight work; the engine retains unknown
        // whenever cancellation/retirement races with dispatch.
        token.ThrowIfCancellationRequested();
        bridge.RequireDevelopmentDocument(documentId);
        return Decode(result).State == "dispatched";
    }

    private static Response Decode(string? result)
    {
        if (result is null || result.Length > 64 * 1024 || Encoding.UTF8.GetByteCount(result) > 64 * 1024)
            throw new InvalidDataException("The package browser response is missing or too large.");
        using var json = JsonDocument.Parse(result, new JsonDocumentOptions { MaxDepth = 8 });
        // Native adapters may return either the script's string or its JSON string
        // representation. Unwrap once, never evaluate returned document content.
        var text = json.RootElement.ValueKind == JsonValueKind.String ? json.RootElement.GetString()! : result;
        return JsonSerializer.Deserialize<Response>(text, Json) ?? throw new InvalidDataException("The package browser response is invalid.");
    }

    private sealed record Response(string State, IReadOnlyList<PackageDevelopmentViewElement>? Elements = null, bool WasTruncated = false, DocumentBounds? Bounds = null);
    private sealed record DocumentBounds(double X, double Y, double Width, double Height, double ViewportWidth, double ViewportHeight);
}
