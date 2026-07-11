namespace Sunder.Runtime.Contracts;

public sealed record PackageAuthStatusResponse(
    string PackageId,
    PackageAuthStatusKind Status,
    string Message,
    bool CanAuthorize,
    bool CanDisconnect);
