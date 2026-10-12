using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

public sealed record RegistryPackageInstallExecutionResult(bool Success, string Message, bool RuntimeSessionApplied,
    bool RequiresAppRestart, IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors,
    IReadOnlyList<string> ImpactedPackageIds, IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> PlanItems)
{
    public bool AppShellApplied { get; init; }
    public RuntimePackageStamp? CommittedStamp { get; init; }
    public IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> Conflicts { get; init; } = [];
    public IReadOnlyList<PackageCapabilityConsent> RequiredCapabilityConsents { get; init; } = [];
    public static RegistryPackageInstallExecutionResult Empty(string message) => new(true, message, true, false, [], [], [], []);
    public static RegistryPackageInstallExecutionResult Failed(string message, IReadOnlyList<string>? errors = null,
        IReadOnlyList<string>? warnings = null, IReadOnlyList<RuntimeRegistryPackageInstallPlanItem>? planItems = null)
        => new(false, message, false, false, warnings ?? [], errors ?? [message], [], planItems ?? []);
}

public sealed record RegistryPackageInstallProgress(string StatusText, double? ProgressPercent);

/// <summary>Preview and combined consent happen before either SDK queue job is submitted.</summary>
public sealed class RegistryPackageInstallService : IDisposable
{
    private readonly Func<RegistryPackageChangePreflight, CancellationToken, Task<bool>>? _confirmation;
    private readonly BackgroundProcessQueueService _queue;
    private readonly bool _ownsQueue;
    private readonly Func<Exception, Task>? _notifyFailure;

    public RegistryPackageInstallService(
        Func<RegistryPackageChangePreflight, CancellationToken, Task<bool>>? confirmationPrompt = null,
        BackgroundProcessQueueService? backgroundProcesses = null,
        Func<Exception, Task>? notifyFailure = null)
    {
        _confirmation = confirmationPrompt;
        _ownsQueue = backgroundProcesses is null;
        _queue = backgroundProcesses ?? new BackgroundProcessQueueService();
        _notifyFailure = notifyFailure;
    }

    public void Dispose() { if (_ownsQueue) _queue.Dispose(); }

    internal Task<PreparedRegistryPackageInstallation?> PreparePackageAsync(string packageId, string? version, string? tag,
        bool allowDowngrade, bool reinstall, IRegistryClient registry, IRuntimeRegistryPackageClient client, CancellationToken token)
    {
        var batch = new RuntimeRegistryPackageBatchRequest(registry.RegistryUrl.AbsoluteUri,
            [new(packageId, version, [], version is null ? tag ?? "latest" : null)], AllowDowngrade: allowDowngrade, Reinstall: reinstall);
        return PrepareAsync(client, cancellation => PreviewBatchAsync(client, batch, cancellation),
            (preview, ready, cancellation) => client.InstallRegistryPackageAsync(new(registry.RegistryUrl.AbsoluteUri, packageId, version,
                version is null ? tag ?? "latest" : null, AllowDowngrade: allowDowngrade, Reinstall: reinstall,
                CapabilityConsents: preview.Plan.RequiredCapabilityConsents, ExpectedResolutionFingerprint: preview.Plan.ResolutionFingerprint)
            { RuntimePrerequisiteExpectation = ready }, cancellation), token);
    }

    internal Task<PreparedRegistryPackageInstallation?> PrepareBatchAsync(IReadOnlyList<SunderStackPackageRequirement> packages,
        IRegistryClient registry, IRuntimeRegistryPackageClient client, CancellationToken token)
    {
        var request = Batch(packages, registry.RegistryUrl);
        return PrepareAsync(client, cancellation => PreviewBatchAsync(client, request, cancellation),
            (preview, ready, cancellation) => client.ApplyRegistryPackagePlanAsync(request with
            {
                CapabilityConsents = preview.Plan.RequiredCapabilityConsents,
                ExpectedResolutionFingerprint = preview.Plan.ResolutionFingerprint,
                RuntimePrerequisiteExpectation = ready,
            }, cancellation), token);
    }

