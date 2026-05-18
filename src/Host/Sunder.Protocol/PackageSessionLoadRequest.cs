namespace Sunder.Protocol;

public sealed record PackageSessionLoadRequest(
    PackageSourceKind SourceKind,
    string Source,
    bool Watch = false);
