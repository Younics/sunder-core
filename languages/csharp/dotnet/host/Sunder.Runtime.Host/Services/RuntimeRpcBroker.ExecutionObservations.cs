using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class RuntimeRpcBroker
{
    // This is Host-internal authority for metadata IO only. It never enters a provider
    // handler or exports a caller/cleanup permit to a package or a replacement activation.
    internal async Task<T> WithAppExecutionObservationAsync<T>(
        string sessionId, string? callScopeId, RuntimeRpcProviderDescriptor? expectedProvider,
        Func<string, RuntimeExecutionReservationIdentity?, CancellationToken, Task<T>> read,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var caller = GetAppCaller(sessionId);
        using var sessionLease = AcquireSessionLease(caller);
        EnsureCurrent(caller, sessionLease);
        var scope = callScopeId is null ? null : GetScope(caller, callScopeId);
        EnsureScope(scope, caller, sessionLease);
        using var callerLease = AcquireCallerLease(caller);
        RuntimeRpcProviderActivation? provider = null;
        RuntimeRpcActivationLease? providerLease = null;
        RuntimeRpcPermissionLease? permission = null;
        try
        {
            RuntimeExecutionReservationIdentity? reservation = null;
            if (expectedProvider is not null)
            {
                var endpoint = new SunderRpcEndpointReference(expectedProvider.EndpointReference);
                var found = TryGetEndpoint(caller, endpoint, out provider, out var stale) && provider is not null;
                ThrowAcquisitionFailure(RuntimeRpcCallAcquisitionPolicy.EvaluateEndpoint(found, stale).Failure);
                var actual = provider!.Snapshot;
                if (actual.State != SunderRpcProviderState.Active || !SameExecutionCaptureProvider(expectedProvider, actual))
                    throw StaleEndpoint("rpc.execution.provider-stale", "The execution capture provider locator is no longer exact and active.");
                if (caller.PackageId != actual.PackageId)
                    throw PermissionDenied(actual.ContractId, "observe-own-execution");
                var loaded = _sessions.GetLoadedPackage(sessionLease, actual.PackageId);
                ThrowAcquisitionFailure(RuntimeRpcCallAcquisitionPolicy.ValidateProviderGeneration(
                    loaded?.RuntimeActivationId, actual, provider.Owner.BindingGeneration).Failure);
                if (provider.Registration.Manifest?.Role != SunderPackageFormat.RuntimeHostRole
                    || loaded?.Worker is not ProcessRuntimeWorker worker
                    || !ReferenceEquals(worker, provider.Owner.Package.Worker))
                    throw Unavailable("rpc.execution.unsupported-worker", "The selected provider has no owned process execution observation.");
                if (!CanUseContract(caller, provider, SunderRpcProtocol.DiscoverAction, out _))
                    throw PermissionDenied(actual.ContractId, SunderRpcProtocol.DiscoverAction);
                permission = AcquirePermission(caller, provider, SunderRpcProtocol.DiscoverAction);
                if (!provider.TryAcquireCallee(_policy.MaxConcurrentObservationsPerCallee, out providerLease, observation: true)
                    || providerLease is null)
                    throw ResourceExhausted("rpc.provider.observation-limit", "The provider observation limit was reached or its activation retired.");
                reservation = worker.ExecutionReservationIdentity;
            }

            var now = _timeProvider.GetUtcNow();
            var duration = TimeSpan.FromSeconds(30);
            if (_policy.DefaultDeadline < duration) duration = _policy.DefaultDeadline;
            if (scope is not null && scope.DeadlineUtc - now < duration) duration = scope.DeadlineUtc - now;
            if (CurrentCall.Value is { } parent && parent.DeadlineUtc - now < duration) duration = parent.DeadlineUtc - now;
            if (duration <= TimeSpan.Zero) throw DeadlineExceeded();
            using var deadline = new CancellationTokenSource(duration, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource([
                token, _hostStopping, sessionLease.RetirementToken, sessionLease.GenerationRetirementToken,
                callerLease.RetirementToken, providerLease?.RetirementToken ?? CancellationToken.None,
                permission?.RevocationToken ?? CancellationToken.None, scope?.RevocationToken ?? CancellationToken.None,
                deadline.Token,
            ]);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                var result = await read(caller.PackageId, reservation, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                EnsureCurrent(caller, sessionLease);
                EnsureScope(scope, caller, sessionLease);
                if (provider is not null)
                {
                    if (!TryGetEndpoint(caller, provider.Snapshot.Endpoint, out var current, out _)
                        || !ReferenceEquals(current, provider) || provider.Snapshot.State != SunderRpcProviderState.Active
                        || !SameExecutionCaptureProvider(expectedProvider!, provider.Snapshot))
                        throw StaleEndpoint("rpc.execution.provider-stale", "The provider retired while its execution reference was captured.");
                }
                return result;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && (deadline.IsCancellationRequested
                || scope is not null && _timeProvider.GetUtcNow() >= scope.DeadlineUtc))
            { throw DeadlineExceeded(); }
        }
        finally
        {
            permission?.Dispose();
            providerLease?.Dispose();
        }
    }

    // Match the existing eleven-field service locator. Catalog revision, the caller's
    // supplied display state and fault text cannot alter execution identity or authority.
    private static bool SameExecutionCaptureProvider(RuntimeRpcProviderDescriptor expected, SunderRpcProviderSnapshot actual)
        => expected.PackageId == actual.PackageId && expected.PackageVersion == actual.PackageVersion
            && expected.ProviderId == actual.ProviderId && expected.ContractId == actual.ContractId
            && expected.ContractVersion == actual.ContractVersion && expected.DescriptorSha256 == actual.DescriptorSha256
            && expected.AbiSha256 == actual.AbiSha256 && expected.ActivationId == actual.ActivationId
            && expected.ActivationEpoch == actual.ActivationEpoch && expected.SessionGeneration == actual.SessionGeneration
            && expected.EndpointReference == actual.Endpoint.Value;
}
