using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed class ExecutionRuntimeRepairConfirmationViewModel
{
    public ExecutionRuntimeRepairConfirmationViewModel(RuntimeExecutionRuntimeImpact impact)
    {
        ArgumentNullException.ThrowIfNull(impact);
        PackageReferenceText = $"{impact.PackageReferenceCount} installed packages use this exact runtime.";
        ImpactedPackageIds = impact.ImpactedPackageIds;
    }

    public string PackageReferenceText { get; }

    public IReadOnlyList<string> ImpactedPackageIds { get; }
}
