namespace Sunder.Runtime.Contracts;

public sealed record PackageLifecycleChangeSet(
    IReadOnlyList<string> StoreChangedPackageIds,
    IReadOnlyList<string> RuntimeReloadPackageIds,
    IReadOnlyList<string> AppReloadPackageIds,
    IReadOnlyList<string> RemovedPackageIds,
    bool SharedAssemblyResetRequired)
{
    public static PackageLifecycleChangeSet Empty { get; } = new([], [], [], [], SharedAssemblyResetRequired: false);
}
