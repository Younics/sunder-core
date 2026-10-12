using System.Text.Json.Serialization;

namespace Sunder.Runtime.Contracts;

public static class ExecutionRuntimeErrorCodes
{
    private const string Prefix = "execution-runtime.";

    public const string RequirementInvalid = Prefix + "requirement-invalid";
    public const string Unsupported = Prefix + "unsupported";
    public const string Unresolved = Prefix + "unresolved";
    public const string Missing = Prefix + "missing";
    public const string Offline = Prefix + "offline";
    public const string SourceNotAllowed = Prefix + "source-not-allowed";
    public const string DownloadFailed = Prefix + "download-failed";
    public const string ArtifactInvalid = Prefix + "artifact-invalid";
    public const string ArtifactChanged = Prefix + "artifact-changed";
    public const string IntegrityFailed = Prefix + "integrity-failed";
    public const string InstallFailed = Prefix + "install-failed";
    public const string PlatformPrerequisiteMissing = Prefix + "platform-prerequisite-missing";
    public const string BindingInvalid = Prefix + "binding-invalid";
    public const string LaunchInvalid = Prefix + "launch-invalid";
    public const string Unhealthy = Prefix + "unhealthy";
    public const string Busy = Prefix + "busy";
    public const string PlanStale = Prefix + "plan-stale";
}

public static class ExecutionRuntimeResolutionConflictCodes
{
    private const string Prefix = "execution-runtime.";

    public const string NoCompatibleVersion = Prefix + "no-compatible-version";
    public const string RidUnavailable = Prefix + "rid-unavailable";
    public const string ModuleAbiMismatch = Prefix + "module-abi-mismatch";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionRuntimeErrorDescriptor(
    [property: JsonRequired] string Code,
    [property: JsonRequired] string Owner,
    [property: JsonRequired] int HttpStatus,
    [property: JsonRequired] bool Retryable,
    [property: JsonRequired] string SafeMessage,
    IReadOnlyList<string> DiagnosticFields)
{
    private IReadOnlyList<string> _diagnosticFields = RuntimeContractCollections.FreezeBounded(DiagnosticFields, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    [JsonRequired]
    public IReadOnlyList<string> DiagnosticFields
    {
        get => _diagnosticFields;
        init => _diagnosticFields = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    }
}

public static class ExecutionRuntimeErrorCatalog
{
    public static IReadOnlyList<ExecutionRuntimeErrorDescriptor> All { get; } = Array.AsReadOnly<ExecutionRuntimeErrorDescriptor>(
    [
        Error(ExecutionRuntimeErrorCodes.RequirementInvalid, 400, false, "The execution runtime requirement is invalid.", "runtimeId", "versionRange", "moduleAbi"),
        Error(ExecutionRuntimeErrorCodes.Unsupported, 409, false, "The execution runtime or platform is not supported.", "runtimeId", "moduleAbi", "rid"),
        Error(ExecutionRuntimeErrorCodes.Unresolved, 409, false, "No official execution runtime could be resolved.", "runtimeId", "versionRange", "rid"),
        Error(ExecutionRuntimeErrorCodes.Missing, 409, false, "The required execution runtime is not installed.", "runtimeId", "vendorVersion", "rid"),
        Error(ExecutionRuntimeErrorCodes.Offline, 503, true, "The official runtime source is unavailable while the device is offline.", "sourceId", "runtimeId", "rid"),
        Error(ExecutionRuntimeErrorCodes.SourceNotAllowed, 422, false, "The runtime source is not a configured official source.", "sourceId"),
        Error(ExecutionRuntimeErrorCodes.DownloadFailed, 502, true, "The official execution runtime could not be downloaded.", "runtimeId", "vendorVersion", "rid"),
        Error(ExecutionRuntimeErrorCodes.ArtifactInvalid, 422, false, "The official execution runtime artifact is invalid.", "runtimeId", "vendorVersion", "rid"),
        Error(ExecutionRuntimeErrorCodes.ArtifactChanged, 409, false, "The vendor checksum changed for the selected runtime release.", "runtimeId", "vendorVersion", "vendorChecksumDigest"),
        Error(ExecutionRuntimeErrorCodes.IntegrityFailed, 422, false, "Execution runtime integrity verification failed.", "runtimeId", "vendorVersion", "rid", "archiveSha256"),
        Error(ExecutionRuntimeErrorCodes.InstallFailed, 500, true, "The execution runtime could not be installed.", "runtimeId", "vendorVersion", "rid"),
        Error(ExecutionRuntimeErrorCodes.PlatformPrerequisiteMissing, 409, false, "An operating-system prerequisite for this runtime is missing.", "runtimeId", "vendorVersion", "rid"),
        Error(ExecutionRuntimeErrorCodes.BindingInvalid, 409, false, "The package execution runtime binding is invalid.", "packageId", "role", "rid", "runtimeId"),
        Error(ExecutionRuntimeErrorCodes.LaunchInvalid, 500, false, "The execution runtime launch configuration is invalid.", "packageId", "runtimeId", "vendorVersion", "rid"),
        Error(ExecutionRuntimeErrorCodes.Unhealthy, 409, true, "The execution runtime is unhealthy and requires repair.", "runtimeId", "vendorVersion", "rid"),
        Error(ExecutionRuntimeErrorCodes.Busy, 409, true, "The execution runtime is busy.", "runtimeId", "vendorVersion", "rid"),
        Error(ExecutionRuntimeErrorCodes.PlanStale, 409, true, "The local execution runtime consent plan is stale.", "expectedFingerprint", "fingerprint"),
        Error(ExecutionRuntimeResolutionConflictCodes.NoCompatibleVersion, 409, false, "No supported official runtime satisfies the package requirement.", "runtimeId", "versionRange", "rid"),
        Error(ExecutionRuntimeResolutionConflictCodes.RidUnavailable, 409, false, "The official runtime is unavailable for the requested platform.", "runtimeId", "vendorVersion", "rid"),
        Error(ExecutionRuntimeResolutionConflictCodes.ModuleAbiMismatch, 409, false, "The execution runtime does not provide the required module ABI.", "runtimeId", "moduleAbi"),
    ]);

    private static ExecutionRuntimeErrorDescriptor Error(string code, int httpStatus, bool retryable, string safeMessage, params string[] diagnosticFields)
        => new(code, "runtime-host", httpStatus, retryable, safeMessage, diagnosticFields);
}
