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
    private static readonly AsyncLocal<RuntimeRpcCallFrame?> CurrentCall = new();
    private readonly RuntimeRpcCatalog _catalog;
    private readonly RuntimeRpcPermissionStore _permissions;
    private readonly PackageSessionState _sessions;
    private readonly RuntimeSessionOwner _sessionOwner;
    private readonly RuntimeRpcPolicyOptions _policy;
    private readonly RuntimeRpcAppSessionManager? _appSessions;
    private readonly RuntimeRpcHostCallerActivation _stackHostCaller;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _hostStopping;

    public RuntimeRpcBroker(
        RuntimeRpcCatalog catalog,
        RuntimeRpcPermissionStore permissions,
        PackageSessionState sessions,
        RuntimeSessionOwner sessionOwner,
        RuntimeRpcPolicyOptions? policy = null,
        TimeProvider? timeProvider = null,
        IHostApplicationLifetime? hostLifetime = null,
        RuntimeRpcAppSessionManager? appSessions = null)
        : this(
            catalog,
            permissions,
            sessions,
            sessionOwner,
            policy,
            timeProvider,
            hostLifetime?.ApplicationStopping ?? CancellationToken.None,
            appSessions)
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
        RuntimeRpcAppSessionManager? appSessions = null)
    {
        _catalog = catalog;
        _permissions = permissions;
        _sessions = sessions;
        _sessionOwner = sessionOwner;
        _policy = policy ?? new RuntimeRpcPolicyOptions();
        _appSessions = appSessions;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _hostStopping = hostStopping;
        _stackHostCaller = new RuntimeRpcHostCallerActivation(
            SunderStackContributorRpc.Descriptor,
            hostStopping);
    }

    public ISunderRpcClient CreateStackHostClient()
        => new RuntimeRpcHostClient(this, _stackHostCaller);

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
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var sessionLease = _sessions.AcquireLease();
        EnsureCurrent(caller, sessionLease);
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
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var sessionLease = _sessions.AcquireLease();
        EnsureCurrent(caller, sessionLease);
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (afterRevision < 0 || afterSequence < 0)
        {
            throw Validation(
                "rpc.catalog.position",
                "RPC catalog revisions and sequences cannot be negative.");
        }
        using var sessionLease = _sessions.AcquireLease();
        EnsureCurrent(caller, sessionLease);
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
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                new[]
                {
                    cancellationToken,
                    _hostStopping,
                    sessionLease.RetirementToken,
                    callerLease.RetirementToken,
                    permissionChanges,
                }.Concat(permissionLeases.Select(static lease => lease.RevocationToken)).ToArray());
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
        CancellationToken cancellationToken)
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
            cancellationToken);
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
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
            requirePermission: true,
            cancellationToken);
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
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessionLease = _sessions.AcquireLease();
        RuntimeRpcActivationLease? callerLease = null;
        RuntimeRpcActivationLease? calleeLease = null;
        RuntimeRpcPermissionLease? permission = null;
        CancellationTokenSource? linked = null;
        try
        {
            if (!caller.IsCurrent(sessionLease))
            {
                throw Unavailable("rpc.caller-retired", "The caller package activation is retired.");
            }
            if (!_catalog.TryGetActiveEndpoint(endpoint, out var provider, out var stale) || provider is null)
            {
                throw stale
                    ? new SunderRpcException(new SunderRpcError(
                        SunderRpcErrorKind.StaleEndpoint,
                        "rpc.endpoint.stale",
                        "The RPC endpoint identifies a retired provider activation."))
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
                throw new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.Validation,
                    "rpc.method.kind-mismatch",
                    "The requested RPC invocation shape does not match the method contract."));
            }
            ValidatePayload(callerContract, method.RequestSchemaReference, request, "request");

            var parent = CurrentCall.Value;
            var depth = (parent?.Depth ?? 0) + 1;
            if (depth > _policy.MaxNestedCallDepth)
            {
                throw ResourceExhausted("rpc.call.depth-limit", "The nested RPC call depth limit was reached.");
            }
            var now = _timeProvider.GetUtcNow();
            var maximumDeadline = now + _policy.DefaultDeadline;
            var deadline = options?.DeadlineUtc is { } requestedDeadline && requestedDeadline < maximumDeadline
                ? requestedDeadline
                : maximumDeadline;
            if (parent is not null && parent.DeadlineUtc < deadline) deadline = parent.DeadlineUtc;
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

            var deadlineCancellation = new CancellationTokenSource(deadline - now);
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
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokens.ToArray());
            return new RuntimeRpcCallLease(
                sessionLease,
                callerLease,
                calleeLease,
                permission,
                linked,
                deadlineCancellation,
                provider,
                method,
                callerContract,
                new RuntimeRpcCallFrame(depth, deadline),
                new SunderRpcInvocationContext(
                    caller.PackageId,
                    caller.PackageVersion,
                    provider.Snapshot,
                    deadline,
                    depth,
                    linked.Token));
        }
        catch
        {
            linked?.Dispose();
            permission?.Dispose();
            calleeLease?.Dispose();
            callerLease?.Dispose();
            sessionLease.Dispose();
            throw;
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
            return new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.StaleEndpoint,
                "rpc.endpoint.stale",
                "The RPC endpoint identifies a retired provider activation."));
        }
        if (exception is OperationCanceledException || call.CancellationToken.IsCancellationRequested)
        {
            return call.DeadlineElapsed || _timeProvider.GetUtcNow() >= call.Frame.DeadlineUtc
                ? DeadlineExceeded()
                : new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.Cancelled,
                    "rpc.call.cancelled",
                    "The RPC call was cancelled."));
        }
        if (exception is SunderRpcException rpcException
            && IsSafeDomainCode(rpcException.Error.Code)
            && (rpcException.Error.Kind == SunderRpcErrorKind.Domain
                || rpcException.Error.Code.StartsWith("rpc.", StringComparison.Ordinal)))
        {
            return new SunderRpcException(rpcException.Error with
            {
                Message = Sanitize(rpcException.Error.Message),
            });
        }
        return FaultProvider(
            call,
            "rpc.provider.handler-fault",
            "The provider handler violated the RPC invocation contract.",
            exception);
    }

    private void ThrowIfCallCancelled(RuntimeRpcCallLease call)
    {
        if (call.CancellationToken.IsCancellationRequested)
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
        var identity = call.SessionLease.GetPackageActivationIdentity(call.Provider.Owner.Package);
        _sessionOwner.HandleRpcProviderFault(
            call.Provider.Snapshot.PackageId,
            identity,
            exception,
            code);
        return new SunderRpcException(new SunderRpcError(SunderRpcErrorKind.ProviderFaulted, code, message));
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

    private static SunderRpcException PermissionDenied(string contractId, string action)
        => new(new SunderRpcError(
            SunderRpcErrorKind.PermissionDenied,
            "rpc.permission.denied",
            $"RPC action '{action}' is not granted for contract '{contractId}'."));

    private static SunderRpcException ResourceExhausted(string code, string message)
        => new(new SunderRpcError(SunderRpcErrorKind.ResourceExhausted, code, message));

    private static SunderRpcException Validation(string code, string message)
        => new(new SunderRpcError(SunderRpcErrorKind.Validation, code, message));

    private static SunderRpcException NotFound(string code, string message)
        => new(new SunderRpcError(SunderRpcErrorKind.NotFound, code, message));

    private static SunderRpcException Unavailable(string code, string message)
        => new(new SunderRpcError(SunderRpcErrorKind.Unavailable, code, message));

    private static SunderRpcException DeadlineExceeded()
        => new(new SunderRpcError(
            SunderRpcErrorKind.DeadlineExceeded,
            "rpc.call.deadline-exceeded",
            "The RPC call deadline elapsed."));
}

