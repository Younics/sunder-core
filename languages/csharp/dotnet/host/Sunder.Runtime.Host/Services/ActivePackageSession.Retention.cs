using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class ActivePackageSession
{
    private readonly HashSet<IRuntimePackageWorker> _borrowedWorkers = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IRuntimePackageWorker> _preservedWorkers = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IRuntimePackageWorker> _transferredWorkers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IRuntimePackageWorker, Task> _workerMonitors = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<RuntimeSessionDirectory> _snapshotDirectories = [];
    private ActivePackageSession? _retentionOwner;

    private bool IsContinuingWorker(IRuntimePackageWorker worker) => _borrowedWorkers.Contains(worker) || _preservedWorkers.Contains(worker);
    private Task[] LifecycleOperationsToDrain() => _lifecycleOperations
        .Except(_workerMonitors.Where(pair => IsContinuingWorker(pair.Key) || _transferredWorkers.Contains(pair.Key)).Select(pair => pair.Value)).ToArray();

    private void InitializeSnapshotDirectories()
    {
        if (SessionFolder is null) return;
        var directory = new RuntimeSessionDirectory(SessionFolder);
        _snapshotDirectories.Add(directory);
        foreach (var source in _packageSourceMap.Values)
        {
            source.SnapshotDirectory ??= directory;
            if (_snapshotDirectories.Add(source.SnapshotDirectory)) source.SnapshotDirectory.Retain();
            if (source.WorkerSnapshotDirectory is { } workerDirectory && _snapshotDirectories.Add(workerDirectory)) workerDirectory.Retain();
        }
    }

    internal async Task RetainUnchangedWorkersAsync(ActivePackageSession previous, IReadOnlyCollection<string> forceRestart, CancellationToken token,
        bool developmentWorkerContinuation = false)
    {
        var sources = GetActivePackageSources();
        var beforeSources = previous.GetActivePackageSources();
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, candidate) in _loadedPackageMap)
        {
            if (forceRestart.Contains(id, StringComparer.Ordinal) || !previous._loadedPackageMap.TryGetValue(id, out var active)
                || !previous.IsPackageEnabled(id) || active.ActivationSessionGeneration is null
                || active.Worker.GenerationCompletion.IsCompleted || previous._sessionPackageMap[id].Readiness != PackageReadinessState.Ready
                || candidate.Source.Manifest is null || candidate.Source.ContentIndex is null
                || !RuntimeRpcPackageMetadata.IsSha256(candidate.Source.ContentIdentity)
                || Fingerprint(id, sources, this, developmentWorkerContinuation) != Fingerprint(id, beforeSources, previous, developmentWorkerContinuation)) continue;
            retained.Add(id);
        }
        // Even an explicit same-byte dependency restart changes its activation ownership.
        // Propagate that replacement through the effective dependency/provider closure.
        bool changed;
        do
        {
            changed = retained.RemoveWhere(id => AppActivationFingerprint.DependencyClosure(id, sources)
                .Any(dependency => dependency != id && previous._loadedPackageMap.ContainsKey(dependency)
                    && (!retained.Contains(dependency) || !_loadedPackageMap.ContainsKey(dependency)))) != 0;
        } while (changed);

        _retentionOwner = previous;
        foreach (var id in retained)
        {
            token.ThrowIfCancellationRequested();
            var unused = _loadedPackageMap[id];
            var active = previous._loadedPackageMap[id];
            if (active.Worker.GenerationCompletion.IsCompleted) throw new RuntimeUnavailableException("A retained worker faulted during candidate preparation.");
            // No lifecycle messages have been sent to this candidate worker yet.
            await unused.Worker.DisposeAsync().ConfigureAwait(false);
            // Publish the freshly validated source for App snapshots and recovery
            // authority, while the continuing process/recovery factory still owns
            // its original on-disk Worker bytes. Never replace the new source map
            // with the previous canonical shadow.
            unused.Source.WorkerSnapshotDirectory = active.Source.WorkerSnapshotDirectory ?? active.Source.SnapshotDirectory;
            _loadedPackageMap[id] = active with { Source = unused.Source };
            _sessionPackageMap[id] = previous._sessionPackageMap[id];
            _borrowedWorkers.Add(active.Worker);
            previous._preservedWorkers.Add(active.Worker);
            if (unused.Source.WorkerSnapshotDirectory is { } directory && _snapshotDirectories.Add(directory)) directory.Retain();
            if (previous._workerMonitors.TryGetValue(active.Worker, out var monitor))
            {
                _workerMonitors[active.Worker] = monitor;
                TrackLifecycleOperation(monitor);
            }
        }
    }

    internal void CommitWorkerRetention()
    {
        if (_retentionOwner is not { } previous) return;
        foreach (var worker in _borrowedWorkers) previous._transferredWorkers.Add(worker);
        _borrowedWorkers.Clear();
        _retentionOwner = null;
    }

    internal void CancelRetentionReservation()
    {
        if (_retentionOwner is { } previous)
            foreach (var worker in _borrowedWorkers) previous._preservedWorkers.Remove(worker);
    }

    internal void CompleteRetentionRollback() => _preservedWorkers.Clear();

    private static string Fingerprint(string id, IReadOnlyList<RuntimePackageSource> sources, ActivePackageSession session, bool developmentWorkerContinuation)
    {
        var source = AppActivationFingerprint.Create(Guid.Empty, id, sources, session.GetSessionPackages(), developmentWorkerContinuation);
        var runtime = AppActivationFingerprint.DependencyClosure(id, sources).Order(StringComparer.Ordinal).Select(packageId =>
        {
            var package = session._loadedPackageMap.GetValueOrDefault(packageId);
            var binding = package?.ExecutionRuntimeBinding;
            return new { PackageId = packageId, Binding = binding is null ? null : new
            {
                binding.Role, binding.Rid, binding.Requirement, binding.Runtime,
                binding.InstalledArtifact.ArchiveSha256, binding.InstalledArtifact.ArchiveSize, binding.InstalledArtifact.ContentIdentity,
                binding.InstalledArtifact.Provenance.Distribution,
            }, Grants = package?.ApprovedSensitiveCapabilities.Order(StringComparer.Ordinal).ToArray() };
        }).ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Source = source, Runtime = runtime })));
    }
}

internal sealed class RuntimeSessionDirectory(string path)
{
    private int _references = 1;
    internal void Retain() => Interlocked.Increment(ref _references);
    internal void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
