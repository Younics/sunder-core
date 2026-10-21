using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class ActivePackagePolicySynchronizer(
    InstalledPackageStore installed, PackageSessionState sessions, RuntimeSessionOwner owner,
    RegistryPackagePolicySynchronizer synchronizer, PackagePolicyAdmission admission, PackageLifecyclePolicyCache cache,
    IPolicyContinuousClock clock, TimeProvider timeProvider, ILogger<ActivePackagePolicySynchronizer> logger,
    IHostApplicationLifetime? lifetime = null, InstalledPackagePolicyCoordinator? installedPolicies = null,
    AppProcessPolicySessions? appProcesses = null) : BackgroundService
{
    private readonly Dictionary<string, (string Identity, SunderPackageManifest Manifest)> _manifests = new(StringComparer.Ordinal);
    private readonly List<Task> _cleanup = [];
    private readonly object _enforcementGate = new();
    private readonly object _persistenceGate = new();
    private readonly CancellationTokenSource _persistenceStopping = new();
    private Task _persistenceTask = Task.CompletedTask;
    private bool _persistenceQuiesced;
    private int _disposed;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        cache.PolicyChanged += EnforceActive;
        return base.StartAsync(cancellationToken);
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        cache.PolicyChanged -= EnforceActive;
        lock (_persistenceGate) _persistenceQuiesced = true;
        RuntimeCancellation.DisposeAfterCallbacks(_persistenceStopping, _persistenceStopping.CancelAsync());
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (lifetime is not null)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
            await started.Task.WaitAsync(stoppingToken);
        }
        // Network waits/backoff must never delay expiry enforcement.
        Task persistence;
        lock (_persistenceGate)
        {
            // Shutdown can win before ApplicationStarted. No persistence work may
            // be admitted after the explicit drain has completed in that case.
            if (!_persistenceQuiesced)
            {
                var token = _persistenceStopping.Token;
                _persistenceTask = Task.Run(() => RunPersistenceAsync(stoppingToken, token), CancellationToken.None);
            }
            persistence = _persistenceTask;
        }
        await Task.WhenAll(RefreshLoopAsync(stoppingToken), EnforcementLoopAsync(stoppingToken), persistence);
    }

    // The Runtime retires its operation gate before stopping the web host. Drain
    // only this background writer first; keep policy refresh/expiry enforcement
    // alive until sessions retire and ordinary hosted-service shutdown runs.
    internal Task StopPersistenceAsync(CancellationToken cancellationToken)
    {
        Task pending;
        lock (_persistenceGate)
        {
            _persistenceQuiesced = true;
            pending = _persistenceTask;
        }
        return Task.WhenAll(_persistenceStopping.CancelAsync(), pending).WaitAsync(cancellationToken);
    }

    private async Task RunPersistenceAsync(CancellationToken serviceToken, CancellationToken persistenceToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(serviceToken, persistenceToken);
        try { await PersistenceLoopAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task RefreshLoopAsync(CancellationToken token)
    {
        var schedules = new Dictionary<string, ActivePolicyRefreshSchedule>(StringComparer.Ordinal);
        var inFlight = new Dictionary<string, Task>(StringComparer.Ordinal);
        using var concurrency = new SemaphoreSlim(4);
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var subjects = await CaptureSubjectsAsync(token);
                    var now = clock.Read();
                    foreach (var group in subjects.GroupBy(value => value.Origin, StringComparer.Ordinal))
                    {
                        if (!schedules.TryGetValue(group.Key, out var schedule)) schedules[group.Key] = schedule = new();
                        var due = schedule.IsDue(now);
                        if (!due || inFlight.TryGetValue(group.Key, out var pending) && !pending.IsCompleted) continue;
                        inFlight[group.Key] = RefreshOriginAsync(group.ToArray(), schedule, concurrency, token);
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
                { logger.LogWarning("Active policy subject/clock capture failed ({FailureType}).", error.GetType().Name); }
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, token);
            }
        }
        finally { await Task.WhenAll(inFlight.Values); }
    }

    private async Task RefreshOriginAsync(PackagePolicySubjects[] subjects, ActivePolicyRefreshSchedule schedule, SemaphoreSlim concurrency, CancellationToken token)
    {
        await concurrency.WaitAsync(token);
        try
        {
            var started = clock.Read();
            var success = false;
            try { await synchronizer.RefreshAsync(subjects, token); success = true; }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            { logger.LogWarning("Active package policy refresh failed ({FailureType}); existing leases retain their original deadlines.", error.GetType().Name); }
            finally { schedule.Completed(started, clock.Read(), success); }
            EnforceActive();
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        { logger.LogWarning("Active policy refresh could not prove clock continuity ({FailureType}).", error.GetType().Name); }
        finally { concurrency.Release(); }
    }

    internal async Task RefreshInstalledAsync(CancellationToken token)
    {
        await synchronizer.RefreshAsync(await CaptureSubjectsAsync(token), token);
        EnforceActive();
    }

    internal async Task<IReadOnlyList<PackagePolicySubjects>> CaptureSubjectsAsync(CancellationToken token)
    {
        // App assemblies remain loaded after a catalog removal or UI generation change.
        var subjects = new List<PackagePolicySubjects>(appProcesses?.CapturePolicySubjects() ?? []);
        var packages = await installed.ListAsync(token);
        foreach (var package in packages)
        {
            if (!_manifests.TryGetValue(package.PackageId, out var metadata) || metadata.Identity != package.ContentIdentity)
            {
                PackageStorePathGuard.EnsureSafe(package.ManifestPath, package.InstallPath);
                await using var stream = new FileStream(package.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                RegistryActivePolicyClient.Require(stream.Length is > 0 and <= 16 * 1024 * 1024, "Installed manifest exceeds its policy metadata bound.");
                var bytes = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(bytes, token);
                RegistryActivePolicyClient.Require(stream.ReadByte() == -1, "Installed manifest changed during policy subject capture.");
                var manifest = StrictJsonSerializer.Deserialize<SunderPackageManifest>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = false })
                    ?? throw new InvalidDataException("Installed policy manifest is empty.");
                RegistryActivePolicyClient.Require(manifest.Id == package.PackageId && manifest.Version == package.Version, "Installed policy manifest identity differs.");
                _manifests[package.PackageId] = metadata = (package.ContentIdentity, manifest);
            }
            var source = PackagePolicyAdmission.WithProvenance(new(package.PackageId, PackageSourceKind.Installed, package.InstallPath,
                ContentIdentity: package.ContentIdentity, Manifest: metadata.Manifest), package);
            AddSubjects(subjects, source);
        }
        foreach (var stale in _manifests.Keys.Except(packages.Select(value => value.PackageId), StringComparer.Ordinal).ToArray()) _manifests.Remove(stale);
        using (var lease = TryAcquireSession())
        {
            if (lease is not null)
                foreach (var package in sessions.ListEnabledLoadedPackages(lease)) AddSubjects(subjects, package.Source);
        }
        return subjects;
    }

    private void AddSubjects(List<PackagePolicySubjects> subjects, RuntimePackageSource source)
    {
        try { subjects.AddRange(admission.Subjects(source)); }
        catch (PackagePolicyDeniedException) when (source.RegistryPolicyOrigin is null)
        {
            // An unbound local archive cannot invent a Registry package-policy origin.
            // The independent enforcement loop keeps missing package authority denied.
        }
    }

    private async Task EnforcementLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                EnforceActive();
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            { logger.LogError("Policy invalidation persistence failed ({FailureType}); execution remains denied.", error.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, token);
        }
    }

    private async Task PersistenceLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await cache.PersistClockInvalidationsAsync(token);
                if (installedPolicies is not null) await installedPolicies.ReconcileAsync(token);
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            { logger.LogError("Installed policy persistence failed ({FailureType}); retained policy evidence remains authoritative.", error.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, token);
        }
    }

    internal void EnforceActive()
    {
        lock (_enforcementGate) EnforceActiveCore();
    }

    private void EnforceActiveCore()
    {
        ActiveLoadedPackage[] packages;
        using (var lease = TryAcquireSession())
            packages = lease is null ? [] : sessions.ListEnabledLoadedPackages(lease).ToArray();
        foreach (var package in packages)
        {
            try { admission.Require(package.Source); }
            catch (Exception error) when (error is PackagePolicyDeniedException or InvalidDataException)
            {
                var code = error is PackagePolicyDeniedException denied ? denied.Code : Sunder.Registry.Contracts.PackagePolicyErrorCodes.PolicyRequired;
                if (owner.FaultPackageExecution(package.Descriptor.PackageId, code, out var cleanup))
                    _cleanup.Add(ObserveCleanupAsync(cleanup, package.Descriptor.PackageId));
            }
        }
        _cleanup.RemoveAll(task => task.IsCompleted);
    }

    private async Task ObserveCleanupAsync(Task cleanup, string packageId)
    {
        try { await cleanup; }
        catch (Exception error) { logger.LogError("Policy-blocked Worker cleanup failed for {PackageId} ({FailureType}).", packageId, error.GetType().Name); }
    }

    private PackageSessionLease? TryAcquireSession()
    {
        try { return sessions.AcquireLease(); }
        catch (RuntimeUnavailableException) { return null; }
    }
}

