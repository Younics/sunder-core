namespace Sunder.Protocol;

public sealed record PackageSourceDescriptor(
    string PackageId,
    PackageSourceKind Kind,
    string Folder);
