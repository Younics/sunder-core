using Sunder.App.Services;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed record RuntimeConfirmationViewModel(
    string Title,
    string Message,
    string ConfirmText,
    IReadOnlyList<string> Items,
    string Details,
    bool CanConfirm = true)
{
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
            restart ? "Restart the affected packages to apply this exact downloaded update?" : "Download side-by-side runtimes. Running work will continue; updates wait until packages stop.",
            restart ? "Restart & Apply" : "Download Update",
            plan.Runtimes.Select(item => $"{RuntimeManagementUi.Identity(item.CurrentRuntime)} → {RuntimeManagementUi.Resolution(item.Resolution)}\n"
                + (restart ? "Downloaded" : $"{RegistryPackageChangePreflight.FormatBytes(item.Resolution.DownloadSize)} download") + $" · {RuntimeManagementUi.Source(item.Resolution.Artifact.Identity)}")
                .Concat(plan.Usage.AffectedPackageIds.Select(id => $"Affected package: {id}"))
                .Concat(plan.Usage.RequiresAppRestart ? ["Sunder also needs to restart. Unsaved package work may be interrupted."] : []).ToArray(),
            $"Running packages: {string.Join(", ", plan.Usage.RunningPackageIds)}\nPlan: {plan.Plan?.Fingerprint}\nImpact: {plan.Usage.Fingerprint}");
}
