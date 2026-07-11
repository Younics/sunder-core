namespace Sunder.Runtime.Contracts;

public sealed record PackageDependencyDescriptor(
    string PackageId,
    string VersionRange);
