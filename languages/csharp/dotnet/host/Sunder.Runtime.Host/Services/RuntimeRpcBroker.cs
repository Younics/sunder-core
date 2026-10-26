using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class RuntimeRpcBroker
{
    internal const string InvariantViolationFaultCode = "rpc.provider.invariant-violation";
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
    private readonly RuntimeRpcCallScopeRegistry _callScopeRegistry;
    private readonly PackagePolicyAdmission? _packagePolicy;
    private readonly RuntimeRpcReadinessEvaluator? _readiness;

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
        RuntimeTransportPolicyOptions? transportPolicy = null,
        PackagePolicyAdmission? packagePolicy = null,
        RuntimeRpcReadinessEvaluator? readiness = null)
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
            transportPolicy,
            packagePolicy,
            readiness)
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
        RuntimeTransportPolicyOptions? transportPolicy = null,
        PackagePolicyAdmission? packagePolicy = null,
        RuntimeRpcReadinessEvaluator? readiness = null)
    {
        _catalog = catalog;
        _packagePolicy = packagePolicy;
        _readiness = readiness;
        _permissions = permissions;
        _sessions = sessions;
        _sessionOwner = sessionOwner;
        _policy = policy ?? new RuntimeRpcPolicyOptions();
        _appSessions = appSessions;
        _contentStore = contentStore;
        _transportPolicy = transportPolicy ?? new RuntimeTransportPolicyOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _hostStopping = hostStopping;
        _callScopeRegistry = new RuntimeRpcCallScopeRegistry(
            _policy.MaxCallScopesPerCaller,
            _timeProvider,
            _hostStopping,
            _contentStore is null ? null : _contentStore.DiscardRpcContentAuthority);
        _stackHostCaller = new RuntimeRpcHostCallerActivation(
            StackTransport.CoreStackContract.Descriptor,
            hostStopping);
    }

    public ISunderRpcClient CreateStackHostClient()
        => new RuntimeRpcHostClient(this, _stackHostCaller);

    internal Task RequirePackagePolicyAsync(PreparedRuntimePackage package, CancellationToken token)
        => _packagePolicy?.RequireAsync(package.Source, token) ?? Task.CompletedTask;

    internal void RequirePackagePolicy(RuntimePackageSource source)
    {
        try { _packagePolicy?.Require(source); }
        catch (PackagePolicyDeniedException error) { throw RuntimeRpcPolicyDenials.ToException(error); }
    }

    internal RuntimeRpcActivationLease? AcquirePublishedPackageDataLease(
        RuntimeRpcCallerStamp stamp, long activationGeneration, IRuntimePackageWorker worker)
    {
        if (!_catalog.TryGetPackage(stamp.PackageId, stamp.ActivationId, out var caller)
            || caller is null || caller.AllowsPrePublicationLease) return null;
        using var admission = _sessions.AcquireRpcLease(caller);
        if (!caller.IsCurrent(admission) || caller.ActivationGeneration != activationGeneration
            || !ReferenceEquals(caller.Package.Worker, worker))
            throw new RuntimeUnavailableException("The package data operation does not own the exact Worker birth.");
        RequirePackagePolicy(caller.Package.Source);
        if (!caller.TryAcquireCallee(out var lease) || lease is null)
            throw new RuntimeUnavailableException("The package data activation is retired.");
        return lease;
    }

    private void RequireCallerPolicy(IRuntimeRpcCallerActivation caller)
    {
        if (caller is RuntimeRpcPackageActivation package) RequirePackagePolicy(package.Package.Source);
        if (caller is RuntimeRpcAppCallerSession app) RequirePackagePolicy(app.Source);
        if (_readiness is not null && caller.SourceKind is not null
            && !(caller is RuntimeRpcPackageActivation candidate && _catalog.IsCandidateCaller(candidate))
            && _readiness.UnsatisfiedConsumers(_sessions.ActiveSession, caller.Role).Contains(caller.PackageId, StringComparer.Ordinal))
            throw Unavailable(Sunder.Registry.Contracts.RpcReadinessErrorCodes.RequiredProviderUnsatisfied, "A required provider is no longer available.");
    }

    internal IDisposable EnterCallFrame(RuntimeRpcCallFrame? frame)
    {
        var previous = CurrentCall.Value;
        CurrentCall.Value = RuntimeRpcCallFrameCalculator.Merge(previous, frame);
        return new CurrentCallFrameLease(previous);
    }

    internal RuntimeRpcCallFrame CaptureInvocationFrame(SunderRpcInvocationContext context)
        => CurrentCall.Value is { } frame
           && frame.Depth == context.CallDepth && frame.DeadlineUtc == context.DeadlineUtc
            ? frame
            : new RuntimeRpcCallFrame(context.CallDepth, context.DeadlineUtc);

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
        var outcome = await _callScopeRegistry.CloseAppAsync(appSessionId, callScopeId).ConfigureAwait(false);
        if (outcome == RuntimeRpcCallScopeRegistry.Outcome.CallerMismatch)
        {
            throw Unavailable("rpc.scope.unavailable", "The RPC call scope is stale or unavailable.");
        }
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
        var deadline = RuntimeRpcCallFrameCalculator.CalculateScopeDeadline(
            _policy,
            options,
            parent,
            now);
        if (deadline <= now) throw DeadlineExceeded();
        var outcome = _callScopeRegistry.Register(
            caller,
            ScopeGeneration(caller, sessionLease),
            ScopeIdentity(caller, sessionLease),
            deadline,
            parent,
            requirePermission,
            sessionLease.RetirementToken,
            sessionLease.GenerationRetirementToken,
            deadline - now,
            out var scope);
        var result = RuntimeRpcCallAcquisitionPolicy.EvaluateScopeRegistration(outcome, scope);
        ThrowAcquisitionFailure(result.Failure);
        return result.Scope!;
    }

    internal RuntimeRpcCallerScopeState GetScope(
        IRuntimeRpcCallerActivation caller,
        string callScopeId)
    {
        var outcome = _callScopeRegistry.Get(caller, callScopeId, out var scope);
        var result = RuntimeRpcCallAcquisitionPolicy.EvaluateScopeLookup(outcome, scope);
        ThrowAcquisitionFailure(result.Failure);
        return result.Scope!;
    }

    internal async ValueTask CloseCallScopeAsync(RuntimeRpcCallerScopeState scope)
    {
        await _callScopeRegistry.CloseAsync(scope).ConfigureAwait(false);
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
        using var sessionLease = AcquireSessionLease(caller);
        if (!caller.IsCurrent(sessionLease)) return ValueTask.FromResult(false);
        EnsureScope(scope, caller, sessionLease);
        if (!TryGetEndpoint(caller, endpoint, out var provider, out _)
            || provider is null
            || provider.Snapshot.State != SunderRpcProviderState.Active
            || provider.Snapshot.SessionGeneration != provider.Owner.BindingGeneration
            || _sessions.GetLoadedPackage(sessionLease, provider.Snapshot.PackageId) is not { } activePackage
            || activePackage.RuntimeActivationId != provider.Snapshot.ActivationId
            || !OwnsReportedContract(caller, provider)
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

    private static bool OwnsReportedContract(IRuntimeRpcCallerActivation caller, RuntimeRpcProviderActivation provider)
    {
        if (caller is RuntimeRpcHostCallerActivation) return caller.RpcContracts.Values.Any(contract => contract.Sha256 == provider.Registration.DescriptorSha256);
        var source = caller switch
        {
            RuntimeRpcPackageActivation package => package.Package.Source,
            RuntimeRpcAppCallerSession app => app.Source,
            _ => null,
        };
        return source?.Manifest?.ContractBundles?.Any(bundle => bundle is not null && bundle.Source == "owned"
            && bundle.AuthorityKind == "package" && bundle.AuthorityId == caller.PackageId
            && bundle.ContractId == provider.Registration.ContractId && bundle.Version == provider.Registration.ContractVersion
            && bundle.DescriptorSha256 == provider.Registration.DescriptorSha256 && bundle.AbiSha256 == provider.Registration.Contract.AbiSha256) == true;
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
        CancellationToken sessionRetirement;
        CancellationToken generationRetirement;
        // Observation owns the caller activation, not an obsolete whole-session
        // object graph. Release the admission snapshot before the first await.
        using (var sessionLease = AcquireSessionLease(caller))
        {
            EnsureCurrent(caller, sessionLease);
            EnsureScope(scope, caller, sessionLease);
            if (caller is RuntimeRpcPackageActivation packageCaller && _catalog.IsCandidateCaller(packageCaller))
                throw Unavailable("rpc.catalog.activating", "RPC catalog watches are unavailable until the candidate Runtime generation is published.");
            sessionRetirement = sessionLease.RetirementToken;
            generationRetirement = sessionLease.GenerationRetirementToken;
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
            // Observe before permission reads so a concurrent grant/revocation
            // cannot leave an obsolete visibility set. Only this caller's exact
            // discovery keys matter; dispose all observers when the watch ends.
            if (caller.SourceKind != PackageSourceKind.Dev)
                foreach (var use in discoverUses)
                    permissionLeases.Add(_permissions.ObserveChanges(new RuntimeRpcPermissionKey(
                        caller.PackageId, caller.PackageVersion, caller.ManifestSha256, caller.Role,
                        use.ContractId!, use.ContractVersion!, use.AbiSha256!, SunderRpcProtocol.DiscoverAction)));
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
            var cancellationTokens = new List<CancellationToken>
            {
                cancellationToken,
                _hostStopping,
                sessionRetirement,
                generationRetirement,
                callerLease.RetirementToken,
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
            await using var events = subscription.Reader.ReadAllAsync(linked.Token).GetAsyncEnumerator();
            while (true)
            {
                bool hasNext;
                try { hasNext = await events.MoveNextAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (caller is RuntimeRpcAppCallerSession
                    && !cancellationToken.IsCancellationRequested && !_hostStopping.IsCancellationRequested
                    && (scope is null || _timeProvider.GetUtcNow() < scope.DeadlineUtc)
                    && (callerLease.RetirementToken.IsCancellationRequested || generationRetirement.IsCancellationRequested))
                {
                    // Catalog observation has no provider invocation to classify
                    // this linked cancellation. Preserve only proved App retirement;
                    // request/scope/permission cancellation keeps its own boundary.
                    throw RetiredAppCaller();
                }
                if (!hasNext) break;
                var item = events.Current;
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
            var callerGeneration = RuntimeRpcCallAcquisitionPolicy.ValidateCallerGeneration(
                caller.IsCurrent(sessionLease));
            ThrowAcquisitionFailure(callerGeneration.Failure);
            RequireCallerPolicy(caller);
            EnsureScope(scope, caller, sessionLease);
            var endpointFound = TryGetEndpoint(caller, endpoint, out var provider, out var stale)
                                && provider is not null;
            var endpointResult = RuntimeRpcCallAcquisitionPolicy.EvaluateEndpoint(endpointFound, stale);
            ThrowAcquisitionFailure(endpointResult.Failure);
            var acquiredProvider = provider!;
            var providerSnapshot = acquiredProvider.Snapshot;
            var calleePackage = _sessions.GetLoadedPackage(sessionLease, providerSnapshot.PackageId);
            var providerGeneration = RuntimeRpcCallAcquisitionPolicy.ValidateProviderGeneration(
                ReferenceEquals(calleePackage?.Worker, acquiredProvider.Owner.Package.Worker) ? calleePackage?.RuntimeActivationId : null,
                providerSnapshot,
                acquiredProvider.Owner.BindingGeneration);
            ThrowAcquisitionFailure(providerGeneration.Failure);
            var callerAction = RuntimeRpcCallAcquisitionPolicy.EvaluateCallerAction(
                caller,
                providerSnapshot,
                acquiredProvider.Registration,
                action,
                requirePermission);
            ThrowAcquisitionFailure(callerAction.Failure);
            var permissionRequirement = callerAction.Permission!;
            permission = AcquirePermission(caller, permissionRequirement);
            var permissionResult = RuntimeRpcCallAcquisitionPolicy.ValidatePermissionAcquisition(
                permissionRequirement,
                permissionRequirement.Kind == RuntimeRpcCallPermissionAcquisitionKind.None
                || permission is not null);
            ThrowAcquisitionFailure(permissionResult.Failure);
            var callerContract = callerAction.Contract!;
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

            var frameResult = RuntimeRpcCallAcquisitionPolicy.AcquireFrame(
                _policy,
                options,
                CurrentCall.Value,
                scope,
                _timeProvider,
                TargetSupportsExtendedDeadline(acquiredProvider),
                callSite: $"{providerSnapshot.PackageId}/{serviceId}.{methodId}");
            ThrowAcquisitionFailure(frameResult.Failure);
            var frame = frameResult.Frame!;
            var now = frameResult.AcquiredAtUtc!.Value;
            var observation = expectedKind == SunderRpcMethodKind.ServerStream;
            var callerLeaseResult = RuntimeRpcCallAcquisitionPolicy.ValidateLeaseAcquisition(
                RuntimeRpcCallLeaseKind.Caller,
                caller.TryAcquireCaller(observation ? _policy.MaxConcurrentObservationsPerCaller : _policy.MaxConcurrentCallsPerCaller,
                    out callerLease, observation)
                && callerLease is not null);
            ThrowAcquisitionFailure(callerLeaseResult.Failure);
            var acquiredCallerLease = callerLease!;
            var calleeLeaseResult = RuntimeRpcCallAcquisitionPolicy.ValidateLeaseAcquisition(
                RuntimeRpcCallLeaseKind.Provider,
                acquiredProvider.TryAcquireCallee(observation ? _policy.MaxConcurrentObservationsPerCallee : _policy.MaxConcurrentCallsPerCallee,
                    out calleeLease, observation)
                && calleeLease is not null);
            ThrowAcquisitionFailure(calleeLeaseResult.Failure);
            var acquiredCalleeLease = calleeLease!;

            deadlineCancellation = new CancellationTokenSource(frame.DeadlineUtc - now, _timeProvider);
            var cancellationTokens = new List<CancellationToken>
            {
                cancellationToken,
                _hostStopping,
                sessionLease.RetirementToken,
                sessionLease.GenerationRetirementToken,
                acquiredCallerLease.RetirementToken,
                acquiredCalleeLease.RetirementToken,
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
                    acquiredProvider.Snapshot.PackageId,
                    scope?.SessionGeneration ?? acquiredProvider.Owner.BindingGeneration,
                    new RuntimeRpcContentEndpoint(
                        acquiredProvider.Snapshot.Endpoint.Value,
                        acquiredProvider.Snapshot.ActivationId),
                    frame.DeadlineUtc,
                    scope?.ContentAuthority
                    ?? new RuntimeRpcContentAuthority(
                        "rpc-invocation-" + Guid.NewGuid().ToString("N")),
                    ownsContentAuthority: scope is null,
                    linked.Token,
                    acquiredProvider.Owner,
                    _timeProvider,
                    (caller as RuntimeRpcAppCallerSession)?.Authority);
            }
            IDisposable? sessionLifetime = sessionLease;
            var generationRetirement = sessionLease.GenerationRetirementToken;
            if (caller is RuntimeRpcPackageActivation { AllowsPrePublicationLease: false } or RuntimeRpcAppCallerSession)
            {
                // Exact activation leases now own execution. Do not root the old
                // session's unrelated packages/ALCs for a long-running call.
                sessionLease.Dispose();
                sessionLifetime = null;
            }
            return new RuntimeRpcCallLease(
                sessionLifetime,
                generationRetirement,
                caller,
                acquiredCallerLease,
                acquiredCalleeLease,
                permission,
                linked,
                deadlineCancellation,
                acquiredProvider,
                service.ServiceId,
                method,
                callerContract,
                frame,
                new SunderRpcInvocationContext(
                    caller.PackageId,
                    caller.PackageVersion,
                    acquiredProvider.Snapshot,
                    frame.DeadlineUtc,
                    frame.Depth,
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
        RuntimeRpcProviderActivation provider;
        using (var admission = AcquireSessionLease(scope.Caller))
        {
            EnsureCurrent(scope.Caller, admission);
            EnsureScope(scope, scope.Caller, admission);
            provider = GetContentTarget(scope, endpoint, admission);
        }
        using var callerLease = AcquireCallerLease(scope.Caller);
        if (!provider.TryAcquireCallee(_policy.MaxConcurrentObservationsPerCallee, out var providerLease, observation: true)
            || providerLease is null)
            throw ResourceExhausted("rpc.provider.observation-limit", "The provider observation limit was reached or its activation retired.");
        using var registrationLease = providerLease;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            scope.RevocationToken,
            callerLease.RetirementToken,
            providerLease.RetirementToken);
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
                scope.SessionGeneration,
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
        using var operation = (scope.Caller as RuntimeRpcAppCallerSession)?.Authority?.Acquire();
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
                scope.SessionGeneration,
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
            scope.SessionGeneration,
            providerEndpoint,
            scope.ContentAuthority)
            ?? throw NotFound(
                "rpc.content.unavailable",
                "The RPC content reference is stale, exhausted, or unavailable to this call scope.");
        IDisposable? streamLifetime = null;
        var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            scope.RevocationToken,
            provider.Owner.RetirementToken);
        try
        {
            var authority = (scope.Caller as RuntimeRpcAppCallerSession)?.Authority;
            streamLifetime = authority?.Acquire();
            Stream stream = new RuntimeRpcContentReadStream(
                lease.Content,
                () =>
                {
                    store.ReleaseRpcContent(lease);
                    readCancellation.Dispose();
                    streamLifetime?.Dispose();
                },
                readCancellation.Token,
                () => ValidateActiveContentProvider(scope.Caller, provider, providerEndpoint),
                authority is null ? null : authority.Acquire,
                authority is null ? null : authority.RecordCleanupFailure);
            return ValueTask.FromResult(stream);
        }
        catch
        {
            readCancellation.Dispose();
            store.ReleaseRpcContent(lease);
            streamLifetime?.Dispose();
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
            || !ReferenceEquals(calleePackage.Worker, provider.Owner.Package.Worker)
            || provider.Snapshot.SessionGeneration != provider.Owner.BindingGeneration
            || provider.Owner.RetirementToken.IsCancellationRequested)
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
            || provider.Snapshot.SessionGeneration != provider.Owner.BindingGeneration
            || provider.Owner.RetirementToken.IsCancellationRequested
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
    {
        if (!_catalog.TryGetPackage(stamp.PackageId, stamp.ActivationId, out var caller) || caller is null)
            throw StaleEndpoint("rpc.caller-retired", "The caller package activation is retired.");
        RequireCallerPolicy(caller);
        return caller;
    }

    private PackageSessionLease AcquireSessionLease(IRuntimeRpcCallerActivation caller)
    {
        if (caller is RuntimeRpcAppCallerSession app)
        {
            var operation = app.Authority?.Acquire();
            try
            {
                if (app.IsRetired) throw RetiredAppCaller();
                var appLease = _sessions.AcquireAppRpcLease(app);
                if (operation is not null) appLease.Own(operation);
                return appLease;
            }
            catch (RuntimeUnavailableException)
            {
                operation?.Dispose();
                throw app.IsRetired ? RetiredAppCaller()
                    : Unavailable("rpc.app-session.unavailable", "The App RPC activation is no longer current.");
            }
            catch { operation?.Dispose(); throw; }
        }
        if (caller is RuntimeRpcPackageActivation { AllowsPrePublicationLease: true } candidate)
        {
            return _sessions.AcquirePackageActivationLease(
                candidate.PackageId,
                new RuntimePackageGeneration(
                    candidate.Package.RuntimeActivationId,
                    candidate.ActivationGeneration));
        }

        var lease = caller is RuntimeRpcPackageActivation runtime
            ? _sessions.AcquireRpcLease(runtime)
            : _sessions.AcquireLease();
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
    {
        var found = caller is RuntimeRpcPackageActivation package
            ? _catalog.TryGetEndpoint(package, endpoint, out provider, out stale)
            : _catalog.TryGetActiveEndpoint(endpoint, out provider, out stale);
        if (found && provider is not null) RequirePackagePolicy(provider.Owner.Package.Source);
        return found;
    }

    private SunderRpcCatalogSnapshot GetSnapshot(
        IRuntimeRpcCallerActivation caller,
        Func<RuntimeRpcProviderActivation, bool> filter)
        => caller is RuntimeRpcPackageActivation package
            ? _catalog.GetSnapshot(package, provider => ProviderPolicyAllows(provider) && filter(provider))
            : _catalog.GetSnapshot(provider => ProviderPolicyAllows(provider) && filter(provider));

    private bool ProviderPolicyAllows(RuntimeRpcProviderActivation provider)
    {
        try { _packagePolicy?.Require(provider.Owner.Package.Source); return true; }
        catch (PackagePolicyDeniedException) { return false; }
    }

    private RuntimeRpcAppCallerSession GetAppCaller(string sessionId)
    {
        var caller = _appSessions?.GetRequired(sessionId)
            ?? throw Unavailable("rpc.app-session.unavailable", "App RPC caller sessions are unavailable.");
        RequireCallerPolicy(caller);
        return caller;
    }

    private RuntimeRpcActivationLease AcquireCallerLease(IRuntimeRpcCallerActivation caller)
        => caller.TryAcquireCaller(_policy.MaxConcurrentObservationsPerCaller, out var lease, observation: true) && lease is not null
            ? lease
            : throw ResourceExhausted(
                "rpc.caller.observation-limit",
                "The caller RPC observation limit was reached.");

    private void EnsureCurrent(
        IRuntimeRpcCallerActivation caller,
        PackageSessionLease sessionLease)
    {
        if (!caller.IsCurrent(sessionLease))
        {
            throw StaleEndpoint("rpc.caller-retired", "The caller package activation is retired.");
        }
        RequireCallerPolicy(caller);
    }

    private void EnsureScope(
        RuntimeRpcCallerScopeState? scope,
        IRuntimeRpcCallerActivation caller,
        PackageSessionLease sessionLease)
    {
        if (scope is null) return;
        var outcome = _callScopeRegistry.Validate(
            scope,
            caller,
            ScopeGeneration(caller, sessionLease),
            ScopeIdentity(caller, sessionLease));
        var result = RuntimeRpcCallAcquisitionPolicy.ValidateScope(outcome);
        ThrowAcquisitionFailure(result.Failure);
    }

    private static long ScopeGeneration(IRuntimeRpcCallerActivation caller, PackageSessionLease lease)
        => caller switch { RuntimeRpcPackageActivation package => package.BindingGeneration,
            RuntimeRpcAppCallerSession app => app.SessionGeneration, _ => lease.Generation };

    private static object ScopeIdentity(IRuntimeRpcCallerActivation caller, PackageSessionLease lease)
        => caller is RuntimeRpcPackageActivation or RuntimeRpcAppCallerSession ? caller : lease.GenerationIdentity;

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

    private RuntimeRpcPermissionLease? AcquirePermission(
        IRuntimeRpcCallerActivation caller,
        RuntimeRpcCallPermissionRequirement requirement)
    {
        switch (requirement.Kind)
        {
            case RuntimeRpcCallPermissionAcquisitionKind.None:
                return null;
            case RuntimeRpcCallPermissionAcquisitionKind.CallerLifetime:
                return new RuntimeRpcPermissionLease(caller.RetirementToken);
            case RuntimeRpcCallPermissionAcquisitionKind.PermissionStore:
                return requirement.Key is not null
                       && _permissions.TryAcquire(requirement.Key, out var permission)
                    ? permission
                    : null;
            default:
                throw new InvalidOperationException("The RPC call acquisition policy returned an invalid permission requirement.");
        }
    }

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
        var use = FindUse(caller, contractId, action);
        if (use?.ContractVersion is null || use.AbiSha256 is null)
        {
            lease = null;
            return false;
        }
        if (caller.SourceKind == PackageSourceKind.Dev)
        {
            lease = new RuntimeRpcPermissionLease(caller.RetirementToken);
            return true;
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
        => RuntimeRpcCallAcquisitionPolicy.TryResolveCallerContract(
            caller,
            provider.Snapshot,
            provider.Registration,
            action,
            out descriptor);

    private static bool IsOwnerProvider(
        IRuntimeRpcCallerActivation caller,
        RuntimeRpcProviderActivation provider)
        => RuntimeRpcCallAcquisitionPolicy.IsOwnerProvider(
            caller,
            provider.Snapshot,
            provider.Registration);

    private static Sunder.Package.Format.SunderPackageContractUseManifest? FindUse(
        IRuntimeRpcCallerActivation caller,
        string contractId,
        string action)
        => RuntimeRpcCallAcquisitionPolicy.FindUse(caller, contractId, action);

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
        if (!call.Contract.IsValidDetailed(
                call.Method.OutputSchemaReference,
                value,
                out _,
                out var validationFailure))
        {
            throw await FaultProviderAsync(
                call,
                "rpc.provider.invalid-output",
                "The provider returned output that violates its contract.",
                RuntimeRpcSemanticValidationException.Schema(validationFailure, measurement)).ConfigureAwait(false);
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
            return call.AppCallerRetirementRequested
                ? RetiredAppCaller()
                : call.DeadlineElapsed || _timeProvider.GetUtcNow() >= call.Frame.DeadlineUtc
                ? DeadlineExceeded()
                : SunderRpcException.Infrastructure(
                    SunderRpcErrorKind.Cancelled,
                    "rpc.call.cancelled",
                    "The RPC call was cancelled.");
        }
        if (exception is WorkerTransportResourceException { Kind: WorkerTransportFailureKind.WriteQueueAdmission or WorkerTransportFailureKind.WriteQueueClosed } transport)
        {
            // The process monitor owns a terminal transport fault. Do not also
            // accuse every in-flight provider handler (and its consumers) of an
            // invariant violation when a Host writer rejects/withdraws a frame.
            return SunderRpcException.Infrastructure(
                transport.Kind == WorkerTransportFailureKind.WriteQueueClosed ? SunderRpcErrorKind.Unavailable : SunderRpcErrorKind.ResourceExhausted,
                transport.Kind == WorkerTransportFailureKind.WriteQueueClosed ? "rpc.worker.transport-closed" : "rpc.worker.write-queue-limit",
                "The process worker transport could not complete the RPC operation.");
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
                    if (!call.Contract.IsValidDetailed(
                            declaration.DetailsSchema,
                            details,
                            out _,
                            out var validationFailure))
                    {
                        return await FaultProviderAsync(
                            call,
                            "rpc.provider.invalid-error-details",
                            "The provider returned domain error details that violate the contract.",
                            RuntimeRpcSemanticValidationException.Schema(validationFailure, measurement)).ConfigureAwait(false);
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
                : call.AppCallerRetirementRequested
                    ? RetiredAppCaller()
                : call.DeadlineElapsed || _timeProvider.GetUtcNow() >= call.Frame.DeadlineUtc
                    ? DeadlineExceeded()
                    : SunderRpcException.Infrastructure(
                        SunderRpcErrorKind.Cancelled,
                        "rpc.call.cancelled",
                        "The RPC call was cancelled.");
        }
    }

    private static SunderRpcException RetiredAppCaller()
        => SunderRpcException.Infrastructure(SunderRpcErrorKind.Unavailable,
            "rpc.app-session.retired", "The App RPC caller session is unavailable or retired.");

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
        try
        {
            using var current = AcquireSessionLease(call.Provider.Owner);
            if (call.Provider.Owner.IsCurrent(current))
                _sessionOwner.HandleRpcProviderFault(provider.PackageId,
                    current.GetPackageActivationIdentity(call.Provider.Owner.Package), exception, code);
        }
        catch (RuntimeUnavailableException)
        {
            // A withdrawn activation cannot fault its replacement during publication.
        }
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

    private static void ThrowAcquisitionFailure(RuntimeRpcCallAcquisitionFailure? failure)
    {
        if (failure is null) return;
        throw SunderRpcException.Infrastructure(failure.Kind, failure.Code, failure.Message);
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
    private readonly IDisposable? _sessionLifetime;
    private readonly CancellationToken _generationRetirement;
    private readonly RuntimeRpcActivationLease _callerLease;
    private readonly RuntimeRpcActivationLease _calleeLease;
    private readonly RuntimeRpcPermissionLease? _permission;
    private readonly CancellationTokenSource _linked;
    private readonly CancellationTokenSource _deadline;

    public RuntimeRpcCallLease(
        IDisposable? sessionLifetime,
        CancellationToken generationRetirement,
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
        _sessionLifetime = sessionLifetime;
        _generationRetirement = generationRetirement;
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

    public IRuntimeRpcCallerActivation Caller { get; }
    public RuntimeRpcProviderActivation Provider { get; }
    public string ServiceId { get; }
    public SunderRpcMethodDescriptor Method { get; }
    public SunderRpcContractDescriptor Contract { get; }
    public RuntimeRpcCallFrame Frame { get; }
    public SunderRpcInvocationContext Context { get; }
    public CancellationToken CancellationToken => _linked.Token;
    public bool DeadlineElapsed => _deadline.IsCancellationRequested;
    // A retained provider is not retired merely because an App connection loses
    // its caller session/generation. Preserve that exact boundary without turning
    // unrelated request, scope, permission or provider cancellation into a handoff.
    public bool AppCallerRetirementRequested => Caller is RuntimeRpcAppCallerSession
        && (_callerLease.RetirementToken.IsCancellationRequested || _generationRetirement.IsCancellationRequested);
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
        _sessionLifetime?.Dispose();
    }
}
