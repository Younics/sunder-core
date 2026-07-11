namespace Sunder.Runtime.Contracts;

public sealed record PackageSessionLoadRequest(
    PackageSourceKind SourceKind,
    string PackageId,
    bool Watch = false);
