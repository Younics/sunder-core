using System.Diagnostics;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

/// <summary>App composition bridge. SDK services themselves are created per package activation.</summary>
public sealed class AppPackageDeveloperServices(RuntimeClientTransport transport, IRuntimeApiClientFactory clients,
    BackgroundTaskQueueService queue, IUiDispatcher dispatcher) : IAppPackageDevelopmentBackend
{
    private WindowLauncher? _launcher;
    private PackageViewHostService? _packageHost;
    internal Func<string, RuntimePackageRuntimePrerequisitePlan, CancellationToken, Task<bool>>? ConfirmRuntimes { get; init; }
    internal void Attach(WindowLauncher launcher, PackageViewHostService packageHost)
    {
        _launcher = launcher;
        _packageHost = packageHost;
    }
    internal void Detach(WindowLauncher launcher)
    {
        if (ReferenceEquals(_launcher, launcher)) _launcher = null;
    }

    void IAppPackageDevelopmentBackend.QueueRetirementReconciliation(string stageId)
    {
        if (_launcher is { } launcher)
            _ = launcher.QueueRetirementReconciliation(stageId, token => ReconcileRetirementStageAsync(stageId, launcher, token));
    }

    void IAppPackageDevelopmentBackend.QueueAvailabilityReconciliation(Guid activationId)
    {
        if (_launcher is { } launcher)
            _ = launcher.QueueRetirementReconciliation($"availability:{activationId:N}",
                token => ReconcileRetirementStageAsync(null, launcher, token));
    }

    private async Task ReconcileRetirementStageAsync(string? stageId, WindowLauncher launcher, CancellationToken token)
    {
        RuntimePackageStageStatus? terminal = null;
        long lastDiagnostic = 0;
        while (ReferenceEquals(_launcher, launcher) && launcher.CanReconcileDevelopment)
        {
            token.ThrowIfCancellationRequested();
            var delay = TimeSpan.FromMilliseconds(250);
            try
            {
                if (terminal is null && stageId is not null)
                {
                    using var client = clients.CreateClient<IRuntimePackageStoreClient>();
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
                    request.CancelAfter(TimeSpan.FromSeconds(5));
                    // This capability uses the generic session/store stage endpoint.
                    var status = await client.GetPackageStoreStageStatusAsync(stageId, request.Token).ConfigureAwait(false);
                    if (!string.Equals(status.StageId, stageId, StringComparison.Ordinal))
                        throw new InvalidDataException("The Runtime returned a different package stage identity.");
                    switch (status.State)
                    {
                        case RuntimePackageStageState.Committed:
                        case RuntimePackageStageState.Failed:
                        case RuntimePackageStageState.Discarded:
                            terminal = status;
                            break;
                        case RuntimePackageStageState.Pending:
                        case RuntimePackageStageState.Committing:
                            // Host TTL/discard owns the admission fence. Expiry alone
                            // is never evidence that a retiring view can be readmitted.
                            if (status.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                                throw new InvalidOperationException("The package stage has expired but the Host has not confirmed its terminal state.");
                            break;
                        default:
                            throw new InvalidDataException("The Runtime returned an unknown package stage state.");
                    }
                }
                if (terminal is not null || stageId is null)
                {
                    RuntimePackageUiStartupResult consistent;
                    using (var client = clients.CreateClient<IRuntimeShellClient>())
                    using (var request = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        request.CancelAfter(TimeSpan.FromSeconds(5));
                        var snapshot = await client.GetRuntimePackageSnapshotAsync(request.Token).ConfigureAwait(false);
                        consistent = await ShellStartupCoordinator.LoadConsistentPackageUiStateAsync(client, snapshot,
                            cancellation => client.GetActivePackageUiSnapshotsAsync(AppPackageTargetEnvironment.CurrentRid, cancellation),
                            requiredRuntimeInstanceId: snapshot.RuntimeInstanceId, cancellationToken: request.Token).ConfigureAwait(false);
                        if (terminal?.CommittedStamp is { } committed && (consistent.Snapshot.RuntimeInstanceId != committed.RuntimeInstanceId
                            || consistent.Snapshot.SessionGeneration < committed.SessionGeneration))
                            throw new InvalidDataException("The Runtime returned a snapshot older than the terminal package stage.");
                    }
                    token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_launcher, launcher) || !launcher.CanReconcileDevelopment) return;
                    // Failure/discard can leave the Runtime generation unchanged,
                    // although this App has already retired its formerly working UI.
                    await launcher.ReconcileDevelopmentReleaseAsync(consistent.Snapshot, consistent.PackageSources, token).ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (AppPackageUiFaultPolicy.IsRecoverable(error))
            {
                if (lastDiagnostic == 0 || Stopwatch.GetElapsedTime(lastDiagnostic) >= TimeSpan.FromSeconds(30))
                {
                    AppSessionLog.WriteError(stageId is null
                        ? "Could not reconcile an unavailable App activation after acknowledged retirement. Retrying an authoritative snapshot while the shell is open."
                        : $"Could not reconcile App retirement for package stage '{stageId}'. Waiting for its exact terminal status and an authoritative snapshot; retrying while the shell is open.", error);
                    lastDiagnostic = Stopwatch.GetTimestamp();
                }
                delay = TimeSpan.FromSeconds(1);
            }
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
    }

    IDevPackageOwnerSession IAppPackageDevelopmentBackend.CreateOwner(string ownerPrefix)
        => new Sunder.Runtime.Client.DevPackageOwnerSession(transport, ownerPrefix,
            (operation, error) => AppSessionLog.WriteError($"Development owner {operation} failed.", error));

    async Task IAppPackageDevelopmentBackend.ReleaseOwnerAsync(IDevPackageOwnerSession owner, CancellationToken token)
    {
        var packageHost = _packageHost ?? throw new InvalidOperationException("Development-owner retirement requires the owning App shell.");
        var snapshot = await owner.ReleaseStagedAsync(async (stage, cancellation) =>
        {
            if (!stage.Success) throw new InvalidOperationException(string.Join(" | ", stage.Errors));
            await packageHost.RetireActivationsAsync(stage.Retirements, cancellation).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        if (!packageHost.IsStopping && _launcher is { } launcher)
        {
            using var client = clients.CreateClient<IRuntimeShellClient>();
            // A prior attempt may already have committed before reconciliation failed.
            // Keep the client retryable and reconcile the authoritative state again.
            snapshot ??= await client.GetRuntimePackageSnapshotAsync(token).ConfigureAwait(false);
            var consistent = await ShellStartupCoordinator.LoadConsistentPackageUiStateAsync(client, snapshot,
                cancellation => client.GetActivePackageUiSnapshotsAsync(AppPackageTargetEnvironment.CurrentRid, cancellation),
                requiredRuntimeInstanceId: snapshot.RuntimeInstanceId, cancellationToken: token).ConfigureAwait(false);
            if (consistent.Snapshot.SessionGeneration < snapshot.SessionGeneration)
                throw new InvalidDataException("The Runtime returned a snapshot older than the acknowledged owner release.");
            await launcher.ReconcileDevelopmentReleaseAsync(consistent.Snapshot, consistent.PackageSources, token).ConfigureAwait(false);
        }
        // The acknowledged operation has cleared ownership. Disposal now only drains
        // its client resources; an earlier failure keeps the original owner retryable.
        await owner.DisposeAsync().ConfigureAwait(false);
    }

    async Task<string> IAppPackageDevelopmentBackend.PrepareAsync(string folder, CancellationToken token)
    {
        using var client = clients.CreateClient<IRuntimePackageRuntimePrerequisiteClient>();
        var source = new RuntimePackageRuntimePrerequisiteRequest { DevelopmentFolder = folder };
        var preview = await client.PreviewPackageRuntimePrerequisitesAsync(source, token).ConfigureAwait(false);
        RegistryPackageInstallService.ValidateRuntimePreview(preview);
        var packageId = preview.DevelopmentPackageId
            ?? throw new InvalidDataException("The Host did not identify the canonical development package. Update the Host and rebuild the folder.");
        var approval = new PackageInstallationApproval(preview);
        async Task<RuntimePackageRuntimePrerequisitePlan> Refresh(CancellationToken cancellation)
        {
            var current = await client.PreviewPackageRuntimePrerequisitesAsync(source with { ExpectedSourceFingerprint = preview.SourceFingerprint }, cancellation).ConfigureAwait(false);
            approval.Validate(current);
            if (current.DevelopmentPackageId != packageId) throw PackageInstallationApproval.Changed();
            return current;
        }
        if (approval.NeedsRuntimeInstallation)
        {
            var confirmed = await dispatcher.InvokeAsync<Task<bool>>(async () =>
            {
                token.ThrowIfCancellationRequested();
                if (ConfirmRuntimes is { } confirm) return await confirm(packageId, preview, token);
                return _launcher is { } launcher && await launcher.ConfirmDevelopmentRuntimesAsync(packageId, preview, token);
            }, token).Unwrap().ConfigureAwait(false);
            if (!confirmed) throw new OperationCanceledException("Runtime installation was declined.", token);
            var cohort = PackageInstallationCohort.Enqueue(queue, $"Prepare development package {packageId}",
                new Dictionary<string, string> { ["sunder.packageId"] = packageId },
                context => approval.InstallRuntimesAsync(client, Refresh, context),
                async context => approval.Validate(await Refresh(context.CancellationToken).ConfigureAwait(false), requireReady: true),
                token, () => ValueTask.CompletedTask, indicator: BackgroundTaskIndicator.Main);
            // Completion includes cancellation of queued jobs, so the client is retained
            // until all delegates have stopped even when the owning activation is revoked.
            var completion = await cohort.Completion.ConfigureAwait(false);
            if (completion.Failure is { } error) throw error;
            if (completion.Cancelled) throw new OperationCanceledException("Development preparation was cancelled.", token);
        }
        approval.Validate(await Refresh(token).ConfigureAwait(false), requireReady: true);
        token.ThrowIfCancellationRequested();
        return packageId;
    }

    async Task<RuntimePackageSnapshot> IAppPackageDevelopmentBackend.GetSnapshotAsync(CancellationToken token)
    {
        using var client = clients.CreateClient<IRuntimeSnapshotClient>();
        return await client.GetRuntimePackageSnapshotAsync(token).ConfigureAwait(false);
    }

    async Task<bool> IAppPackageDevelopmentBackend.IsInstalledAsync(string packageId, CancellationToken token)
    {
        using var client = clients.CreateClient<IRuntimePackageStoreClient>();
        return (await client.GetInstalledPackagesAsync(token).ConfigureAwait(false)).Any(item => item.PackageId == packageId);
    }

    async Task<string> IAppPackageDevelopmentBackend.GetAppReadinessAsync(string packageId, RuntimePackageStamp stamp, CancellationToken token)
        => (await GetAppObservationAsync(packageId, stamp, null, token).ConfigureAwait(false)).Readiness.ToString();

    Task<AppDevelopmentApplicationObservation> IAppPackageDevelopmentBackend.GetAppObservationAsync(string packageId,
        RuntimePackageStamp stamp, string? sourceContentIdentity, CancellationToken token)
        => GetAppObservationAsync(packageId, stamp, sourceContentIdentity, token);

    private async Task<AppDevelopmentApplicationObservation> GetAppObservationAsync(string packageId, RuntimePackageStamp stamp,
        string? sourceContentIdentity, CancellationToken token)
    {
        using var client = clients.CreateClient<IRuntimePackageUiClient>();
        var sources = await client.GetActivePackageUiSnapshotsAsync(AppPackageTargetEnvironment.CurrentRid, token).ConfigureAwait(false);
        var source = sources.SingleOrDefault(item => item.PackageId == packageId);
        if (source?.SessionGeneration != stamp.SessionGeneration || source.ActivationFingerprint is null
            || sourceContentIdentity is not null && source.SourceContentIdentity != sourceContentIdentity) return new(PackageDevelopmentAppReadiness.Pending, null);
        return await dispatcher.InvokeAsync(() =>
        {
            var readiness = _launcher?.GetPackageAppReadiness(packageId, stamp, source.ActivationFingerprint, sourceContentIdentity) ?? "Unavailable";
            var observed = AppPackageDevelopmentService.ParseAppReadiness(readiness);
            return new AppDevelopmentApplicationObservation(observed,
                observed == PackageDevelopmentAppReadiness.Activated && sourceContentIdentity is not null ? sourceContentIdentity : null);
        }, token).ConfigureAwait(false);
    }

    Task IAppPackageDevelopmentBackend.OpenLogsAsync(string packageId, AppPackageGenerationPublication publication, CancellationToken token)
        => dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            publication.RequirePublished("package-log-navigation");
            var launcher = _launcher ?? throw new InvalidOperationException("The Sunder shell is not available.");
            launcher.ShowPackageDeveloperLogs(packageId);
        }, token);
}
