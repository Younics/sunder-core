using Sunder.Registry.Contracts;
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
        Title = $"{binding.Runtime.RuntimeId} {binding.Runtime.Version}";
        TargetText = $"{binding.Role} | {binding.Runtime.Rid} | revision {binding.Runtime.PackagingRevision}";
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
                "The exact bound execution runtime is missing. Restore that exact trusted runtime and reload every dependent package.",
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

        var lifecycle = runtime.Lifecycle.State;
        if (lifecycle == RuntimeExecutionRuntimeLifecycleStates.PolicyRequired)
        {
            return new(
                binding,
                "Policy required",
                runtime.Lifecycle.PolicyMessage
                ?? "Trusted Registry lifecycle policy is required before new packages can bind this runtime.",
                showWarning: true,
                canRepair: false);
        }
        if (lifecycle == ExecutionRuntimeLifecycleStates.Revoked)
        {
            return new(
                binding,
                "Revoked",
                runtime.Lifecycle.PolicyMessage
                ?? "Registry policy revoked this runtime. Update the package before activating it.",
                showWarning: true,
                canRepair: false);
        }
        if (lifecycle == ExecutionRuntimeLifecycleStates.Retired)
        {
            return new(
                binding,
                "Retired",
                runtime.Lifecycle.PolicyMessage
                ?? "This runtime is retired. Update the package to a supported runtime.",
                showWarning: true,
                canRepair: false);
        }
        if (lifecycle == ExecutionRuntimeLifecycleStates.Deprecated)
        {
            return new(
                binding,
                "Deprecated",
                runtime.Lifecycle.PolicyMessage
                ?? "This runtime remains usable but should be replaced by a package update.",
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
            "The exact signed execution runtime is installed, healthy, and supported.",
            showWarning: false,
            canRepair: false);
    }
}