internal readonly record struct RuntimeRpcCallerStamp(string PackageId, Guid ActivationId);

internal sealed class RuntimeRpcClient(
    RuntimeRpcBroker broker,
    RuntimeRpcCallerStamp callerStamp) : ISunderRpcClient
{
    public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken = default)
        => broker.GetProviderAsync(callerStamp, endpoint, cancellationToken);

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

    public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken = default)
        => ValueTask.FromException<SunderRpcProviderSnapshot?>(Unavailable());

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
        => new(new SunderRpcError(
            SunderRpcErrorKind.Unavailable,
            "rpc.host.unavailable",
            "Schema-first RPC is unavailable in this host context."));

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
        Method = method;
        Contract = contract;
        Frame = frame;
        Context = context;
    }

    public PackageSessionLease SessionLease { get; }
    public RuntimeRpcProviderActivation Provider { get; }
    public SunderRpcMethodDescriptor Method { get; }
    public SunderRpcContractDescriptor Contract { get; }
    public RuntimeRpcCallFrame Frame { get; }
    public SunderRpcInvocationContext Context { get; }
    public CancellationToken CancellationToken => _linked.Token;
    public bool DeadlineElapsed => _deadline.IsCancellationRequested;
    public bool ProviderRetirementRequested => _calleeLease.RetirementToken.IsCancellationRequested;

    public void Dispose()
    {
        _linked.Dispose();
        _deadline.Dispose();
        _permission?.Dispose();
        _calleeLease.Dispose();
        _callerLease.Dispose();
        SessionLease.Dispose();
    }
}

internal sealed record RuntimeRpcCallFrame(int Depth, DateTimeOffset DeadlineUtc);
