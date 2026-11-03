namespace Sunder.Registry.Contracts;

public static class RegistryPublishRequestHeaders
{
    public const string ExpectedResourceId = "X-Sunder-Expected-Resource-Id";
    public const string SetLatest = "X-Sunder-Set-Latest";
}

public static class RegistryPublishTokenScopes
{
    public const string PackagePublish = RegistryApiKeyPermissions.PackagePublish;
    public const string PackagePromoteLatest = RegistryApiKeyPermissions.PackagePromoteLatest;
    public const string StackPublish = RegistryApiKeyPermissions.StackPublish;
}

public sealed record RegistryPublishTokenGrant(
    string Scope,
    string ResourceId = "",
    bool AllPackages = false);

public sealed record RegistryCreatePublishTokenRequest(
    string DisplayName,
    IReadOnlyList<RegistryPublishTokenGrant> Grants,
    int? LifetimeDays = null,
    bool NeverExpires = false);

public sealed record RegistryCreatePublishTokenResponse(
    Guid Id,
    string Token,
    string DisplayName,
    IReadOnlyList<RegistryPublishTokenGrant> Grants,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc);

public sealed record RegistryPublishTokenSummary(
    Guid Id,
    string DisplayName,
    IReadOnlyList<RegistryPublishTokenGrant> Grants,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    DateTimeOffset? ExpiresAtUtc);
