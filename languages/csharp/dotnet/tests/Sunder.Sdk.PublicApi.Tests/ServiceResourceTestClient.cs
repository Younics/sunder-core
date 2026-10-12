using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.PublicApi.Tests;

internal sealed class ServiceResourceTestClient : ISunderRpcClient
{
    internal static readonly SunderRpcContractDescriptor Contract = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
        {"descriptorVersion":1,"schemaProfile":"sunder.rpc.values.v1","contractId":"test.resource",
         "version":"1.0.0","errors":{},"services":{"resource":{"methods":{"get":{
         "kind":"unary","requestSchema":{"$ref":"#/$defs/Value"},"responseSchema":{"$ref":"#/$defs/Value"},
         "idempotency":"none","errors":[]}}}},
         "$defs":{"Value":{"type":"object","properties":{},"required":[],"additionalProperties":false}}}
        """));

    internal static SunderRpcProviderSnapshot Provider => new("test.package", "1.0.0", "test.provider",
        Contract.ContractId, Contract.Version, Contract.Sha256, Contract.AbiSha256,
        Guid.Parse("12345678-1234-1234-1234-123456789abc"), 3, 7, new("exact-provider"), 11, SunderRpcProviderState.Active);

    internal SunderRpcProviderSnapshot? Visible { get; set; } = Provider;
    internal List<Scope> Scopes { get; } = [];
    internal DateTimeOffset Deadline { get; set; } = DateTimeOffset.UtcNow.AddMinutes(5);
    internal int Resolutions { get; private set; }
    internal TaskCompletionSource? OpenEntered { get; set; }
    internal bool BlockOpen { get; set; }

    public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = new Scope(this, options?.DeadlineUtc ?? Deadline);
        Scopes.Add(scope);
        return ValueTask.FromResult<ISunderRpcCallScope>(scope);
    }

    public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Resolutions++;
        return ValueTask.FromResult(Visible);
    }

    public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new SunderRpcCatalogSnapshot(Math.Max(11, Visible?.CatalogRevision ?? 0),
            Math.Max(11, Visible?.CatalogRevision ?? 0), Visible is { } value && value.ContractId == contractId ? [value] : []));
    public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }
    public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId,
        JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(request);
    public async IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId,
        string methodId, JsonElement request, SunderRpcCallOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield return request;
    }

    internal static SunderRpcContentReference Content(string id = "content")
        => new(id, 3, new string('a', 64), "text/plain", "value.txt", DateTimeOffset.UtcNow.AddMinutes(5),
            SunderRpcContentRepeatability.SingleUse);

    internal static JsonElement ContentJson(string id = "content")
        => JsonSerializer.SerializeToElement(Content(id), new JsonSerializerOptions { Converters = { new SunderRpcJsonConverterFactory() } });

    internal sealed class Scope(ServiceResourceTestClient parent, DateTimeOffset deadline) : ISunderRpcCallScope
    {
        private int _disposals;
        internal int Disposals => Volatile.Read(ref _disposals);
        internal Action? Disposing { get; set; }
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<(SunderRpcEndpointReference Target, byte[] Bytes)> Registrations { get; } = [];
        internal List<TrackingStream> Reads { get; } = [];
        public DateTimeOffset DeadlineUtc => deadline;

        public async ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint,
            Stream source, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await source.CopyToAsync(copy, cancellationToken);
            Registrations.Add((endpoint, copy.ToArray()));
            return Content("registered-" + Registrations.Count);
        }
        public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint,
            string filePath, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public async ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Disposals != 0, this);
            parent.OpenEntered?.TrySetResult();
            if (parent.BlockOpen) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var stream = new TrackingStream([1, 2, 3]);
            Reads.Add(stream);
            return stream;
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref _disposals); Disposing?.Invoke(); Disposed.TrySetResult(); return ValueTask.CompletedTask; }
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default) => parent.GetProviderAsync(endpoint, cancellationToken);
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception,
            CancellationToken cancellationToken = default) => parent.TryReportInvariantViolationAsync(endpoint, exception, cancellationToken);
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
            => parent.DiscoverAsync(contractId, cancellationToken);
        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence,
            CancellationToken cancellationToken = default) => parent.WatchAsync(afterRevision, afterSequence, cancellationToken);
        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId,
            JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => parent.InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId,
            JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => parent.SubscribeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
    }

    internal sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        private int _disposals;
        internal int Disposals => Volatile.Read(ref _disposals);
        protected override void Dispose(bool disposing) { if (disposing) Interlocked.Increment(ref _disposals); base.Dispose(disposing); }
    }
}
