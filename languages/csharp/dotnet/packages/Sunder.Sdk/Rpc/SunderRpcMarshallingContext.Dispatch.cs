using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Sunder.Sdk.Rpc;

public sealed partial class SunderRpcMarshallingContext
{
    // Only Client is a stage-aware view. Receiver bindings, content operations and
    // the shared owner's disposal retain the original scope/client identities.
    private sealed class OperationScope(SunderRpcMarshallingContext context, ISunderRpcCallScope scope,
        SunderRpcEndpointReference target) : ISunderRpcCallScope, ISunderRpcGeneratedOperationScope
    {
        private readonly SunderRpcDispatchOperation _evidence = new();
        private SunderRpcProviderSnapshot? _expected;
        private bool BeforeInvoke => _evidence.DispatchEpoch == 0 && _evidence.IsUnary;
        public DateTimeOffset DeadlineUtc
        {
            get
            {
                try { return scope.DeadlineUtc; }
                catch (Exception error) { ExceptionDispatchInfo.Capture(Normalize(error)).Throw(); throw; }
            }
        }
        public SunderRpcResourceProfile? ResourceProfile
        {
            get
            {
                try { return scope.ResourceProfile; }
                catch (Exception error) { ExceptionDispatchInfo.Capture(Normalize(error)).Throw(); throw; }
            }
        }

        internal void ThrowIfCancelled(CancellationToken token)
        {
            if (BeforeInvoke) SunderRpcDispatchEvidence.ThrowIfCancelled(token, _evidence);
            else token.ThrowIfCancellationRequested();
        }

        internal Exception Normalize(Exception error, bool authenticatedTransportRead = false)
        {
            // Observation proof is unary-only. Preserve existing stream transport
            // errors and identity, but never forward another unary call's proof.
            if (!_evidence.IsUnary) return SunderRpcDispatchEvidence.WithoutProof(error);
            if (!BeforeInvoke) return SunderRpcDispatchEvidence.AfterDispatch(error);
            if (SunderRpcDispatchEvidence.IsFor(error, _evidence)) return error;
            if (authenticatedTransportRead && scope is ISunderRpcTrustedTransportScope && error is SunderRpcException { IsHostAuthenticated: true } rpc
                && rpc.Error.Kind != SunderRpcErrorKind.Domain && Enum.IsDefined(rpc.Error.Kind)
                && rpc.Error.DispatchState == SunderRpcDispatchState.NotDispatched && rpc.Error.RetrySafety == SunderRpcRetrySafety.Safe)
                return SunderRpcDispatchEvidence.Issue(rpc, _evidence);
            return SunderRpcDispatchEvidence.WithoutProof(error);
        }

        private SunderRpcException Stale()
        {
            var stale = SunderRpcServiceLocator.Stale();
            return BeforeInvoke ? SunderRpcDispatchEvidence.Issue(stale, _evidence)
                : stale.WithDispatchMetadata(SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe);
        }

        async ValueTask ISunderRpcGeneratedOperationScope.EnsureCurrentAsync(SunderRpcProviderSnapshot provider, CancellationToken token)
        {
            context.Check();
            ThrowIfCancelled(token);
            if (provider.State != SunderRpcProviderState.Active) throw Stale();
            SunderRpcServiceLocator.Validate(provider);
            if (provider.Endpoint != target) throw SunderRpcServiceLocator.Stale();
            _expected = provider;
            var current = await GetProviderAsync(provider.Endpoint, token).ConfigureAwait(false);
            ThrowIfCancelled(token);
            if (current is null || current.State != SunderRpcProviderState.Active || !SunderRpcServiceLocator.SameIdentity(provider, current))
                throw Stale();
            SunderRpcServiceLocator.Validate(current);
        }

        private async ValueTask<T> ReadAsync<T>(Func<ValueTask<T>> read)
        {
            try { return await read().ConfigureAwait(false); }
            catch (Exception error)
            {
                var normalized = Normalize(error, authenticatedTransportRead: true);
                if (!ReferenceEquals(normalized, error)) ExceptionDispatchInfo.Capture(normalized).Throw();
                throw;
            }
        }

        public async ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
        {
            var current = await ReadAsync(() => scope.GetProviderAsync(endpoint, cancellationToken)).ConfigureAwait(false);
            // The emitter's post-Invoke comparison runs outside this class. Reject
            // a changed locator here first so its local public-constructor stale
            // exception cannot accidentally describe post-dispatch failure as safe.
            if (!BeforeInvoke && _expected is { } expected && endpoint == target
                && (current is null || current.State != SunderRpcProviderState.Active || !SunderRpcServiceLocator.SameIdentity(expected, current)))
                throw Stale();
            return current;
        }

        public async ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId,
            JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            context.Check();
            ThrowIfCancelled(cancellationToken);
            var first = _evidence.BeginInvoke() == 1;
            try { return await scope.InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken).ConfigureAwait(false); }
            catch (Exception error)
            {
                Exception normalized;
                if (first && _evidence.DispatchEpoch == 1 && _evidence.IsUnary && endpoint == target && scope is ISunderRpcTrustedDispatchMetadataScope
                    && error is SunderRpcException { IsHostAuthenticated: true } rpc && Enum.IsDefined(rpc.Error.Kind)
                    && Enum.IsDefined(rpc.Error.DispatchState) && Enum.IsDefined(rpc.Error.RetrySafety))
                {
                    // These flags describe the actual Invoke, not a later
                    // metadata request. Preserve even a non-qualifying Host
                    // decision (for example idempotent PossiblyDispatched/Safe).
                    normalized = scope is ISunderRpcTrustedTransportScope && rpc.Error.Kind != SunderRpcErrorKind.Domain
                        && rpc.Error.DispatchState == SunderRpcDispatchState.NotDispatched && rpc.Error.RetrySafety == SunderRpcRetrySafety.Safe
                        ? SunderRpcDispatchEvidence.Issue(rpc, _evidence, expectedEpoch: 1)
                        : SunderRpcDispatchEvidence.WithoutProof(rpc);
                }
                else normalized = SunderRpcDispatchEvidence.AfterDispatch(error);
                if (!ReferenceEquals(normalized, error)) ExceptionDispatchInfo.Capture(normalized).Throw();
                throw;
            }
        }

        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId,
            JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            _evidence.BeginStream(); // The public predicate is deliberately a unary guarantee.
            return ReadStream(() => scope.SubscribeAsync(endpoint, serviceId, methodId, request, options, cancellationToken),
                cancellationToken, dispatch: true);
        }

        private async IAsyncEnumerable<T> ReadStream<T>(Func<IAsyncEnumerable<T>> open,
            [EnumeratorCancellation] CancellationToken token, bool dispatch = false)
        {
            if (dispatch) { context.Check(); _evidence.BeginInvoke(); }
            IAsyncEnumerator<T> iterator;
            try { iterator = open().GetAsyncEnumerator(token); }
            catch (Exception error) { ExceptionDispatchInfo.Capture(Normalize(error)).Throw(); throw; }
            try
            {
                while (true)
                {
                    bool next;
                    try { next = await iterator.MoveNextAsync().ConfigureAwait(false); }
                    catch (Exception error) { ExceptionDispatchInfo.Capture(Normalize(error)).Throw(); throw; }
                    if (!next) yield break;
                    T current;
                    try { current = iterator.Current; }
                    catch (Exception error) { ExceptionDispatchInfo.Capture(Normalize(error)).Throw(); throw; }
                    yield return current;
                }
            }
            finally
            {
                try { await iterator.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { ExceptionDispatchInfo.Capture(Normalize(error)).Throw(); }
            }
        }

        public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => ReadAsync(() => scope.CreateCallScopeAsync(options, cancellationToken));
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
            => ReadAsync(() => scope.TryReportInvariantViolationAsync(endpoint, exception, cancellationToken));
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
            => ReadAsync(() => scope.DiscoverAsync(contractId, cancellationToken));
        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
            => ReadStream(() => scope.WatchAsync(afterRevision, afterSequence, cancellationToken), cancellationToken);
        public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source,
            SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)
            => ReadAsync(() => scope.RegisterContentAsync(endpoint, source, options, cancellationToken));
        public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string filePath,
            SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)
            => ReadAsync(() => scope.RegisterContentFileAsync(endpoint, filePath, options, cancellationToken));
        public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default)
            => ReadAsync(() => scope.OpenContentAsync(reference, cancellationToken));
        public async ValueTask DisposeAsync()
        {
            try { await scope.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { ExceptionDispatchInfo.Capture(SunderRpcDispatchEvidence.AfterDispatch(error)).Throw(); }
        }
    }
}
