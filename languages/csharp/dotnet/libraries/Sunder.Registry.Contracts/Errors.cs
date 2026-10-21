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
    public const string Internal = "registry.v1.internal";
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
