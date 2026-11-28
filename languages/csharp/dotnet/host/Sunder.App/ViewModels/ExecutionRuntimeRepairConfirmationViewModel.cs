using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed class ExecutionRuntimeRepairConfirmationViewModel
{
    public ExecutionRuntimeRepairConfirmationViewModel(PackageExecutionRuntimeStatusViewModel runtime, RuntimeExecutionRuntimeImpact impact)
    {
        ArgumentNullException.ThrowIfNull(impact);
        Title = $"Repair {runtime.Title}";
        Target = runtime.TargetText;
        RuntimeId = runtime.Identity.RuntimeId;
        Status = runtime.StatusText;
        ImpactedPackageIds = impact.ImpactedPackageIds;
    }

    public string Title { get; }
    public string Target { get; }
    public string RuntimeId { get; }
    public string Status { get; }

    public IReadOnlyList<string> ImpactedPackageIds { get; }
}
