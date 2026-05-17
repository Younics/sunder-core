using Sunder.Protocol;
using Sunder.Registry.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

internal sealed class PackageOperationFinalizer(
    Func<IReadOnlyList<string>, CancellationToken, Task> applyPackageLifecycleChangesAsync,
    NotificationCenterService notificationCenter)
{
    public async Task FinishRegistryOperationAsync(
        BackgroundProcessContext context,
        RegistryPackageInstallExecutionResult result,
        string successTitle,
        string successFallbackMessage)
    {
        if (!result.Success)
        {
            var message = result.Errors.FirstOrDefault() ?? result.Message ?? "Package operation failed.";
            await PublishFailureAsync(message).ConfigureAwait(false);
            throw new InvalidOperationException(message);
        }

        await FinishSuccessfulOperationAsync(
            context,
            result.ImpactedPackageIds,
            result.Message,
            successTitle,
            BuildRegistryPackageOperationToastMessage(result, successFallbackMessage),
            successFallbackMessage).ConfigureAwait(false);
    }

    public async Task FinishLocalOperationAsync(
        BackgroundProcessContext context,
        PackageOperationResult result,
        string successTitle,
        string successFallbackMessage)
    {
        if (!result.Success)
        {
            var message = result.Errors.FirstOrDefault() ?? result.Message ?? "Package operation failed.";
            await PublishFailureAsync(message).ConfigureAwait(false);
            throw new InvalidOperationException(message);
        }

        await FinishSuccessfulOperationAsync(
            context,
            result.ImpactedPackageIds,
            result.Message,
            successTitle,
            string.IsNullOrWhiteSpace(result.Message) ? successFallbackMessage : result.Message.Trim(),
            successFallbackMessage).ConfigureAwait(false);
    }

    private async Task FinishSuccessfulOperationAsync(
        BackgroundProcessContext context,
        IReadOnlyList<string> impactedPackageIds,
        string? resultMessage,
        string successTitle,
        string successToastMessage,
        string successFallbackMessage)
    {
        if (impactedPackageIds.Count > 0)
        {
            context.ReportProgress(92, "Applying package changes to the running shell...");
            var lifecycleWarning = await ApplyPackageLifecycleChangesAsync(impactedPackageIds, context.CancellationToken).ConfigureAwait(false);
            if (lifecycleWarning is not null)
            {
                context.ReportProgress(100, lifecycleWarning);
                return;
            }
        }

        context.ReportProgress(100, resultMessage ?? successFallbackMessage);
        if (impactedPackageIds.Count > 0)
        {
            await PublishSuccessAsync(successTitle, successToastMessage).ConfigureAwait(false);
        }
    }

    private async Task<string?> ApplyPackageLifecycleChangesAsync(IReadOnlyList<string> impactedPackageIds, CancellationToken cancellationToken)
    {
        try
        {
            await applyPackageLifecycleChangesAsync(impactedPackageIds, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Package store updated, but the running shell did not apply the change.", ex);
            var warning = $"Package store updated, but the running shell did not apply the change: {ex.Message}";
            await PublishWarningAsync("Restart Sunder to apply package changes", warning).ConfigureAwait(false);
            return warning;
        }
    }

    private async Task PublishSuccessAsync(string title, string message)
        => await notificationCenter.PublishAsync(
            "sunder.app",
            "Sunder",
            new PackageNotificationRequest(
                title,
                message,
                PackageNotificationDisplayMode.ToastOnly,
                PackageNotificationSeverity.Success)).ConfigureAwait(false);

    private async Task PublishWarningAsync(string title, string message)
        => await notificationCenter.PublishAsync(
            "sunder.app",
            "Sunder",
            new PackageNotificationRequest(
                title,
                message,
                PackageNotificationDisplayMode.ToastAndTray,
                PackageNotificationSeverity.Warning)).ConfigureAwait(false);

    private async Task PublishFailureAsync(string message)
        => await notificationCenter.PublishAsync(
            "sunder.app",
            "Sunder",
            new PackageNotificationRequest(
                "Package operation failed",
                string.IsNullOrWhiteSpace(message) ? "Package operation failed." : message,
                PackageNotificationDisplayMode.ToastAndTray,
                PackageNotificationSeverity.Error)).ConfigureAwait(false);

    private static string BuildRegistryPackageOperationToastMessage(RegistryPackageInstallExecutionResult result, string fallback)
    {
        if (result.PlanItems.Count == 1)
        {
            var item = result.PlanItems[0];
            if (item.CurrentVersion is null)
            {
                return $"{item.PackageId} {item.Version} was installed.";
            }

            if (string.Equals(item.CurrentVersion, item.Version, StringComparison.OrdinalIgnoreCase))
            {
                return $"{item.PackageId} {item.Version} was reinstalled.";
            }

            return $"{item.PackageId} was updated from {item.CurrentVersion} to {item.Version}.";
        }

        var packageChangeCount = result.PlanItems
            .Select(item => item.PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (packageChangeCount > 0)
        {
            return $"{packageChangeCount} package change(s) completed.";
        }

        return string.IsNullOrWhiteSpace(result.Message) ? fallback : result.Message.Trim();
    }
}