    internal Task<PreparedRegistryPackageInstallation?> PrepareUpdateAsync(string? packageId, IRuntimeRegistryPackageClient client, CancellationToken token)
    {
        // Keep one backend update transaction, including multi-origin/package batches.
        var request = new RuntimeRegistryUpdateRequest(PackageId: packageId, DryRun: true);
        return PrepareAsync(client, async cancellation =>
        {
            var result = await client.UpdateRegistryPackagesAsync(request, cancellation).ConfigureAwait(false);
            RequireSuccessfulPreview(result);
            var plan = FromPreview(result);
            var runtimes = result.RuntimePrerequisites
                ?? throw new InvalidOperationException("The update preview did not include runtime readiness. Refresh the installation preview.");
            ValidateExpectations(result.PlanExpectations, plan.Items.Count != 0);
            return new(plan, runtimes, result.PlanExpectations);
        }, (preview, ready, cancellation) => client.UpdateRegistryPackagesAsync(request with
        {
            DryRun = false,
            CapabilityConsents = preview.Plan.RequiredCapabilityConsents,
            ExpectedPlans = preview.ExpectedPlans,
            RuntimePrerequisiteExpectation = ready,
        }, cancellation), token);
    }

    internal Task<PreparedRegistryPackageInstallation?> PrepareAdoptionAsync(string packageId, Uri origin,
        IRuntimeRegistryPackageClient client, CancellationToken token)
    {
        var request = new RuntimeRegistrySourceAdoptionRequest(packageId, origin.AbsoluteUri, Tag: "latest", DryRun: true);
        return PrepareAsync(client, async cancellation =>
        {
            var result = await client.AdoptRegistryPackageSourceAsync(request, cancellation).ConfigureAwait(false);
            RequireSuccessfulPreview(result);
            ValidateExpectations(result.PlanExpectations, required: true);
            if (result.PlanExpectations.Count != 1 || result.PlanExpectations[0].RegistryOrigin != origin.AbsoluteUri
                || result.PlanExpectations[0].IncludePrerelease)
                throw new InvalidOperationException("The adoption preview changed source. Review it again.");
            var runtimes = result.RuntimePrerequisites ?? await client.PreviewPackageRuntimePrerequisitesAsync(new() { Adoption = request }, cancellation).ConfigureAwait(false);
            return new(FromPreview(result) with { ResolutionFingerprint = result.PlanExpectations[0].ExpectedResolutionFingerprint }, runtimes, result.PlanExpectations);
        }, (preview, ready, cancellation) => client.AdoptRegistryPackageSourceAsync(request with
        {
            DryRun = false, Confirm = true,
            CapabilityConsents = preview.Plan.RequiredCapabilityConsents,
            ExpectedResolutionFingerprint = preview.ExpectedPlans[0].ExpectedResolutionFingerprint,
            RuntimePrerequisiteExpectation = ready,
        }, cancellation), token);
    }

    private async Task<PreparedRegistryPackageInstallation?> PrepareAsync(IRuntimeRegistryPackageClient client,
        Func<CancellationToken, Task<RegistryInstallationPreview>> preview,
        Func<RegistryInstallationPreview, RuntimePackageRuntimePrerequisiteExpectation, CancellationToken, Task<RuntimeRegistryPackageChangeResult>> apply,
        CancellationToken token)
    {
        var selected = await preview(token).ConfigureAwait(false);
        ValidateRuntimePreview(selected.Runtimes);
        var review = await RegistryPackageChangePreflight.CreateAsync(selected.Plan, client, token, selected.ExpectedPlans,
            selected.Runtimes).ConfigureAwait(false);
        if (_confirmation is null) throw new InvalidOperationException("Package confirmation is unavailable. Open Packages to review the installation.");
        if (!await _confirmation(review, token).WaitAsync(token).ConfigureAwait(false)) return null;
        token.ThrowIfCancellationRequested();
        return new(client, selected, preview, apply);
    }

