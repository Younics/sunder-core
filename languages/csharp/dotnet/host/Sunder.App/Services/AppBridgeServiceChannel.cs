using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Bridge;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.App.Services;

// App V1 wire DTOs are JSON values. No object from the shell's SDK is passed to package code.
internal class AppBridgeServiceChannel(
    ISunderRpcClient? rpc = null,
    IPackageDevelopmentSession? development = null,
    SunderContent? content = null,
    bool ownsRpc = false) : IAppServiceChannelV1
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    protected static JsonElement Encode<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    protected static T Decode<T>(JsonElement value) => value.Deserialize<T>(Json)!;
    protected static T Arg<T>(JsonElement value, string name) => Decode<T>(value.GetProperty(name));
    protected static NotSupportedException Unsupported(string operation) => new($"Unsupported App V1 operation '{operation}'.");
    private readonly object _gate = new();
    private readonly HashSet<AppBridgeServiceChannel> _children = [];
    private readonly List<IDisposable> _subscriptions = [];
    private int _opening;
    private TaskCompletionSource? _opensDrained;
    private Action? _released;
    private Task? _disposal;

    protected void RequireAlive() { lock (_gate) ObjectDisposedException.ThrowIf(_disposal is not null, this); }

    protected IDisposable OwnSubscription(IDisposable subscription)
    {
        lock (_gate)
        {
            if (_disposal is null) { _subscriptions.Add(subscription); return subscription; }
        }
        subscription.Dispose();
        throw new ObjectDisposedException(nameof(AppBridgeServiceChannel));
    }

    public JsonElement Get(string operation, JsonElement args)
    {
        RequireAlive();
        try { return GetCore(operation, args); }
        catch (SunderRpcException error) { throw Translate(error); }
    }

    protected virtual JsonElement GetCore(string operation, JsonElement args) => operation switch
    {
        "rpc.profile" when rpc is not null => Encode(rpc.ResourceProfile),
        "rpc.deadline" when rpc is ISunderRpcCallScope scope => Encode(scope.DeadlineUtc),
        "development.package-id" when development is not null => Encode(development.PackageId),
        "content.metadata" when content is not null => Encode(new { content.MediaType, content.FileName, content.Length, content.Sha256, content.ExpiresAtUtc, content.IsRepeatable }),
        _ => throw Unsupported(operation),
    };

    public async ValueTask<JsonElement> InvokeAsync(string operation, JsonElement args, CancellationToken token)
    {
        RequireAlive();
        token.ThrowIfCancellationRequested();
        try { return await InvokeCoreAsync(operation, args, token).ConfigureAwait(false); }
        catch (SunderRpcException error) { throw Translate(error); }
    }

    protected virtual async ValueTask<JsonElement> InvokeCoreAsync(string operation, JsonElement a, CancellationToken token)
    {
        if (rpc is not null)
        {
            switch (operation)
            {
                case "rpc.provider": return Encode(await rpc.GetProviderAsync(Arg<SunderRpcEndpointReference>(a, "endpoint"), token).ConfigureAwait(false));
                case "rpc.report": return Encode(await rpc.TryReportInvariantViolationAsync(Arg<SunderRpcEndpointReference>(a, "endpoint"), new InvalidOperationException(Arg<string>(a, "message")), token).ConfigureAwait(false));
                case "rpc.discover": return Encode(await rpc.DiscoverAsync(Arg<string>(a, "contractId"), token).ConfigureAwait(false));
                case "rpc.invoke": return await rpc.InvokeAsync(Arg<SunderRpcEndpointReference>(a, "endpoint"), Arg<string>(a, "serviceId"), Arg<string>(a, "methodId"), a.GetProperty("request"), Arg<SunderRpcCallOptions?>(a, "options"), token).ConfigureAwait(false);
                case "rpc.content-file" when rpc is ISunderRpcCallScope scope:
                    return Encode(await scope.RegisterContentFileAsync(Arg<SunderRpcEndpointReference>(a, "endpoint"), Arg<string>(a, "filePath"), Arg<SunderRpcContentRegistrationOptions>(a, "options"), token).ConfigureAwait(false));
            }
        }
        if (development is not null)
        {
            switch (operation)
            {
                case "development.status": return Encode(await development.GetStatusAsync(token).ConfigureAwait(false));
                case "development.watch": await development.SetWatchAsync(Arg<bool>(a, "watch"), token).ConfigureAwait(false); return Encode<object?>(null);
                case "development.reload": return Encode(await development.ReloadAsync(Arg<string>(a, "operationId"), Arg<string>(a, "sourceContentIdentity"), token).ConfigureAwait(false));
                case "development.views": return Encode(await development.GetViewsAsync(token).ConfigureAwait(false));
                case "development.open-view": await development.OpenViewAsync(Arg<string>(a, "viewId"), token).ConfigureAwait(false); return Encode<object?>(null);
                case "development.query-view": return Encode(await development.QueryViewAsync(Arg<string>(a, "viewId"), Arg<PackageDevelopmentViewQuery>(a, "query"), token).ConfigureAwait(false));
                case "development.recent-logs": return Encode(await development.GetLogsAsync(Arg<int>(a, "maximumEntries"), token).ConfigureAwait(false));
                case "development.logs": return Encode(await development.GetLogsAsync(Arg<PackageDevelopmentLogQuery>(a, "query"), token).ConfigureAwait(false));
                case "development.inspect-view": return Encode(await development.InspectViewAsync(Arg<string>(a, "viewId"), token).ConfigureAwait(false));
                case "development.interact-view": return Encode(await development.InteractViewAsync(Arg<PackageDevelopmentViewInput>(a, "request"), token).ConfigureAwait(false));
            }
        }
        throw Unsupported(operation);
    }

    public async IAsyncEnumerable<JsonElement> WatchAsync(string operation, JsonElement a, [EnumeratorCancellation] CancellationToken token)
    {
        RequireAlive();
        IAsyncEnumerator<JsonElement> items;
        try
        {
            var stream = operation switch
            {
                "rpc.watch" when rpc is not null => EncodeStream(rpc.WatchAsync(Arg<long>(a, "afterRevision"), Arg<long>(a, "afterSequence"), token), token),
                "rpc.subscribe" when rpc is not null => rpc.SubscribeAsync(Arg<SunderRpcEndpointReference>(a, "endpoint"), Arg<string>(a, "serviceId"), Arg<string>(a, "methodId"), a.GetProperty("request"), Arg<SunderRpcCallOptions?>(a, "options"), token),
                _ => throw Unsupported(operation),
            };
            items = stream.GetAsyncEnumerator(token);
        }
        catch (SunderRpcException error) { throw Translate(error); }
        try
        {
            while (true)
            {
                JsonElement current;
                try
                {
                    if (!await items.MoveNextAsync().ConfigureAwait(false)) break;
                    current = items.Current;
                }
                catch (SunderRpcException error) { throw Translate(error); }
                yield return current;
            }
        }
        finally
        {
            try { await items.DisposeAsync().ConfigureAwait(false); }
            catch (SunderRpcException error) { throw Translate(error); }
        }
    }

    private static async IAsyncEnumerable<JsonElement> EncodeStream<T>(IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var item in source.WithCancellation(token).ConfigureAwait(false)) yield return Encode(item);
    }

    public async ValueTask<IAppServiceChannelV1> OpenAsync(string operation, JsonElement args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            _opening++;
        }
        try
        {
            var child = await OpenCoreAsync(operation, args, token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposal is null)
                {
                    _children.Add(child);
                    child._released = () => { lock (_gate) _children.Remove(child); };
                    return child;
                }
            }
            await child.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(AppBridgeServiceChannel));
        }
        catch (SunderRpcException error) { throw Translate(error); }
        finally
        {
            lock (_gate) { if (--_opening == 0) _opensDrained?.TrySetResult(); }
        }
    }

    protected virtual async ValueTask<AppBridgeServiceChannel> OpenCoreAsync(string operation, JsonElement a, CancellationToken token) => operation switch
    {
        "rpc.scope" when rpc is not null => new(rpc: await rpc.CreateCallScopeAsync(Arg<SunderRpcCallOptions?>(a, "options"), token).ConfigureAwait(false), ownsRpc: true),
        "development.capture" when development is not null => new(content: await development.CaptureViewAsync(Arg<string>(a, "viewId"), Arg<PackageDevelopmentViewQuery>(a, "scope"), Arg<int>(a, "maximumEdge"), token).ConfigureAwait(false)),
        _ => throw Unsupported(operation),
    };

    public async ValueTask<Stream?> OpenReadAsync(string operation, JsonElement a, CancellationToken token)
    {
        RequireAlive();
        try
        {
            var stream = await ReadCoreAsync(operation, a, token).ConfigureAwait(false);
            return stream is null ? null : new AppBridgeStream(stream, error => error is SunderRpcException rpcError ? Translate(rpcError) : null);
        }
        catch (SunderRpcException error) { throw Translate(error); }
    }
    protected virtual async ValueTask<Stream?> ReadCoreAsync(string operation, JsonElement a, CancellationToken token) => operation switch
    {
        "rpc.content" when rpc is ISunderRpcCallScope scope => await scope.OpenContentAsync(Arg<SunderRpcContentReference>(a, "reference"), token).ConfigureAwait(false),
        "content.read" when content is not null => await content.OpenReadAsync(token).ConfigureAwait(false),
        _ => throw Unsupported(operation),
    };

    public async ValueTask<JsonElement> WriteAsync(string operation, JsonElement a, Stream input, CancellationToken token)
    {
        RequireAlive();
        using var source = new AppBridgeStream(input, error => error is AppServiceExceptionV1 { Domain: "rpc" } bridge
            ? new SunderRpcException(Decode<SunderRpcError>(bridge.Error)) : null, leaveOpen: true);
        try { return await WriteCoreAsync(operation, a, source, token).ConfigureAwait(false); }
        catch (SunderRpcException error) { throw Translate(error); }
    }
    protected virtual async ValueTask<JsonElement> WriteCoreAsync(string operation, JsonElement a, Stream input, CancellationToken token) => operation switch
    {
        "rpc.content" when rpc is ISunderRpcCallScope scope => Encode(await scope.RegisterContentAsync(Arg<SunderRpcEndpointReference>(a, "endpoint"), input, Arg<SunderRpcContentRegistrationOptions>(a, "options"), token).ConfigureAwait(false)),
        _ => throw Unsupported(operation),
    };

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        // Yield before callbacks: publish disposal before any package code can re-enter.
        await Task.Yield();
        AppBridgeServiceChannel[] children;
        IDisposable[] subscriptions;
        Task opensDrained;
        lock (_gate)
        {
            children = _children.ToArray(); _children.Clear();
            subscriptions = _subscriptions.ToArray(); _subscriptions.Clear();
            opensDrained = _opening == 0 ? Task.CompletedTask
                : (_opensDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        List<Exception>? failures = null;
        foreach (var subscription in subscriptions)
        {
            try { subscription.Dispose(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        foreach (var child in children)
        {
            try { await child.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        await opensDrained.ConfigureAwait(false);
        foreach (var owned in new IAsyncDisposable?[] { content, development, ownsRpc ? rpc as IAsyncDisposable : null })
        {
            try { if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        Interlocked.Exchange(ref _released, null)?.Invoke();
        if (failures is not null) throw new AggregateException(failures.Select(TranslateDisposal));
    }

    private static Exception TranslateDisposal(Exception error) => error switch
    {
        SunderRpcException rpcError => Translate(rpcError),
        AggregateException aggregate => new AggregateException(aggregate.InnerExceptions.Select(TranslateDisposal)),
        _ => error,
    };

    private static AppServiceExceptionV1 Translate(SunderRpcException error)
        => new("rpc", Encode(error.Error), error.Message, error.IsHostAuthenticated);
}
