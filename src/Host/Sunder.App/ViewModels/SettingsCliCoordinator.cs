using Sunder.App.Services;

namespace Sunder.App.ViewModels;

internal sealed class SettingsCliCoordinator(CliInstallationService cliInstallationService)
{
    public async Task<SettingsCliOperationResult> RefreshStatusAsync(bool showSuccessStatus)
    {
        try
        {
            var status = await cliInstallationService.GetStatusAsync();
            return SettingsCliOperationResult.Completed(
                SettingsCliState.FromStatus(status),
                showSuccessStatus ? "CLI status refreshed." : null);
        }
        catch (Exception ex)
        {
            return SettingsCliOperationResult.Failure(ex.Message);
        }
    }

    public async Task<SettingsCliOperationResult> InstallOrRepairAsync()
    {
        try
        {
            var result = await cliInstallationService.EnsureInstalledAsync();
            return SettingsCliOperationResult.Completed(SettingsCliState.FromStatus(result.Status), result.Status.Summary);
        }
        catch (Exception ex)
        {
            return SettingsCliOperationResult.Failure(ex.Message);
        }
    }

    public async Task<SettingsCliOperationResult> UninstallAsync()
    {
        try
        {
            var status = await cliInstallationService.UninstallAsync();
            return SettingsCliOperationResult.Completed(
                SettingsCliState.FromStatus(status),
                "CLI user install removed. Existing PATH entries were left unchanged.");
        }
        catch (Exception ex)
        {
            return SettingsCliOperationResult.Failure(ex.Message);
        }
    }
}

internal sealed record SettingsCliOperationResult(
    bool Success,
    SettingsCliState? State,
    string? StatusText,
    string? WarningText)
{
    public static SettingsCliOperationResult Completed(SettingsCliState state, string? statusText)
        => new(true, state, statusText, null);

    public static SettingsCliOperationResult Failure(string message)
        => new(false, null, message, message);
}

internal sealed record SettingsCliState(
    string StatusText,
    string StatusDescription,
    string PlatformText,
    string BundledPath,
    string InstalledPath,
    string ShimPath,
    string WarningText,
    string PathInstructions,
    bool CanInstallOrRepair,
    bool CanUninstall)
{
    public static SettingsCliState FromStatus(CliInstallationStatus status)
        => new(
            status.Summary,
            status.IsFullyInstalled
                ? "Sunder installs the command shim for this user but does not verify shell-profile PATH entries. Use the PATH instructions below, then run sunder --help in your terminal to confirm."
                : status.Warning ?? "The sunder command is not fully configured.",
            status.PlatformName,
            status.Paths.BundledCliPath ?? "Not found",
            status.Paths.InstalledCliPath,
            status.Paths.ShimPath,
            status.Warning ?? string.Empty,
            status.PathInstructions,
            status.CanInstallOrRepair,
            status.IsInstalled || status.IsShimInstalled);
}
