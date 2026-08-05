using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeRpcBroker
{
    internal const string InvariantViolationFaultCode = "rpc.provider.invariant-violation";
    private const string InvariantReporterPackageId = "sunder.package.agent";
    private static readonly AsyncLocal<RuntimeRpcCallFrame?> CurrentCall = new();
    private readonly RuntimeRpcCatalog _catalog;
    private readonly RuntimeRpcPermissionStore _permissions;
    private readonly PackageSessionState _sessions;
    private readonly RuntimeSessionOwner _sessionOwner;
    private readonly RuntimeRpcPolicyOptions _policy;
    private readonly RuntimeRpcAppSessionManager? _appSessions;
    private readonly RuntimeContentTransferStore? _contentStore;
    private readonly RuntimeTransportPolicyOptions _transportPolicy;
    private readonly RuntimeRpcHostCallerActivation _stackHostCaller;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _hostStopping;
    private readonly ConcurrentDictionary<string, RuntimeRpcCallerScopeState> _callScopes = new(StringComparer.Ordinal);
    private readonly object _callScopeGate = new();

    public RuntimeRpcBroker(
        RuntimeRpcCatalog catalog,
        RuntimeRpcPermissionStore permissions,
        PackageSessionState sessions,
        RuntimeSessionOwner sessionOwner,
        RuntimeRpcPolicyOptions? policy = null,
        TimeProvider? timeProvider = null,
        IHostApplicationLifetime? hostLifetime = null,
        RuntimeRpcAppSessionManager? appSessions = null,
        RuntimeContentTransferStore? contentStore = null,
        RuntimeTransportPolicyOptions? transportPolicy = null)
        : this(
            catalog,
            permissions,
            sessions,
            sessionOwner,
            policy,
            timeProvider,
            hostLifetime?.ApplicationStopping ?? CancellationToken.None,
            appSessions,
            contentStore,
            transportPolicy)
    {
    }

    internal RuntimeRpcBroker(
        RuntimeRpcCatalog catalog,
        RuntimeRpcPermissionStore permissions,
        PackageSessionState sessions,
        RuntimeSessionOwner sessionOwner,
        RuntimeRpcPolicyOptions? policy,
        TimeProvider? timeProvider,
        CancellationToken hostStopping,
        RuntimeRpcAppSessionManager? appSessions = null,
        RuntimeContentTransferStore? contentStore = null,
        RuntimeTransportPolicyOptions? transportPolicy = null)
    {
        _catalog = catalog;
        _permissions = permissions;
        _sessions = sessions;
        _sessionOwner = sessionOwner;
        _policy = policy ?? new RuntimeRpcPolicyOptions();
        _appSessions = appSessions;
        _contentStore = contentStore;
        _transportPolicy = transportPolicy ?? new RuntimeTransportPolicyOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _hostStopping = hostStopping;
        _stackHostCaller = new RuntimeRpcHostCallerActivation(
            SunderStackContributorRpc.Descriptor,
            hostStopping);
    }

    public ISunderRpcClient CreateStackHostClient()
        => new RuntimeRpcHostClient(this, _stackHostCaller);

    public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(
        RuntimeRpcCallerStamp callerStamp,
        SunderRpcCallOptions? options,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<ISunderRpcCallScope>(new RuntimeRpcCallScope(
            this,
            CreateCallScope(GetCaller(callerStamp), options, requirePermission: true, cancellationToken)));

    internal ValueTask<ISunderRpcCallScope> CreateHostCallScopeAsync(
        RuntimeRpcHostCallerActivation caller,
        SunderRpcCallOptions? options,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<ISunderRpcCallScope>(new RuntimeRpcCallScope(
            this,
            CreateCallScope(caller, options, requirePermission: false, cancellationToken)));

    public RuntimeRpcCallerScopeState CreateAppCallScope(
        string appSessionId,
        SunderRpcCallOptions? options,
        CancellationToken cancellationToken)
        => CreateCallScope(GetAppCaller(appSessionId), options, requirePermission: true, cancellationToken);

    public RuntimeRpcCallerScopeState GetAppCallScope(string appSessionId, string callScopeId)
        => GetScope(GetAppCaller(appSessionId), callScopeId);

    public ValueTask CloseAppCallScopeAsync(
        string appSessionId,
        string callScopeId)
        => CloseCallScopeAsync(GetScope(GetAppCaller(appSessionId), callScopeId));

    private RuntimeRpcCallerScopeState CreateCallScope(
        IRuntimeRpcCallerActivation caller,
        SunderRpcCallOptions? options,
        bool requirePermission,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var sessionLease = _sessions.AcquireLease();
        EnsureCurrent(caller, sessionLease);
        var now = _timeProvider.GetUtcNow();
        var deadline = GetEffectiveDeadline(options, CurrentCall.Value, now);
        if (deadline <= now) throw DeadlineExceeded();
        var scope = new RuntimeRpcCallerScopeState(
            "rpc-scope-" + Guid.NewGuid().ToString("N"),
            caller,
            sessionLease.Generation,
            deadline,
            requirePermission,
            _hostStopping);
        lock (_callScopeGate)
        {
            if (_callScopes.Values.Count(item => ReferenceEquals(item.Caller, caller)) >= _policy.MaxCallScopesPerCaller)
            {
                scope.Revoke();
                throw ResourceExhausted(
                    "rpc.scope.limit",
                    "The caller RPC call-scope limit was reached.");
            }
            if (!_callScopes.TryAdd(scope.Id, scope))
            {
                scope.Revoke();
                throw Unavailable("rpc.scope.allocation", "The Runtime could not allocate an RPC call scope.");
            }
        }
        scope.RevocationToken.Register(static stateValue =>
        {
            var (broker, registeredScope) = ((RuntimeRpcBroker, RuntimeRpcCallerScopeState))stateValue!;
            broker._callScopes.TryRemove(
                new KeyValuePair<string, RuntimeRpcCallerScopeState>(registeredScope.Id, registeredScope));
            broker._contentStore?.DiscardRpcContentAuthority(registeredScope.ContentAuthority);
            _ = registeredScope.Revoke();
        }, (this, scope));
        try
        {
            scope.ArmDeadline(_timeProvider, deadline - now, ExpireCallScope);
        }
        catch
        {
            ExpireCallScope(scope);
            throw;
        }
        return scope;
    }

    private void ExpireCallScope(RuntimeRpcCallerScopeState scope)
    {
        lock (_callScopeGate)
        {
            _callScopes.TryRemove(
                new KeyValuePair<string, RuntimeRpcCallerScopeState>(scope.Id, scope));
        }
        _ = scope.Revoke();
        _contentStore?.DiscardRpcContentAuthority(scope.ContentAuthority);
    }

    internal RuntimeRpcCallerScopeState GetScope(
        IRuntimeRpcCallerActivation caller,
        string callScopeId)
    {
        if (string.IsNullOrWhiteSpace(callScopeId)
            || !_callScopes.TryGetValue(callScopeId, out var scope)
            || !ReferenceEquals(scope.Caller, caller)
            || scope.IsRevoked)
        {
            throw Unavailable("rpc.scope.unavailable", "The RPC call scope is stale or unavailable.");
        }
        if (_timeProvider.GetUtcNow() >= scope.DeadlineUtc)
        {
            _ = scope.Revoke();
            throw DeadlineExceeded();
        }
        return scope;
    }

    internal async ValueTask CloseCallScopeAsync(RuntimeRpcCallerScopeState scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_callScopeGate)
        {
            _callScopes.TryRemove(new KeyValuePair<string, RuntimeRpcCallerScopeState>(scope.Id, scope));
        }
        var callbacks = scope.Revoke();
        _contentStore?.DiscardRpcContentAuthority(scope.ContentAuthority);
        await callbacks.ConfigureAwait(false);
    }

    public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
        RuntimeRpcCallerStamp callerStamp,
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken)
        => GetProviderCoreAsync(GetCaller(callerStamp), endpoint, requirePermission: true, cancellationToken);

    public ValueTask<SunderRpcProviderSnapshot?> GetProviderAppAsync(
        string appSessionId,
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken)
        => GetProviderCoreAsync(GetAppCaller(appSessionId), endpoint, requirePermission: true, cancellationToken);

    internal ValueTask<SunderRpcProviderSnapshot?> GetProviderHostAsync(
        RuntimeRpcHostCallerActivation caller,
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken)
        => GetProviderCoreAsync(caller, endpoint, requirePermission: false, cancellationToken);

    private ValueTask<SunderRpcProviderSnapshot?> GetProviderCoreAsync(
        IRuntimeRpcCallerActivation caller,
        SunderRpcEndpointReference endpoint,
        bool requirePermission,
        CancellationToken cancellationToken,
        RuntimeRpcCallerScopeState? scope = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var sessionLease = _sessions.AcquireLease();
        EnsureCurrent(caller, sessionLease);
        EnsureScope(scope, caller, sessionLease);
        if (!_catalog.TryGetActiveEndpoint(endpoint, out var provider, out _)
            || provider is null
            || !CanUseContract(caller, provider.Snapshot, SunderRpcProtocol.DiscoverAction, out _))
        {
            return ValueTask.FromResult<SunderRpcProviderSnapshot?>(null);
        }
        if (requirePermission)
        {
            using var permission = AcquirePermission(
                caller,
                provider.Snapshot.ContractId,
                SunderRpcProtocol.DiscoverAction);
        }
        return ValueTask.FromResult<SunderRpcProviderSnapshot?>(provider.Snapshot);
    }

    public ValueTask<bool> TryReportInvariantViolationAsync(
        RuntimeRpcCallerStamp callerStamp,
        SunderRpcEndpointReference endpoint,
        Exception exception,
        CancellationToken cancellationToken)
        => TryReportInvariantViolationCoreAsync(
            GetCaller(callerStamp),
            endpoint,
            exception.Message,
            requirePermission: true,
            cancellationToken);

    public ValueTask<bool> TryReportInvariantViolationAppAsync(
        string appSessionId,
        SunderRpcEndpointReference endpoint,
        string exceptionMessage,
        CancellationToken cancellationToken)
        => TryReportInvariantViolationCoreAsync(
            GetAppCaller(appSessionId),
            endpoint,
            exceptionMessage,
            requirePermission: true,
            cancellationToken);

    internal ValueTask<bool> TryReportInvariantViolationHostAsync(
        RuntimeRpcHostCallerActivation caller,
        SunderRpcEndpointReference endpoint,
        Exception exception,
        CancellationToken cancellationToken)
        => TryReportInvariantViolationCoreAsync(
            caller,
            endpoint,
            exception.Message,
            requirePermission: false,
            cancellationToken);

    internal ValueTask<bool> TryReportInvariantViolationScopeAsync(
        RuntimeRpcCallerScopeState scope,
        SunderRpcEndpointReference endpoint,
        Exception exception,
        CancellationToken cancellationToken)
        => TryReportInvariantViolationCoreAsync(
            scope.Caller,
            endpoint,
            exception.Message,
            scope.RequirePermission,
            cancellationToken,
            scope);

    private ValueTask<bool> TryReportInvariantViolationCoreAsync(
        IRuntimeRpcCallerActivation caller,
        SunderRpcEndpointReference endpoint,
        string? exceptionMessage,
        bool requirePermission,
        CancellationToken cancellationToken,
        RuntimeRpcCallerScopeState? scope = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(caller.PackageId, InvariantReporterPackageId, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(false);
        }

        using var sessionLease = _sessions.AcquireLease();
        if (!caller.IsCurrent(sessionLease)) return ValueTask.FromResult(false);
        EnsureScope(scope, caller, sessionLease);
        if (!_catalog.TryGetActiveEndpoint(endpoint, out var provider, out _)
            || provider is null
            || provider.Snapshot.State != SunderRpcProviderState.Active
            || provider.Snapshot.SessionGeneration != sessionLease.Generation
            || _sessions.GetLoadedPackage(sessionLease, provider.Snapshot.PackageId) is not { } activePackage
            || activePackage.RuntimeActivationId != provider.Snapshot.ActivationId
            || !CanUseContract(caller, provider.Snapshot, SunderRpcProtocol.DiscoverAction, out _))
        {
            return ValueTask.FromResult(false);
        }

        RuntimeRpcPermissionLease? permission = null;
        if (requirePermission
            && !TryAcquirePermission(
                caller,
                provider.Snapshot.ContractId,
                SunderRpcProtocol.DiscoverAction,
                out permission))
        {
            return ValueTask.FromResult(false);
        }

        using (permission)
        {
            var activationIdentity = sessionLease.GetPackageActivationIdentity(activePackage);
            var accepted = _sessionOwner.HandleRpcProviderFault(
                provider.Snapshot.PackageId,
                activationIdentity,
                new InvalidOperationException(SanitizeInvariantMessage(exceptionMessage)),
                InvariantViolationFaultCode);
            return ValueTask.FromResult(accepted);
        }
    }

    public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
        RuntimeRpcCallerStamp callerStamp,
        string contractId,
        CancellationToken cancellationToken)
        => DiscoverCoreAsync(GetCaller(callerStamp), contractId, requirePermission: true, cancellationToken);

    public ValueTask<SunderRpcCatalogSnapshot> DiscoverAppAsync(
        string appSessionId,
        string contractId,
        CancellationToken cancellationToken)
        => DiscoverCoreAsync(GetAppCaller(appSessionId), contractId, requirePermission: true, cancellationToken);

    internal ValueTask<SunderRpcCatalogSnapshot> DiscoverHostAsync(
        RuntimeRpcHostCallerActivation caller,
        string contractId,
        CancellationToken cancellationToken)
        => DiscoverCoreAsync(caller, contractId, requirePermission: false, cancellationToken);

    private ValueTask<SunderRpcCatalogSnapshot> DiscoverCoreAsync(
        IRuntimeRpcCallerActivation caller,
        string contractId,
        bool requirePermission,
        CancellationToken cancellationToken,
        RuntimeRpcCallerScopeState? scope = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var sessionLease = _sessions.AcquireLease();
        EnsureCurrent(caller, sessionLease);
        EnsureScope(scope, caller, sessionLease);
        var use = FindUse(caller, contractId, SunderRpcProtocol.DiscoverAction)
                   ?? throw PermissionDenied(contractId, SunderRpcProtocol.DiscoverAction);
        using var permission = requirePermission
            ? AcquirePermission(caller, contractId, SunderRpcProtocol.DiscoverAction)
            : null;
        var snapshot = _catalog.GetSnapshot(provider =>
            string.Equals(provider.ContractId, contractId, StringComparison.Ordinal)
            && CanUseContract(caller, provider, SunderRpcProtocol.DiscoverAction, out _)
            && PackageVersionRange.IsSatisfiedBy(provider.ContractVersion, use.VersionRange!));
        return ValueTask.FromResult(snapshot);
    }

    public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
        RuntimeRpcCallerStamp callerStamp,
        long afterRevision,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in WatchCoreAsync(
                           GetCaller(callerStamp),
                           afterRevision,
                           afterSequence,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAppAsync(
        string appSessionId,
        long afterRevision,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in WatchCoreAsync(
                           GetAppCaller(appSessionId),
                           afterRevision,
                           afterSequence,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<SunderRpcCatalogEvent> WatchCoreAsync(
        IRuntimeRpcCallerActivation caller,
        long afterRevision,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        RuntimeRpcCallerScopeState? scope = null)
    {
        if (afterRevision < 0 || afterSequence < 0)
        {
            throw Validation(
                "rpc.catalog.position",
                "RPC catalog revisions and sequences cannot be negative.");
        }
        using var sessionLease = _sessions.AcquireLease();
        EnsureCurrent(caller, sessionLease);
        EnsureScope(scope, caller, sessionLease);
        using var callerLease = AcquireCallerLease(caller);
        var discoverUses = caller.RpcContractUses.Where(use =>
                (use.Actions ?? []).Contains(SunderRpcProtocol.DiscoverAction, StringComparer.Ordinal))
            .ToArray();
        if (discoverUses.Length == 0)
        {
            throw PermissionDenied("*", SunderRpcProtocol.DiscoverAction);
        }
        var permissionLeases = new List<RuntimeRpcPermissionLease>();
        var permittedContracts = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var use in discoverUses)
            {
                if (TryAcquirePermission(
                        caller,
                        use.ContractId!,
                        SunderRpcProtocol.DiscoverAction,
                        out var permission))
                {
                    permissionLeases.Add(permission!);
                    permittedContracts.Add(use.ContractId!);
                }
            }
            if (permissionLeases.Count == 0)
            {
                throw PermissionDenied("*", SunderRpcProtocol.DiscoverAction);
            }
            await using var subscription = _catalog.Subscribe(afterRevision, afterSequence);
            var permissionChanges = caller.SourceKind == PackageSourceKind.Dev
                ? CancellationToken.None
                : _permissions.ChangeToken;
            var cancellationTokens = new List<CancellationToken>
            {
                cancellationToken,
                _hostStopping,
                sessionLease.RetirementToken,
                callerLease.RetirementToken,
                permissionChanges,
            };
            if (scope is not null) cancellationTokens.Add(scope.RevocationToken);
            cancellationTokens.AddRange(permissionLeases.Select(static lease => lease.RevocationToken));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokens.ToArray());
            if (subscription.ResetRequired)
            {
                yield return new SunderRpcCatalogEvent(
                    subscription.Revision,
                    subscription.Sequence,
                    SunderRpcCatalogEventKind.ResetRequired,
                    null);
            }
            await foreach (var item in subscription.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                if (item.Provider is null
                    || !permittedContracts.Contains(item.Provider.ContractId)
                    || !CanUseContract(caller, item.Provider, SunderRpcProtocol.DiscoverAction, out _))
                {
                    continue;
                }
                yield return item;
            }
        }
        finally
        {
            foreach (var permission in permissionLeases) permission.Dispose();
        }
    }

    internal ValueTask<SunderRpcProviderSnapshot?> GetProviderScopeAsync(
        RuntimeRpcCallerScopeState scope,
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken)
        => GetProviderCoreAsync(
            scope.Caller,
            endpoint,
            scope.RequirePermission,
            cancellationToken,
            scope);

    internal ValueTask<SunderRpcCatalogSnapshot> DiscoverScopeAsync(
        RuntimeRpcCallerScopeState scope,
        string contractId,
        CancellationToken cancellationToken)
        => DiscoverCoreAsync(
            scope.Caller,
            contractId,
            scope.RequirePermission,
            cancellationToken,
            scope);

    internal async IAsyncEnumerable<SunderRpcCatalogEvent> WatchScopeAsync(
        RuntimeRpcCallerScopeState scope,
        long afterRevision,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = WatchCoreAsync(
                scope.Caller,
                afterRevision,
                afterSequence,
                cancellationToken,
                scope)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_timeProvider.GetUtcNow() >= scope.DeadlineUtc)
            {
                throw DeadlineExceeded();
            }
            if (!hasNext) yield break;
            yield return enumerator.Current;
        }
    }

    public async ValueTask<JsonElement> InvokeAsync(
        RuntimeRpcCallerStamp callerStamp,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options,
        CancellationToken cancellationToken)
        => await InvokeCoreAsync(
            GetCaller(callerStamp),
            endpoint,
            serviceId,
            methodId,
            request,
            options,
            requirePermission: true,
            cancellationToken).ConfigureAwait(false);

    public ValueTask<JsonElement> InvokeAppAsync(
        string appSessionId,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options,
        CancellationToken cancellationToken)
        => InvokeCoreAsync(
            GetAppCaller(appSessionId),
            endpoint,
            serviceId,
            methodId,
            request,
            options,
            requirePermission: true,
            cancellationToken);

    internal ValueTask<JsonElement> InvokeHostAsync(
        RuntimeRpcHostCallerActivation caller,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options,
        CancellationToken cancellationToken)
        => InvokeCoreAsync(
            caller,
            endpoint,
            serviceId,
            methodId,
            request,
            options,
            requirePermission: false,
            cancellationToken);

    private async ValueTask<JsonElement> InvokeCoreAsync(
        IRuntimeRpcCallerActivation caller,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options,
        bool requirePermission,
        CancellationToken cancellationToken,
        RuntimeRpcCallerScopeState? scope = null)
    {
        var call = AcquireCall(
            caller,
            endpoint,
            serviceId,
            methodId,
            request,
            SunderRpcMethodKind.Unary,
            SunderRpcProtocol.InvokeAction,
            options,
            requirePermission,
            cancellationToken,
            scope);
        using (call)
        {
            var previous = CurrentCall.Value;
            CurrentCall.Value = call.Frame;
            try
            {
                JsonElement response;
                try
                {
                    response = await call.Provider.Registration.Handler.InvokeUnaryAsync(
                        call.Context,
                        serviceId,
                        methodId,
                        request.Clone(),
                        call.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    throw HandleProviderException(call, exception);
                }
                ThrowIfCallCancelled(call);
                JsonElement clone;
                try
                {
                    clone = response.Clone();
                }
                catch (Exception exception)
                {
                    throw FaultProvider(call, "rpc.provider.invalid-json", "The provider returned an invalid JSON value.", exception);
                }
                ValidateOutput(call, clone);
                ThrowIfCallCancelled(call);
                return clone;
            }
            finally
            {
                CurrentCall.Value = previous;
            }
        }
    }

    internal ValueTask<JsonElement> InvokeScopeAsync(
        RuntimeRpcCallerScopeState scope,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options,
        CancellationToken cancellationToken)
        => InvokeCoreAsync(
            scope.Caller,
            endpoint,
            serviceId,
            methodId,
            request,
            options,
            scope.RequirePermission,
            cancellationToken,
            scope);

    public async IAsyncEnumerable<JsonElement> SubscribeAsync(
        RuntimeRpcCallerStamp callerStamp,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in SubscribeCoreAsync(
                           GetCaller(callerStamp),
                           endpoint,
                           serviceId,
                           methodId,
                           request,
                           options,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public async IAsyncEnumerable<JsonElement> SubscribeAppAsync(
        string appSessionId,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in SubscribeCoreAsync(
                           GetAppCaller(appSessionId),
                           endpoint,
                           serviceId,
                           methodId,
                           request,
                           options,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<JsonElement> SubscribeCoreAsync(
        IRuntimeRpcCallerActivation caller,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        RuntimeRpcCallerScopeState? scope = null)
    {
        var call = AcquireCall(
            caller,
            endpoint,
            serviceId,
            methodId,
            request,
            SunderRpcMethodKind.ServerStream,
            SunderRpcProtocol.SubscribeAction,
            options,
            requirePermission: scope?.RequirePermission ?? true,
            cancellationToken,
            scope);
        using (call)
        {
            var previous = CurrentCall.Value;
            CurrentCall.Value = call.Frame;
            var eventCount = 0;
            var windowStarted = Stopwatch.GetTimestamp();
            var eventsInWindow = 0;
            try
            {
                IAsyncEnumerable<JsonElement> stream;
                try
                {
                    stream = call.Provider.Registration.Handler.InvokeServerStreamAsync(
                                 call.Context,
                                 serviceId,
                                 methodId,
                                 request.Clone(),
                                 call.CancellationToken)
                             ?? throw new InvalidOperationException("The provider returned a null event stream.");
                }
                catch (Exception exception)
                {
                    throw HandleProviderException(call, exception);
                }

                IAsyncEnumerator<JsonElement> enumerator;
                try
                {
                    enumerator = stream.GetAsyncEnumerator(call.CancellationToken);
                }
                catch (Exception exception)
                {
                    throw HandleProviderException(call, exception);
                }
                try
                {
                    while (true)
                    {
                        bool hasNext;
                        try
                        {
                            hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            throw HandleProviderException(call, exception);
                        }
                        if (!hasNext) break;
                        ThrowIfCallCancelled(call);
                        if (++eventCount > _policy.MaxStreamEvents)
                        {
                            throw ResourceExhausted("rpc.stream.event-limit", "The RPC stream exceeded its event limit.");
                        }
                        var elapsed = Stopwatch.GetElapsedTime(windowStarted);
                        if (elapsed >= TimeSpan.FromSeconds(1))
                        {
                            windowStarted = Stopwatch.GetTimestamp();
                            eventsInWindow = 0;
                        }
                        if (++eventsInWindow > _policy.MaxStreamEventsPerSecond)
                        {
                            throw ResourceExhausted("rpc.stream.rate-limit", "The RPC stream exceeded its event-rate limit.");
                        }
                        JsonElement item;
                        try
                        {
                            item = enumerator.Current.Clone();
                        }
                        catch (Exception exception)
                        {
                            throw FaultProvider(call, "rpc.provider.invalid-event", "The provider returned an invalid JSON event.", exception);
                        }
                        ValidateOutput(call, item);
                        ThrowIfCallCancelled(call);
                        yield return item;
                    }
                    call.CancellationToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    try
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        throw HandleProviderException(call, exception);
                    }
                }
            }
            finally
            {
                CurrentCall.Value = previous;
            }
        }
    }

    internal async IAsyncEnumerable<JsonElement> SubscribeScopeAsync(
        RuntimeRpcCallerScopeState scope,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in SubscribeCoreAsync(
                           scope.Caller,
                           endpoint,
                           serviceId,
                           methodId,
                           request,
                           options,
                           cancellationToken,
                           scope).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private RuntimeRpcCallLease AcquireCall(
        IRuntimeRpcCallerActivation caller,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcMethodKind expectedKind,
        string action,
        SunderRpcCallOptions? options,
        bool requirePermission,
        CancellationToken cancellationToken,
        RuntimeRpcCallerScopeState? scope = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessionLease = _sessions.AcquireLease();
        RuntimeRpcActivationLease? callerLease = null;
        RuntimeRpcActivationLease? calleeLease = null;
        RuntimeRpcPermissionLease? permission = null;
        CancellationTokenSource? linked = null;
        CancellationTokenSource? deadlineCancellation = null;
        RuntimeRpcInvocationAuthority? invocationAuthority = null;
        try
        {
            if (!caller.IsCurrent(sessionLease))
            {
                throw Unavailable("rpc.caller-retired", "The caller package activation is retired.");
            }
            EnsureScope(scope, caller, sessionLease);
            if (!_catalog.TryGetActiveEndpoint(endpoint, out var provider, out var stale) || provider is null)
            {
                throw stale
                    ? SunderRpcException.Infrastructure(
                        SunderRpcErrorKind.StaleEndpoint,
                        "rpc.endpoint.stale",
                        "The RPC endpoint identifies a retired provider activation.")
                    : NotFound("rpc.endpoint.not-found", "The RPC endpoint was not found.");
            }
            if (_sessions.GetLoadedPackage(sessionLease, provider.Snapshot.PackageId) is not { } calleePackage
                || calleePackage.RuntimeActivationId != provider.Snapshot.ActivationId
                || sessionLease.Generation != provider.Snapshot.SessionGeneration)
            {
                throw Unavailable("rpc.provider-retired", "The provider activation is no longer in the active Runtime session.");
            }
            if (!CanUseContract(caller, provider.Snapshot, action, out var callerContract))
            {
                throw PermissionDenied(provider.Snapshot.ContractId, action);
            }
            if (requirePermission)
            {
                permission = AcquirePermission(caller, provider.Snapshot.ContractId, action);
            }
            var service = callerContract!.FindService(serviceId)
                          ?? throw NotFound("rpc.service.not-found", "The RPC service was not found in the bundled contract.");
            var method = service.FindMethod(methodId)
                         ?? throw NotFound("rpc.method.not-found", "The RPC method was not found in the bundled contract.");
            if (method.Kind != expectedKind)
            {
                throw SunderRpcException.Infrastructure(
                    SunderRpcErrorKind.Validation,
                    "rpc.method.kind-mismatch",
                    "The requested RPC invocation shape does not match the method contract.");
            }
            ValidatePayload(callerContract, method.RequestSchemaReference, request, "request");

            var parent = CurrentCall.Value;
            var depth = (parent?.Depth ?? 0) + 1;
            if (depth > _policy.MaxNestedCallDepth)
            {
                throw ResourceExhausted("rpc.call.depth-limit", "The nested RPC call depth limit was reached.");
            }
            var now = _timeProvider.GetUtcNow();
            var deadline = GetEffectiveDeadline(options, parent, now, scope?.DeadlineUtc);
            if (deadline <= now)
            {
                throw DeadlineExceeded();
            }
            if (!caller.TryAcquireCaller(_policy.MaxConcurrentCallsPerCaller, out callerLease)
                || callerLease is null)
            {
                throw ResourceExhausted("rpc.caller.concurrency-limit", "The caller RPC concurrency limit was reached.");
            }
            if (!provider.TryAcquireCallee(_policy.MaxConcurrentCallsPerCallee, out calleeLease)
                || calleeLease is null)
            {
                throw ResourceExhausted("rpc.provider.concurrency-limit", "The provider RPC concurrency limit was reached.");
            }

            deadlineCancellation = new CancellationTokenSource(deadline - now, _timeProvider);
            var cancellationTokens = new List<CancellationToken>
            {
                cancellationToken,
                _hostStopping,
                sessionLease.RetirementToken,
                callerLease.RetirementToken,
                calleeLease.RetirementToken,
                deadlineCancellation.Token,
            };
            if (permission is not null) cancellationTokens.Add(permission.RevocationToken);
            if (scope is not null) cancellationTokens.Add(scope.RevocationToken);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokens.ToArray());
            if (_contentStore is not null)
            {
                invocationAuthority = new RuntimeRpcInvocationAuthority(
                    _contentStore,
                    _sessions,
                    _catalog,
                    _transportPolicy,
                    caller.PackageId,
                    provider.Snapshot.PackageId,
                    sessionLease.Generation,
                    new RuntimeRpcContentEndpoint(
                        provider.Snapshot.Endpoint.Value,
                        provider.Snapshot.ActivationId),
                    deadline,
                    scope?.ContentAuthority
                    ?? new RuntimeRpcContentAuthority(
                        "rpc-invocation-" + Guid.NewGuid().ToString("N")),
                    ownsContentAuthority: scope is null,
                    linked.Token,
                    _timeProvider);
            }
            return new RuntimeRpcCallLease(
                sessionLease,
                callerLease,
                calleeLease,
                permission,
                linked,
                deadlineCancellation,
                provider,
                service.ServiceId,
                method,
                callerContract,
                new RuntimeRpcCallFrame(depth, deadline),
                new SunderRpcInvocationContext(
                    caller.PackageId,
                    caller.PackageVersion,
                    provider.Snapshot,
                    deadline,
                    depth,
                    linked.Token,
                    invocationAuthority));
        }
        catch
        {
            invocationAuthority?.Revoke();
            linked?.Dispose();
            deadlineCancellation?.Dispose();
            permission?.Dispose();
            calleeLease?.Dispose();
            callerLease?.Dispose();
            sessionLease.Dispose();
            throw;
        }
    }

    internal async ValueTask<SunderRpcContentReference> RegisterScopeContentAsync(
        RuntimeRpcCallerScopeState scope,
        SunderRpcEndpointReference endpoint,
        Stream source,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        var store = _contentStore
                    ?? throw Unavailable(
                        "rpc.content.host-unavailable",
                        "Host-mediated RPC content transfer is unavailable.");
        RuntimeRpcInvocationAuthority.ValidateOptions(options);
        using var sessionLease = _sessions.AcquireLease();
        EnsureCurrent(scope.Caller, sessionLease);
        EnsureScope(scope, scope.Caller, sessionLease);
        var provider = GetContentTarget(scope, endpoint, sessionLease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            scope.RevocationToken,
            sessionLease.RetirementToken,
            scope.Caller.RetirementToken,
            provider.Owner.RetirementToken);
        var now = _timeProvider.GetUtcNow();
        var expiresAt = RuntimeRpcInvocationAuthority.Min(
            options.ExpiresAtUtc ?? now + _transportPolicy.ContentTransferLifetime,
            scope.DeadlineUtc,
            now + _transportPolicy.ContentTransferLifetime);
        try
        {
            return await store.RegisterRpcContentAsync(
                source,
                options.Length,
                options.MediaType,
                options.FileName,
                scope.Caller.PackageId,
                provider.Snapshot.PackageId,
                sessionLease.Generation,
                new RuntimeRpcContentEndpoint(
                    provider.Snapshot.Endpoint.Value,
                    provider.Snapshot.ActivationId),
                expiresAt,
                options.Repeatability,
                options.MaximumUses,
                linked.Token,
                scope.ContentAuthority).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (scope.IsRevoked)
        {
            throw _timeProvider.GetUtcNow() >= scope.DeadlineUtc
                ? DeadlineExceeded()
                : Unavailable("rpc.scope.unavailable", "The RPC call scope is stale or unavailable.");
        }
    }

    internal async ValueTask<SunderRpcContentReference> RegisterScopeContentFileAsync(
        RuntimeRpcCallerScopeState scope,
        SunderRpcEndpointReference endpoint,
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using var source = new FileStream(
            Path.GetFullPath(filePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await RegisterScopeContentAsync(
            scope,
            endpoint,
            source,
            options,
            cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<Stream> OpenScopeContentAsync(
        RuntimeRpcCallerScopeState scope,
        SunderRpcContentReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        var store = _contentStore
                    ?? throw Unavailable(
                        "rpc.content.host-unavailable",
                        "Host-mediated RPC content transfer is unavailable.");
        using var sessionLease = _sessions.AcquireLease();
        EnsureCurrent(scope.Caller, sessionLease);
        EnsureScope(scope, scope.Caller, sessionLease);
        if (!store.TryGetRpcContentEndpoint(
                reference,
                scope.Caller.PackageId,
                sessionLease.Generation,
                scope.ContentAuthority,
                out var providerEndpoint))
        {
            throw NotFound(
                "rpc.content.unavailable",
                "The RPC content reference is stale, exhausted, or unavailable to this call scope.");
        }
        var provider = GetActiveContentProvider(providerEndpoint, sessionLease);
        var lease = store.AcquireRpcContentForAudience(
            reference,
            scope.Caller.PackageId,
            sessionLease.Generation,
            providerEndpoint,
            scope.ContentAuthority)
            ?? throw NotFound(
                "rpc.content.unavailable",
                "The RPC content reference is stale, exhausted, or unavailable to this call scope.");
        var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            scope.RevocationToken,
            provider.Owner.RetirementToken);
        try
        {
            Stream stream = new RuntimeRpcContentReadStream(
                lease.Content,
                () =>
                {
                    store.ReleaseRpcContent(lease);
                    readCancellation.Dispose();
                },
                readCancellation.Token,
                () => ValidateActiveContentProvider(provider, providerEndpoint));
            return ValueTask.FromResult(stream);
        }
        catch
        {
            readCancellation.Dispose();
            store.ReleaseRpcContent(lease);
            throw;
        }
    }

    private RuntimeRpcProviderActivation GetContentTarget(
        RuntimeRpcCallerScopeState scope,
        SunderRpcEndpointReference endpoint,
        PackageSessionLease sessionLease)
    {
        if (!_catalog.TryGetActiveEndpoint(endpoint, out var provider, out var stale) || provider is null)
        {
            throw stale
                ? SunderRpcException.Infrastructure(
                    SunderRpcErrorKind.StaleEndpoint,
                    "rpc.endpoint.stale",
                    "The RPC endpoint identifies a retired provider activation.")
                : NotFound("rpc.endpoint.not-found", "The RPC endpoint was not found.");
        }
        if (_sessions.GetLoadedPackage(sessionLease, provider.Snapshot.PackageId) is not { } calleePackage
            || calleePackage.RuntimeActivationId != provider.Snapshot.ActivationId
            || sessionLease.Generation != provider.Snapshot.SessionGeneration)
        {
            throw Unavailable(
                "rpc.provider-retired",
                "The provider activation is no longer in the active Runtime session.");
        }

        foreach (var action in new[] { SunderRpcProtocol.InvokeAction, SunderRpcProtocol.SubscribeAction })
        {
            if (!CanUseContract(scope.Caller, provider.Snapshot, action, out _)) continue;
            if (!scope.RequirePermission) return provider;
            if (TryAcquirePermission(scope.Caller, provider.Snapshot.ContractId, action, out var permission))
            {
                permission!.Dispose();
                return provider;
            }
        }
        throw PermissionDenied(provider.Snapshot.ContractId, "invoke or subscribe");
    }

    private RuntimeRpcProviderActivation GetActiveContentProvider(
        RuntimeRpcContentEndpoint endpoint,
        PackageSessionLease sessionLease)
    {
        if (!_catalog.TryGetActiveEndpoint(
                new SunderRpcEndpointReference(endpoint.EndpointReference),
                out var provider,
                out _)
            || provider is null
            || provider.Snapshot.ActivationId != endpoint.ActivationId
            || provider.Snapshot.SessionGeneration != sessionLease.Generation
            || _sessions.GetLoadedPackage(sessionLease, provider.Snapshot.PackageId)?.RuntimeActivationId
            != endpoint.ActivationId)
        {
            throw NotFound(
                "rpc.content.unavailable",
                "The RPC content reference identifies a retired provider endpoint.");
        }
        return provider;
    }

    private void ValidateActiveContentProvider(
        RuntimeRpcProviderActivation expectedProvider,
        RuntimeRpcContentEndpoint endpoint)
    {
        if (!_catalog.TryGetActiveEndpoint(
                new SunderRpcEndpointReference(endpoint.EndpointReference),
                out var provider,
                out _)
            || !ReferenceEquals(provider, expectedProvider)
            || provider.Snapshot.State != SunderRpcProviderState.Active
            || provider.Snapshot.ActivationId != endpoint.ActivationId)
        {
            throw new OperationCanceledException(
                "The RPC content provider endpoint was retired.");
        }
    }

    private RuntimeRpcPackageActivation GetCaller(RuntimeRpcCallerStamp stamp)
        => _catalog.TryGetPackage(stamp.PackageId, stamp.ActivationId, out var caller) && caller is not null
            ? caller
            : throw Unavailable("rpc.caller-retired", "The caller package activation is retired.");

    private RuntimeRpcAppCallerSession GetAppCaller(string sessionId)
        => _appSessions?.GetRequired(sessionId)
           ?? throw Unavailable("rpc.app-session.unavailable", "App RPC caller sessions are unavailable.");

    private RuntimeRpcActivationLease AcquireCallerLease(IRuntimeRpcCallerActivation caller)
        => caller.TryAcquireCaller(_policy.MaxConcurrentCallsPerCaller, out var lease) && lease is not null
            ? lease
            : throw ResourceExhausted(
                "rpc.caller.concurrency-limit",
                "The caller RPC concurrency limit was reached.");

    private static void EnsureCurrent(
        IRuntimeRpcCallerActivation caller,
        PackageSessionLease sessionLease)
    {
        if (!caller.IsCurrent(sessionLease))
        {
            throw Unavailable("rpc.caller-retired", "The caller package activation is retired.");
        }
    }

    private void EnsureScope(
        RuntimeRpcCallerScopeState? scope,
        IRuntimeRpcCallerActivation caller,
        PackageSessionLease sessionLease)
    {
        if (scope is null) return;
        if (_timeProvider.GetUtcNow() >= scope.DeadlineUtc)
        {
            _ = scope.Revoke();
            throw DeadlineExceeded();
        }
        if (scope.IsRevoked
            || !ReferenceEquals(scope.Caller, caller)
            || scope.SessionGeneration != sessionLease.Generation)
        {
            throw Unavailable("rpc.scope.unavailable", "The RPC call scope is stale or unavailable.");
        }
    }

    private DateTimeOffset GetEffectiveDeadline(
        SunderRpcCallOptions? options,
        RuntimeRpcCallFrame? parent,
        DateTimeOffset now,
        DateTimeOffset? scopeDeadline = null)
    {
        var deadline = now + _policy.DefaultDeadline;
        if (options?.DeadlineUtc is { } requestedDeadline && requestedDeadline < deadline)
        {
            deadline = requestedDeadline;
        }
        if (parent is not null && parent.DeadlineUtc < deadline) deadline = parent.DeadlineUtc;
        if (scopeDeadline is { } scoped && scoped < deadline) deadline = scoped;
        return deadline;
    }

    private RuntimeRpcPermissionLease AcquirePermission(
        IRuntimeRpcCallerActivation caller,
        string contractId,
        string action)
        => TryAcquirePermission(caller, contractId, action, out var lease) && lease is not null
            ? lease
            : throw PermissionDenied(contractId, action);

    private bool TryAcquirePermission(
        IRuntimeRpcCallerActivation caller,
        string contractId,
        string action,
        out RuntimeRpcPermissionLease? lease)
    {
        if (caller.SourceKind == PackageSourceKind.Dev)
        {
            lease = new RuntimeRpcPermissionLease(caller.RetirementToken);
            return true;
        }
        return _permissions.TryAcquire(
            caller.PackageId,
            caller.PackageVersion,
            caller.ManifestSha256,
            contractId,
            action,
            out lease);
    }

    private static bool CanUseContract(
        IRuntimeRpcCallerActivation caller,
        SunderRpcProviderSnapshot provider,
        string action,
        out SunderRpcContractDescriptor? descriptor)
    {
        descriptor = null;
        var use = FindUse(caller, provider.ContractId, action);
        if (use is null || !PackageVersionRange.IsSatisfiedBy(provider.ContractVersion, use.VersionRange!)) return false;
        return caller.RpcContracts.TryGetValue(
                   PackageSessionPreparer.ContractKey(provider.ContractId, provider.ContractVersion),
                   out descriptor)
               && string.Equals(descriptor.Sha256, provider.ContractSha256, StringComparison.Ordinal);
    }

    private static Sunder.Package.Format.SunderPackageContractUseManifest? FindUse(
        IRuntimeRpcCallerActivation caller,
        string contractId,
        string action)
        => caller.RpcContractUses.FirstOrDefault(use =>
            string.Equals(use.ContractId, contractId, StringComparison.Ordinal)
            && (use.Actions ?? []).Contains(action, StringComparer.Ordinal));

    private void ValidatePayload(
        SunderRpcContractDescriptor contract,
        string schemaReference,
        JsonElement value,
        string label)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw Validation($"rpc.{label}.undefined", $"The RPC {label} is undefined.");
        }
        var bytes = Encoding.UTF8.GetByteCount(value.GetRawText());
        if (bytes > _policy.MaxPayloadBytes || GetDepth(value) > _policy.MaxPayloadDepth)
        {
            throw ResourceExhausted($"rpc.{label}.limit", $"The RPC {label} exceeds a payload limit.");
        }
        if (!contract.IsValid(schemaReference, value, out var error))
        {
            throw Validation(
                $"rpc.{label}.schema-invalid",
                $"The RPC {label} does not match its contract schema: {Sanitize(error)}");
        }
    }

    private void ValidateOutput(RuntimeRpcCallLease call, JsonElement value)
    {
        try
        {
            ValidatePayload(call.Contract, call.Method.OutputSchemaReference, value, "output");
        }
        catch (SunderRpcException exception) when (exception.Error.Kind is SunderRpcErrorKind.Validation or SunderRpcErrorKind.ResourceExhausted)
        {
            throw FaultProvider(
                call,
                "rpc.provider.invalid-output",
                "The provider returned output that violates its contract.",
                exception);
        }
    }

    private Exception HandleProviderException(RuntimeRpcCallLease call, Exception exception)
    {
        if (call.ProviderRetirementRequested)
        {
            return SunderRpcException.Infrastructure(
                SunderRpcErrorKind.StaleEndpoint,
                "rpc.endpoint.stale",
                "The RPC endpoint identifies a retired provider activation.");
        }
        if (exception is OperationCanceledException || call.CancellationToken.IsCancellationRequested)
        {
            return call.DeadlineElapsed || _timeProvider.GetUtcNow() >= call.Frame.DeadlineUtc
                ? DeadlineExceeded()
                : SunderRpcException.Infrastructure(
                    SunderRpcErrorKind.Cancelled,
                    "rpc.call.cancelled",
                    "The RPC call was cancelled.");
        }
        if (exception is SunderRpcException rpcException
            && IsSafeDomainCode(rpcException.Error.Code))
        {
            if (rpcException.Error.Kind == SunderRpcErrorKind.Domain)
            {
                return new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.Domain,
                    rpcException.Error.Code,
                    Sanitize(rpcException.Error.Message)));
            }
            if (rpcException.IsHostAuthenticated)
            {
                return RuntimeRpcFailureContextStore.Copy(
                    rpcException,
                    SunderRpcException.Infrastructure(
                        rpcException.Error.Kind,
                        rpcException.Error.Code,
                        Sanitize(rpcException.Error.Message)));
            }
        }
        return FaultProvider(
            call,
            "rpc.provider.handler-fault",
            "The provider handler violated the RPC invocation contract.",
            exception);
    }

    private void ThrowIfCallCancelled(RuntimeRpcCallLease call)
    {
        if (call.ProviderRetirementRequested || call.CancellationToken.IsCancellationRequested)
        {
            throw HandleProviderException(
                call,
                new OperationCanceledException(call.CancellationToken));
        }
    }

    private SunderRpcException FaultProvider(
        RuntimeRpcCallLease call,
        string code,
        string message,
        Exception exception)
    {
        var failure = RuntimeRpcFailureContextStore.AttachProviderFault(
            SunderRpcException.Infrastructure(SunderRpcErrorKind.ProviderFaulted, code, message),
            call.Context.CallerPackageId,
            call.Provider.Snapshot,
            call.ServiceId,
            call.Method.MethodId,
            exception);
        var identity = call.SessionLease.GetPackageActivationIdentity(call.Provider.Owner.Package);
        _sessionOwner.HandleRpcProviderFault(
            call.Provider.Snapshot.PackageId,
            identity,
            exception,
            code);
        return failure;
    }

    private static int GetDepth(JsonElement value)
    {
        var maximum = 1;
        var stack = new Stack<(JsonElement Value, int Depth)>();
        stack.Push((value, 1));
        while (stack.TryPop(out var item))
        {
            maximum = Math.Max(maximum, item.Depth);
            if (item.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in item.Value.EnumerateArray()) stack.Push((child, item.Depth + 1));
            }
            else if (item.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in item.Value.EnumerateObject()) stack.Push((child.Value, item.Depth + 1));
            }
        }
        return maximum;
    }

    private static bool IsSafeDomainCode(string value)
        => value.Length is > 0 and <= 128
           && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "RPC validation failed.";
        var output = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return output.Length <= 512 ? output : output[..512];
    }

    private static string SanitizeInvariantMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "The provider violated an orchestrator invariant.";
        var output = new string(value.Select(static character => char.IsControl(character) ? ' ' : character).ToArray())
            .Trim();
        if (output.Length == 0) return "The provider violated an orchestrator invariant.";
        return output.Length <= 512 ? output : output[..512];
    }

    private static SunderRpcException PermissionDenied(string contractId, string action)
        => SunderRpcException.Infrastructure(
            SunderRpcErrorKind.PermissionDenied,
            "rpc.permission.denied",
            $"RPC action '{action}' is not granted for contract '{contractId}'.");

    private static SunderRpcException ResourceExhausted(string code, string message)
        => SunderRpcException.Infrastructure(SunderRpcErrorKind.ResourceExhausted, code, message);

    private static SunderRpcException Validation(string code, string message)
        => SunderRpcException.Infrastructure(SunderRpcErrorKind.Validation, code, message);

    private static SunderRpcException NotFound(string code, string message)
        => SunderRpcException.Infrastructure(SunderRpcErrorKind.NotFound, code, message);

    private static SunderRpcException Unavailable(string code, string message)
        => SunderRpcException.Infrastructure(SunderRpcErrorKind.Unavailable, code, message);

    private static SunderRpcException DeadlineExceeded()
        => SunderRpcException.Infrastructure(
            SunderRpcErrorKind.DeadlineExceeded,
            "rpc.call.deadline-exceeded",
            "The RPC call deadline elapsed.");
}

