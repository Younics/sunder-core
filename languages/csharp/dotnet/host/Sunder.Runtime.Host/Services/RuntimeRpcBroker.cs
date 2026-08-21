using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;
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

    internal IDisposable EnterCallFrame(RuntimeRpcCallFrame? frame)
    {
        var previous = CurrentCall.Value;
        CurrentCall.Value = MergeCallFrames(previous, frame);
        return new CurrentCallFrameLease(previous);
    }

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

    public async ValueTask CloseAppCallScopeAsync(
        string appSessionId,
        string callScopeId)
    {
        var caller = GetAppCaller(appSessionId);
        if (string.IsNullOrWhiteSpace(callScopeId)
            || !_callScopes.TryGetValue(callScopeId, out var scope))
        {
            return;
        }
        if (!ReferenceEquals(scope.Caller, caller))
        {
            throw Unavailable("rpc.scope.unavailable", "The RPC call scope is stale or unavailable.");
        }
        await CloseCallScopeAsync(scope).ConfigureAwait(false);
    }

    private RuntimeRpcCallerScopeState CreateCallScope(
        IRuntimeRpcCallerActivation caller,
        SunderRpcCallOptions? options,
        bool requirePermission,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var sessionLease = AcquireSessionLease(caller);
        EnsureCurrent(caller, sessionLease);
        var now = _timeProvider.GetUtcNow();
        var parent = CurrentCall.Value;
        var deadline = GetEffectiveScopeDeadline(options, parent, now);
        if (deadline <= now) throw DeadlineExceeded();
        var scope = new RuntimeRpcCallerScopeState(
            "rpc-scope-" + Guid.NewGuid().ToString("N"),
            caller,
            sessionLease.Generation,
            sessionLease.GenerationIdentity,
            deadline,
            parent,
            requirePermission,
            sessionLease.RetirementToken,
            sessionLease.GenerationRetirementToken,
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
        using var sessionLease = AcquireSessionLease(caller);
        EnsureCurrent(caller, sessionLease);
        EnsureScope(scope, caller, sessionLease);
        if (!TryGetEndpoint(caller, endpoint, out var provider, out _)
            || provider is null
            || !CanUseContract(caller, provider, SunderRpcProtocol.DiscoverAction, out _))
        {
            return ValueTask.FromResult<SunderRpcProviderSnapshot?>(null);
        }
        if (requirePermission)
        {
            using var permission = AcquirePermission(
                caller,
                provider,
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

        using var sessionLease = AcquireSessionLease(caller);
        if (!caller.IsCurrent(sessionLease)) return ValueTask.FromResult(false);
        EnsureScope(scope, caller, sessionLease);
        if (!TryGetEndpoint(caller, endpoint, out var provider, out _)
            || provider is null
            || provider.Snapshot.State != SunderRpcProviderState.Active
            || provider.Snapshot.SessionGeneration != sessionLease.Generation
            || _sessions.GetLoadedPackage(sessionLease, provider.Snapshot.PackageId) is not { } activePackage
            || activePackage.RuntimeActivationId != provider.Snapshot.ActivationId
            || !CanUseContract(caller, provider, SunderRpcProtocol.DiscoverAction, out _))
        {
            return ValueTask.FromResult(false);
        }

        RuntimeRpcPermissionLease? permission = null;
        if (requirePermission
            && !TryAcquirePermission(
                caller,
                provider,
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
        using var sessionLease = AcquireSessionLease(caller);
        EnsureCurrent(caller, sessionLease);
        EnsureScope(scope, caller, sessionLease);
        _ = FindUse(caller, contractId, SunderRpcProtocol.DiscoverAction)
            ?? throw PermissionDenied(contractId, SunderRpcProtocol.DiscoverAction);
        RuntimeRpcPermissionLease? permission = null;
        var permissionGranted = !requirePermission
                                || TryAcquirePermission(
                                    caller,
                                    contractId,
                                    SunderRpcProtocol.DiscoverAction,
                                    out permission);
        using (permission)
        {
            var snapshot = GetSnapshot(caller, provider =>
                string.Equals(provider.Snapshot.ContractId, contractId, StringComparison.Ordinal)
                && CanUseContract(caller, provider, SunderRpcProtocol.DiscoverAction, out _)
                && (permissionGranted || IsOwnerProvider(caller, provider)));
            if (!permissionGranted && snapshot.Providers.Count == 0)
            {
                throw PermissionDenied(contractId, SunderRpcProtocol.DiscoverAction);
            }
            return ValueTask.FromResult(snapshot);
        }
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
        using var sessionLease = AcquireSessionLease(caller);
        EnsureCurrent(caller, sessionLease);
        EnsureScope(scope, caller, sessionLease);
        if (caller is RuntimeRpcPackageActivation packageCaller
            && _catalog.IsCandidateCaller(packageCaller))
        {
            throw Unavailable(
                "rpc.catalog.activating",
                "RPC catalog watches are unavailable until the candidate Runtime generation is published.");
        }
        using var callerLease = AcquireCallerLease(caller);
        var discoverUses = caller.RpcContractUses.Where(use =>
                string.Equals(use.Role, caller.Role, StringComparison.Ordinal)
                && (use.Actions ?? []).Contains(SunderRpcProtocol.DiscoverAction, StringComparer.Ordinal))
            .ToArray();
        if (discoverUses.Length == 0)
        {
            throw PermissionDenied("*", SunderRpcProtocol.DiscoverAction);
        }
        var permissionLeases = new List<RuntimeRpcPermissionLease>();
        var permittedContracts = new HashSet<string>(StringComparer.Ordinal);
        var ownerOnlyContracts = new HashSet<string>(StringComparer.Ordinal);
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
                else
                {
                    var ownerProvider = GetSnapshot(caller, provider =>
                        string.Equals(provider.Snapshot.ContractId, use.ContractId, StringComparison.Ordinal)
                        && IsOwnerProvider(caller, provider)
                        && CanUseContract(caller, provider, SunderRpcProtocol.DiscoverAction, out _));
                    if (ownerProvider.Providers.Count > 0)
                    {
                        permittedContracts.Add(use.ContractId!);
                        ownerOnlyContracts.Add(use.ContractId!);
                    }
                }
            }
            if (permittedContracts.Count == 0)
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
                sessionLease.GenerationRetirementToken,
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
                RuntimeRpcProviderActivation? provider = null;
                if (item.Provider is not null)
                {
                    _catalog.TryGetActiveEndpoint(item.Provider.Endpoint, out provider, out _);
                }
                if (item.Provider is null
                    || provider is null
                    || !permittedContracts.Contains(item.Provider.ContractId)
                    || (ownerOnlyContracts.Contains(item.Provider.ContractId)
                        && !IsOwnerProvider(caller, provider))
                    || !CanUseContract(caller, provider, SunderRpcProtocol.DiscoverAction, out _))
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
        RuntimeRpcCallLease call;
        try
        {
            call = AcquireCall(
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
        }
        catch (SunderRpcException exception)
        {
            throw StampNotDispatched(exception);
        }
        using (call)
        {
            var previous = CurrentCall.Value;
            CurrentCall.Value = call.Frame;
            var dispatched = false;
            try
            {
                JsonElement response;
                try
                {
                    var requestClone = request.Clone();
                    dispatched = true;
                    response = await call.Provider.Registration.Handler.InvokeUnaryAsync(
                        call.Context,
                        serviceId,
                        methodId,
                        requestClone,
                        call.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    throw await HandleProviderExceptionAsync(call, exception).ConfigureAwait(false);
                }
                ThrowIfCallCancelled(call);
                JsonElement clone;
                try
                {
                    clone = response.Clone();
                }
                catch (Exception)
                {
                    throw await FaultProviderAsync(
                        call,
                        "rpc.provider.invalid-json",
                        "The provider returned an invalid JSON value.",
                        RuntimeRpcSemanticValidationException.InvalidJson()).ConfigureAwait(false);
                }
                await ValidateOutputAsync(call, clone).ConfigureAwait(false);
                ThrowIfCallCancelled(call);
                return clone;
            }
            catch (SunderRpcException exception)
            {
                throw StampCallFailure(call, exception, dispatched, isStream: false);
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
        RuntimeRpcCallLease call;
        try
        {
            call = AcquireCall(
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
        }
        catch (SunderRpcException exception)
        {
            throw StampNotDispatched(exception);
        }
        using (call)
        {
            var previous = CurrentCall.Value;
            CurrentCall.Value = call.Frame;
            var eventCount = 0;
            var windowStarted = Stopwatch.GetTimestamp();
            var eventsInWindow = 0;
            var dispatched = false;
            try
            {
                IAsyncEnumerable<JsonElement> stream;
                try
                {
                    var requestClone = request.Clone();
                    dispatched = true;
                    stream = call.Provider.Registration.Handler.InvokeServerStreamAsync(
                                 call.Context,
                                 serviceId,
                                 methodId,
                                 requestClone,
                                 call.CancellationToken)
                             ?? throw new InvalidOperationException("The provider returned a null event stream.");
                }
                catch (Exception exception)
                {
                    throw StampCallFailure(
                        call,
                        await HandleProviderExceptionAsync(call, exception).ConfigureAwait(false),
                        dispatched,
                        isStream: true);
                }

                IAsyncEnumerator<JsonElement> enumerator;
                try
                {
                    enumerator = stream.GetAsyncEnumerator(call.CancellationToken);
                }
                catch (Exception exception)
                {
                    throw StampCallFailure(
                        call,
                        await HandleProviderExceptionAsync(call, exception).ConfigureAwait(false),
                        dispatched,
                        isStream: true);
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
                            throw StampCallFailure(
                                call,
                                await HandleProviderExceptionAsync(call, exception).ConfigureAwait(false),
                                dispatched,
                                isStream: true);
                        }
                        if (!hasNext) break;
                        ThrowIfStreamCallCancelled(call);
                        if (++eventCount > _policy.MaxStreamEvents)
                        {
                            throw StampCallFailure(
                                call,
                                ResourceExhausted("rpc.stream.event-limit", "The RPC stream exceeded its event limit."),
                                dispatched,
                                isStream: true);
                        }
                        var elapsed = Stopwatch.GetElapsedTime(windowStarted);
                        if (elapsed >= TimeSpan.FromSeconds(1))
                        {
                            windowStarted = Stopwatch.GetTimestamp();
                            eventsInWindow = 0;
                        }
                        if (++eventsInWindow > _policy.MaxStreamEventsPerSecond)
                        {
                            throw StampCallFailure(
                                call,
                                ResourceExhausted("rpc.stream.rate-limit", "The RPC stream exceeded its event-rate limit."),
                                dispatched,
                                isStream: true);
                        }
                        JsonElement item;
                        try
                        {
                            item = enumerator.Current.Clone();
                        }
                        catch (Exception)
                        {
                            throw StampCallFailure(
                                call,
                                await FaultProviderAsync(
                                    call,
                                    "rpc.provider.invalid-event",
                                    "The provider returned an invalid JSON event.",
                                    RuntimeRpcSemanticValidationException.InvalidJson()).ConfigureAwait(false),
                                dispatched,
                                isStream: true);
                        }
                        await ValidateStreamOutputAsync(call, item).ConfigureAwait(false);
                        yield return item;
                    }
                    ThrowIfStreamCallCancelled(call);
                }
                finally
                {
                    try
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        throw StampCallFailure(
                            call,
                            await HandleProviderExceptionAsync(call, exception).ConfigureAwait(false),
                            dispatched,
                            isStream: true);
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
        var sessionLease = AcquireSessionLease(caller);
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
            if (!TryGetEndpoint(caller, endpoint, out var provider, out var stale) || provider is null)
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
            if (!CanUseContract(caller, provider, action, out var callerContract))
            {
                throw PermissionDenied(provider.Snapshot.ContractId, action);
            }
            if (requirePermission)
            {
                permission = AcquirePermission(caller, provider, action);
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

            var parent = MergeCallFrames(CurrentCall.Value, scope?.ParentFrame);
            var depth = (parent?.Depth ?? 0) + 1;
            if (depth > _policy.MaxNestedCallDepth)
            {
                throw ResourceExhausted("rpc.call.depth-limit", "The nested RPC call depth limit was reached.");
            }
            var now = _timeProvider.GetUtcNow();
            var deadline = GetEffectiveDeadline(
                options,
                parent,
                now,
                TargetSupportsExtendedDeadline(provider),
                scope?.DeadlineUtc);
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
                sessionLease.GenerationRetirementToken,
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
                    provider.Owner,
                    _timeProvider);
            }
            return new RuntimeRpcCallLease(
                sessionLease,
                caller,
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
        using var sessionLease = AcquireSessionLease(scope.Caller);
        EnsureCurrent(scope.Caller, sessionLease);
        EnsureScope(scope, scope.Caller, sessionLease);
        var provider = GetContentTarget(scope, endpoint, sessionLease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            scope.RevocationToken,
            sessionLease.RetirementToken,
            sessionLease.GenerationRetirementToken,
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
        using var sessionLease = AcquireSessionLease(scope.Caller);
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
        var provider = GetActiveContentProvider(scope.Caller, providerEndpoint, sessionLease);
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
                () => ValidateActiveContentProvider(scope.Caller, provider, providerEndpoint));
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
        if (!TryGetEndpoint(scope.Caller, endpoint, out var provider, out var stale) || provider is null)
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
            if (!CanUseContract(scope.Caller, provider, action, out _)) continue;
            if (!scope.RequirePermission) return provider;
            if (TryAcquirePermission(scope.Caller, provider, action, out var permission))
            {
                permission!.Dispose();
                return provider;
            }
        }
        throw PermissionDenied(provider.Snapshot.ContractId, "invoke or subscribe");
    }

    private RuntimeRpcProviderActivation GetActiveContentProvider(
        IRuntimeRpcCallerActivation caller,
        RuntimeRpcContentEndpoint endpoint,
        PackageSessionLease sessionLease)
    {
        if (!TryGetEndpoint(
                caller,
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
        IRuntimeRpcCallerActivation caller,
        RuntimeRpcProviderActivation expectedProvider,
        RuntimeRpcContentEndpoint endpoint)
    {
        if (!TryGetEndpoint(
                caller,
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
            : throw StaleEndpoint("rpc.caller-retired", "The caller package activation is retired.");

    private PackageSessionLease AcquireSessionLease(IRuntimeRpcCallerActivation caller)
    {
        if (caller is RuntimeRpcPackageActivation { AllowsPrePublicationLease: true } candidate)
        {
            return _sessions.AcquirePackageActivationLease(
                candidate.PackageId,
                new RuntimePackageGeneration(
                    candidate.Package.RuntimeActivationId,
                    candidate.ActivationGeneration));
        }

        var lease = _sessions.AcquireLease();
        if (caller is RuntimeRpcPackageActivation package)
        {
            _ = _sessions.GetLoadedPackage(lease, package.PackageId);
        }
        return lease;
    }

    private bool TryGetEndpoint(
        IRuntimeRpcCallerActivation caller,
        SunderRpcEndpointReference endpoint,
        out RuntimeRpcProviderActivation? provider,
        out bool stale)
        => caller is RuntimeRpcPackageActivation package
            ? _catalog.TryGetEndpoint(package, endpoint, out provider, out stale)
            : _catalog.TryGetActiveEndpoint(endpoint, out provider, out stale);

    private SunderRpcCatalogSnapshot GetSnapshot(
        IRuntimeRpcCallerActivation caller,
        Func<RuntimeRpcProviderActivation, bool> filter)
        => caller is RuntimeRpcPackageActivation package
            ? _catalog.GetSnapshot(package, filter)
            : _catalog.GetSnapshot(filter);

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
            throw StaleEndpoint("rpc.caller-retired", "The caller package activation is retired.");
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
            || !ReferenceEquals(scope.SessionGenerationIdentity, sessionLease.GenerationIdentity)
            || scope.SessionGeneration != sessionLease.Generation)
        {
            throw Unavailable("rpc.scope.unavailable", "The RPC call scope is stale or unavailable.");
        }
    }

    private DateTimeOffset GetEffectiveDeadline(
        SunderRpcCallOptions? options,
        RuntimeRpcCallFrame? parent,
        DateTimeOffset now,
        bool targetSupportsExtendedDeadline,
        DateTimeOffset? scopeDeadline = null)
    {
        var defaultDeadline = now + _policy.DefaultDeadline;
        var maximumDeadline = targetSupportsExtendedDeadline
            ? now + _policy.MaximumExtendedDeadline
            : defaultDeadline;
        var requestedDeadline = options?.DeadlineUtc ?? scopeDeadline;
        var deadline = requestedDeadline is { } requested
            ? requested < maximumDeadline ? requested : maximumDeadline
            : defaultDeadline;
        if (parent is not null && parent.DeadlineUtc < deadline) deadline = parent.DeadlineUtc;
        if (scopeDeadline is { } scoped && scoped < deadline) deadline = scoped;
        return deadline;
    }

    private DateTimeOffset GetEffectiveScopeDeadline(
        SunderRpcCallOptions? options,
        RuntimeRpcCallFrame? parent,
        DateTimeOffset now)
    {
        var defaultDeadline = now + _policy.DefaultDeadline;
        var maximumDeadline = now + _policy.MaximumExtendedDeadline;
        var deadline = options?.DeadlineUtc is { } requestedDeadline
            ? requestedDeadline < maximumDeadline ? requestedDeadline : maximumDeadline
            : defaultDeadline;
        if (parent is not null && parent.DeadlineUtc < deadline) deadline = parent.DeadlineUtc;
        return deadline;
    }

    private static RuntimeRpcCallFrame? MergeCallFrames(
        RuntimeRpcCallFrame? first,
        RuntimeRpcCallFrame? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        return new RuntimeRpcCallFrame(
            Math.Max(first.Depth, second.Depth),
            first.DeadlineUtc < second.DeadlineUtc ? first.DeadlineUtc : second.DeadlineUtc);
    }

    private static bool TargetSupportsExtendedDeadline(RuntimeRpcProviderActivation provider)
        => provider.Owner.Package.Source.SelectedTarget?.RequiredHostCapabilities?.Contains(
            SunderSdkCapabilities.RpcExtendedDeadlineV1,
            StringComparer.Ordinal) == true;

    private RuntimeRpcPermissionLease AcquirePermission(
        IRuntimeRpcCallerActivation caller,
        string contractId,
        string action)
        => TryAcquirePermission(caller, contractId, action, out var lease) && lease is not null
            ? lease
            : throw PermissionDenied(contractId, action);

    private RuntimeRpcPermissionLease AcquirePermission(
        IRuntimeRpcCallerActivation caller,
        RuntimeRpcProviderActivation provider,
        string action)
        => TryAcquirePermission(caller, provider, action, out var lease) && lease is not null
            ? lease
            : throw PermissionDenied(provider.Snapshot.ContractId, action);

    private bool TryAcquirePermission(
        IRuntimeRpcCallerActivation caller,
        RuntimeRpcProviderActivation provider,
        string action,
        out RuntimeRpcPermissionLease? lease)
    {
        if (IsOwnerProvider(caller, provider))
        {
            lease = new RuntimeRpcPermissionLease(caller.RetirementToken);
            return true;
        }
        return TryAcquirePermission(caller, provider.Snapshot.ContractId, action, out lease);
    }

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
        var use = FindUse(caller, contractId, action);
        if (use?.ContractVersion is null || use.AbiSha256 is null)
        {
            lease = null;
            return false;
        }
        return _permissions.TryAcquire(
            new RuntimeRpcPermissionKey(
                caller.PackageId,
                caller.PackageVersion,
                caller.ManifestSha256,
                caller.Role,
                contractId,
                use.ContractVersion,
                use.AbiSha256,
                action),
            out lease);
    }

    private static bool CanUseContract(
        IRuntimeRpcCallerActivation caller,
        RuntimeRpcProviderActivation provider,
        string action,
        out SunderRpcContractDescriptor? descriptor)
    {
        descriptor = null;
        var snapshot = provider.Snapshot;
        var registration = provider.Registration;
        var declaration = registration.Manifest;
        var use = FindUse(caller, snapshot.ContractId, action);
        if (use?.ContractVersion is null
            || declaration is null
            || !string.Equals(declaration.Role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal)
            || !AudienceAllows(caller.PackageId, snapshot.PackageId, declaration.Audience)
            || !string.Equals(use.ContractVersion, snapshot.ContractVersion, StringComparison.Ordinal)
            || !string.Equals(declaration.ContractId, snapshot.ContractId, StringComparison.Ordinal)
            || !string.Equals(declaration.ContractVersion, snapshot.ContractVersion, StringComparison.Ordinal)
            || !string.Equals(registration.ContractId, snapshot.ContractId, StringComparison.Ordinal)
            || !string.Equals(registration.ContractVersion, snapshot.ContractVersion, StringComparison.Ordinal)
            || !string.Equals(registration.Contract.ContractId, snapshot.ContractId, StringComparison.Ordinal)
            || !string.Equals(registration.Contract.Version, snapshot.ContractVersion, StringComparison.Ordinal)
            || !string.Equals(registration.DescriptorSha256, declaration.DescriptorSha256, StringComparison.Ordinal)
            || !string.Equals(registration.Contract.Sha256, declaration.DescriptorSha256, StringComparison.Ordinal)
            || !string.Equals(snapshot.DescriptorSha256, declaration.DescriptorSha256, StringComparison.Ordinal)
            || !string.Equals(snapshot.AbiSha256, declaration.AbiSha256, StringComparison.Ordinal)
            || !caller.RpcContracts.TryGetValue(
                PackageSessionPreparer.ContractKey(snapshot.ContractId, use.ContractVersion),
                out descriptor)
            || !string.Equals(descriptor.ContractId, snapshot.ContractId, StringComparison.Ordinal)
            || !string.Equals(descriptor.Version, use.ContractVersion, StringComparison.Ordinal))
        {
            descriptor = null;
            return false;
        }

        return descriptor.DescriptorVersion == SunderRpcProtocol.DescriptorVersionV2
               && registration.Contract.DescriptorVersion == SunderRpcProtocol.DescriptorVersionV2
               && use.AbiSha256 is not null
               && declaration.AbiSha256 is not null
               && string.Equals(descriptor.AbiSha256, use.AbiSha256, StringComparison.Ordinal)
               && string.Equals(use.AbiSha256, declaration.AbiSha256, StringComparison.Ordinal)
               && string.Equals(registration.Contract.AbiSha256, declaration.AbiSha256, StringComparison.Ordinal);
    }

    private static bool AudienceAllows(string callerPackageId, string providerPackageId, string? audience)
        => audience == SunderPackageFormat.PublicProviderAudience
           || (audience == SunderPackageFormat.OwnerProviderAudience
               && string.Equals(callerPackageId, providerPackageId, StringComparison.Ordinal));

    private static bool IsOwnerProvider(
        IRuntimeRpcCallerActivation caller,
        RuntimeRpcProviderActivation provider)
        => provider.Registration.Manifest?.Audience == SunderPackageFormat.OwnerProviderAudience
           && string.Equals(caller.PackageId, provider.Snapshot.PackageId, StringComparison.Ordinal);

    private static Sunder.Package.Format.SunderPackageContractUseManifest? FindUse(
        IRuntimeRpcCallerActivation caller,
        string contractId,
        string action)
        => caller.RpcContractUses.FirstOrDefault(use =>
            string.Equals(use.Role, caller.Role, StringComparison.Ordinal)
            && string.Equals(use.ContractId, contractId, StringComparison.Ordinal)
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
        _ = MeasurePayload(contract, schemaReference, value, label);
        if (!contract.IsValid(schemaReference, value, out var error))
        {
            throw Validation(
                $"rpc.{label}.schema-invalid",
                $"The RPC {label} does not match its contract schema: {Sanitize(error)}");
        }
    }

    private RuntimeRpcValueMeasurement MeasurePayload(
        SunderRpcContractDescriptor contract,
        string schemaReference,
        JsonElement value,
        string label)
    {
        try
        {
            var measurement = RuntimeRpcValueMeasurer.Measure(value, _policy);
            return measurement with
            {
                InlineDecodedBytes = RuntimeRpcInlineDecodedMeasurer.Measure(
                    contract,
                    schemaReference,
                    value,
                    _policy.MaxInlineDecodedBytes),
            };
        }
        catch (RuntimeRpcValueLimitException exception)
        {
            throw ResourceExhausted(
                $"rpc.{label}.{GetValueLimitName(exception.LimitKind)}-limit",
                $"The RPC {label} exceeds its {GetValueLimitName(exception.LimitKind)} limit.");
        }
    }

    private async ValueTask ValidateOutputAsync(RuntimeRpcCallLease call, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw await FaultProviderAsync(
                call,
                "rpc.provider.invalid-output",
                "The provider returned output that violates its contract.",
                RuntimeRpcSemanticValidationException.Undefined()).ConfigureAwait(false);
        }

        var measurement = MeasurePayload(
            call.Contract,
            call.Method.OutputSchemaReference,
            value,
            "output");
        if (!call.Contract.IsValid(call.Method.OutputSchemaReference, value, out var error))
        {
            throw await FaultProviderAsync(
                call,
                "rpc.provider.invalid-output",
                "The provider returned output that violates its contract.",
                RuntimeRpcSemanticValidationException.Schema(error, measurement)).ConfigureAwait(false);
        }
    }

    private async ValueTask<SunderRpcException> HandleProviderExceptionAsync(
        RuntimeRpcCallLease call,
        Exception exception)
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
                if (!call.Method.Errors.Contains(rpcException.Error.Code, StringComparer.Ordinal))
                {
                    return await FaultProviderAsync(
                        call,
                        "rpc.provider.undeclared-domain-error",
                        "The provider returned a domain error that is not declared by the RPC method.",
                        rpcException).ConfigureAwait(false);
                }
                var declaration = call.Contract.FindError(rpcException.Error.Code);
                if (rpcException.Error.Details is { } details)
                {
                    if (declaration?.DetailsSchema is null)
                    {
                        return await FaultProviderAsync(
                            call,
                            "rpc.provider.invalid-error-details",
                            "The provider returned domain error details that are not declared by the contract.",
                            rpcException).ConfigureAwait(false);
                    }
                    RuntimeRpcValueMeasurement measurement;
                    try
                    {
                        measurement = MeasurePayload(
                            call.Contract,
                            declaration.DetailsSchema,
                            details,
                            "error-details");
                    }
                    catch (SunderRpcException resourceException)
                    {
                        return resourceException;
                    }
                    if (!call.Contract.IsValid(declaration.DetailsSchema, details, out var error))
                    {
                        return await FaultProviderAsync(
                            call,
                            "rpc.provider.invalid-error-details",
                            "The provider returned domain error details that violate the contract.",
                            RuntimeRpcSemanticValidationException.Schema(error, measurement)).ConfigureAwait(false);
                    }
                }
                return new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.Domain,
                    rpcException.Error.Code,
                    Sanitize(rpcException.Error.Message),
                    details: rpcException.Error.Details));
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
        return await FaultProviderAsync(
            call,
            "rpc.provider.handler-fault",
            "The provider handler violated the RPC invocation contract.",
            exception).ConfigureAwait(false);
    }

    private void ThrowIfStreamCallCancelled(RuntimeRpcCallLease call)
    {
        try
        {
            ThrowIfCallCancelled(call);
        }
        catch (SunderRpcException exception)
        {
            throw StampCallFailure(call, exception, dispatched: true, isStream: true);
        }
    }

    private async ValueTask ValidateStreamOutputAsync(RuntimeRpcCallLease call, JsonElement value)
    {
        try
        {
            await ValidateOutputAsync(call, value).ConfigureAwait(false);
            ThrowIfCallCancelled(call);
        }
        catch (SunderRpcException exception)
        {
            throw StampCallFailure(call, exception, dispatched: true, isStream: true);
        }
    }

    private static SunderRpcException StampNotDispatched(SunderRpcException exception)
        => RuntimeRpcFailureContextStore.Copy(
            exception,
            exception.WithDispatchMetadata(
                SunderRpcDispatchState.NotDispatched,
                SunderRpcRetrySafety.Safe));

    private static SunderRpcException StampCallFailure(
        RuntimeRpcCallLease call,
        SunderRpcException exception,
        bool dispatched,
        bool isStream)
    {
        var dispatchState = dispatched
            ? SunderRpcDispatchState.PossiblyDispatched
            : SunderRpcDispatchState.NotDispatched;
        var retrySafety = !dispatched
                          || !isStream
                          && call.Method.Idempotency == SunderRpcMethodIdempotency.Idempotent
            ? SunderRpcRetrySafety.Safe
            : SunderRpcRetrySafety.Unsafe;
        return RuntimeRpcFailureContextStore.Copy(
            exception,
            exception.WithDispatchMetadata(dispatchState, retrySafety));
    }

    private void ThrowIfCallCancelled(RuntimeRpcCallLease call)
    {
        if (call.ProviderRetirementRequested || call.CancellationToken.IsCancellationRequested)
        {
            throw call.ProviderRetirementRequested
                ? SunderRpcException.Infrastructure(
                    SunderRpcErrorKind.StaleEndpoint,
                    "rpc.endpoint.stale",
                    "The RPC endpoint identifies a retired provider activation.")
                : call.DeadlineElapsed || _timeProvider.GetUtcNow() >= call.Frame.DeadlineUtc
                    ? DeadlineExceeded()
                    : SunderRpcException.Infrastructure(
                        SunderRpcErrorKind.Cancelled,
                        "rpc.call.cancelled",
                        "The RPC call was cancelled.");
        }
    }

    private async ValueTask<SunderRpcException> FaultProviderAsync(
        RuntimeRpcCallLease call,
        string code,
        string message,
        Exception exception)
    {
        var provider = call.Provider.Snapshot;
        var failure = RuntimeRpcFailureContextStore.AttachProviderFault(
            SunderRpcException.Infrastructure(SunderRpcErrorKind.ProviderFaulted, code, message),
            call.Caller.PackageId,
            call.Caller.PackageVersion,
            call.Caller.ActivationId,
            provider,
            call.ServiceId,
            call.Method.MethodId,
            GetCallKind(call.Method.Kind),
            exception);
        if (!RuntimeRpcFailureContextStore.TryGet(failure, out var diagnostic))
        {
            throw new InvalidOperationException("A provider fault did not retain its Host diagnostic context.");
        }
        _sessionOwner.LogRpcProviderFaultDiagnostic(diagnostic);
        if (call.Provider.Owner.Package.Worker is ProcessRuntimeWorker processWorker
            && exception is not ProcessRpcProviderFaultException { ProviderExceptionType: not null })
        {
            await processWorker.WriteHostProviderFaultDiagnosticAsync(diagnostic).ConfigureAwait(false);
        }
        var identity = call.SessionLease.GetPackageActivationIdentity(call.Provider.Owner.Package);
        _sessionOwner.HandleRpcProviderFault(
            provider.PackageId,
            identity,
            exception,
            code);
        return failure;
    }

    private static string GetCallKind(SunderRpcMethodKind kind)
        => kind == SunderRpcMethodKind.Unary ? "unary" : "server-stream";

    private static string GetValueLimitName(RuntimeRpcValueLimitKind kind)
        => kind switch
        {
            RuntimeRpcValueLimitKind.Bytes => "byte",
            RuntimeRpcValueLimitKind.Depth => "depth",
            RuntimeRpcValueLimitKind.Nodes => "node",
            RuntimeRpcValueLimitKind.ObjectMembers => "object-member",
            RuntimeRpcValueLimitKind.InlineDecodedBytes => "inline-decoded-byte",
            _ => "resource",
        };

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

    private static SunderRpcException StaleEndpoint(string code, string message)
        => SunderRpcException.Infrastructure(SunderRpcErrorKind.StaleEndpoint, code, message);

    private static SunderRpcException Unavailable(string code, string message)
        => SunderRpcException.Infrastructure(SunderRpcErrorKind.Unavailable, code, message);

    private static SunderRpcException DeadlineExceeded()
        => SunderRpcException.Infrastructure(
            SunderRpcErrorKind.DeadlineExceeded,
            "rpc.call.deadline-exceeded",
            "The RPC call deadline elapsed.");

    private sealed class CurrentCallFrameLease(RuntimeRpcCallFrame? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) CurrentCall.Value = previous;
        }
    }
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
        object sessionGenerationIdentity,
        DateTimeOffset deadlineUtc,
        RuntimeRpcCallFrame? parentFrame,
        bool requirePermission,
        CancellationToken sessionRetirement,
        CancellationToken generationRetirement,
        CancellationToken hostStopping)
    {
        Id = id;
        Caller = caller;
        SessionGeneration = sessionGeneration;
        SessionGenerationIdentity = sessionGenerationIdentity;
        DeadlineUtc = deadlineUtc;
        ParentFrame = parentFrame;
        RequirePermission = requirePermission;
        ContentAuthority = new RuntimeRpcContentAuthority(id);
        _revocation = CancellationTokenSource.CreateLinkedTokenSource(
            [
                caller.RetirementToken,
                sessionRetirement,
                generationRetirement,
                hostStopping,
            ]);
    }

    public string Id { get; }
    public IRuntimeRpcCallerActivation Caller { get; }
    public long SessionGeneration { get; }
    public object SessionGenerationIdentity { get; }
    public DateTimeOffset DeadlineUtc { get; }
    public RuntimeRpcCallFrame? ParentFrame { get; }
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
        IRuntimeRpcCallerActivation caller,
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
        Caller = caller;
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
    public IRuntimeRpcCallerActivation Caller { get; }
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
