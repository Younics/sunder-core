using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed class PackageExecutionRuntimeStatusViewModel
{
    private PackageExecutionRuntimeStatusViewModel(
        PackageTargetExecutionRuntimeBinding binding,
        string statusText,
        string detailText,
        bool showWarning,
        bool canRepair,
        string actionText = "Repair")
    {
        Identity = binding.Runtime;
        Role = binding.Role;
        Title = $"{Services.RuntimeManagementUi.Family(binding.Runtime.RuntimeId)} {binding.Runtime.VendorVersion}";
        TargetText = $"{binding.Role} | {binding.Runtime.Rid} | {Services.RuntimeManagementUi.Source(binding.Runtime)}";
        StatusText = statusText;
        DetailText = detailText;
        ShowWarning = showWarning;
        CanRepair = canRepair;
        ActionText = actionText;
    }

    public RuntimeExecutionRuntimeIdentity Identity { get; }

    public string Role { get; }

    public string Title { get; }

    public string TargetText { get; }

    public string StatusText { get; }

    public string DetailText { get; }

    public bool ShowWarning { get; }

    public bool CanRepair { get; }

    public string ActionText { get; }

    internal static PackageExecutionRuntimeStatusViewModel Create(
        PackageTargetExecutionRuntimeBinding binding,
        InstalledExecutionRuntimeSummary? runtime)
    {
        if (runtime is null)
        {
            return new(
                binding,
                "Missing",
                "The exact bound runtime is missing. Restore it from its official source, verify its vendor checksum, and reload every dependent package.",
                showWarning: true,
                canRepair: true,
                actionText: "Restore");
        }

        if (runtime.Health.RepairRecommended
            || runtime.Status is RuntimeExecutionRuntimeStatuses.Unhealthy
                or RuntimeExecutionRuntimeStatuses.Quarantined)
        {
            return new(
                binding,
                "Repair required",
                runtime.Health.Message ?? "Integrity verification failed for this execution runtime.",
                showWarning: true,
                canRepair: true);
        }

        if (runtime.Support.State != RuntimeExecutionRuntimeSupportState.Supported)
        {
            return new(
                binding,
                runtime.Support.State == RuntimeExecutionRuntimeSupportState.EndOfLife ? "End of support" : "Support unknown",
                runtime.Support.Message ?? "Check Package Runtimes for an eligible official update.",
                showWarning: true,
                canRepair: false);
        }
        if (runtime.Status != RuntimeExecutionRuntimeStatuses.Ready)
        {
            return new(
                binding,
                "Attention required",
                runtime.Health.Message ?? "The execution runtime is not ready for normal activation.",
                showWarning: true,
                canRepair: false);
        }

        return new(
            binding,
            "Compatible",
            "The exact official runtime is installed and verified against its vendor checksum.",
            showWarning: false,
            canRepair: false);
    }
}