internal readonly record struct RuntimeRpcCallerStamp(string PackageId, Guid ActivationId);

internal sealed class RuntimeRpcClient(
    RuntimeRpcBroker broker,
    RuntimeRpcCallerStamp callerStamp) : ISunderRpcClient
{
    public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
        => broker.CreateCallScopeAsync(callerStamp, options, cancellationToken);

    public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken = default)
        => broker.GetProviderAsync(callerStamp, endpoint, cancellationToken);

    public ValueTask<bool> TryReportInvariantViolationAsync(
        SunderRpcEndpointReference endpoint,
        Exception exception,
        CancellationToken cancellationToken = default)
        => broker.TryReportInvariantViolationAsync(callerStamp, endpoint, exception, cancellationToken);

    public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
        string contractId,
        CancellationToken cancellationToken = default)
        => broker.DiscoverAsync(callerStamp, contractId, cancellationToken);

    public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
        long afterRevision,
        long afterSequence,
        CancellationToken cancellationToken = default)
        => broker.WatchAsync(callerStamp, afterRevision, afterSequence, cancellationToken);

    public ValueTask<JsonElement> InvokeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
        => broker.InvokeAsync(callerStamp, endpoint, serviceId, methodId, request, options, cancellationToken);

    public IAsyncEnumerable<JsonElement> SubscribeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
        => broker.SubscribeAsync(callerStamp, endpoint, serviceId, methodId, request, options, cancellationToken);
}

