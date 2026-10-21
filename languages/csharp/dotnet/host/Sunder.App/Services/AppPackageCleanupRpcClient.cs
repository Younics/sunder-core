using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.App.Services;

// Used only by BeginCleanup. Every issued object borrows the same exact client and
// authority, including when its first operation is deferred until after disposal.
internal class AppPackageCleanupRpcClient(ISunderRpcClient client, AppPackageRpcCleanupAuthority authority) : ISunderRpcClient
{
    public SunderRpcResourceProfile? ResourceProfile => client.ResourceProfile;
    protected AppPackageRpcCleanupAuthority Authority => authority;
    protected virtual void RequireAccess(CancellationToken caller) => authority.RequireActive(caller);
    protected void RequireBusinessAccess(CancellationToken caller)
    {
        RequireAccess(caller);
        authority.RequireAmbient(caller);
    }

    protected async ValueTask<T> RunAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken caller, bool observation = false)
    {
        if (observation) RequireAccess(caller); else RequireBusinessAccess(caller);
        using var linked = authority.BeginOperation(caller);
        var result = await operation(linked.Token).ConfigureAwait(false);
        if (observation) RequireAccess(caller); else RequireBusinessAccess(caller);
        return result;
    }

    public async ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
    {
        RequireBusinessAccess(cancellationToken);
        using var linked = authority.BeginOperation(cancellationToken);
        var bounded = authority.Clamp(options);
        var scope = await client.CreateCallScopeAsync(bounded, linked.Token).ConfigureAwait(false);
        try
        {
            RequireBusinessAccess(cancellationToken);
            return new CleanupCallScope(scope, authority, bounded.DeadlineUtc!.Value);
        }
        catch (Exception rejected)
        {
            // A transport may deliver an acquired resource despite cancellation.
            // Close that exact resource before reporting rejection; never leak it.
            try { await scope.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Failed to close a late App RPC cleanup scope.", rejected, cleanupFailure);
            }
            throw;
        }
    }

    public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
        => RunAsync(token => client.GetProviderAsync(endpoint, token), cancellationToken, observation: true);
    public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
        => RunAsync(token => client.TryReportInvariantViolationAsync(endpoint, exception, token), cancellationToken);
    public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
        => RunAsync(token => client.DiscoverAsync(contractId, token), cancellationToken, observation: true);
    public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        => RunAsync(token => client.InvokeAsync(endpoint, serviceId, methodId, request, authority.Clamp(options), token), cancellationToken);

    public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
    {
        RequireAccess(cancellationToken);
        return ReadAsync(token => client.WatchAsync(afterRevision, afterSequence, token), observation: true, cancellationToken);
    }

    public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
    {
        RequireBusinessAccess(cancellationToken);
        var bounded = authority.Clamp(options);
        return ReadAsync(token => client.SubscribeAsync(endpoint, serviceId, methodId, request, bounded, token), observation: false, cancellationToken);
    }

    private async IAsyncEnumerable<T> ReadAsync<T>(Func<CancellationToken, IAsyncEnumerable<T>> source,
        bool observation, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (observation) RequireAccess(cancellationToken); else RequireBusinessAccess(cancellationToken);
        using var linked = authority.BeginOperation(cancellationToken);
        await using var enumerator = source(linked.Token).GetAsyncEnumerator(linked.Token);
        while (true)
        {
            if (observation) RequireAccess(cancellationToken); else RequireBusinessAccess(cancellationToken);
            var moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            if (observation) RequireAccess(cancellationToken); else RequireBusinessAccess(cancellationToken);
            if (!moved) yield break;
            yield return enumerator.Current;
        }
    }

    private sealed class CleanupCallScope(ISunderRpcCallScope scope, AppPackageRpcCleanupAuthority authority, DateTimeOffset deadline)
        : AppPackageCleanupRpcClient(scope, authority), ISunderRpcCallScope
    {
        private readonly object _disposeGate = new();
        private Task? _disposal;
        private int _disposed;
        public DateTimeOffset DeadlineUtc => scope.DeadlineUtc < deadline ? scope.DeadlineUtc : deadline;

        protected override void RequireAccess(CancellationToken caller)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            base.RequireAccess(caller);
        }

        public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source,
            SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)
            => RunAsync(token => scope.RegisterContentAsync(endpoint, source, options, token), cancellationToken);
        public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string filePath,
            SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)
            => RunAsync(token => scope.RegisterContentFileAsync(endpoint, filePath, options, token), cancellationToken);

        public async ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default)
        {
            RequireBusinessAccess(cancellationToken);
            using var linked = Authority.BeginOperation(cancellationToken);
            var stream = await scope.OpenContentAsync(reference, linked.Token).ConfigureAwait(false);
            try
            {
                RequireBusinessAccess(cancellationToken);
                return new AppPackageCleanupContentStream(stream, Authority, RequireBusinessAccess);
            }
            catch (Exception rejected)
            {
                try { await stream.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException("Failed to close late App RPC cleanup content.", rejected, cleanupFailure);
                }
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                Interlocked.Exchange(ref _disposed, 1);
                return new(_disposal ??= CloseAsync());
            }
        }

        // Closing borrowed resources remains legal after the authority expires.
        private async Task CloseAsync() => await scope.DisposeAsync().ConfigureAwait(false);
    }
}
