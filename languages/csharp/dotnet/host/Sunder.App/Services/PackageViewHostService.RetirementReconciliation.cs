using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed partial class PackageViewHostService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _availabilityNotices = new();

    private async Task RetireRequestedActivationsAsync(
        IReadOnlyList<RuntimeAppActivationRetirementRequirement> requirements,
        IAppPackageDevelopmentBackend? developerServices, CancellationToken token)
    {
        foreach (var stage in requirements.Where(_packagePolicy.OwnsRetirement).GroupBy(item => item.StageId, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(stage.Key))
                await _uiDispatcher.InvokeAsync(() =>
                {
                    foreach (var requirement in stage)
                    {
                        if (!_availabilityNotices.TryAdd(requirement.AppActivationId, 0)) continue;
                        var packageId = requirement.Package.PackageId;
                        var displayName = CurrentGeneration.ActivePackages.FirstOrDefault(item => item.PackageId == packageId)?.DisplayName ?? packageId;
                        var reference = Guid.NewGuid().ToString("N");
                        var message = $"{displayName} lost a required Runtime provider. Its UI is being isolated; other packages remain available. "
                            + $"Inspect this package's developer logs and retry after the provider recovers. Reference: {reference}.";
                        AppSessionLog.WriteError(message, developerLogScope: DeveloperLogEntryScope.Package, developerLogSource: packageId);
                        // Availability incidents belong to an activation, not forever
                        // to its unchanged bytes: a later recovered owner may fail anew.
                        var notice = _faultNoticeCoordinator.Register(new(packageId, displayName,
                            ContentHash: null, GenerationId: requirement.AppActivationId), AppPackageFaultNoticeKind.Package,
                            null, $"{displayName}: Runtime provider unavailable", message, reference, deferNotification: false);
                        if (notice.ShouldPublishImmediately) notice.Incident.Publish();
                    }
                }, token).ConfigureAwait(false);
            await RetireActivationsAsync(stage.ToArray(), token).ConfigureAwait(false);
            // Other Apps may still owe this stage an acknowledgement. The shell owns
            // its terminal-status wait; it must not block later retirement requests.
            if (!string.IsNullOrWhiteSpace(stage.Key)) developerServices?.QueueRetirementReconciliation(stage.Key);
            else foreach (var requirement in stage.DistinctBy(item => item.AppActivationId))
                developerServices?.QueueAvailabilityReconciliation(requirement.AppActivationId);
        }
    }
}