internal sealed class UnavailableRuntimeRpcClient : ISunderRpcClient
{
    public static UnavailableRuntimeRpcClient Instance { get; } = new();

    private UnavailableRuntimeRpcClient()
    {
    }

    public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromException<ISunderRpcCallScope>(Unavailable());

    public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken = default)
        => ValueTask.FromException<SunderRpcProviderSnapshot?>(Unavailable());

    public ValueTask<bool> TryReportInvariantViolationAsync(
        SunderRpcEndpointReference endpoint,
        Exception exception,
        CancellationToken cancellationToken = default)
        => ValueTask.FromException<bool>(Unavailable());

    public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
        string contractId,
        CancellationToken cancellationToken = default)
        => ValueTask.FromException<SunderRpcCatalogSnapshot>(Unavailable());

    public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
        long afterRevision,
        long afterSequence,
        CancellationToken cancellationToken = default)
        => ThrowAsync<SunderRpcCatalogEvent>(cancellationToken);

    public ValueTask<JsonElement> InvokeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromException<JsonElement>(Unavailable());

    public IAsyncEnumerable<JsonElement> SubscribeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
        => ThrowAsync<JsonElement>(cancellationToken);

    private static SunderRpcException Unavailable()
        => SunderRpcException.Infrastructure(
            SunderRpcErrorKind.Unavailable,
            "rpc.host.unavailable",
            "Schema-first RPC is unavailable in this host context.");

    private static async IAsyncEnumerable<T> ThrowAsync<T>(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        throw Unavailable();
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}

