namespace Sunder.Runtime.Contracts;

public sealed record InstalledPackageSessionReloadRequest(IReadOnlyList<string> ImpactedPackageIds);
