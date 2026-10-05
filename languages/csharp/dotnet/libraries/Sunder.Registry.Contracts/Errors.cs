namespace Sunder.Registry.Contracts;

public static class RegistryV1ErrorCodes
{
    public const string InvalidRequest = "registry.v1.request.invalid";
    public const string RequestTooLarge = "registry.v1.request.too_large";
    public const string RateLimited = "registry.v1.request.rate_limited";
    public const string Unauthorized = "registry.v1.auth.unauthorized";
    public const string Forbidden = "registry.v1.auth.forbidden";
    public const string NotFound = "registry.v1.resource.not_found";
    public const string Conflict = "registry.v1.resource.conflict";
    public const string PackageVersionExists = "registry.v1.package.version_exists";
    public const string ContractDescriptorConflict = "registry.v1.contract.descriptor_conflict";
    public const string PackageRequirementUnavailable = "registry.v1.package.requirement_unavailable";
    public const string PackageRequirementUnsatisfied = "registry.v1.package.requirement_unsatisfied";
    public const string PackageTargetUnavailable = "registry.v1.package.target_unavailable";
    public const string BootstrapRequired = "registry.bootstrap-required";
    public const string PolicyRecoveryRequired = "registry.policy-recovery-required";
    public const string PolicyEventUnsupported = "registry.policy-event-unsupported";
    public const string Internal = "registry.v1.internal";
}

public static class ExecutionRuntimeErrorCodes
{
    private const string Prefix = "execution-runtime.";

    public const string RequirementInvalid = Prefix + "requirement-invalid";
    public const string Unsupported = Prefix + "unsupported";
    public const string Unresolved = Prefix + "unresolved";
    public const string Missing = Prefix + "missing";
    public const string Offline = Prefix + "offline";
    public const string PolicyRequired = Prefix + "policy-required";
    public const string DownloadFailed = Prefix + "download-failed";
    public const string ArtifactInvalid = Prefix + "artifact-invalid";
    public const string IntegrityFailed = Prefix + "integrity-failed";
    public const string InstallFailed = Prefix + "install-failed";
    public const string BindingInvalid = Prefix + "binding-invalid";
    public const string LaunchInvalid = Prefix + "launch-invalid";
    public const string Unhealthy = Prefix + "unhealthy";
    public const string Deprecated = Prefix + "deprecated";
    public const string Retired = Prefix + "retired";
    public const string Revoked = Prefix + "revoked";
    public const string Busy = Prefix + "busy";
    public const string PlanStale = Prefix + "plan-stale";
    public const string HostTrustUpdateRequired = Prefix + "host-trust-update-required";
    public const string CurrentRuntimeTrustDenied = Prefix + "current-runtime-trust-denied";
}

public static class ExecutionRuntimeResolutionConflictCodes
{
    private const string Prefix = "execution-runtime.";

    public const string NoCompatibleVersion = Prefix + "no-compatible-version";
    public const string RidUnavailable = Prefix + "rid-unavailable";
    public const string ModuleAbiMismatch = Prefix + "module-abi-mismatch";
}

public sealed record ExecutionRuntimeErrorDescriptor(
    string Code,
    string Owner,
    int HttpStatus,
    bool Retryable,
    string SafeMessage,
    IReadOnlyList<string> DiagnosticFields);

