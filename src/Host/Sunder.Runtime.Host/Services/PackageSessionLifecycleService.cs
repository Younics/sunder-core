using Microsoft.Extensions.Logging;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class PackageSessionLifecycleService
{
    private const int MaxPendingStages = 32;
    private readonly RuntimeSessionOwner _sessions;
    private readonly RuntimeOperationGate _gate;
    private readonly PackageSessionReconciler _reconciler;
    private readonly InstalledPackageStore _installedPackages;
    private readonly PackageSessionPublisher _publisher;
    private readonly PackageLifecycleStageStore _stages;
    private readonly ILogger<PackageSessionLifecycleService> _logger;
    private readonly RuntimeLifecyclePolicyOptions _lifecyclePolicy;
    private readonly TimeProvider _timeProvider;

    public PackageSessionLifecycleService(
        RuntimeSessionOwner sessions,
        RuntimeOperationGate gate,
        PackageSessionReconciler reconciler,
        InstalledPackageStore installedPackages,
        PackageSessionPublisher publisher,
        PackageLifecycleStageStore stages,
        ILogger<PackageSessionLifecycleService> logger,
        RuntimeLifecyclePolicyOptions? lifecyclePolicy = null,
        TimeProvider? timeProvider = null)
    {
        _sessions = sessions;
        _gate = gate;
        _reconciler = reconciler;
        _installedPackages = installedPackages;
        _publisher = publisher;
        _stages = stages;
        _logger = logger;
        _lifecyclePolicy = lifecyclePolicy ?? new RuntimeLifecyclePolicyOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<ActivePackageDescriptor> GetActivePackages() => _sessions.GetSnapshot().ActivePackages;

    public IReadOnlyList<SessionPackageDescriptor> GetSessionPackages() => _sessions.GetSnapshot().SessionPackages;

    public RuntimePackageSnapshot GetSnapshot() => _sessions.GetSnapshot();

    internal IReadOnlyList<RuntimePackageSource> GetActiveSources() => _sessions.State.GetActivePackageSources();

    public long Generation => _sessions.Generation;

    public async Task<PackageLifecycleOperationResult> LoadStartupDevPackagesAsync(
        IReadOnlyList<string> folders,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        var operationToken = operation.CancellationToken;
        var sources = _sessions.Sources.Snapshot();
        sources.RemoveDevOverlaysOwnedBy(PackageSessionOverlayOwner.Startup, PackageSessionOverlayOwner.HotReload);
        var errors = new List<string>();
        foreach (var folder in folders)
        {
            operationToken.ThrowIfCancellationRequested();
            await AddDevOverlayAsync(
                sources,
                folder,
                watch: false,
                PackageSessionOverlayOwner.Startup,
                errors,
                operationToken);
        }
        if (errors.Count > 0)
        {
            return PackageLifecycleOperationResult.Failed(errors[0], errors: errors);
        }
        return await LoadLifecycleCoreAsync([], operationToken, sources);
    }

    internal async Task<PackageSessionOperationResult> LoadDevPackageAsync(
        string folder,
        bool watch = true,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(folder);
        var identity = await ReadDevPackageIdentityAsync(fullPath, cancellationToken);
        if (identity.PackageId is null)
        {
            return PackageSessionOperationResult.Failed(
                identity.Error ?? "The Runtime dev package input is invalid.");
        }
        var packageId = identity.PackageId;

        return await CommitMergedSessionAsync(
            sources =>
            {
                if (sources.DevOverlays.Any(existing =>
                        existing.Owner == PackageSessionOverlayOwner.AppInvocation
                        && string.Equals(existing.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
                        && !PathsEqual(existing.Folder, fullPath)))
                {
                    return false;
                }
                sources.SetDevOverlay(new PackageSessionDevOverlay(packageId, fullPath, watch, PackageSessionOverlayOwner.Sdk));
                return true;
            },
            $"Dev package '{packageId}' is already owned from a different folder.",
            $"Loaded Runtime dev package input '{packageId}'.",
            [packageId],
            packageId,
            cancellationToken);
    }

    public async Task<PackageSessionOperationResult> UnloadDevPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default)
        => await CommitMergedSessionAsync(
            sources => sources.RemoveDevOverlay(packageId, PackageSessionOverlayOwner.Sdk),
            $"Dev package overlay '{packageId}' is not loaded.",
            $"Unloaded dev package '{packageId}'.",
            [packageId],
            packageId,
            cancellationToken);

    public async Task<PackageSessionStatus?> GetStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }
        var overlay = _sessions.Sources.TryGetActiveDevOverlay(packageId);
        var installed = await _installedPackages.GetAsync(packageId, cancellationToken);
        var current = _sessions.State.GetSessionPackage(packageId);
        if (overlay is null && installed is null && current is null)
        {
            return null;
        }

        return new PackageSessionStatus(
            packageId,
            current?.DisplayName ?? installed?.Name,
            current?.Version ?? installed?.Version,
            overlay is null ? PackageSourceKind.Installed : PackageSourceKind.Dev,
            current?.IsEnabled == true,
            overlay?.Watch ?? false,
            overlay is not null && installed is not null,
            current?.Readiness,
            current?.LastError);
    }

    public async Task<PackageLifecycleStageResult> StageAsync(
        PackageLifecycleStageRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        var operationToken = operation.CancellationToken;
        await SweepStagesCoreAsync(_timeProvider.GetUtcNow());
        if (_stages.Count >= MaxPendingStages)
        {
            return PackageLifecycleStageResult.Failed(
                $"The Runtime already has {MaxPendingStages} pending package lifecycle stages.",
                _sessions.State.GetActivePackages());
        }
        var warnings = new List<string>();
        var errors = new List<string>();
        var baseGeneration = _sessions.Generation;
        var currentPackages = _sessions.State.GetActivePackages();
        var currentSources = _sessions.State.GetActivePackageSources();
        var sources = _sessions.Sources.Snapshot();
        var reloadFolders = ResolveReloadFolders(sources, request.PackageIds, errors);
        if (errors.Count > 0)
        {
            return PackageLifecycleStageResult.Failed(errors[0], currentPackages, warnings, errors);
        }

        var loaded = await _reconciler.LoadMergedSessionAsync(sources.ActiveDevOverlays, operationToken);
        warnings.AddRange(loaded.Warnings);
        errors.AddRange(loaded.Errors);
        if (loaded.Session is null || errors.Count > 0)
        {
            if (loaded.Session is not null) await loaded.Session.DisposeAsync();
            return PackageLifecycleStageResult.Failed(
                errors.FirstOrDefault() ?? "Package lifecycle stage failed.",
                currentPackages,
                warnings,
                errors);
        }
        if (_sessions.Generation != baseGeneration)
        {
            await loaded.Session.DisposeAsync();
            const string message = "Package lifecycle stage is stale because the active package session changed while it was being prepared.";
            return PackageLifecycleStageResult.Failed(message, currentPackages, warnings, [message]);
        }

        var stagedPackages = loaded.Session.GetActivePackages();
        var stagedSources = loaded.Session.GetActivePackageSources();
        var impacted = PackageSessionImpactAnalyzer.Compare(currentPackages, currentSources, stagedPackages, stagedSources, reloadFolders);
        var stageId = Guid.NewGuid().ToString("N");
        PreparedPackageSession candidate;
        try
        {
            candidate = _publisher.Prepare(
                loaded.Session,
                sources,
                warnings,
                errors,
                baseGeneration,
                stageId);
        }
        catch (Exception exception)
        {
            await loaded.Session.DisposeAsync();
            _logger.LogError(exception, "Failed to prepare package lifecycle stage {StageId}", stageId);
            const string message = "Package lifecycle stage preparation failed.";
            return PackageLifecycleStageResult.Failed(message, currentPackages, warnings, [message]);
        }
        var createdAtUtc = _timeProvider.GetUtcNow();
        var expiresAtUtc = createdAtUtc + _lifecyclePolicy.PendingStageLifetime;
        _stages.Add(stageId, new PendingPackageLifecycleStage(
            candidate,
            impacted,
            baseGeneration,
            createdAtUtc,
            expiresAtUtc));
        _sessions.RegisterStage(
            stageId,
            baseGeneration + 1,
            baseGeneration,
            RuntimePackageStageKind.PackageSession,
            createdAtUtc,
            expiresAtUtc);
        return new PackageLifecycleStageResult(
            stageId,
            stagedPackages,
            warnings,
            errors,
            impacted);
    }

    public async Task<PackageLifecycleOperationResult> CommitStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        var operationToken = operation.CancellationToken;
        await SweepStagesCoreAsync(_timeProvider.GetUtcNow());
        if (!_stages.TryTake(stageId, out var stage))
        {
            var statusMessage = _sessions.GetStageStatus(stageId)?.Message;
            return PackageLifecycleOperationResult.Failed(
                statusMessage ?? $"Package lifecycle stage '{stageId}' was not found.",
                _sessions.State.GetActivePackages(),
                errors: statusMessage is null ? null : [statusMessage]);
        }
        if (stage.BaseGeneration != _sessions.Generation)
        {
            await _publisher.DiscardAsync(stage.Candidate);
            var message = $"Package lifecycle stage '{stageId}' is stale because the active package session changed before commit.";
            _sessions.MarkStageFailed(stageId, message);
            return PackageLifecycleOperationResult.Failed(message, _sessions.State.GetActivePackages(), errors: [message], impactedPackageIds: stage.ImpactedPackageIds);
        }

        _sessions.MarkStageCommitting(stageId);
        try
        {
            var publication = await _publisher.BeginPublishAsync(stage.Candidate, operationToken);
            var committedPublication = await _publisher.CommitAsync(publication, operationToken);
            var warnings = stage.Candidate.Warnings.Concat(committedPublication.CleanupWarnings).ToArray();
            var committed = _sessions.GetSnapshot();
            var result = new PackageLifecycleOperationResult(
                true,
                "Package lifecycle stage committed.",
                committed.ActivePackages,
                warnings,
                stage.Candidate.Errors,
                stage.ImpactedPackageIds)
            {
                CommittedStamp = committedPublication.Stamp,
            };
            _sessions.MarkStageCommitted(
                stageId,
                committedPublication.Stamp,
                runtimeSessionApplied: true,
                committedPublication.ReconciliationPending,
                result.Message);
            return result;
        }
        catch (OperationCanceledException)
        {
            _sessions.MarkStageFailed(stageId, "The package lifecycle stage was cancelled before commit.");
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Failed to commit package lifecycle stage {StageId}", stageId);
            const string message = "Package lifecycle stage could not be committed.";
            _sessions.MarkStageFailed(stageId, message);
            return PackageLifecycleOperationResult.Failed(message, _sessions.State.GetActivePackages(), errors: [message], impactedPackageIds: stage.ImpactedPackageIds);
        }
    }

    public async Task<bool> DiscardStageAsync(string stageId, CancellationToken cancellationToken = default)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        operation.CancellationToken.ThrowIfCancellationRequested();
        await SweepStagesCoreAsync(_timeProvider.GetUtcNow());
        if (!_stages.TryTake(stageId, out var stage)) return false;
        await _publisher.DiscardAsync(stage.Candidate);
        _sessions.MarkStageDiscarded(stageId);
        return true;
    }

    internal ActivePackageSession? GetStagedSession(string stageId)
        => _stages.GetCandidate(stageId)?.Session;

    private async Task<PackageLifecycleOperationResult> LoadLifecycleCoreAsync(
        IReadOnlyCollection<string> reloadFolders,
        CancellationToken cancellationToken,
        PackageSessionSourceSnapshot? candidateSources = null,
        bool allowPackageErrors = false)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var baseGeneration = _sessions.Generation;
        var currentPackages = _sessions.State.GetActivePackages();
        var currentSources = _sessions.State.GetActivePackageSources();
        var sources = candidateSources ?? _sessions.Sources.Snapshot();
        var loaded = await _reconciler.LoadMergedSessionAsync(sources.ActiveDevOverlays, cancellationToken);
        warnings.AddRange(loaded.Warnings);
        errors.AddRange(loaded.Errors);
        if (loaded.Session is null || errors.Count > 0 && !allowPackageErrors)
        {
            if (loaded.Session is not null) await loaded.Session.DisposeAsync();
            return PackageLifecycleOperationResult.Failed(errors.FirstOrDefault() ?? "Package lifecycle load failed.", currentPackages, warnings, errors);
        }
        if (errors.Count > 0)
        {
            warnings.AddRange(errors.Select(error =>
                $"Installed package session restored with a package error: {error}"));
        }

        var packages = loaded.Session.GetActivePackages();
        var packageSources = loaded.Session.GetActivePackageSources();
        var impacted = PackageSessionImpactAnalyzer.Compare(currentPackages, currentSources, packages, packageSources, reloadFolders);
        PackageSessionPublicationResult publication;
        try
        {
            publication = await _publisher.PublishAsync(
                loaded.Session,
                sources,
                warnings,
                errors,
                baseGeneration,
                cancellationToken);
            warnings.AddRange(publication.CleanupWarnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Failed to start package background services");
            const string message = "Package lifecycle load failed while starting background services.";
            return PackageLifecycleOperationResult.Failed(message, currentPackages, warnings, [message], impacted);
        }
        var committed = _sessions.GetSnapshot();
        return new PackageLifecycleOperationResult(
            true,
            "Package lifecycle loaded.",
            committed.ActivePackages,
            warnings,
            [],
            impacted)
        {
            CommittedStamp = publication.Stamp,
        };
    }

    private async Task<PackageSessionOperationResult> CommitMergedSessionAsync(
        Func<PackageSessionSourceSnapshot, bool> updateSources,
        string? failureMessage,
        string successMessage,
        IReadOnlyList<string> impactedPackageIds,
        string statusPackageId,
        CancellationToken cancellationToken)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        var operationToken = operation.CancellationToken;
        var baseGeneration = _sessions.Generation;
        var sources = _sessions.Sources.Snapshot();
        if (!updateSources(sources)) return PackageSessionOperationResult.Failed(failureMessage ?? "Package session source update failed.");
        var loaded = await _reconciler.LoadMergedSessionAsync(sources.ActiveDevOverlays, operationToken);
        var warnings = loaded.Warnings.ToList();
        var errors = loaded.Errors.ToList();
        if (loaded.Session is null || errors.Count > 0)
        {
            if (loaded.Session is not null) await loaded.Session.DisposeAsync();
            return new PackageSessionOperationResult(false, errors.FirstOrDefault() ?? "Package session load failed.", warnings, errors, impactedPackageIds, null);
        }
        PackageSessionPublicationResult publication;
        try
        {
            publication = await _publisher.PublishAsync(
                loaded.Session,
                sources,
                warnings,
                errors,
                baseGeneration,
                operationToken);
            warnings.AddRange(publication.CleanupWarnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Failed to start background services for merged package session");
            const string message = "Package session load failed while starting background services.";
            return new PackageSessionOperationResult(false, message, warnings, [message], impactedPackageIds, null);
        }
        return new PackageSessionOperationResult(true, successMessage, warnings, [], impactedPackageIds, await GetStatusAsync(statusPackageId, CancellationToken.None))
        {
            CommittedStamp = publication.Stamp,
        };
    }

    private static async Task AddDevOverlayAsync(
        PackageSessionSourceSnapshot sources,
        string folder,
        bool watch,
        PackageSessionOverlayOwner owner,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(folder);
        var identity = await ReadDevPackageIdentityAsync(fullPath, cancellationToken);
        if (identity.PackageId is null)
        {
            errors.Add(identity.Error ?? $"'{folder}' is not a loadable Sunder dev package folder.");
            return;
        }
        sources.SetDevOverlay(new PackageSessionDevOverlay(identity.PackageId, fullPath, watch, owner));
    }

    private static IReadOnlyList<string> ResolveReloadFolders(PackageSessionSourceSnapshot sources, IReadOnlyList<string>? packageIds, ICollection<string> errors)
    {
        var overlays = sources.ActiveDevOverlays;
        if (packageIds is null || packageIds.Count == 0) return overlays.Select(overlay => overlay.Folder).ToArray();
        var byId = overlays.ToDictionary(overlay => overlay.PackageId, StringComparer.OrdinalIgnoreCase);
        var folders = new List<string>();
        foreach (var packageId in packageIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!byId.TryGetValue(packageId, out var overlay)) errors.Add($"Package '{packageId}' is not an active dev package and cannot be hot reloaded.");
            else folders.Add(overlay.Folder);
        }
        return folders;
    }

    private static async Task<(string? PackageId, string? Error)> ReadDevPackageIdentityAsync(
        string folder,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder))
        {
            return (null, "The dev package folder does not exist.");
        }
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(
            folder,
            cancellationToken);
        if (!validation.Success || validation.Manifest?.Id is null)
        {
            return (
                null,
                validation.Errors.FirstOrDefault() is { } error
                    ? $"The dev package folder is not a strict canonical exploded package: {error}"
                    : "The dev package folder is not a strict canonical exploded package.");
        }
        return (validation.Manifest.Id, null);
    }

}