internal sealed class ActivePolicyRefreshSchedule
{
    private readonly object _gate = new();
    private PolicyClockReading? _last;
    private long _due;
    private int _retrySeconds = 5;

    internal bool IsDue(PolicyClockReading now)
    {
        lock (_gate)
        {
            RegistryActivePolicyClient.ValidateClock(now);
            var interrupted = _last is null || !now.SameEpoch(_last) || now.Ticks < _last.Ticks
                || (Int128)now.Ticks - _last.Ticks > (Int128)now.Frequency * 2;
            _last = now;
            return interrupted || now.Ticks >= _due;
        }
    }

    internal void Completed(PolicyClockReading started, PolicyClockReading completed, bool success, int? jitterSeconds = null)
    {
        lock (_gate)
        {
            var jitter = jitterSeconds ?? RandomNumberGenerator.GetInt32(RuntimePolicyTiming.MaximumRefreshJitterSeconds + 1);
            RegistryActivePolicyClient.Require(jitter is >= 0 and <= RuntimePolicyTiming.MaximumRefreshJitterSeconds, "Invalid refresh jitter.");
            var seconds = success ? RuntimePolicyTiming.RefreshSeconds + jitter : _retrySeconds;
            _retrySeconds = success ? 5 : Math.Min(_retrySeconds * 2, RuntimePolicyTiming.MaximumRetrySeconds);
            _due = checked((success ? started.Ticks : completed.Ticks) + completed.Frequency * seconds);
            // Time spent doing HTTP is not a suspend notification on the next iteration.
            _last = completed;
        }
    }
}
