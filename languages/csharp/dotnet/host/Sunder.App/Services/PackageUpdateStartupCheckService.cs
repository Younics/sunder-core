using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

public sealed class PackageUpdateStartupCheckService(
    IBackgroundTaskQueue backgroundTasks,
    IRuntimeApiClientFactory runtimeApiClientFactory,
    IPackageNotificationService notificationService)
{
    internal const string GroupKey = "sunder.package-update-check";
    private const string SourceMetadataKey = "sunder.packageUpdateCheck";

    private readonly IBackgroundTaskQueue _backgroundTasks = backgroundTasks;
    private readonly IRuntimeApiClientFactory _runtimeApiClientFactory = runtimeApiClientFactory;
    private readonly IPackageNotificationService _notificationService = notificationService;
    internal event Action? ResultsChanged;
    private sealed record Observation(InstalledPackageProvenance Provenance, DateTimeOffset InstalledAt, RegistryPackageUpdate Update);
    private volatile Observation[] _results = [];

    internal IReadOnlyList<RegistryPackageUpdate> ForInstalled(IReadOnlyList<InstalledPackageDescriptor> packages)
        => _results.Where(entry => packages.Any(package => package.PackageId == entry.Update.PackageId
            && package.Version == entry.Update.CurrentVersion && package.Provenance == entry.Provenance
            && package.InstalledAtUtc == entry.InstalledAt)).Select(entry => entry.Update).ToArray();

    public BackgroundTaskSnapshot EnqueueStartupCheck()
        => _backgroundTasks.Enqueue(new BackgroundTaskRequest(
            "Check package updates",
            GroupKey,
            BackgroundTaskIndicator.Hidden,
            BackgroundTaskConcurrencyMode.SequentialWithinGroup,
            CanCancel: false,
            CheckForPackageUpdatesAsync,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [SourceMetadataKey] = bool.TrueString,
            }));

    private async Task CheckForPackageUpdatesAsync(BackgroundTaskContext context)
    {
        try
        {
            context.ReportProgress(0, "Checking installed packages...");
            using var runtimeApiClient = _runtimeApiClientFactory.CreateClient<IRuntimePackageUpdateClient>();
            var installedPackages = await runtimeApiClient.GetInstalledPackagesAsync(context.CancellationToken).ConfigureAwait(false);
            if (installedPackages.Count == 0)
            {
                _results = [];
                ResultsChanged?.Invoke();
                context.ReportProgress(100, "No installed packages.");
                return;
            }

            context.ReportProgress(35, "Resolving package updates...");
            var updates = new List<Observation>();
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
                foreach (var item in plan.Items.Where(item => item.ActionKind == RegistryPackageActionKinds.PackageUpdate
                             && requestedPackageIds.Contains(item.PackageId) && item.Version is not null && item.Version != item.CurrentVersion))
                {
                    var installed = installedPackages.FirstOrDefault(package => package.PackageId == item.PackageId && package.Version == item.CurrentVersion);
                    if (installed is not null)
                        updates.Add(new(installed.Provenance, installed.InstalledAtUtc,
                            new(item.PackageId, installed.Version, item.Version!, item.DeprecatedMessage, [])));
                }
            }

            _results = updates.ToArray();
            ResultsChanged?.Invoke();
            var updateCount = updates.Count;
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
