namespace Sunder.Registry.Contracts;

/// <summary>Operations an API key may perform within its resource scope.</summary>
public static class RegistryApiKeyPermissions
{
    public const string PackagePublish = "package:publish";
    public const string PackagePromoteLatest = "package:promote-latest";
    public const string PackageRelease = "package:release";
    public const string StackPublish = "stack:publish";

    public static IReadOnlyList<RegistryApiKeyPermission> Catalog { get; } = Array.AsReadOnly<RegistryApiKeyPermission>(
    [
        new(PackagePublish, "Publish versions", "Upload new immutable package versions.", "Package", []),
        new(PackagePromoteLatest, "Promote on publish", "Update latest when publishing a package directly.", "Package", [PackagePublish]),
        new(PackageRelease, "Complete releases", "Make staged versions available and advance latest or preview. Owners only; no yanking, tag deletion or rollback.", "Package", [PackagePublish]),
        new(StackPublish, "Publish revisions", "Publish new Stack revisions.", "Stack", []),
    ]);
}

public sealed record RegistryApiKeyPermission(string Id, string Name, string Description, string ResourceKind, IReadOnlyList<string> Requires);
/// <summary>ResourceId is empty only when AllPackages is explicitly selected. Stack grants are always exact.</summary>
public sealed record RegistryApiKeyGrant(string Permission, string ResourceId = "", bool AllPackages = false);
public sealed record RegistryCreateApiKeyRequest(string DisplayName, IReadOnlyList<RegistryApiKeyGrant> Grants,
    int? LifetimeDays = null, bool NeverExpires = false);
public sealed record RegistryCreateApiKeyResponse(Guid Id, string Key, string DisplayName, IReadOnlyList<RegistryApiKeyGrant> Grants,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? ExpiresAtUtc);
public sealed record RegistryApiKeySummary(Guid Id, string DisplayName, IReadOnlyList<RegistryApiKeyGrant> Grants,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? ExpiresAtUtc);
