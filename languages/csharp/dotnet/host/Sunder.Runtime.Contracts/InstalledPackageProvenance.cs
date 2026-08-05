namespace Sunder.Runtime.Contracts;

public enum InstalledPackageSourceKind
{
    Unknown = 0,
    Registry = 1,
    LocalArchive = 2,
    Development = 3,
}

public enum InstalledPackageVersionPolicy
{
    Unmanaged = 0,
    FollowTag = 1,
    ExplicitVersion = 2,
    TransitiveDependency = 3,
}

public sealed record InstalledPackageProvenance(
    InstalledPackageSourceKind SourceKind,
    InstalledPackageVersionPolicy VersionPolicy,
    string? RegistryOrigin = null,
    string? SourcePackageId = null,
    string? RequestedTag = null,
    string? RequestedVersion = null,
    string? VersionRange = null,
    string? SourceIdentity = null,
    bool IncludePrerelease = false)
{
    public static InstalledPackageProvenance Unknown { get; } = new(
        InstalledPackageSourceKind.Unknown,
        InstalledPackageVersionPolicy.Unmanaged);
}
