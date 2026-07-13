namespace Sunder.Registry.Contracts;

public static class RegistryV1ErrorCodes
{
    public const string InvalidRequest = "registry.v1.request.invalid";
    public const string Unauthorized = "registry.v1.auth.unauthorized";
    public const string Forbidden = "registry.v1.auth.forbidden";
    public const string NotFound = "registry.v1.resource.not_found";
    public const string Conflict = "registry.v1.resource.conflict";
    public const string PackageVersionExists = "registry.v1.package.version_exists";
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
