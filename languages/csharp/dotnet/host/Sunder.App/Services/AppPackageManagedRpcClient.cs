using System.Text.Json;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.App.Services;

// Stable App activation, replaceable Host connection. Ordinary scopes/streams
// capture the inner client; cleanup additionally binds them to its bounded authority.
internal sealed partial class AppPackageManagedRpcClient(ISunderRpcClient initial,
    Func<PackageUiSnapshotDescriptor, CancellationToken, Task<ISunderRpcClient>> open,
    AppPackageGenerationPublication publication) : ISunderRpcClient, IAsyncDisposable
{
    private ISunderRpcClient _current = initial;
    private readonly List<Task> _retirements = [];
    private readonly AsyncLocal<AppPackageRpcCleanupAuthority?> _cleanup = new();
    private AppPackageRpcCleanupAuthority? _cleanupObservation;
    private bool _disposed;
    public SunderRpcResourceProfile? ResourceProfile
    {
        get
        {
            lock (_retirements)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _current.ResourceProfile; // Negotiated metadata is safe during composition.
            }
        }
    }
    private ISunderRpcClient Current
        => GetClient(allowCleanupObservation: false);

    private ISunderRpcClient GetClient(bool allowCleanupObservation)
    {
        lock (_retirements)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cleanup.Value is { IsActive: true } cleanup) return cleanup.Client;
            // Generated bindings have a pre-existing, flow-suppressed catalog observer.
            // It may refresh metadata for this exact retiring client, never dispatch
            // business work or borrow another activation's cleanup authority.
            if (allowCleanupObservation && _cleanupObservation is { IsActive: true } observation) return observation.Client;
            publication.RequireRuntimeAccess("rpc");
            return _current;
        }
    }
    internal async Task<AppPackageRpcReplacement> PrepareAsync(PackageUiSnapshotDescriptor source, CancellationToken token)
        => new(this, await open(source, token).ConfigureAwait(false));
    internal void Commit(ISunderRpcClient replacement)
    {
        lock (_retirements)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var previous = _current;
            _current = replacement;
            _retirements.RemoveAll(task => task.IsCompletedSuccessfully);
            _retirements.Add(DisposeClientAsync(previous));
        }
    }
    internal static async Task DisposeClientAsync(ISunderRpcClient client)
    {
        if (client is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
    }

    // Only the host's exact-owner disposal path may acknowledge outstanding work
    // after ordinary access is fenced. This captures the existing session; it cannot
    // reopen/retarget it or override Runtime generation/policy authorization.
    internal IDisposable BeginCleanup(TimeSpan? budget = null)
    {
        var limit = budget ?? AppShutdownBudgets.GenerationRetirement;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limit, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, AppShutdownBudgets.GenerationRetirement);
        lock (_retirements)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var previous = _cleanup.Value;
            var authority = new AppPackageRpcCleanupAuthority(_current, limit, _cleanup);
            _cleanup.Value = authority;
            _cleanupObservation = authority;
            return new CleanupScope(this, authority, previous);
        }
    }

    private sealed class CleanupScope(AppPackageManagedRpcClient owner, AppPackageRpcCleanupAuthority authority,
        AppPackageRpcCleanupAuthority? previous) : IDisposable
    {
        public void Dispose()
        {
            // Captured ExecutionContexts retain this object, never a reusable permit.
            authority.Complete();
            lock (owner._retirements)
            {
                if (ReferenceEquals(owner._cleanupObservation, authority))
                    owner._cleanupObservation = previous is { IsActive: true } ? previous : null;
            }
            if (ReferenceEquals(owner._cleanup.Value, authority)) owner._cleanup.Value = previous;
        }
    }
    public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        => Current.CreateCallScopeAsync(options, cancellationToken);
    public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
        => ReadMetadataAsync(GetClient(allowCleanupObservation: true),
            (client, token) => client.GetProviderAsync(endpoint, token), cancellationToken);
    public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
        => Current.TryReportInvariantViolationAsync(endpoint, exception, cancellationToken);
    public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
        => ReadMetadataAsync(GetClient(allowCleanupObservation: true),
            (client, token) => client.DiscoverAsync(contractId, token), cancellationToken);

    private async ValueTask<T> ReadMetadataAsync<T>(ISunderRpcClient client,
        Func<ISunderRpcClient, CancellationToken, ValueTask<T>> read, CancellationToken token)
    {
        try { return await read(client, token).ConfigureAwait(false); }
        catch (Exception error) when (IsMetadataHandoffFailure(error))
        {
            token.ThrowIfCancellationRequested();
            if (client is AppPackageCleanupRpcClient) throw;
            // Retry a read only against a proved, committed replacement. Do not
            // wait for an imagined future handoff, fabricate cached metadata, or
            // retarget an exact cleanup scope. GetClient rechecks publication and
            // original cleanup authority, using the caller's unchanged token.
            var replacement = GetClient(allowCleanupObservation: true);
            if (ReferenceEquals(client, replacement) || replacement is AppPackageCleanupRpcClient) throw;
            return await read(replacement, token).ConfigureAwait(false);
        }
    }

    private static bool IsMetadataHandoffFailure(Exception error)
        => error is ObjectDisposedException or SunderRpcException
        {
            Error:
            {
                DispatchState: SunderRpcDispatchState.NotDispatched,
                RetrySafety: SunderRpcRetrySafety.Safe,
            } and ({ Kind: SunderRpcErrorKind.StaleEndpoint, Code: "rpc.caller-retired" }
                or { Kind: SunderRpcErrorKind.Unavailable, Code: "rpc.app-session.retired" }),
        };
    public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
        => GetClient(allowCleanupObservation: true).WatchAsync(afterRevision, afterSequence, cancellationToken);
    public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        => Current.InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
    public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        => Current.SubscribeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_retirements)
        {
            if (_disposed) return;
            _disposed = true;
            _cleanupObservation?.Complete();
            _cleanupObservation = null;
            tasks = _retirements.Append(DisposeClientAsync(_current)).ToArray();
            _retirements.Clear();
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

internal sealed class AppPackageRpcReplacement(AppPackageManagedRpcClient owner, ISunderRpcClient replacement) : IAsyncDisposable
{
    private ISunderRpcClient? _replacement = replacement;
    internal void Commit()
    {
        var client = _replacement ?? throw new InvalidOperationException("RPC replacement already consumed.");
        owner.Commit(client);
        _replacement = null;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _replacement, null) is { } client)
            await AppPackageManagedRpcClient.DisposeClientAsync(client).ConfigureAwait(false);
    }
}