public static class ExecutionRuntimeErrorCatalog
{
    public static IReadOnlyList<ExecutionRuntimeErrorDescriptor> All { get; } = Array.AsReadOnly<ExecutionRuntimeErrorDescriptor>(
    [
        Error(ExecutionRuntimeErrorCodes.RequirementInvalid, "registry", 400, false, "The execution runtime requirement is invalid.", "runtimeId", "versionRange", "moduleAbi"),
        Error(ExecutionRuntimeErrorCodes.Unsupported, "registry", 409, false, "The execution runtime is not supported.", "runtimeId", "moduleAbi"),
        Error(ExecutionRuntimeErrorCodes.Unresolved, "registry", 409, false, "No execution runtime could be resolved.", "runtimeId", "versionRange", "rid"),
        Error(ExecutionRuntimeErrorCodes.Missing, "runtime-host", 409, false, "The required execution runtime is not installed.", "runtimeId", "version", "packagingRevision", "rid"),
        Error(ExecutionRuntimeErrorCodes.Offline, "runtime-host", 503, true, "Registry is unavailable while the device is offline.", "runtimeId", "version", "rid"),
        Error(ExecutionRuntimeErrorCodes.PolicyRequired, "registry", 409, false, "Trusted execution runtime policy is required.", "runtimeId", "version", "rid"),
        Error(ExecutionRuntimeErrorCodes.DownloadFailed, "runtime-host", 502, true, "The execution runtime could not be downloaded.", "runtimeId", "version", "rid"),
        Error(ExecutionRuntimeErrorCodes.ArtifactInvalid, "runtime-host", 422, false, "The execution runtime artifact is invalid.", "runtimeId", "version", "rid", "artifactSha256"),
        Error(ExecutionRuntimeErrorCodes.IntegrityFailed, "runtime-host", 422, false, "Execution runtime integrity verification failed.", "runtimeId", "version", "rid", "artifactSha256"),
        Error(ExecutionRuntimeErrorCodes.InstallFailed, "runtime-host", 500, true, "The execution runtime could not be installed.", "runtimeId", "version", "rid"),
        Error(ExecutionRuntimeErrorCodes.BindingInvalid, "runtime-host", 409, false, "The package execution runtime binding is invalid.", "packageId", "role", "rid", "runtimeId"),
        Error(ExecutionRuntimeErrorCodes.LaunchInvalid, "runtime-host", 500, false, "The execution runtime launch configuration is invalid.", "packageId", "runtimeId", "version", "rid"),
        Error(ExecutionRuntimeErrorCodes.Unhealthy, "runtime-host", 409, true, "The execution runtime is unhealthy and requires repair.", "runtimeId", "version", "rid"),
        Error(ExecutionRuntimeErrorCodes.Deprecated, "registry", 409, false, "The execution runtime is deprecated.", "runtimeId", "version", "policyMessage"),
        Error(ExecutionRuntimeErrorCodes.Retired, "registry", 410, false, "The execution runtime is retired.", "runtimeId", "version", "activationBlockedAtUtc"),
        Error(ExecutionRuntimeErrorCodes.Revoked, "registry", 410, false, "The execution runtime is revoked.", "runtimeId", "version", "revokedAtUtc"),
        Error(ExecutionRuntimeErrorCodes.Busy, "runtime-host", 409, true, "The execution runtime is busy.", "runtimeId", "version", "rid"),
        Error(ExecutionRuntimeErrorCodes.PlanStale, "registry", 409, true, "The execution runtime resolution plan is stale.", "expectedResolutionFingerprint", "resolutionFingerprint", "runtimePolicyRevision"),
        Error(ExecutionRuntimeErrorCodes.HostTrustUpdateRequired, "registry", 409, false, "The Host must be updated before it can trust this execution runtime.", "runtimeId", "version", "requiredHostTrustGeneration"),
        Error(ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied, "registry", 410, false, "Registry policy denies the exact currently bound execution runtime.", "runtimeId", "version", "packagingRevision", "rid"),
        Error(ExecutionRuntimeResolutionConflictCodes.NoCompatibleVersion, "registry", 409, false, "No execution runtime satisfies the reviewed requirements.", "runtimeId", "versionRange", "rid"),
        Error(ExecutionRuntimeResolutionConflictCodes.RidUnavailable, "registry", 409, false, "The execution runtime is unavailable for the requested platform.", "runtimeId", "version", "rid"),
        Error(ExecutionRuntimeResolutionConflictCodes.ModuleAbiMismatch, "registry", 409, false, "The execution runtime does not provide the required module ABI.", "runtimeId", "moduleAbi"),
    ]);

    private static ExecutionRuntimeErrorDescriptor Error(
        string code,
        string owner,
        int httpStatus,
        bool retryable,
        string safeMessage,
        params string[] diagnosticFields)
        => new(code, owner, httpStatus, retryable, safeMessage, Array.AsReadOnly(diagnosticFields));
}

public sealed record RegistryProblemDetails(
    string Type,
    string Title,
    int Status,
    string Detail,
    string Instance,
    string Code,
    string TraceId,
    string CorrelationId,
    IReadOnlyDictionary<string, string[]>? Errors = null);