internal sealed class RuntimeRpcCallerScopeState
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _revocation;
    private readonly TaskCompletionSource _revocationCompleted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private ITimer? _deadlineTimer;
    private int _revoked;

    public RuntimeRpcCallerScopeState(
        string id,
        IRuntimeRpcCallerActivation caller,
        long sessionGeneration,
        DateTimeOffset deadlineUtc,
        bool requirePermission,
        CancellationToken hostStopping)
    {
        Id = id;
        Caller = caller;
        SessionGeneration = sessionGeneration;
        DeadlineUtc = deadlineUtc;
        RequirePermission = requirePermission;
        ContentAuthority = new RuntimeRpcContentAuthority(id);
        _revocation = CancellationTokenSource.CreateLinkedTokenSource(
            caller.RetirementToken,
            hostStopping);
    }

    public string Id { get; }
    public IRuntimeRpcCallerActivation Caller { get; }
    public long SessionGeneration { get; }
    public DateTimeOffset DeadlineUtc { get; }
    public bool RequirePermission { get; }
    public RuntimeRpcContentAuthority ContentAuthority { get; }
    public CancellationToken RevocationToken => _revocation.Token;
    public bool IsRevoked => Volatile.Read(ref _revoked) != 0 || _revocation.IsCancellationRequested;

    public void ArmDeadline(
        TimeProvider timeProvider,
        TimeSpan dueTime,
        Action<RuntimeRpcCallerScopeState> expire)
    {
        var timer = timeProvider.CreateTimer(
            static state =>
            {
                var (scope, expireScope) =
                    ((RuntimeRpcCallerScopeState, Action<RuntimeRpcCallerScopeState>))state!;
                expireScope(scope);
            },
            (this, expire),
            dueTime <= TimeSpan.Zero ? TimeSpan.Zero : dueTime,
            Timeout.InfiniteTimeSpan);
        lock (_gate)
        {
            if (IsRevoked)
            {
                timer.Dispose();
                return;
            }
            _deadlineTimer = timer;
        }
    }

    public Task Revoke()
    {
        if (Interlocked.Exchange(ref _revoked, 1) != 0) return _revocationCompleted.Task;
        ContentAuthority.Revoke();
        ITimer? timer;
        lock (_gate)
        {
            timer = _deadlineTimer;
            _deadlineTimer = null;
        }
        timer?.Dispose();
        var callbacks = RuntimeCancellation.Signal(_revocation);
        _ = CompleteRevocationAsync(callbacks);
        return _revocationCompleted.Task;
    }

    private async Task CompleteRevocationAsync(Task callbacks)
    {
        await callbacks.ConfigureAwait(false);
        _revocation.Dispose();
        _revocationCompleted.TrySetResult();
    }
}

