namespace Sunder.Registry.Contracts;

public static class PackagePolicyErrorCodes
{
    public const string AuthorizationPolicyStale = "registry.authorization-policy-stale";
    public const string IdentityReservationRequired = "package.identity-reservation-required";
    public const string IdentityReservationLimit = "package.identity-reservation-limit";
    public const string StackIdentityReservationRequired = "stack.identity-reservation-required";
    public const string StackIdentityReservationLimit = "stack.identity-reservation-limit";
    public const string StackSecurityBlocked = "stack.security-blocked";
    public const string ContractAuthorityReservationLimit = "contract-authority.reservation-limit";
    public const string SecurityBlocked = "package.security-blocked";
    public const string AppProcessSupervisionRequired = "package.app-process-supervision-required";
    public const string AppProcessTerminationRequired = "package.app-process-termination-required";
    public const string ContractAuthorityRevoked = "contract-authority.revoked";
    public const string ContractAuthorityReleaseConflict = "contract-authority.release-conflict";
    public const string CandidateAuthorityUnconfirmed = "package-api.candidate-authority-unconfirmed";
    public const string ContractVersionBumpRequired = "contract-version.bump-required";
    public const string PlanStale = "package.plan-stale";
    public const string PolicyRequired = "package.policy-required";
    public const string PolicyStale = "package.policy-stale";
    public const string PolicyConflict = "package.policy-conflict";
    public const string PolicyExpired = "package.policy-expired";
    public const string PolicyClockInvalid = "package.policy-clock-invalid";
}

public static class RpcReadinessErrorCodes
{
    public const string ProviderNotFound = "rpc.provider-not-found";
    public const string ProviderAmbiguous = "rpc.provider-ambiguous";
    public const string RequiredProviderUnsatisfied = "rpc.required-provider-unsatisfied";
    public const string RequiredProviderCycle = "rpc.required-provider-cycle";
}

public sealed record PolicyErrorDescriptor(string Code, string Owner, int HttpStatus, bool Retryable,
    string SafeMessage, IReadOnlyList<string> DiagnosticFields);

public static class PolicyErrorCatalog
{
    public static IReadOnlyList<PolicyErrorDescriptor> All { get; } = Array.AsReadOnly<PolicyErrorDescriptor>(
    [
        Error(PackagePolicyErrorCodes.AuthorizationPolicyStale, "registry", 503, false, "Registry authorization configuration is stale.", "authorizationGeneration"),
        Error(PackagePolicyErrorCodes.IdentityReservationRequired, "registry", 409, false, "An owned package identity reservation is required.", "packageId"),
        Error(PackagePolicyErrorCodes.IdentityReservationLimit, "registry", 409, false, "The package identity reservation limit was reached.", "limit"),
        Error(PackagePolicyErrorCodes.StackIdentityReservationRequired, "registry", 409, false, "An owned Stack identity reservation is required.", "stackId"),
        Error(PackagePolicyErrorCodes.StackIdentityReservationLimit, "registry", 409, false, "The Stack identity reservation limit was reached.", "limit"),
        Error(PackagePolicyErrorCodes.StackSecurityBlocked, "registry", 410, false, "The Stack is blocked by security policy.", "stackId"),
        Error(PackagePolicyErrorCodes.ContractAuthorityReservationLimit, "registry", 409, false, "The contract authority reservation limit was reached.", "limit"),
        Error(PackagePolicyErrorCodes.SecurityBlocked, "registry", 410, false, "The package is blocked by security policy.", "packageId", "version", "blockScope"),
        Error(PackagePolicyErrorCodes.AppProcessSupervisionRequired, "runtime-host", 409, false, "An authenticated App process policy session is required.", "runtimeInstanceId", "appGenerationId"),
        Error(PackagePolicyErrorCodes.AppProcessTerminationRequired, "runtime-host", 409, false, "The previous App process must exit before package code can load.", "runtimeInstanceId", "appGenerationId", "terminationState"),
        Error(PackagePolicyErrorCodes.ContractAuthorityRevoked, "registry", 410, false, "The required contract authority release is revoked.", "authorityId", "releaseVersion", "catalogSha256"),
        Error(PackagePolicyErrorCodes.ContractAuthorityReleaseConflict, "registry", 409, false, "Included package ranges or exact RPC requirements conflict.", "packageId", "requirements"),
        Error(PackagePolicyErrorCodes.CandidateAuthorityUnconfirmed, "registry", 409, false, "The restore-selected package artifact has not been accepted by the Registry.", "packageId", "version"),
        Error(PackagePolicyErrorCodes.ContractVersionBumpRequired, "registry", 409, false, "The contract requires a compatible new version.", "contractId", "contractVersion"),
        Error(PackagePolicyErrorCodes.PlanStale, "registry", 409, true, "The package resolution plan is stale.", "expectedResolutionFingerprint", "resolutionFingerprint", "packagePolicyRevision"),
        Error(PackagePolicyErrorCodes.PolicyRequired, "runtime-host", 409, true, "Trusted package policy is required.", "registryOrigin", "packageId"),
        Error(PackagePolicyErrorCodes.PolicyStale, "runtime-host", 409, true, "The policy response is older than the retained watermark.", "packagePolicyRevision", "minimumPackagePolicyRevision"),
        Error(PackagePolicyErrorCodes.PolicyConflict, "runtime-host", 409, false, "The policy response conflicts with retained evidence.", "packagePolicyRevision", "packagePolicyFingerprint"),
        Error(PackagePolicyErrorCodes.PolicyExpired, "runtime-host", 409, true, "The package policy lease expired.", "registryOrigin", "validUntilUtc"),
        Error(PackagePolicyErrorCodes.PolicyClockInvalid, "runtime-host", 409, true, "Elapsed policy lease time cannot be established.", "registryOrigin", "clockState"),
        Error(RpcReadinessErrorCodes.ProviderNotFound, "runtime-broker", 404, false, "No matching provider is available.", "contract", "selectedPackageId", "matchCount"),
        Error(RpcReadinessErrorCodes.ProviderAmbiguous, "runtime-broker", 409, false, "Provider selection is ambiguous.", "contract", "selectedPackageId", "matchCount"),
        Error(RpcReadinessErrorCodes.RequiredProviderUnsatisfied, "runtime-host", 409, false, "A required provider is unavailable in the requested graph.", "predicates"),
        Error(RpcReadinessErrorCodes.RequiredProviderCycle, "runtime-host", 409, false, "Required providers form an activation cycle.", "packageIds", "predicates"),
    ]);

    private static PolicyErrorDescriptor Error(string code, string owner, int status, bool retryable, string message, params string[] fields)
        => new(code, owner, status, retryable, message, Array.AsReadOnly(fields));
}
