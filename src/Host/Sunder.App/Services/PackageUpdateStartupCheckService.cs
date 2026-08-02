using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

public sealed class PackageUpdateStartupCheckService(
    IBackgroundProcessQueue backgroundProcesses,
    IRuntimeApiClientFactory runtimeApiClientFactory,
    IPackageNotificationService notificationService)
{
    internal const string GroupKey = "sunder.package-update-check";
    private const string SourceMetadataKey = "sunder.packageUpdateCheck";

    private readonly IBackgroundProcessQueue _backgroundProcesses = backgroundProcesses;
    private readonly IRuntimeApiClientFactory _runtimeApiClientFactory = runtimeApiClientFactory;
    private readonly IPackageNotificationService _notificationService = notificationService;

    public BackgroundProcessSnapshot EnqueueStartupCheck()
        => _backgroundProcesses.Enqueue(new BackgroundProcessRequest(
            "Check package updates",
            GroupKey,
            BackgroundProcessIndicator.Hidden,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            CanCancel: false,
            CheckForPackageUpdatesAsync,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [SourceMetadataKey] = bool.TrueString,
            }));

    private async Task CheckForPackageUpdatesAsync(BackgroundProcessContext context)
    {
        try
        {
            context.ReportProgress(0, "Checking installed packages...");
            using var runtimeApiClient = _runtimeApiClientFactory.CreateClient<IRuntimePackageUpdateClient>();
            var installedPackages = await runtimeApiClient.GetInstalledPackagesAsync(context.CancellationToken).ConfigureAwait(false);
            if (installedPackages.Count == 0)
            {
                context.ReportProgress(100, "No installed packages.");
                return;
            }

            context.ReportProgress(35, "Resolving package updates...");
            var updateCount = 0;
            foreach (var request in InstalledPackageUpdateRequestBuilder.Build(installedPackages))
            {
                var requestedPackageIds = request.Packages
                    .Select(package => package.PackageId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var plan = await runtimeApiClient.ResolveRegistryPackagePlanAsync(
                    request,
                    context.CancellationToken).ConfigureAwait(false);
                if (!plan.Success)
                {
                    context.ReportProgress(100, "Package update check failed.");
                    return;
                }
                updateCount += plan.Items.Count(item => requestedPackageIds.Contains(item.PackageId)
                                                        && item.CurrentVersion is not null
                                                        && !string.Equals(item.CurrentVersion, item.Version, StringComparison.OrdinalIgnoreCase));
            }

            if (updateCount > 0)
            {
                await _notificationService.PublishAsync(
                    new PackageNotificationRequest(
                        "Package updates available",
                        FormatUpdateNotificationMessage(updateCount),
                        PackageNotificationDisplayMode.ToastAndTray,
                        PackageNotificationSeverity.Information),
                    context.CancellationToken).ConfigureAwait(false);
            }

            context.ReportProgress(100, updateCount == 0
                ? "Installed packages are up to date."
                : $"{updateCount} package update(s) available.");
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Sunder startup package update check failed.", ex);
            context.ReportProgress(100, "Package update check failed.");
        }
    }

    private static string FormatUpdateNotificationMessage(int updateCount)
        => updateCount == 1
            ? "1 installed package can be updated. Open Packages to review and update."
            : $"{updateCount} installed packages can be updated. Open Packages to review and update.";
}
