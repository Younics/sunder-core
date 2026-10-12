using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.PublicApi.Tests;

internal sealed class ServiceBindingTestClient : ISunderRpcClient
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SunderRpcProviderSnapshot> _providers = new(StringComparer.Ordinal);
    private readonly List<Observation> _observers = [];
    private readonly Channel<Observation> _started = Channel.CreateUnbounded<Observation>();
    private long _revision = 11;
    private long _sequence = 37;
    private int _discoveries;
    private int _watches;
    private int _maximumWatches;
    private int _queries;
    internal int Discoveries => Volatile.Read(ref _discoveries);
    internal int Watches => Volatile.Read(ref _watches);
    internal int MaximumConcurrentWatches => Volatile.Read(ref _maximumWatches);
    internal int Queries => Volatile.Read(ref _queries);
    internal Func<string, SunderRpcException?>? DiscoveryFailure { get; set; }
    internal Func<SunderRpcProviderSnapshot?, SunderRpcProviderSnapshot?>? ScopedProviderTransform { get; set; }
    internal SunderRpcException? QueryFailure { get; set; }
    internal SunderRpcException? ReportFailure { get; set; }
    internal Action<int>? WatchStarting { get; set; }
    internal Func<CancellationToken, Task>? QueryWait { get; set; }
    internal Func<JsonElement, CancellationToken, ValueTask<JsonElement>>? Invoke { get; set; }
    internal List<(SunderRpcEndpointReference Endpoint, Exception Error)> Reports { get; } = [];
    internal List<Scope> Scopes { get; } = [];

    internal ServiceBindingTestClient(params SunderRpcProviderSnapshot[] providers)
    {
        foreach (var provider in providers) _providers.Add(provider.Endpoint.Value, provider);
    }

    internal void ReplaceSilently(params SunderRpcProviderSnapshot[] providers)
    {
        lock (_gate)
        {
            _providers.Clear();
            foreach (var provider in providers) _providers.Add(provider.Endpoint.Value, provider);
            _revision++;
            _sequence++;
        }
    }

    internal void Publish(SunderRpcCatalogEventKind kind, SunderRpcProviderSnapshot? provider = null)
    {
        lock (_gate)
        {
            if (provider is not null)
            {
                if (kind is SunderRpcCatalogEventKind.Removed or SunderRpcCatalogEventKind.Deactivated or SunderRpcCatalogEventKind.Faulted)
                    _providers.Remove(provider.Endpoint.Value);
                else _providers[provider.Endpoint.Value] = provider;
            }
            var change = new SunderRpcCatalogEvent(++_revision, ++_sequence, kind, provider);
            foreach (var observer in _observers) observer.Events.Writer.TryWrite(new(change, null));
        }
    }

    internal void Disconnect(Exception? error = null)
    {
        lock (_gate)
            foreach (var observer in _observers)
                if (error is null) observer.Events.Writer.TryComplete();
                else observer.Events.Writer.TryWrite(new(null, error));
    }

    internal async Task<Observation> NextObservationAsync()
        => await _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _discoveries);
        if (DiscoveryFailure?.Invoke(contractId) is { } failure) throw failure;
        lock (_gate) return ValueTask.FromResult(new SunderRpcCatalogSnapshot(_revision, _sequence,
            _providers.Values.Where(provider => provider.ContractId == contractId)));
    }

    public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var observer = new Observation(afterRevision, afterSequence);
        var count = Interlocked.Increment(ref _watches);
        lock (_gate)
        {
            _observers.Add(observer);
            _maximumWatches = Math.Max(_maximumWatches, _observers.Count);
        }
        _started.Writer.TryWrite(observer);
        try
        {
            WatchStarting?.Invoke(count);
            await foreach (var signal in observer.Events.Reader.ReadAllAsync(cancellationToken))
            {
                if (signal.Error is { } error) throw error;
                yield return signal.Change!;
            }
        }
        finally
        {
            observer.DisposalStarted.TrySetResult();
            if (observer.DisposalWait is { } drain) await drain;
            lock (_gate) _observers.Remove(observer);
            observer.Drained.TrySetResult();
        }
    }

    public async ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _queries);
        if (QueryFailure is { } failure) throw failure;
        SunderRpcProviderSnapshot? provider;
        lock (_gate) _providers.TryGetValue(endpoint.Value, out provider);
        if (QueryWait is { } wait) await wait(cancellationToken);
        return provider;
    }

    public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reports.Add((endpoint, exception));
        if (ReportFailure is { } failure) throw failure;
        return ValueTask.FromResult(true);
    }

    public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = new Scope(this, cancellationToken);
        Scopes.Add(scope);
        return ValueTask.FromResult<ISunderRpcCallScope>(scope);
    }

    public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId,
        JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        => Invoke is { } call ? call(request, cancellationToken) : ValueTask.FromResult(request);

    public async IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId,
        string methodId, JsonElement request, SunderRpcCallOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return await InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
    }

    internal sealed class Observation(long revision, long sequence)
    {
        internal long Revision { get; } = revision;
        internal long Sequence { get; } = sequence;
        internal Channel<Signal> Events { get; } = Channel.CreateUnbounded<Signal>();
        internal Task? DisposalWait { get; set; }
        internal TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed record Signal(SunderRpcCatalogEvent? Change, Exception? Error);

    internal sealed class Scope(ServiceBindingTestClient parent, CancellationToken creationToken) : ISunderRpcCallScope
    {
        private int _disposals;
        internal int Disposals => Volatile.Read(ref _disposals);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset DeadlineUtc { get; } = DateTimeOffset.UtcNow.AddMinutes(5);
        public ValueTask DisposeAsync() { Interlocked.Increment(ref _disposals); Disposed.TrySetResult(); return ValueTask.CompletedTask; }
        public async ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
        {
            var provider = await parent.GetProviderAsync(endpoint, cancellationToken);
            return parent.ScopedProviderTransform is { } transform ? transform(provider) : provider;
        }
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
            => parent.TryReportInvariantViolationAsync(endpoint, exception, cancellationToken);
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
            => parent.DiscoverAsync(contractId, cancellationToken);
        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
            => parent.WatchAsync(afterRevision, afterSequence, cancellationToken);
        public async ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId,
            JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(creationToken, cancellationToken);
            return await parent.InvokeAsync(endpoint, serviceId, methodId, request, options, linked.Token);
        }
        public async IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId,
            string methodId, JsonElement request, SunderRpcCallOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return await InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
        }
        public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source,
            SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string filePath,
            SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Disposals != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            creationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3]));
        }
    }
}