internal sealed class RuntimeRpcCallScope(
    RuntimeRpcBroker broker,
    RuntimeRpcCallerScopeState state) : ISunderRpcCallScope
{
    private readonly CancellationToken _revocationToken = state.RevocationToken;
    private int _disposed;

    public DateTimeOffset DeadlineUtc => state.DeadlineUtc;

    internal CancellationToken RevocationToken => _revocationToken;

    public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return broker.GetProviderScopeAsync(state, endpoint, cancellationToken);
    }

    public ValueTask<bool> TryReportInvariantViolationAsync(
        SunderRpcEndpointReference endpoint,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return broker.TryReportInvariantViolationScopeAsync(state, endpoint, exception, cancellationToken);
    }

    public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
        string contractId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return broker.DiscoverScopeAsync(state, contractId, cancellationToken);
    }

    public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
        long afterRevision,
        long afterSequence,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return broker.WatchScopeAsync(state, afterRevision, afterSequence, cancellationToken);
    }

    public ValueTask<JsonElement> InvokeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return broker.InvokeScopeAsync(
            state,
            endpoint,
            serviceId,
            methodId,
            request,
            options,
            cancellationToken);
    }

    public IAsyncEnumerable<JsonElement> SubscribeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return broker.SubscribeScopeAsync(
            state,
            endpoint,
            serviceId,
            methodId,
            request,
            options,
            cancellationToken);
    }

    public ValueTask<SunderRpcContentReference> RegisterContentAsync(
        SunderRpcEndpointReference endpoint,
        Stream source,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return broker.RegisterScopeContentAsync(state, endpoint, source, options, cancellationToken);
    }

    public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(
        SunderRpcEndpointReference endpoint,
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return broker.RegisterScopeContentFileAsync(state, endpoint, filePath, options, cancellationToken);
    }

    public ValueTask<Stream> OpenContentAsync(
        SunderRpcContentReference reference,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return broker.OpenScopeContentAsync(state, reference, cancellationToken);
    }

    public ValueTask DisposeAsync()
        => Interlocked.Exchange(ref _disposed, 1) == 0
            ? broker.CloseCallScopeAsync(state)
            : ValueTask.CompletedTask;

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

