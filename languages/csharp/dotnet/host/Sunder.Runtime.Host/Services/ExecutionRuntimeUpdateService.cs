using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;
using static Sunder.Runtime.Host.Services.ExecutionRuntimeManagementSupport;

namespace Sunder.Runtime.Host.Services;

internal sealed record ExecutionRuntimeUpdateConsent(RuntimeExecutionRuntimeUpdatePlan Preview, long SettingsRevision);
internal sealed record ExecutionRuntimeUninstallConsent(RuntimeExecutionRuntimeIdentity Runtime, RuntimeExecutionRuntimeUsage Usage);

internal sealed partial class ExecutionRuntimeUpdateService(
    ExecutionRuntimeInstallationService installation, ExecutionRuntimeManagementState state,
    ExecutionRuntimeManagementPlans plans, ExecutionRuntimeUsageService usage, RuntimeOperationGate operations,
    InstalledPackageStore installed, InstalledPackageLifecycleService lifecycle, PackageStoreCoordinator packages,
    ExecutionRuntimeStore store, TimeProvider clock, RuntimeEventStreamService? events = null)
{
    private readonly SemaphoreSlim _preparation = new(1, 1);
    private int _checking;
    public async Task<RuntimeExecutionRuntimeUpdateStatus> StatusAsync(CancellationToken token)
    {
        var value = await state.ReadAsync(token);
        return new(Volatile.Read(ref _checking) != 0, value.LastCheckedAtUtc, value.NextCheckAtUtc, value.LastErrorCode, value.Updates);
    }

    public async Task<RuntimeExecutionRuntimeUpdatePlan> PreviewAsync(RuntimeExecutionRuntimeUpdatePreviewRequest request, CancellationToken token)
    {
        if (request.Runtime is not null && request.PreparedUpdateId is not null) throw new RuntimeValidationException("Use one runtime update selector.");
        await using var operation = await operations.EnterAsync(token);
        token = operation.CancellationToken;
        var management = await state.ReadAsync(token);
        var current = await installed.ListAsync(token);
        var changes = new List<RuntimeExecutionRuntimeBindingChange>();
        var candidates = new List<RuntimeExecutionRuntimeUpdateCandidate>();
        if (request.PreparedUpdateId is { } updateId)
        {
            var pending = management.Updates.SingleOrDefault(value => value.UpdateId == updateId && ExecutionRuntimeManagementState.Pending(value))
                ?? throw Stale();
            var refreshed = await RefreshPreparedAuthorityAsync(pending, token);
            candidates.AddRange(refreshed.Runtimes);
            changes.AddRange(refreshed.BindingChanges);
        }
        else
        {
            foreach (var package in current)
            foreach (var binding in installed.ToDescriptor(package).ExecutionRuntimeBindings)
            {
                if (request.Runtime is not null && binding.Runtime != request.Runtime) continue;
                var resolution = await installation.ResolveAsync(binding.Requirement, token, binding.Runtime);
                if (!IsForward(binding.Runtime, resolution)) continue;
                changes.Add(new(package.PackageId, package.Version, package.ContentIdentity, binding, resolution));
                candidates.Add(new(binding.Runtime, resolution, false));
            }
            foreach (var retained in management.ManualInstalls)
            {
                if (request.Runtime is not null && retained.Runtime != request.Runtime) continue;
                var resolution = await installation.ResolveAsync(retained.Requirement, token, retained.Runtime);
                if (IsForward(retained.Runtime, resolution)) candidates.Add(new(retained.Runtime, resolution, true));
            }
        }
        var unique = candidates.GroupBy(value => (value.CurrentRuntime, Desired: value.Resolution.Artifact.Identity))
            .Select(group => group.First() with { TransferManualRetention = group.Any(value => value.TransferManualRetention) }).ToArray();
        var impact = await usage.CaptureAsync(unique.Select(value => value.CurrentRuntime).Distinct().ToArray(), token, changes.Select(value => value.PackageId).ToArray());
        // Existing manual candidate publication is generation-wide; disclose every package it restarts.
        var manualAffected = impact.AffectedPackageIds.Concat(lifecycle.RuntimeRestartScope).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        impact = new(Hash(new { impact.Fingerprint, manualAffected }), impact.PackageReferences, manualAffected, impact.RunningPackageIds,
            impact.ActiveLeaseCount, impact.PendingTransactionCount, impact.TemporaryRetentionCount, impact.ManualRetentionCount, impact.RequiresAppRestart);
        var preview = new RuntimeExecutionRuntimeUpdatePlan(true, null, unique.Length == 0 ? "No compatible Supported runtime updates." : "Compatible runtime update preview.",
            null, request.PreparedUpdateId, unique, changes, impact);
        return preview with { Plan = plans.Add(new ExecutionRuntimeUpdateConsent(preview, management.Settings.Revision),
            unique.Length == 0 ? null : unique.Min(value => value.Resolution.ExpiresAtUtc)) };
    }

    internal static bool IsForward(RuntimeExecutionRuntimeIdentity old, RuntimeExecutionRuntimeResolution selected)
    {
        var next = selected.Artifact.Identity;
        if (next.RuntimeId != old.RuntimeId || next.Rid != old.Rid || next.ModuleAbi != old.ModuleAbi
            || selected.Artifact.Support.State != RuntimeExecutionRuntimeSupportState.Supported) throw Stale();
        var comparison = SemanticVersion.Parse(next.VendorVersion).CompareTo(SemanticVersion.Parse(old.VendorVersion));
        if (comparison == 0 && next != old) throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.ArtifactChanged);
        return comparison > 0;
    }

    public Task<RuntimeExecutionRuntimeUpdateResult> PrepareAsync(RuntimeExecutionRuntimePlanConfirmation confirmation, CancellationToken token)
        => PrepareCoreAsync(confirmation, automatic: false, token);

    private async Task<RuntimeExecutionRuntimeUpdateResult> PrepareCoreAsync(RuntimeExecutionRuntimePlanConfirmation confirmation, bool automatic, CancellationToken token)
    {
        await _preparation.WaitAsync(token);
        var retentions = new List<IAsyncDisposable>();
        try
        {
            var consent = plans.Get<ExecutionRuntimeUpdateConsent>(confirmation);
            if (consent.Preview.Runtimes.Count == 0) return new(true, null, "No changes.", RuntimeExecutionRuntimeUpdateState.NoChanges, null);
            var management = await state.ReadAsync(token);
            if (automatic && (!management.Settings.AutomaticUpdatesEnabled || management.Settings.Revision != consent.SettingsRevision)) throw Stale();
            if (management.Updates.Count(ExecutionRuntimeManagementState.Pending) >= 32) throw new RuntimeConflictException("Pending runtime update limit reached.");
            await ValidateBindingsAsync(consent.Preview.BindingChanges, token);
            await RequireCurrentAuthoritiesAsync(consent.Preview.Runtimes, token);
            foreach (var candidate in consent.Preview.Runtimes)
            {
                retentions.Add(await store.RetainAsync(new(Identity(candidate.Resolution.Artifact.Identity)), token));
                if (automatic && !(await state.ReadAsync(token)).Settings.AutomaticUpdatesEnabled) throw Stale();
                await installation.AcquireAsync(candidate.Resolution, token, candidate.CurrentRuntime);
            }
            await ValidateBindingsAsync(consent.Preview.BindingChanges, token);
            var update = new RuntimeExecutionRuntimePreparedUpdate(Guid.NewGuid().ToString("N"), Hash(new { consent.Preview.Runtimes, consent.Preview.BindingChanges }),
                RuntimeExecutionRuntimeUpdateState.Prepared, clock.GetUtcNow(), consent.Preview.Runtimes, consent.Preview.BindingChanges);
            // Bridge transient store pins to durable pending authority under the same gate
            // GC uses to capture reachability; a pre-pin GC snapshot must finish first.
            await using (var handoff = await operations.EnterAsync(token))
            {
                var currentSettings = (await state.ReadAsync(handoff.CancellationToken)).Settings;
                if (automatic && (!currentSettings.AutomaticUpdatesEnabled || currentSettings.Revision != consent.SettingsRevision)) throw Stale();
                await ValidateBindingsAsync(consent.Preview.BindingChanges, handoff.CancellationToken);
                await RequireCurrentAuthoritiesAsync(consent.Preview.Runtimes, handoff.CancellationToken);
                handoff.CancellationToken.ThrowIfCancellationRequested();
                await state.SaveUpdateAsync(update, CancellationToken.None);
            }
            return new(true, null, "Runtime bytes prepared side by side; running packages were not changed.", update.State, update);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return Failure(error); }
        finally
        {
            foreach (var retention in retentions) await retention.DisposeAsync();
            _preparation.Release();
        }
    }

    public async Task<RuntimeExecutionRuntimeUpdateResult> ApplyAsync(RuntimeExecutionRuntimeUpdateApplyRequest request, CancellationToken token)
    {
        try
        {
            if (!Enum.IsDefined(request.Mode)) throw new RuntimeValidationException("Invalid runtime apply mode.");
            var consent = plans.Get<ExecutionRuntimeUpdateConsent>(request.Confirmation);
            if (consent.Preview.PreparedUpdateId is null || request.ExpectedImpactFingerprint != consent.Preview.Usage.Fingerprint) throw Stale();
            await using var operation = await operations.EnterAsync(token);
            token = operation.CancellationToken;
            _ = plans.Get<ExecutionRuntimeUpdateConsent>(request.Confirmation);
            var update = (await state.ReadAsync(token)).Updates.SingleOrDefault(value => value.UpdateId == consent.Preview.PreparedUpdateId) ?? throw Stale();
            update = new(update.UpdateId, Hash(new { consent.Preview.Runtimes, consent.Preview.BindingChanges }), update.State,
                update.PreparedAtUtc, consent.Preview.Runtimes, consent.Preview.BindingChanges);
            var impact = await usage.CaptureAsync(update.Runtimes.Select(value => value.CurrentRuntime).Distinct().ToArray(), token, update.BindingChanges.Select(value => value.PackageId).ToArray());
            var allAffected = impact.AffectedPackageIds.Concat(lifecycle.RuntimeRestartScope).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
            if (Hash(new { impact.Fingerprint, manualAffected = allAffected }) != request.ExpectedImpactFingerprint) throw Stale();
            return await ApplyUnderGateAsync(update, request.Mode, token);
        }
        catch (Exception error) when (error is not OperationCanceledException) { return Failure(error); }
    }

    internal async Task ApplyPreparedWhenStoppedAsync(CancellationToken token)
    {
        await using var operation = await operations.EnterAsync(token);
        token = operation.CancellationToken;
        var management = await state.ReadAsync(token);
        if (!management.Settings.AutomaticUpdatesEnabled) return;
        foreach (var update in management.Updates.Where(ExecutionRuntimeManagementState.Pending))
        {
            try { await ApplyUnderGateAsync(update, RuntimeExecutionRuntimeApplyMode.WhenStopped, token); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                var code = ExecutionRuntimeInstallationService.ErrorCode(error);
                if (code == ExecutionRuntimeErrorCodes.PlanStale)
                    await state.SaveUpdateAsync(update with { State = RuntimeExecutionRuntimeUpdateState.Stale }, token);
                else if (error is ExecutionRuntimeCurrentAuthorityException)
                    await state.SaveUpdateAsync(update with { State = RuntimeExecutionRuntimeUpdateState.Blocked }, token);
                await state.ChangeAsync(value => value with { LastErrorCode = code }, token);
            }
        }
    }

    private async Task<RuntimeExecutionRuntimeUpdateResult> ApplyUnderGateAsync(RuntimeExecutionRuntimePreparedUpdate update,
        RuntimeExecutionRuntimeApplyMode mode, CancellationToken token)
    {
        var current = await installed.ListAsync(token);
        var alreadyApplied = update.BindingChanges.Count > 0 && update.BindingChanges.All(change => current.Any(package => package.PackageId == change.PackageId
            && package.Version == change.PackageVersion && package.ContentIdentity == change.PackageContentIdentity
            && package.ExecutionRuntimeBindings.Any(binding => Matches(binding, change.Resolution.Artifact.Identity, change.PreviousBinding.Role))));
        if (alreadyApplied)
        {
            if (await packages.GetPendingRuntimeCutoverAsync(token) is not null)
            {
                var recovering = update with { State = RuntimeExecutionRuntimeUpdateState.ReconciliationPending };
                if (update.State != recovering.State) await state.SaveUpdateAsync(recovering, token);
                return new(true, null, "Committed runtime update still requires session reconciliation.", recovering.State, recovering, true);
            }
            return await CompleteAsync(update, PackageOperationResults.Success("Exact bindings were already durably committed.") with { StoreCommitted = true }, token);
        }
        await ValidateBindingsAsync(update.BindingChanges, token);
        await RequireCurrentAuthoritiesAsync(update, token);
        var impact = await usage.CaptureAsync(update.Runtimes.Select(value => value.CurrentRuntime).Distinct().ToArray(), token, update.BindingChanges.Select(value => value.PackageId).ToArray());
        if (mode == RuntimeExecutionRuntimeApplyMode.WhenStopped && (impact.RunningPackageIds.Count != 0 || impact.ActiveLeaseCount != 0 || impact.RequiresAppRestart))
        {
            var deferred = update with { State = RuntimeExecutionRuntimeUpdateState.Deferred };
            if (update.State != deferred.State) await state.SaveUpdateAsync(deferred, token);
            return new(true, null, "Prepared update is waiting for the affected packages to stop.", deferred.State, deferred);
        }
        if (packages.HasPreparedStages) throw new RuntimeConflictException("A package transaction is pending.");
        var mutations = new List<PackageExecutionRuntimeRebindMutation>();
        foreach (var group in update.BindingChanges.GroupBy(value => value.PackageId, StringComparer.OrdinalIgnoreCase))
        {
            var package = current.Single(value => value.PackageId == group.Key);
            var bindings = package.ExecutionRuntimeBindings.ToArray();
            foreach (var change in group)
            {
                var resolution = change.Resolution;
                var identity = Identity(resolution.Artifact.Identity);
                var receipt = await store.GetReceiptAsync(identity, token) ?? throw Stale();
                if (!await installation.IsHealthyAsync(resolution.Artifact, token)) throw Stale();
                var previous = change.PreviousBinding;
                var binding = new InstalledPackageExecutionRuntimeBindingRecord(previous.Role, previous.Rid,
                    previous.Requirement, receipt.Identity.ToContract(), receipt.InstalledArtifact, clock.GetUtcNow());
                bindings[Array.FindIndex(bindings, value => value.Role == previous.Role && value.Rid == previous.Rid)] = binding;
            }
            mutations.Add(new(package.PackageId, package, bindings));
        }
        foreach (var candidate in update.Runtimes)
        {
            if (!await installation.IsHealthyAsync(candidate.Resolution.Artifact, token)) throw Stale();
        }
        var result = mutations.Count == 0 ? null : await lifecycle.ApplyRuntimeBindingsUnderGateAsync(mutations,
            mode == RuntimeExecutionRuntimeApplyMode.RestartAffectedPackages, token,
            confirmCurrent: currentToken => RequireCurrentAuthoritiesAsync(update, currentToken));
        if (result is { Success: false, StoreCommitted: false })
        {
            var rejected = update with { State = result.RuntimeSessionReconciliationPending ? RuntimeExecutionRuntimeUpdateState.ReconciliationPending : RuntimeExecutionRuntimeUpdateState.Error };
            await state.SaveUpdateAsync(rejected, CancellationToken.None);
            return new(false, ExecutionRuntimeErrorCodes.InstallFailed, result.Message ?? "Candidate failed; previous binding retained.", rejected.State, rejected);
        }
        return await CompleteAsync(update, result, CancellationToken.None);
    }

    private async Task<RuntimeExecutionRuntimeUpdateResult> CompleteAsync(RuntimeExecutionRuntimePreparedUpdate update, PackageOperationResult? result, CancellationToken token)
    {
        var completed = update with { State = result?.RuntimeSessionReconciliationPending == true
            ? RuntimeExecutionRuntimeUpdateState.ReconciliationPending : RuntimeExecutionRuntimeUpdateState.Applied };
        try
        {
            await state.ChangeAsync(value => value with
            {
                ManualInstalls = value.ManualInstalls.Select(retained => update.Runtimes.FirstOrDefault(candidate => candidate.TransferManualRetention && candidate.CurrentRuntime == retained.Runtime) is { } candidate
                    ? retained with { Runtime = candidate.Resolution.Artifact.Identity } : retained).Distinct().ToArray(),
                Updates = value.Updates.Select(existing => existing.UpdateId == completed.UpdateId ? completed : existing).ToArray(),
            }, token);
        }
        catch (Exception error) when (error is not OperationCanceledException && result?.StoreCommitted == true)
        {
            return new(false, ExecutionRuntimeInstallationService.ErrorCode(error),
                "Exact bindings were committed; runtime management-state reconciliation requires recovery. The old binding was not restored.",
                RuntimeExecutionRuntimeUpdateState.ReconciliationPending, update with { State = RuntimeExecutionRuntimeUpdateState.ReconciliationPending },
                true, result.RuntimeSessionApplied, result.CommittedStamp);
        }
        return new(true, null, completed.State == RuntimeExecutionRuntimeUpdateState.Applied
                ? update.BindingChanges.Count == 0 ? "Runtime update installed and manual retention transferred." : "Exact runtime bindings committed."
                : "Committed; session reconciliation is pending.",
            completed.State, completed, true, result?.RuntimeSessionApplied == true, result?.CommittedStamp);
    }

    private async Task ValidateBindingsAsync(IReadOnlyList<RuntimeExecutionRuntimeBindingChange> changes, CancellationToken token)
    {
        var current = await installed.ListAsync(token);
        foreach (var change in changes)
        {
            var previous = change.PreviousBinding;
            var package = current.SingleOrDefault(value => value.PackageId == change.PackageId);
            if (package is null || package.Version != change.PackageVersion || package.ContentIdentity != change.PackageContentIdentity
                || !package.ExecutionRuntimeBindings.Any(value => Matches(value, previous.Runtime, previous.Role)
                    && value.InstalledArtifact == previous.InstalledArtifact)) throw Stale();
        }
    }
    private static bool Matches(InstalledPackageExecutionRuntimeBindingRecord binding, RuntimeExecutionRuntimeIdentity identity, string role)
        => binding.Role == role && binding.Runtime == identity;
    private static RuntimeExecutionRuntimeUpdateResult Failure(Exception error)
        => new(false, ExecutionRuntimeInstallationService.ErrorCode(error), error.Message,
            error is ExecutionRuntimeCurrentAuthorityException ? RuntimeExecutionRuntimeUpdateState.Blocked
                : error is RuntimeExecutionRuntimeException { Code: ExecutionRuntimeErrorCodes.PlanStale } ? RuntimeExecutionRuntimeUpdateState.Stale : RuntimeExecutionRuntimeUpdateState.Error, null);

    public async Task<RuntimeExecutionRuntimeUninstallPlan> PreviewUninstallAsync(RuntimeExecutionRuntimeUninstallPreviewRequest request, CancellationToken token)
    {
        ValidateIdentity(request.Runtime);
        await using var operation = await operations.EnterAsync(token);
        token = operation.CancellationToken;
        var receipt = await store.GetReceiptAsync(Identity(request.Runtime), token);
        if (receipt is null) throw new RuntimeNotFoundException("Runtime is not installed.");
        if (Identity(receipt) != request.Runtime) throw Stale();
        var impact = await usage.CaptureAsync([request.Runtime], token);
        var allowed = impact.PackageReferences.Count == 0 && impact.RunningPackageIds.Count == 0 && impact.ActiveLeaseCount == 0
            && impact.PendingTransactionCount == 0 && impact.TemporaryRetentionCount == 0 && !impact.RequiresAppRestart;
        return new(true, null, allowed ? "Uninstall this unreferenced runtime." : "Rebind or uninstall referencing packages and wait for running uses to stop.",
            plans.Add(new ExecutionRuntimeUninstallConsent(request.Runtime, impact)), request.Runtime, impact, allowed);
    }
    public async Task<RuntimeExecutionRuntimeUninstallResult> UninstallAsync(RuntimeExecutionRuntimePlanConfirmation confirmation, CancellationToken token)
    {
        var consent = plans.Get<ExecutionRuntimeUninstallConsent>(confirmation);
        await using var operation = await operations.EnterAsync(token);
        token = operation.CancellationToken;
        _ = plans.Get<ExecutionRuntimeUninstallConsent>(confirmation);
        var impact = await usage.CaptureAsync([consent.Runtime], token);
        if (impact.Fingerprint != consent.Usage.Fingerprint) throw Stale();
        if (impact.PackageReferences.Count != 0 || impact.RunningPackageIds.Count != 0 || impact.ActiveLeaseCount != 0
            || impact.PendingTransactionCount != 0 || impact.TemporaryRetentionCount != 0 || impact.RequiresAppRestart)
            throw new RuntimeConflictException("Runtime is still referenced or in use; uninstall was not applied.");
        var authority = await packages.CaptureExecutionRuntimeGarbageCollectionAuthorityAsync(token);
        var management = await state.ReadAsync(token);
        var retained = ExecutionRuntimeManagementState.Retentions(management with
        { ManualInstalls = management.ManualInstalls.Where(value => value.Runtime != consent.Runtime).ToArray() });
        var removed = await store.RemoveUnreferencedAsync(Identity(consent.Runtime), new(authority.PackageBindings, retentions: retained),
            async () => { await state.ChangeAsync(value => value with { ManualInstalls = value.ManualInstalls.Where(item => item.Runtime != consent.Runtime).ToArray() }, CancellationToken.None); }, token);
        events?.PublishExecutionRuntimesChanged();
        return new(true, null, removed ? "Runtime uninstalled." : "Runtime removal is pending safe cleanup.", consent.Runtime, true, !removed);
    }

    internal async Task CheckAutomaticallyAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0) return;
        events?.PublishExecutionRuntimesChanged();
        using var automaticCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        void PreferenceChanged(bool enabled) { if (!enabled) automaticCancellation.Cancel(); }
        state.AutomaticUpdatesChanged += PreferenceChanged;
        token = automaticCancellation.Token;
        string? error = null;
        try
        {
            var management = await state.ReadAsync(token);
            if (!management.Settings.AutomaticUpdatesEnabled) return;
            var pendingAtStart = management.Updates.Where(ExecutionRuntimeManagementState.Pending)
                .SelectMany(update => update.Runtimes.Select(candidate => candidate.CurrentRuntime)).ToHashSet();
            error = await RefreshPendingUpdatesAsync(management, token);
            await ApplyPreparedWhenStoppedAsync(token);
            management = await state.ReadAsync(token);
            var identities = (await installed.ListAsync(token)).SelectMany(package => installed.ToDescriptor(package).ExecutionRuntimeBindings)
                .Select(binding => binding.Runtime)
                .Concat(management.ManualInstalls.Select(value => value.Runtime)).Distinct().ToArray();
            foreach (var identity in identities)
            {
                // A stale/blocked prepared intent is not replaced by a different download
                // in the same check. A later check may create a fresh independent plan.
                if (pendingAtStart.Contains(identity)) continue;
                if (management.Updates.Where(ExecutionRuntimeManagementState.Pending).Any(update => update.Runtimes.Any(candidate => candidate.CurrentRuntime == identity))) continue;
                try
                {
                    var preview = await PreviewAsync(new(Runtime: identity), token);
                    var rejected = management.Updates.Where(value => value.State == RuntimeExecutionRuntimeUpdateState.Error);
                    if (rejected.Any(value => SameTransition(value, preview))) continue;
                    var prepared = await PrepareCoreAsync(new(preview.Plan!.PlanId, preview.Plan.Fingerprint), true, token);
                    error ??= prepared.ErrorCode;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { error ??= ExecutionRuntimeInstallationService.ErrorCode(exception); }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { error = ExecutionRuntimeInstallationService.ErrorCode(exception); }
        finally
        {
            state.AutomaticUpdatesChanged -= PreferenceChanged;
            try
            {
                await state.ChangeAsync(value => value with { LastCheckedAtUtc = clock.GetUtcNow(), LastErrorCode = error,
                    NextCheckAtUtc = value.Settings.AutomaticUpdatesEnabled
                        ? clock.GetUtcNow().Add(error is null ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(15)) : null }, CancellationToken.None);
            }
            finally
            {
                Interlocked.Exchange(ref _checking, 0);
                // Completion is observable even if the durable values did not change
                // or persistence failed. A reader must not remain in "Checking".
                events?.PublishExecutionRuntimesChanged();
            }
        }
    }

    private static bool SameTransition(RuntimeExecutionRuntimePreparedUpdate rejected, RuntimeExecutionRuntimeUpdatePlan preview)
        => rejected.Runtimes.Select(value => (value.CurrentRuntime, Next: value.Resolution.Artifact.Identity))
                .SequenceEqual(preview.Runtimes.Select(value => (value.CurrentRuntime, Next: value.Resolution.Artifact.Identity)))
            && rejected.BindingChanges.Select(value => (value.PackageId, value.PackageVersion, value.PackageContentIdentity))
                .SequenceEqual(preview.BindingChanges.Select(value => (value.PackageId, value.PackageVersion, value.PackageContentIdentity)));
}