    private static async Task<RegistryInstallationPreview> PreviewBatchAsync(IRuntimeRegistryPackageClient client,
        RuntimeRegistryPackageBatchRequest request, CancellationToken token)
    {
        var plan = await client.ResolveRegistryPackagePlanAsync(request, token).ConfigureAwait(false);
        if (!plan.Success || plan.Conflicts.Count != 0 || plan.CompatibilityConflicts.Count != 0 || plan.Errors.Count != 0)
            throw new InvalidOperationException(plan.Errors.FirstOrDefault() ?? plan.Conflicts.FirstOrDefault()?.Message ?? "The package plan is not available.");
        if (!RuntimeManagementUi.IsFingerprint(plan.ResolutionFingerprint))
            throw new InvalidOperationException("The package preview has no valid resolution fingerprint. Refresh and review again.");
        var runtimes = plan.RuntimePrerequisites ?? await client.PreviewPackageRuntimePrerequisitesAsync(new(Registry: request), token).ConfigureAwait(false);
        return new(plan, runtimes, [new(request.RegistryOrigin, request.IncludePrerelease, plan.ResolutionFingerprint)]);
    }

    internal PackageInstallationCohort EnqueuePrepared(PreparedRegistryPackageInstallation prepared, string title,
        PackageOperationMetadata metadata, CancellationToken token, Func<ValueTask> release,
        Func<BackgroundProcessContext, RegistryPackageInstallExecutionResult, Task>? applied = null)
        => PackageInstallationCohort.Enqueue(_queue, title, (metadata with { PackageIds = prepared.PackageIds }).ToMetadata(),
            prepared.NeedsRuntimeInstallation ? prepared.InstallRuntimesAsync : null,
            async context =>
            {
                var result = ToAppResult(await prepared.ApplyAsync(context).ConfigureAwait(false));
                if (applied is not null) await applied(context, result).ConfigureAwait(false);
                else context.ReportProgress(100, result.Message);
            }, token, release, _notifyFailure);