internal sealed class RuntimeRpcCallLease : IDisposable
{
    private readonly RuntimeRpcActivationLease _callerLease;
    private readonly RuntimeRpcActivationLease _calleeLease;
    private readonly RuntimeRpcPermissionLease? _permission;
    private readonly CancellationTokenSource _linked;
    private readonly CancellationTokenSource _deadline;

    public RuntimeRpcCallLease(
        PackageSessionLease sessionLease,
        RuntimeRpcActivationLease callerLease,
        RuntimeRpcActivationLease calleeLease,
        RuntimeRpcPermissionLease? permission,
        CancellationTokenSource linked,
        CancellationTokenSource deadline,
        RuntimeRpcProviderActivation provider,
        string serviceId,
        SunderRpcMethodDescriptor method,
        SunderRpcContractDescriptor contract,
        RuntimeRpcCallFrame frame,
        SunderRpcInvocationContext context)
    {
        SessionLease = sessionLease;
        _callerLease = callerLease;
        _calleeLease = calleeLease;
        _permission = permission;
        _linked = linked;
        _deadline = deadline;
        Provider = provider;
        ServiceId = serviceId;
        Method = method;
        Contract = contract;
        Frame = frame;
        Context = context;
    }

    public PackageSessionLease SessionLease { get; }
    public RuntimeRpcProviderActivation Provider { get; }
    public string ServiceId { get; }
    public SunderRpcMethodDescriptor Method { get; }
    public SunderRpcContractDescriptor Contract { get; }
    public RuntimeRpcCallFrame Frame { get; }
    public SunderRpcInvocationContext Context { get; }
    public CancellationToken CancellationToken => _linked.Token;
    public bool DeadlineElapsed => _deadline.IsCancellationRequested;
    public bool ProviderRetirementRequested =>
        Provider.Snapshot.State != SunderRpcProviderState.Active
        || _calleeLease.RetirementToken.IsCancellationRequested;

    public void Dispose()
    {
        Context.Revoke();
        _linked.Dispose();
        _deadline.Dispose();
        _permission?.Dispose();
        _calleeLease.Dispose();
        _callerLease.Dispose();
        SessionLease.Dispose();
    }
}

internal sealed record RuntimeRpcCallFrame(int Depth, DateTimeOffset DeadlineUtc);
