using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed record RuntimeRpcCallAcquisitionFailure(
    SunderRpcErrorKind Kind,
    string Code,
    string Message,
    string? ContractId = null,
    string? Action = null);

internal readonly record struct RuntimeRpcCallerGenerationValidationResult(
    RuntimeRpcCallAcquisitionFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

internal readonly record struct RuntimeRpcEndpointAcquisitionResult(
    RuntimeRpcCallAcquisitionFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

internal readonly record struct RuntimeRpcProviderGenerationValidationResult(
    RuntimeRpcCallAcquisitionFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

internal readonly record struct RuntimeRpcCallScopeAcquisitionResult(
    RuntimeRpcCallerScopeState? Scope,
    RuntimeRpcCallAcquisitionFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

internal readonly record struct RuntimeRpcCallScopeValidationResult(
    RuntimeRpcCallAcquisitionFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

internal enum RuntimeRpcCallPermissionAcquisitionKind
{
    None,
    CallerLifetime,
    PermissionStore,
}

internal sealed record RuntimeRpcCallPermissionRequirement(
    RuntimeRpcCallPermissionAcquisitionKind Kind,
    RuntimeRpcPermissionKey? Key,
    string ContractId,
    string Action);

internal readonly record struct RuntimeRpcCallerActionAcquisitionResult(
    SunderRpcContractDescriptor? Contract,
    RuntimeRpcCallPermissionRequirement? Permission,
    RuntimeRpcCallAcquisitionFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

internal readonly record struct RuntimeRpcPermissionAcquisitionResult(
    RuntimeRpcCallAcquisitionFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

internal readonly record struct RuntimeRpcCallFrameAcquisitionResult(
    RuntimeRpcCallFrame? Frame,
    DateTimeOffset? AcquiredAtUtc,
    RuntimeRpcCallAcquisitionFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

internal enum RuntimeRpcCallLeaseKind
{
    Caller,
    Provider,
}

internal readonly record struct RuntimeRpcCallLeaseAcquisitionResult(
    RuntimeRpcCallAcquisitionFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

internal static class RuntimeRpcCallAcquisitionPolicy
{
    public static RuntimeRpcCallerGenerationValidationResult ValidateCallerGeneration(bool isCurrent)
        => new(isCurrent
            ? null
            : new RuntimeRpcCallAcquisitionFailure(
                SunderRpcErrorKind.Unavailable,
                "rpc.caller-retired",
                "The caller package activation is retired."));

    public static RuntimeRpcCallScopeAcquisitionResult EvaluateScopeRegistration(
        RuntimeRpcCallScopeRegistry.Outcome outcome,
        RuntimeRpcCallerScopeState? scope)
        => outcome switch
        {
            RuntimeRpcCallScopeRegistry.Outcome.Success when scope is not null => new(scope, null),
            RuntimeRpcCallScopeRegistry.Outcome.Success => throw new InvalidOperationException(
                "The RPC call-scope registry returned success without a registered scope."),
            RuntimeRpcCallScopeRegistry.Outcome.CallerLimitReached => FailedScope(
                SunderRpcErrorKind.ResourceExhausted,
                "rpc.scope.limit",
                "The caller RPC call-scope limit was reached."),
            RuntimeRpcCallScopeRegistry.Outcome.AllocationFailed => FailedScope(
                SunderRpcErrorKind.Unavailable,
                "rpc.scope.allocation",
                "The Runtime could not allocate an RPC call scope."),
            RuntimeRpcCallScopeRegistry.Outcome.Unavailable => FailedScope(
                SunderRpcErrorKind.Unavailable,
                "rpc.scope.unavailable",
                "The RPC call scope is stale or unavailable."),
            RuntimeRpcCallScopeRegistry.Outcome.DeadlineExceeded => FailedScope(DeadlineExceeded()),
            _ => throw new InvalidOperationException(
                "The RPC call-scope registry returned an invalid registration outcome."),
        };

    public static RuntimeRpcCallScopeAcquisitionResult EvaluateScopeLookup(
        RuntimeRpcCallScopeRegistry.Outcome outcome,
        RuntimeRpcCallerScopeState? scope)
        => outcome switch
        {
            RuntimeRpcCallScopeRegistry.Outcome.Success when scope is not null => new(scope, null),
            RuntimeRpcCallScopeRegistry.Outcome.Success => throw new InvalidOperationException(
                "The RPC call-scope registry returned success without a matching scope."),
            RuntimeRpcCallScopeRegistry.Outcome.DeadlineExceeded => FailedScope(DeadlineExceeded()),
            _ => FailedScope(
                SunderRpcErrorKind.Unavailable,
                "rpc.scope.unavailable",
                "The RPC call scope is stale or unavailable."),
        };

    public static RuntimeRpcCallScopeValidationResult ValidateScope(
        RuntimeRpcCallScopeRegistry.Outcome outcome)
        => outcome switch
        {
            RuntimeRpcCallScopeRegistry.Outcome.Success => new(null),
            RuntimeRpcCallScopeRegistry.Outcome.DeadlineExceeded => new(DeadlineExceeded()),
            _ => new(new RuntimeRpcCallAcquisitionFailure(
                SunderRpcErrorKind.Unavailable,
                "rpc.scope.unavailable",
                "The RPC call scope is stale or unavailable.")),
        };

    public static RuntimeRpcEndpointAcquisitionResult EvaluateEndpoint(
        bool found,
        bool stale)
        => found
            ? new RuntimeRpcEndpointAcquisitionResult(null)
            : stale
                ? new RuntimeRpcEndpointAcquisitionResult(new RuntimeRpcCallAcquisitionFailure(
                    SunderRpcErrorKind.StaleEndpoint,
                    "rpc.endpoint.stale",
                    "The RPC endpoint identifies a retired provider activation."))
                : new RuntimeRpcEndpointAcquisitionResult(new RuntimeRpcCallAcquisitionFailure(
                    SunderRpcErrorKind.NotFound,
                    "rpc.endpoint.not-found",
                    "The RPC endpoint was not found."));

    public static RuntimeRpcProviderGenerationValidationResult ValidateProviderGeneration(
        Guid? loadedPackageActivationId,
        SunderRpcProviderSnapshot provider,
        long sessionGeneration)
        => loadedPackageActivationId == provider.ActivationId
           && sessionGeneration == provider.SessionGeneration
            ? new RuntimeRpcProviderGenerationValidationResult(null)
            : new RuntimeRpcProviderGenerationValidationResult(new RuntimeRpcCallAcquisitionFailure(
                SunderRpcErrorKind.Unavailable,
                "rpc.provider-retired",
                "The provider activation is no longer in the active Runtime session."));

    public static RuntimeRpcCallerActionAcquisitionResult EvaluateCallerAction(
        IRuntimeRpcCallerActivation caller,
        SunderRpcProviderSnapshot provider,
        RuntimeRpcProviderRegistration registration,
        string action,
        bool requirePermission)
    {
        if (!TryResolveCallerContract(caller, provider, registration, action, out var contract, out var use))
        {
            return new RuntimeRpcCallerActionAcquisitionResult(
                null,
                null,
                PermissionDenied(provider.ContractId, action));
        }

        RuntimeRpcCallPermissionRequirement permission;
        if (!requirePermission)
        {
            permission = new RuntimeRpcCallPermissionRequirement(
                RuntimeRpcCallPermissionAcquisitionKind.None,
                null,
                provider.ContractId,
                action);
        }
        else if (IsOwnerProvider(caller, provider, registration)
                 || caller.SourceKind == PackageSourceKind.Dev)
        {
            permission = new RuntimeRpcCallPermissionRequirement(
                RuntimeRpcCallPermissionAcquisitionKind.CallerLifetime,
                null,
                provider.ContractId,
                action);
        }
        else
        {
            permission = new RuntimeRpcCallPermissionRequirement(
                RuntimeRpcCallPermissionAcquisitionKind.PermissionStore,
                new RuntimeRpcPermissionKey(
                    caller.PackageId,
                    caller.PackageVersion,
                    caller.ManifestSha256,
                    caller.Role,
                    provider.ContractId,
                    use!.ContractVersion!,
                    use.AbiSha256!,
                    action),
                provider.ContractId,
                action);
        }

        return new RuntimeRpcCallerActionAcquisitionResult(contract, permission, null);
    }

    public static RuntimeRpcPermissionAcquisitionResult ValidatePermissionAcquisition(
        RuntimeRpcCallPermissionRequirement requirement,
        bool acquired)
        => new(acquired ? null : PermissionDenied(requirement.ContractId, requirement.Action));

    public static RuntimeRpcCallFrameAcquisitionResult AcquireFrame(
        RuntimeRpcPolicyOptions policy,
        SunderRpcCallOptions? options,
        RuntimeRpcCallFrame? currentFrame,
        RuntimeRpcCallerScopeState? scope,
        TimeProvider timeProvider,
        bool targetSupportsExtendedDeadline)
    {
        var parent = RuntimeRpcCallFrameCalculator.Merge(currentFrame, scope?.ParentFrame);
        var depth = (parent?.Depth ?? 0) + 1;
        if (depth > policy.MaxNestedCallDepth)
        {
            return new RuntimeRpcCallFrameAcquisitionResult(
                null,
                null,
                new RuntimeRpcCallAcquisitionFailure(
                    SunderRpcErrorKind.ResourceExhausted,
                    "rpc.call.depth-limit",
                    "The nested RPC call depth limit was reached."));
        }

        var now = timeProvider.GetUtcNow();
        var deadline = RuntimeRpcCallFrameCalculator.CalculateInvocationDeadline(
            policy,
            options,
            parent,
            now,
            targetSupportsExtendedDeadline,
            scope?.DeadlineUtc);
        if (deadline <= now)
        {
            return new RuntimeRpcCallFrameAcquisitionResult(null, now, DeadlineExceeded());
        }

        return new RuntimeRpcCallFrameAcquisitionResult(
            new RuntimeRpcCallFrame(depth, deadline),
            now,
            null);
    }

    public static RuntimeRpcCallLeaseAcquisitionResult ValidateLeaseAcquisition(
        RuntimeRpcCallLeaseKind kind,
        bool acquired)
    {
        if (acquired) return new RuntimeRpcCallLeaseAcquisitionResult(null);
        return kind switch
        {
            RuntimeRpcCallLeaseKind.Caller => new RuntimeRpcCallLeaseAcquisitionResult(
                new RuntimeRpcCallAcquisitionFailure(
                    SunderRpcErrorKind.ResourceExhausted,
                    "rpc.caller.concurrency-limit",
                    "The caller RPC concurrency limit was reached.")),
            RuntimeRpcCallLeaseKind.Provider => new RuntimeRpcCallLeaseAcquisitionResult(
                new RuntimeRpcCallAcquisitionFailure(
                    SunderRpcErrorKind.ResourceExhausted,
                    "rpc.provider.concurrency-limit",
                    "The provider RPC concurrency limit was reached.")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    public static bool TryResolveCallerContract(
        IRuntimeRpcCallerActivation caller,
        SunderRpcProviderSnapshot provider,
        RuntimeRpcProviderRegistration registration,
        string action,
        out SunderRpcContractDescriptor? descriptor)
        => TryResolveCallerContract(
            caller,
            provider,
            registration,
            action,
            out descriptor,
            out _);

    public static SunderPackageContractUseManifest? FindUse(
        IRuntimeRpcCallerActivation caller,
        string contractId,
        string action)
        => caller.RpcContractUses.FirstOrDefault(use =>
            string.Equals(use.Role, caller.Role, StringComparison.Ordinal)
            && string.Equals(use.ContractId, contractId, StringComparison.Ordinal)
            && (use.Actions ?? []).Contains(action, StringComparer.Ordinal));

    public static bool IsOwnerProvider(
        IRuntimeRpcCallerActivation caller,
        SunderRpcProviderSnapshot provider,
        RuntimeRpcProviderRegistration registration)
        => registration.Manifest?.Audience == SunderPackageFormat.OwnerProviderAudience
           && string.Equals(caller.PackageId, provider.PackageId, StringComparison.Ordinal);

    private static bool TryResolveCallerContract(
        IRuntimeRpcCallerActivation caller,
        SunderRpcProviderSnapshot provider,
        RuntimeRpcProviderRegistration registration,
        string action,
        out SunderRpcContractDescriptor? descriptor,
        out SunderPackageContractUseManifest? use)
    {
        descriptor = null;
        use = FindUse(caller, provider.ContractId, action);
        var declaration = registration.Manifest;
        if (use?.ContractVersion is null
            || declaration is null
            || !string.Equals(declaration.Role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal)
            || !AudienceAllows(caller.PackageId, provider.PackageId, declaration.Audience)
            || !string.Equals(use.ContractVersion, provider.ContractVersion, StringComparison.Ordinal)
            || !string.Equals(declaration.ContractId, provider.ContractId, StringComparison.Ordinal)
            || !string.Equals(declaration.ContractVersion, provider.ContractVersion, StringComparison.Ordinal)
            || !string.Equals(registration.ContractId, provider.ContractId, StringComparison.Ordinal)
            || !string.Equals(registration.ContractVersion, provider.ContractVersion, StringComparison.Ordinal)
            || !string.Equals(registration.Contract.ContractId, provider.ContractId, StringComparison.Ordinal)
            || !string.Equals(registration.Contract.Version, provider.ContractVersion, StringComparison.Ordinal)
            || !string.Equals(registration.DescriptorSha256, declaration.DescriptorSha256, StringComparison.Ordinal)
            || !string.Equals(registration.Contract.Sha256, declaration.DescriptorSha256, StringComparison.Ordinal)
            || !string.Equals(provider.DescriptorSha256, declaration.DescriptorSha256, StringComparison.Ordinal)
            || !string.Equals(provider.AbiSha256, declaration.AbiSha256, StringComparison.Ordinal)
            || !caller.RpcContracts.TryGetValue(
                PackageSessionPreparer.ContractKey(provider.ContractId, use.ContractVersion),
                out descriptor)
            || !string.Equals(descriptor.ContractId, provider.ContractId, StringComparison.Ordinal)
            || !string.Equals(descriptor.Version, use.ContractVersion, StringComparison.Ordinal)
            || !string.Equals(descriptor.Sha256, provider.DescriptorSha256, StringComparison.Ordinal)
            || !CallerExposureAllows(caller, provider))
        {
            descriptor = null;
            return false;
        }

        if (descriptor.DescriptorVersion == SunderRpcProtocol.DescriptorVersion
            && registration.Contract.DescriptorVersion == SunderRpcProtocol.DescriptorVersion
            && use.AbiSha256 is not null
            && declaration.AbiSha256 is not null
            && string.Equals(descriptor.AbiSha256, use.AbiSha256, StringComparison.Ordinal)
            && string.Equals(use.AbiSha256, declaration.AbiSha256, StringComparison.Ordinal)
            && string.Equals(registration.Contract.AbiSha256, declaration.AbiSha256, StringComparison.Ordinal))
        {
            return true;
        }

        descriptor = null;
        return false;
    }

    private static bool CallerExposureAllows(IRuntimeRpcCallerActivation caller, SunderRpcProviderSnapshot provider)
    {
        if (caller.PackageId == provider.PackageId) return true;
        var source = caller switch
        {
            RuntimeRpcPackageActivation package => package.Package.Source,
            RuntimeRpcAppCallerSession app => app.Source,
            _ => null,
        };
        return source is null || source.Manifest?.ContractBundles?.Any(bundle => bundle?.ContractId == provider.ContractId
            && bundle.Version == provider.ContractVersion && bundle.Exposure == SunderPackageFormat.PublicContractExposure) == true;
    }

    private static bool AudienceAllows(
        string callerPackageId,
        string providerPackageId,
        string? audience)
        => audience == SunderPackageFormat.PublicProviderAudience
           || (audience == SunderPackageFormat.OwnerProviderAudience
               && string.Equals(callerPackageId, providerPackageId, StringComparison.Ordinal));

    private static RuntimeRpcCallScopeAcquisitionResult FailedScope(
        SunderRpcErrorKind kind,
        string code,
        string message)
        => FailedScope(new RuntimeRpcCallAcquisitionFailure(kind, code, message));

    private static RuntimeRpcCallScopeAcquisitionResult FailedScope(
        RuntimeRpcCallAcquisitionFailure failure)
        => new(null, failure);

    private static RuntimeRpcCallAcquisitionFailure PermissionDenied(
        string contractId,
        string action)
        => new(
            SunderRpcErrorKind.PermissionDenied,
            "rpc.permission.denied",
            $"RPC action '{action}' is not granted for contract '{contractId}'.",
            contractId,
            action);

    private static RuntimeRpcCallAcquisitionFailure DeadlineExceeded()
        => new(
            SunderRpcErrorKind.DeadlineExceeded,
            "rpc.call.deadline-exceeded",
            "The RPC call deadline elapsed.");
}
