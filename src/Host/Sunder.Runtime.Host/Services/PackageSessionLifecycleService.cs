using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionLifecycleService
{
    private readonly RuntimeSessionOwner _sessions;
    private readonly RuntimeOperationGate _gate;
    private readonly PackageSessionReconciler _reconciler;
    private readonly RuntimePackageUiService _ui;
    private readonly InstalledPackageStore _installedPackages;
    private readonly ILogger<PackageSessionLifecycleService> _logger;
    private readonly Dictionary<string, PendingLifecycleStage> _stages = new(StringComparer.OrdinalIgnoreCase);

    public PackageSessionLifecycleService(
        RuntimeSessionOwner sessions,
        RuntimeOperationGate gate,
        PackageSessionReconciler reconciler,
        RuntimePackageUiService ui,
        InstalledPackageStore installedPackages,
        ILogger<PackageSessionLifecycleService> logger)
    {
        _sessions = sessions;
        _gate = gate;
        _reconciler = reconciler;
        _ui = ui;
        _installedPackages = installedPackages;
        _logger = logger;
    }

    public IReadOnlyList<ActivePackageDescriptor> GetActivePackages() => _sessions.State.GetActivePackages();

    public IReadOnlyList<SessionPackageDescriptor> GetSessionPackages() => _sessions.State.GetSessionPackages();

    internal IReadOnlyList<RuntimePackageSource> GetActiveSources() => _sessions.State.GetActivePackageSources();

    public long Generation => _sessions.Generation;

    public async Task<IReadOnlyList<DevPackageWatchTarget>> ConfigureDevWatchingAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sources = _sessions.Sources.Snapshot();
            var overlays = sources.ActiveDevOverlays;
            foreach (var overlay in overlays)
            {
                sources.SetDevOverlay(overlay with { Watch = enabled });
            }
            _sessions.Sources.Replace(sources);
            return overlays.Select(overlay => new DevPackageWatchTarget(overlay.PackageId, overlay.Folder)).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PackageLifecycleOperationResult> LoadStartupDevPackagesAsync(
        IReadOnlyList<string> folders,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sources = _sessions.Sources.Snapshot();
            sources.RemoveDevOverlaysOwnedBy(PackageSessionOverlayOwner.Startup, PackageSessionOverlayOwner.HotReload);
            var errors = new List<string>();
            foreach (var folder in folders)
            {
                AddDevOverlay(sources, folder, watch: false, PackageSessionOverlayOwner.Startup, errors);
            }
            if (errors.Count > 0)
            {
                return PackageLifecycleOperationResult.Failed(errors[0], errors: errors);
            }
            _sessions.Sources.Replace(sources);
            return await LoadLifecycleCoreAsync([], cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal Task<PackageSessionOperationResult> LoadDevPackageAsync(
        string folder,
        bool watch = true,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(folder);
        if (!TryReadDevPackageId(fullPath, out var packageId, out var error))
        {
            return Task.FromResult(PackageSessionOperationResult.Failed(error ?? "The Runtime dev package input is invalid."));
        }

        return CommitMergedSessionAsync(
            sources =>
            {
                sources.SetDevOverlay(new PackageSessionDevOverlay(packageId, fullPath, watch, PackageSessionOverlayOwner.Sdk));
                return true;
            },
            failureMessage: null,
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
            current?.LastError)
        {
            GenerationId = _sessions.Generation,
        };
    }

    public async Task<PackageLifecycleOperationResult> LoadPackageLifecycleAsync(
        PackageLifecycleLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var errors = new List<string>();
            var reloadFolders = ResolveReloadFolders(_sessions.Sources.Snapshot(), request.PackageIds, errors);
            return errors.Count > 0
                ? PackageLifecycleOperationResult.Failed(
                    errors[0],
                    _sessions.State.GetActivePackages(),
                    _ui.GetActiveSnapshots(),
                    errors: errors)
                : await LoadLifecycleCoreAsync(reloadFolders, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PackageLifecycleStageResult> StageAsync(
        PackageLifecycleStageRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var warnings = new List<string>();
            var errors = new List<string>();
            var currentPackages = _sessions.State.GetActivePackages();
            var currentSources = _sessions.State.GetActivePackageSources();
            var currentSnapshots = _ui.GetActiveSnapshots();
            var sources = _sessions.Sources.Snapshot();
            var reloadFolders = ResolveReloadFolders(sources, request.PackageIds, errors);
            if (errors.Count > 0)
            {
                return PackageLifecycleStageResult.Failed(errors[0], currentPackages, currentSnapshots, warnings, errors);
            }

            var loaded = await _reconciler.LoadMergedSessionAsync(sources.ActiveDevOverlays, startBackgroundServices: false, cancellationToken);
            warnings.AddRange(loaded.Warnings);
            errors.AddRange(loaded.Errors);
            if (loaded.Session is null || errors.Count > 0)
            {
                if (loaded.Session is not null) await loaded.Session.DisposeAsync();
                return PackageLifecycleStageResult.Failed(
                    errors.FirstOrDefault() ?? "Package lifecycle stage failed.",
                    currentPackages,
                    currentSnapshots,
                    warnings,
                    errors);
            }

            var stagedPackages = loaded.Session.GetActivePackages();
            var stagedSources = loaded.Session.GetActivePackageSources();
            var impacted = BuildImpactedPackageIds(currentPackages, currentSources, stagedPackages, stagedSources, reloadFolders);
            var stageId = Guid.NewGuid().ToString("N");
            _stages[stageId] = new PendingLifecycleStage(loaded.Session, sources, impacted, _sessions.Generation);
            _sessions.RegisterStage(stageId, _sessions.Generation + 1);
            return new PackageLifecycleStageResult(
                stageId,
                stagedPackages,
                _ui.CreateSnapshots(stagedSources, _sessions.Generation + 1, stageId),
                warnings,
                errors,
                impacted);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PackageLifecycleOperationResult> CommitStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_stages.Remove(stageId, out var stage))
            {
                return PackageLifecycleOperationResult.Failed(
                    $"Package lifecycle stage '{stageId}' was not found.",
                    _sessions.State.GetActivePackages(),
                    _ui.GetActiveSnapshots());
            }
            if (stage.BaseGeneration != _sessions.Generation)
            {
                await stage.Session.DisposeAsync();
                _ui.DiscardStage(stageId);
                var message = $"Package lifecycle stage '{stageId}' is stale because the active package session changed before commit.";
                return PackageLifecycleOperationResult.Failed(message, _sessions.State.GetActivePackages(), _ui.GetActiveSnapshots(), errors: [message], impactedPackageIds: stage.ImpactedPackageIds);
            }

            try
            {
                await stage.Session.StartBackgroundServicesAsync(_logger, cancellationToken);
                var packages = stage.Session.GetActivePackages();
                var sources = stage.Session.GetActivePackageSources();
                var warnings = (await _sessions.State.ClearActiveSessionAsync()).ToList();
                _sessions.Sources.Replace(stage.Sources);
                _sessions.Publish(stage.Session);
                _ui.CommitStage(stageId);
                return new PackageLifecycleOperationResult(true, "Package lifecycle stage committed.", packages, _ui.CreateSnapshots(sources, _sessions.Generation), warnings, [], stage.ImpactedPackageIds);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await stage.Session.DisposeAsync();
                _ui.DiscardStage(stageId);
                _logger.LogError(exception, "Failed to commit package lifecycle stage {StageId}", stageId);
                const string message = "Package lifecycle stage could not be committed.";
                return PackageLifecycleOperationResult.Failed(message, _sessions.State.GetActivePackages(), _ui.GetActiveSnapshots(), errors: [message], impactedPackageIds: stage.ImpactedPackageIds);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DiscardStageAsync(string stageId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_stages.Remove(stageId, out var stage)) return false;
            await stage.Session.DisposeAsync();
            _ui.DiscardStage(stageId);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ShutdownStagesAsync()
    {
        foreach (var stage in _stages.Values)
        {
            await stage.Session.DisposeAsync();
        }
        _stages.Clear();
    }

    private async Task<PackageLifecycleOperationResult> LoadLifecycleCoreAsync(
        IReadOnlyCollection<string> reloadFolders,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var currentPackages = _sessions.State.GetActivePackages();
        var currentSources = _sessions.State.GetActivePackageSources();
        var currentSnapshots = _ui.GetActiveSnapshots();
        var sources = _sessions.Sources.Snapshot();
        var loaded = await _reconciler.LoadMergedSessionAsync(sources.ActiveDevOverlays, startBackgroundServices: false, cancellationToken);
        warnings.AddRange(loaded.Warnings);
        errors.AddRange(loaded.Errors);
        if (loaded.Session is null || errors.Count > 0)
        {
            if (loaded.Session is not null) await loaded.Session.DisposeAsync();
            return PackageLifecycleOperationResult.Failed(errors.FirstOrDefault() ?? "Package lifecycle load failed.", currentPackages, currentSnapshots, warnings, errors);
        }

        var packages = loaded.Session.GetActivePackages();
        var packageSources = loaded.Session.GetActivePackageSources();
        var impacted = BuildImpactedPackageIds(currentPackages, currentSources, packages, packageSources, reloadFolders);
        try
        {
            await loaded.Session.StartBackgroundServicesAsync(_logger, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await loaded.Session.DisposeAsync();
            _logger.LogError(exception, "Failed to start package background services");
            const string message = "Package lifecycle load failed while starting background services.";
            return PackageLifecycleOperationResult.Failed(message, currentPackages, currentSnapshots, warnings, [message], impacted);
        }

        warnings.AddRange(await _sessions.State.ClearActiveSessionAsync());
        _sessions.Sources.Replace(sources);
        _sessions.Publish(loaded.Session);
        return new PackageLifecycleOperationResult(true, "Package lifecycle loaded.", packages, _ui.CreateSnapshots(packageSources, _sessions.Generation), warnings, [], impacted);
    }

    private async Task<PackageSessionOperationResult> CommitMergedSessionAsync(
        Func<PackageSessionSourceSnapshot, bool> updateSources,
        string? failureMessage,
        string successMessage,
        IReadOnlyList<string> impactedPackageIds,
        string statusPackageId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sources = _sessions.Sources.Snapshot();
            if (!updateSources(sources)) return PackageSessionOperationResult.Failed(failureMessage ?? "Package session source update failed.");
            var loaded = await _reconciler.LoadMergedSessionAsync(sources.ActiveDevOverlays, startBackgroundServices: false, cancellationToken);
            var warnings = loaded.Warnings.ToList();
            var errors = loaded.Errors.ToList();
            if (loaded.Session is null || errors.Count > 0)
            {
                if (loaded.Session is not null) await loaded.Session.DisposeAsync();
                return new PackageSessionOperationResult(false, errors.FirstOrDefault() ?? "Package session load failed.", warnings, errors, impactedPackageIds, null);
            }
            try
            {
                await loaded.Session.StartBackgroundServicesAsync(_logger, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await loaded.Session.DisposeAsync();
                _logger.LogError(exception, "Failed to start background services for merged package session");
                const string message = "Package session load failed while starting background services.";
                return new PackageSessionOperationResult(false, message, warnings, [message], impactedPackageIds, null);
            }
            warnings.AddRange(await _sessions.State.ClearActiveSessionAsync());
            _sessions.Sources.Replace(sources);
            _sessions.Publish(loaded.Session);
            return new PackageSessionOperationResult(true, successMessage, warnings, [], impactedPackageIds, await GetStatusAsync(statusPackageId, cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void AddDevOverlay(PackageSessionSourceSnapshot sources, string folder, bool watch, PackageSessionOverlayOwner owner, ICollection<string> errors)
    {
        if (!TryReadDevPackageId(folder, out var packageId, out var error))
        {
            errors.Add(error ?? $"'{folder}' is not a loadable Sunder dev package folder.");
            return;
        }
        sources.SetDevOverlay(new PackageSessionDevOverlay(packageId, Path.GetFullPath(folder), watch, owner));
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

    private static bool TryReadDevPackageId(string folder, out string packageId, out string? error)
    {
        packageId = string.Empty;
        error = null;
        if (!Directory.Exists(folder))
        {
            error = "The dev package folder does not exist.";
            return false;
        }
        var manifestPath = Path.Combine(folder, "sunder-package.json");
        if (!File.Exists(manifestPath))
        {
            error = "The dev package folder does not contain sunder-package.json.";
            return false;
        }
        try
        {
            var manifest = JsonSerializer.Deserialize<RuntimePackageManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (string.IsNullOrWhiteSpace(manifest?.Id))
            {
                error = "The dev package manifest is missing id.";
                return false;
            }
            packageId = manifest.Id.Trim();
            return true;
        }
        catch (JsonException)
        {
            error = "The dev package manifest is not valid JSON.";
            return false;
        }
    }

    private static IReadOnlyList<string> BuildImpactedPackageIds(
        IReadOnlyList<ActivePackageDescriptor> currentPackages,
        IReadOnlyList<RuntimePackageSource> currentSources,
        IReadOnlyList<ActivePackageDescriptor> stagedPackages,
        IReadOnlyList<RuntimePackageSource> stagedSources,
        IReadOnlyCollection<string> reloadFolders)
    {
        var impacted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentById = currentPackages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var stagedById = stagedPackages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in currentById.Keys.Concat(stagedById.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            currentById.TryGetValue(packageId, out var current);
            stagedById.TryGetValue(packageId, out var staged);
            if (!DescriptorsEqual(current, staged)) impacted.Add(packageId);
        }
        var currentSourceById = currentSources.GroupBy(source => source.PackageId, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var stagedSourceById = stagedSources.GroupBy(source => source.PackageId, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in currentSourceById.Keys.Concat(stagedSourceById.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            currentSourceById.TryGetValue(packageId, out var current);
            stagedSourceById.TryGetValue(packageId, out var staged);
            if (current is null || staged is null || current.Kind != staged.Kind || !string.Equals(Path.GetFullPath(current.SourceFolder), Path.GetFullPath(staged.SourceFolder), StringComparison.OrdinalIgnoreCase)) impacted.Add(packageId);
        }
        var forced = reloadFolders.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var source in stagedSources.Where(source => source.Kind == PackageSourceKind.Dev && forced.Contains(Path.GetFullPath(source.SourceFolder)))) impacted.Add(source.PackageId);
        return impacted.OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool DescriptorsEqual(ActivePackageDescriptor? current, ActivePackageDescriptor? staged)
    {
        if (current is null || staged is null) return current is null && staged is null;
        return string.Equals(current.PackageId, staged.PackageId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(current.DisplayName, staged.DisplayName, StringComparison.Ordinal)
               && string.Equals(current.Version, staged.Version, StringComparison.OrdinalIgnoreCase)
               && current.Icon == staged.Icon
               && current.IsEnabled == staged.IsEnabled
               && current.Readiness == staged.Readiness
               && current.Views.SequenceEqual(staged.Views);
    }

    private sealed record PendingLifecycleStage(
        ActivePackageSession Session,
        PackageSessionSourceSnapshot Sources,
        IReadOnlyList<string> ImpactedPackageIds,
        long BaseGeneration);
}
