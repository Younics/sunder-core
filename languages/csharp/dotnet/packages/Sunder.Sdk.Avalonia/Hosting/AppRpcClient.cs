using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Avalonia.Hosting;

internal class AppRpcClient(AppChannel channel) : ISunderRpcClient
{
    public SunderRpcResourceProfile? ResourceProfile => channel.Get<SunderRpcResourceProfile?>("rpc.profile");
    public async ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        => new Scope(await channel.Open("rpc.scope", new { options }, cancellationToken).ConfigureAwait(false));
    public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
        => channel.Call<SunderRpcProviderSnapshot?>("rpc.provider", new { endpoint }, cancellationToken);
    public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
        => channel.Call<bool>("rpc.report", new { endpoint, message = exception.Message }, cancellationToken);
    public async ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
    {
        var snapshot = await channel.Call<CatalogSnapshot>("rpc.discover", new { contractId }, cancellationToken).ConfigureAwait(false);
        return new(snapshot.Revision, snapshot.Sequence, snapshot.Providers, snapshot.ResetRequired);
    }
    public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
        => channel.Watch<SunderRpcCatalogEvent>("rpc.watch", new { afterRevision, afterSequence }, cancellationToken);
    public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        => channel.Call<JsonElement>("rpc.invoke", new { endpoint, serviceId, methodId, request, options }, cancellationToken);
    public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        => channel.Watch<JsonElement>("rpc.subscribe", new { endpoint, serviceId, methodId, request, options }, cancellationToken);

    // The immutable SDK catalog copies an IEnumerable into an IReadOnlyList;
    // reconstruct it from the wire shape instead of binding its constructor to JSON.
    private sealed record CatalogSnapshot(long Revision, long Sequence, SunderRpcProviderSnapshot[] Providers, bool ResetRequired);

    private sealed class Scope : AppRpcClient, ISunderRpcCallScope, ISunderRpcTrustedTransportScope
    {
        private readonly AppChannel channel;
        internal Scope(AppChannel channel) : base(channel) => this.channel = channel;
        public DateTimeOffset DeadlineUtc => channel.Get<DateTimeOffset>("rpc.deadline");
        public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)
            => channel.Write<SunderRpcContentReference>("rpc.content", new { endpoint, options }, source, cancellationToken);
        public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string filePath, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)
            => channel.Call<SunderRpcContentReference>("rpc.content-file", new { endpoint, filePath, options }, cancellationToken);
        public async ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default)
            => await channel.Read("rpc.content", new { reference }, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The App returned no RPC content stream.");
        public ValueTask DisposeAsync() => channel.DisposeAsync();
    }
}
