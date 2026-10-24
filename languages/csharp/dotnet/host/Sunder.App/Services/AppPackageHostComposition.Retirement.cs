namespace Sunder.App.Services;

internal sealed class AppPackageActivationCleanupUnprovenException(string packageId)
    : Exception($"App activation '{packageId}' has unproven local cleanup. Its original resources and development ownership remain quarantined; retirement has not been acknowledged.");

internal sealed partial class AppPackageHostComposition
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Retirement> _packageRetirements = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyList<string> RetiringPackageIds => _packageRetirements.Keys.ToArray();
    internal bool HasAcknowledgedRetirement(string packageId)
        => _packageRetirements.TryGetValue(packageId, out var retirement)
            && retirement.Drain.IsCompletedSuccessfully
            && retirement.Drain.Result.CleanupOutcome == AppPackageUiCleanupOutcome.Proven
            && retirement.Drain.Result.DataSettlementProven && retirement.Admission?.IsRetired == true;

    private sealed class Retirement(Func<Retirement, Task<AppPackageUnloadResult>> drain)
    {
        private Task<AppPackageUnloadResult>? _drain;
        private readonly object _gate = new();
        internal AppPackageLoadAdmission? Admission { get; set; }
        internal Task<AppPackageUnloadResult> Drain
        {
            get { lock (_gate) return _drain ??= drain(this); }
        }
    }

    private async Task<AppPackageUnloadResult> RetireOwnedPackageAsync(string packageId, bool preserveDisabled, bool useRetirementDeadline)
    {
        var retirement = _packageRetirements.GetOrAdd(packageId,
            _ => new(entry => DrainRetirementAsync(packageId, preserveDisabled, entry)));
        var acknowledged = AcknowledgeRetirementAsync(packageId, retirement);
        if (!useRetirementDeadline) return await acknowledged.ConfigureAwait(false);
        try { return await acknowledged.WaitAsync(AppShutdownBudgets.GenerationRetirement).ConfigureAwait(false); }
        catch (TimeoutException error)
        {
            AppCleanupQuarantine.Retain(acknowledged, $"retiring App activation '{packageId}'");
            AppSessionLog.WriteError($"App activation '{packageId}' did not finish retirement within its cleanup budget.", error);
            return new(WasLoaded: true, AppPackageUiCleanupOutcome.Unproven);
        }
    }

    private async Task<AppPackageUnloadResult> DrainRetirementAsync(string packageId, bool preserveDisabled, Retirement retirement)
    {
        if (!_state.TryRemoveLoadedPackage(packageId, preserveDisabled, out var handle) || handle is null)
            return AppPackageUnloadResult.NotLoaded;
        retirement.Admission = handle.LoadAdmission;
        var settledBeforeRevocation = await _publication.SettleAndRevokeAsync().ConfigureAwait(false);
        var dataSettlementProven = await _publication.CompleteDataMutationsAsync().ConfigureAwait(false)
            && settledBeforeRevocation;
        if (!dataSettlementProven)
            AppSessionLog.WriteInfo($"App activation '{packageId}' has an unconfirmed data mutation; local resource cleanup will continue without claiming state settlement.", visibleInDeveloperLog: true);
        var outcome = await _unloadCoordinator.DrainPackageAsync(packageId, handle).ConfigureAwait(false);
        RecordCleanupOutcome(outcome);
        return new(WasLoaded: true, outcome) { DataSettlementProven = dataSettlementProven };
    }

    private static async Task<AppPackageUnloadResult> AcknowledgeRetirementAsync(string packageId, Retirement retirement)
    {
        var drained = await retirement.Drain.ConfigureAwait(false);
        if (drained.CleanupOutcome != AppPackageUiCleanupOutcome.Proven || !drained.DataSettlementProven || retirement.Admission is not { } admission)
            return drained;
        try
        {
            // Local disposal happens once. A transient control-plane failure can
            // retry only the original admission, including an ambiguous response.
            await admission.RetireAsync().ConfigureAwait(false);
            return drained;
        }
        catch (Exception error) when (AppPackageUiFaultPolicy.IsRecoverable(error))
        {
            AppSessionLog.WriteError($"The Host did not acknowledge retirement of App activation '{packageId}'.", error);
            return drained with { CleanupOutcome = AppPackageUiCleanupOutcome.Unproven };
        }
    }

    internal async Task RetireForOwnerReleaseAsync(string packageId, CancellationToken token)
    {
        AppPackageLoadAdmission.RequireOutsideCleanup(_activationOwnerId);
        var result = await RetireOwnedPackageAsync(packageId, preserveDisabled: false, useRetirementDeadline: true)
            .WaitAsync(token).ConfigureAwait(false);
        if (_packageRetirements.TryGetValue(packageId, out var retirement) && retirement.Drain.IsCompletedSuccessfully
            && (retirement.Drain.Result.CleanupOutcome != AppPackageUiCleanupOutcome.Proven || !retirement.Drain.Result.DataSettlementProven))
            throw new AppPackageActivationCleanupUnprovenException(packageId);
        if (!result.WasLoaded || result.CleanupOutcome != AppPackageUiCleanupOutcome.Proven || !result.DataSettlementProven)
            throw new InvalidOperationException($"App activation '{packageId}' has not confirmed retirement; its development owner remains loaded.");
        PublishPackageStateChanged();
    }
}
