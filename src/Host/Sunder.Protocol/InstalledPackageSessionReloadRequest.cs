namespace Sunder.Protocol;

public sealed record InstalledPackageSessionReloadRequest(IReadOnlyList<string> ImpactedPackageIds);
