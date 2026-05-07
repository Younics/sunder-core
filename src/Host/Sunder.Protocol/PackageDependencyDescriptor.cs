namespace Sunder.Protocol;

public sealed record PackageDependencyDescriptor(
    string PackageId,
    string VersionRange);
