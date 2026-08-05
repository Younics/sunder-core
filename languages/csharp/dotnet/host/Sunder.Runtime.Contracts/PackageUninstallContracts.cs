namespace Sunder.Runtime.Contracts;

public enum PackageUninstallDataBehavior
{
    Retain = 0,
}

public sealed record PackageUninstallPlanPackage(
    string PackageId,
    string Name,
    string Version);

public sealed record PackageUninstallPlan(
    string PackageId,
    IReadOnlyList<PackageUninstallPlanPackage> DirectRemovals,
    IReadOnlyList<PackageUninstallPlanPackage> CascadingRemovals,
    IReadOnlyList<string> ExpectedRemovalPackageIds,
    PackageLifecycleChangeSet ReloadImpact,
    PackageUninstallDataBehavior DataBehavior,
    IReadOnlyList<string> RetainedDataPackageIds,
    string ConfirmationToken)
{
    public IReadOnlyList<PackageUninstallPlanPackage> DirectRemovals { get; }
        = RuntimeContractCollections.Freeze(DirectRemovals);

    public IReadOnlyList<PackageUninstallPlanPackage> CascadingRemovals { get; }
        = RuntimeContractCollections.Freeze(CascadingRemovals);

    public IReadOnlyList<string> ExpectedRemovalPackageIds { get; }
        = RuntimeContractCollections.Freeze(ExpectedRemovalPackageIds);

    public PackageLifecycleChangeSet ReloadImpact { get; } = ReloadImpact with { };

    public IReadOnlyList<string> RetainedDataPackageIds { get; }
        = RuntimeContractCollections.Freeze(RetainedDataPackageIds);

    public bool RequiresCascadeConsent => CascadingRemovals.Count > 0;
}

public sealed record PackageUninstallRequest(
    bool AllowCascade = false,
    string? ConfirmationToken = null);