    private async Task<RegistryPackageInstallExecutionResult> ExecuteAsync(Task<PreparedRegistryPackageInstallation?> preparation,
        string title, PackageOperationKind kind, string? packageId, Action<RegistryPackageInstallProgress>? progress, CancellationToken token)
    {
        IProgress<RegistryPackageInstallProgress>? reports = progress is null ? null : new Progress<RegistryPackageInstallProgress>(progress);
        try
        {
            var prepared = await preparation.ConfigureAwait(false);
            if (prepared is null) return RegistryPackageInstallExecutionResult.Failed("Installation cancelled before queueing.");
            RegistryPackageInstallExecutionResult? result = null;
            reports?.Report(new("Queueing the approved runtime and package installation…", null));
            var installation = EnqueuePrepared(prepared, title, new(packageId, kind, title), token, () => ValueTask.CompletedTask,
                (_, applied) => { result = applied; return Task.CompletedTask; });
            // Stack/other UI callers await outside the queue, so maxParallelism=1 cannot deadlock.
            var completed = await installation.Completion.ConfigureAwait(false);
            return completed.Success ? result! : RegistryPackageInstallExecutionResult.Failed(completed.Failure?.Message ?? "Installation cancelled.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { return RegistryPackageInstallExecutionResult.Failed(error.Message); }
    }

    public Task<RegistryPackageInstallExecutionResult> InstallPackageAsync(string packageId, string? version, string? tag,
        bool allowDowngrade, bool reinstall, IRegistryClient registryClient, IRuntimeRegistryPackageClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(PreparePackageAsync(packageId, version, tag, allowDowngrade, reinstall, registryClient, runtimeApiClient, cancellationToken),
            $"Install {packageId}", PackageOperationKind.InstallMarketplace, packageId, progress, cancellationToken);

    public Task<RegistryPackageInstallExecutionResult> InstallPackagesAsync(IReadOnlyList<SunderStackPackageRequirement> packages,
        IRegistryClient registryClient, IRuntimeRegistryPackageClient runtimeApiClient, Action<RegistryPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default, string? expectedResolutionFingerprint = null)
        => ExecuteAsync(PrepareBatchAsync(packages, registryClient, runtimeApiClient, cancellationToken),
            "Install Stack packages", PackageOperationKind.InstallMarketplace, null, progress, cancellationToken);

    public Task<RegistryPackageInstallExecutionResult> UpdateAllAsync(IRuntimeRegistryPackageClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(PrepareUpdateAsync(null, runtimeApiClient, cancellationToken), "Update packages", PackageOperationKind.UpdateAll, null, progress, cancellationToken);

    public Task<RegistryPackageInstallExecutionResult> UpdatePackageAsync(string packageId, IRuntimeRegistryPackageClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(PrepareUpdateAsync(packageId, runtimeApiClient, cancellationToken), $"Update {packageId}", PackageOperationKind.UpdateMarketplace, packageId, progress, cancellationToken);

    public Task<RegistryPackageInstallExecutionResult> AdoptPackageSourceAsync(string packageId, Uri registryUrl, IRuntimeRegistryPackageClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(PrepareAdoptionAsync(packageId, registryUrl, runtimeApiClient, cancellationToken), $"Adopt source for {packageId}",
            PackageOperationKind.AdoptRegistrySource, packageId, progress, cancellationToken);

    public async Task<RuntimeRegistryResolveInstallPlanResponse> ResolveInstallPlanForPackagesAsync(IReadOnlyList<SunderStackPackageRequirement> packages,
        IRegistryClient registryClient, IRuntimeRegistryPackageClient runtimeApiClient, Action<RegistryPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Invoke(new("Resolving package changes…", null));
        return await runtimeApiClient.ResolveRegistryPackagePlanAsync(Batch(packages, registryClient.RegistryUrl), cancellationToken).ConfigureAwait(false);
    }

    private static RuntimeRegistryPackageBatchRequest Batch(IReadOnlyList<SunderStackPackageRequirement> packages, Uri origin)
        => new(origin.AbsoluteUri, packages.Where(package => !string.IsNullOrWhiteSpace(package.PackageId)).Select(package =>
            new RuntimeRegistryPackageChangeRequest(package.PackageId!, null, [], string.IsNullOrWhiteSpace(package.InstallTag) ? "latest" : package.InstallTag,
                string.IsNullOrWhiteSpace(package.MinimumVersion) ? ">=0.0.0-0" : $">={package.MinimumVersion}", package.Required != false)).ToArray());

    private static void RequireSuccessfulPreview(RuntimeRegistryPackageChangeResult result)
    {
        if ((!result.Success && result.ErrorCode != RuntimeRegistryErrorCode.RuntimePrerequisitesRequired) || result.Conflicts.Count != 0)
            throw new InvalidOperationException(result.Errors.FirstOrDefault() ?? result.Message);
    }

    private static void ValidateExpectations(IReadOnlyList<RuntimeRegistryPackagePlanExpectation> expectations, bool required)
    {
        if (required && expectations.Count == 0 || expectations.Any(item => !RuntimeManagementUi.IsFingerprint(item.ExpectedResolutionFingerprint)))
            throw new InvalidOperationException("The package preview has no valid resolution fingerprint. Refresh and review again.");
    }

    internal static void ValidateRuntimePreview(RuntimePackageRuntimePrerequisitePlan plan)
    {
        if (!plan.Success || !RuntimeManagementUi.IsFingerprint(plan.SourceFingerprint) || string.IsNullOrWhiteSpace(plan.Rid)
            || plan.Requirements.Any(item => !item.Satisfied && item.Installation is not { Success: true, Plan: not null, Resolution: not null, RetainUntilUninstall: true }))
            throw new InvalidOperationException(plan.Message);
    }

    private static RuntimeRegistryResolveInstallPlanResponse FromPreview(RuntimeRegistryPackageChangeResult preview)
        => new(true, preview.PlanItems, preview.Warnings, [], preview.Conflicts, [], preview.RequiredCapabilityConsents)
        { RuntimePrerequisites = preview.RuntimePrerequisites };

    internal static RegistryPackageInstallExecutionResult ToAppResult(RuntimeRegistryPackageChangeResult result)
        => new(result.Success, result.Message, result.RuntimeSessionApplied, result.RequiresAppRestart, result.Warnings, result.Errors,
            result.ImpactedPackageIds, result.PlanItems)
        { CommittedStamp = result.CommittedStamp, Conflicts = result.Conflicts, RequiredCapabilityConsents = result.RequiredCapabilityConsents };
}
