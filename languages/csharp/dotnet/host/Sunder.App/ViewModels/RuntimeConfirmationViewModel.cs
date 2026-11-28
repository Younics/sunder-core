using Sunder.App.Services;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed record RuntimeConfirmationViewModel(
    string Title,
    string Message,
    string ConfirmText,
    IReadOnlyList<string> Items,
    string Details,
    bool CanConfirm = true,
    bool IsRemoval = false,
    string? Impact = null)
{
    public static RuntimeConfirmationViewModel Uninstall(PackageUninstallPlan plan)
    {
        var target = plan.DirectRemovals.Single();
        var dependents = plan.CascadingRemovals.Count;
        var effect = dependents switch
        {
            0 => "No other installed packages will be removed.",
            1 => $"One installed package depends on {target.Name}.",
            _ => $"{dependents} installed packages depend on {target.Name}.",
        };
        return new($"Uninstall {target.Name}?",
            effect,
            dependents == 0 ? "Uninstall 1 package" : $"Uninstall {plan.ExpectedRemovalPackageIds.Count} packages",
            [$"Requested: {target.Name} {target.Version}", ..plan.CascadingRemovals.Select(package => $"Also removed: {package.Name} {package.Version}")],
            string.Join("\n", plan.DirectRemovals.Concat(plan.CascadingRemovals).Select(package => $"{package.PackageId} {package.Version}")),
            IsRemoval: true, Impact: "Package data will be kept.");
    }

    public static RuntimeConfirmationViewModel Install(RuntimeExecutionRuntimeInstallPlan plan)
    {
        var resolution = plan.Resolution!;
        return new("Install package runtime", "This runtime will be shared by compatible packages and kept until you remove it.",
            "Download & Install Runtime",
            [$"{RuntimeManagementUi.Resolution(resolution)} — {RegistryPackageChangePreflight.FormatBytes(resolution.DownloadSize)} download", RuntimeManagementUi.Source(resolution.Artifact.Identity)],
            $"Device: {plan.Plan?.Rid}\nABI: {resolution.Artifact.Identity.ModuleAbi}\nPlan: {plan.Plan?.Fingerprint}");
    }

    public static RuntimeConfirmationViewModel Update(RuntimeExecutionRuntimeUpdatePlan plan, bool restart)
        => new(restart ? "Restart & apply runtime update" : "Download runtime update",
            restart ? "Restart the affected packages to apply this downloaded update." : "Download the runtime update alongside the running version.",
            restart ? "Restart & Apply" : "Download Update",
            plan.Runtimes.Select(item => $"{RuntimeManagementUi.Identity(item.CurrentRuntime)} → {RuntimeManagementUi.Resolution(item.Resolution)}\n"
                + (restart ? "Downloaded" : $"{RegistryPackageChangePreflight.FormatBytes(item.Resolution.DownloadSize)} download") + $" · {RuntimeManagementUi.Source(item.Resolution.Artifact.Identity)}")
                .Concat(plan.Usage.AffectedPackageIds.Select(id => $"Affected package: {id}")).ToArray(),
            $"Running packages: {string.Join(", ", plan.Usage.RunningPackageIds)}\nPlan: {plan.Plan?.Fingerprint}\nImpact: {plan.Usage.Fingerprint}",
            Impact: restart
                ? plan.Usage.RequiresAppRestart ? "Sunder also needs to restart. Unsaved package work may be interrupted." : "Work in the affected packages may be interrupted."
                : "Running packages continue on their current runtime. The update waits until they stop.");
}
