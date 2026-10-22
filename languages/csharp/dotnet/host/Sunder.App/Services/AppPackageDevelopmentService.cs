using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal interface IAppPackageDevelopmentBackend
{
    IDevPackageOwnerSession CreateOwner(string ownerPrefix);
    Task<string> PrepareAsync(string folder, CancellationToken token);
    Task<RuntimePackageSnapshot> GetSnapshotAsync(CancellationToken token);
    Task<bool> IsInstalledAsync(string packageId, CancellationToken token);
    Task<string> GetAppReadinessAsync(string packageId, RuntimePackageStamp stamp, CancellationToken token);
    async Task<AppDevelopmentApplicationObservation> GetAppObservationAsync(string packageId, RuntimePackageStamp stamp,
        string? sourceContentIdentity, CancellationToken token)
        => new(AppPackageDevelopmentService.ParseAppReadiness(await GetAppReadinessAsync(packageId, stamp, token).ConfigureAwait(false)), null);
    Task OpenLogsAsync(string packageId, AppPackageGenerationPublication publication, CancellationToken token);
    Task ReleaseOwnerAsync(IDevPackageOwnerSession owner, CancellationToken token)
        => owner.DisposeAsync().AsTask();
    void QueueRetirementReconciliation(string stageId) { }
    void QueueAvailabilityReconciliation(Guid activationId) { }
}

internal sealed record AppDevelopmentApplicationObservation(PackageDevelopmentAppReadiness Readiness, string? AppliedSourceContentIdentity);

// One instance per package activation, never a root SDK service. The shared Client owns
// lease tokens, heartbeat/reconnection and ambiguous-mutation recovery.
internal sealed class AppPackageDevelopmentService : IPackageDevelopmentService, IPackageLogNavigationService, IAsyncDisposable
{
    // A boundary mapping, never a cast between SDK and Host DTO enum identities.
    internal static PackageDevelopmentAppReadiness ParseAppReadiness(string? value) => value switch
    {
        "Pending" => PackageDevelopmentAppReadiness.Pending,
        "Activated" => PackageDevelopmentAppReadiness.Activated,
        "Quarantined" => PackageDevelopmentAppReadiness.Quarantined,
        "Unavailable" => PackageDevelopmentAppReadiness.Unavailable,
        "NotApplicable" => PackageDevelopmentAppReadiness.NotApplicable,
        _ => PackageDevelopmentAppReadiness.Unknown,
    };

    private static PackageDevelopmentRuntimeReadiness RuntimeReadiness(PackageReadinessState value) => value switch
    {
        PackageReadinessState.Ready => PackageDevelopmentRuntimeReadiness.Ready,
        PackageReadinessState.NeedsConfiguration => PackageDevelopmentRuntimeReadiness.NeedsConfiguration,
        PackageReadinessState.Degraded => PackageDevelopmentRuntimeReadiness.Degraded,
        PackageReadinessState.Failed => PackageDevelopmentRuntimeReadiness.Failed,
        PackageReadinessState.Disabled => PackageDevelopmentRuntimeReadiness.Disabled,
        _ => PackageDevelopmentRuntimeReadiness.Unknown,
    };

    private static PackageDevelopmentHostState HostState(RuntimeBootstrapState value) => value switch
    {
        RuntimeBootstrapState.Starting => PackageDevelopmentHostState.Starting,
        RuntimeBootstrapState.Ready => PackageDevelopmentHostState.Ready,
        RuntimeBootstrapState.Failed => PackageDevelopmentHostState.Failed,
        RuntimeBootstrapState.ShuttingDown => PackageDevelopmentHostState.ShuttingDown,
        _ => PackageDevelopmentHostState.Unknown,
    };

    private readonly IAppPackageDevelopmentBackend? _backend;
    private readonly AppPackageGenerationPublication _publication;
    private readonly string _ownerPrefix;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenRegistration _revocation;
    private readonly Dictionary<string, Session> _sessions = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object _disposeGate = new();
    private Task? _disposal;

    internal AppPackageDevelopmentService(IAppPackageDevelopmentBackend? backend, AppPackageGenerationPublication publication, Guid activationId)
    {
        _backend = backend;
        _publication = publication;
        _ownerPrefix = $"app-development-{activationId:N}";
        // Fence and cancel operations at unpublication, but keep accepted owner leases
        // until the package container has drained its jobs. Releasing here races the
        // owning view-model's asynchronous stop acknowledgement during DI disposal.
        _revocation = publication.RevocationToken.Register(_lifetime.Cancel);
    }

    public async Task<IPackageDevelopmentSession> LoadAsync(string developmentFolder, bool watch, CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation(cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(developmentFolder);
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(developmentFolder));
        await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            if (_sessions.TryGetValue(folder, out var existing))
            {
                if (!existing.IsDisposed)
                {
                    await existing.SetWatchAsync(watch, operation.Token).ConfigureAwait(false);
                    return existing;
                }
                // A failed release keeps its original owner. Do not replace that
                // exact lease with a new load until withdrawal is acknowledged.
                await existing.DisposeAsync().ConfigureAwait(false);
                _sessions.Remove(folder);
            }
            var packageId = await Backend.PrepareAsync(folder, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            var session = new Session(this, Backend.CreateOwner(_ownerPrefix), folder, packageId, watch);
            _sessions[folder] = session;
            try
            {
                await session.SetWatchAsync(watch, operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                return session;
            }
            catch
            {
                // Acquisition may have committed before its response was cancelled. The
                // Client resolves/releases its pending mutation with an independent budget.
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task OpenPackageLogsAsync(string packageId, CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation(cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        await Backend.OpenLogsAsync(packageId, _publication, operation.Token).ConfigureAwait(false);
    }

    private IAppPackageDevelopmentBackend Backend => _backend
        ?? throw new InvalidOperationException("Package development requires a connected Sunder App shell.");

    private void RequireAccess(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
        _publication.RequirePublished("package-development/log-navigation");
    }

    private CancellationTokenSource BeginOperation(CancellationToken token)
    {
        RequireAccess(token);
        var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token, _publication.RevocationToken);
        if (operation.IsCancellationRequested)
        {
            operation.Dispose();
            throw new OperationCanceledException("The owning App activation was retired.", token);
        }
        return operation;
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposal is null || _disposal.IsFaulted || _disposal.IsCanceled) _disposal = DisposeCoreAsync();
            return new(_disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_sessions.Values.Select(session => session.DisposeAsync().AsTask())).ConfigureAwait(false);
            _sessions.Clear();
        }
        finally
        {
            _gate.Release();
            _revocation.Unregister();
        }
    }

    private sealed class Session(AppPackageDevelopmentService service, IDevPackageOwnerSession owner,
        string folder, string packageId, bool watch) : IPackageDevelopmentSession
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly object _disposeGate = new();
        private Task? _disposal;
        private bool _watch = watch;
        public string PackageId => packageId;
        internal bool IsDisposed => _lifetime.IsCancellationRequested;

        public async Task SetWatchAsync(bool watch, CancellationToken cancellationToken = default)
        {
            using var operation = BeginOperation(cancellationToken);
            await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
            try
            {
                operation.Token.ThrowIfCancellationRequested();
                var lease = await owner.AcquireAsync([folder], watch, operation.Token).ConfigureAwait(false);
                ValidateIdentity(lease);
                _watch = watch;
                operation.Token.ThrowIfCancellationRequested();
            }
            finally { _gate.Release(); }
        }

        public async Task<PackageDevelopmentStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            using var operation = BeginOperation(cancellationToken);
            try
            {
                await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
                try
                {
                    // Reuses the owner protocol to reacquire after a Host restart. No App
                    // activation inference: readiness below is read from the actual Host snapshot.
                    var lease = await owner.AcquireAsync([folder], _watch, operation.Token).ConfigureAwait(false);
                    ValidateIdentity(lease);
                    var snapshot = await service.Backend.GetSnapshotAsync(operation.Token).ConfigureAwait(false);
                    var current = snapshot.SessionPackages.FirstOrDefault(item => string.Equals(item.PackageId, PackageId, StringComparison.Ordinal));
                    var sameGeneration = snapshot.RuntimeInstanceId == lease.RuntimeInstanceId && snapshot.SessionGeneration >= lease.SessionGeneration
                        && lease.ExpiresAtUtc > DateTimeOffset.UtcNow;
                    var observed = sameGeneration && snapshot.BootstrapState == RuntimeBootstrapState.Ready && current is not null;
                    var installed = await service.Backend.IsInstalledAsync(PackageId, operation.Token).ConfigureAwait(false);
                    var sourceIdentity = observed ? current!.SourceContentIdentity : null;
                    if (sourceIdentity is not null && (!IsSourceIdentity(sourceIdentity) || snapshot.RuntimeInstanceId == Guid.Empty
                        || snapshot.SessionGeneration < 0))
                        throw new InvalidDataException("The Runtime returned an invalid development source content identity.");
                    var hasApp = observed && (current!.HostRoles & PackageHostRoles.App) != 0;
                    var app = !observed ? new AppDevelopmentApplicationObservation(PackageDevelopmentAppReadiness.Pending, null)
                        : !hasApp ? new(PackageDevelopmentAppReadiness.NotApplicable, null)
                        : await service.Backend.GetAppObservationAsync(PackageId, new(snapshot.RuntimeInstanceId, snapshot.SessionGeneration),
                            sourceIdentity, operation.Token).ConfigureAwait(false);
                    if (app.AppliedSourceContentIdentity is { } appIdentity && !IsSourceIdentity(appIdentity))
                        throw new InvalidDataException("The App returned an invalid applied development source identity.");
                    var revision = sourceIdentity is null ? null
                        : new PackageDevelopmentRevision(snapshot.RuntimeInstanceId, snapshot.SessionGeneration, sourceIdentity);
                    var applied = revision is not null && current!.IsEnabled
                        && current.Readiness is PackageReadinessState.Ready or PackageReadinessState.Degraded
                        && (!hasApp || app.Readiness == PackageDevelopmentAppReadiness.Activated && app.AppliedSourceContentIdentity == sourceIdentity)
                            ? revision : null;
                    var ownerPackage = lease.Packages[0];
                    var effectiveWatch = ownerPackage.EffectiveWatch
                        ?? throw new InvalidDataException("Update the Host to observe effective development watch state.");
                    operation.Token.ThrowIfCancellationRequested();
                    return new(PackageId, observed, effectiveWatch,
                        observed ? RuntimeReadiness(current!.Readiness)
                            : sameGeneration && snapshot.BootstrapState == RuntimeBootstrapState.Ready
                                ? PackageDevelopmentRuntimeReadiness.NotLoaded : PackageDevelopmentRuntimeReadiness.Unknown,
                        observed ? current!.LastError : sameGeneration && snapshot.Errors.Count > 0 ? string.Join(" | ", snapshot.Errors)
                            : "The Host has not yet confirmed this development owner in its current package snapshot.",
                        observed && installed) { RequestedWatch = ownerPackage.Watch, AppReadiness = app.Readiness,
                            HostState = sameGeneration ? HostState(snapshot.BootstrapState) : PackageDevelopmentHostState.Reconnecting,
                            RuntimeRevision = revision, AppliedRevision = applied };
                }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException error) when (error.CancellationToken == operation.Token && operation.IsCancellationRequested)
            {
                // The linked token is private to this call. Public monitors may only
                // treat their own cancelled token as a successful stop; retirement is
                // a disposed service, not cancellation of a still-live public caller.
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(error.Message, error, cancellationToken);
                throw new ObjectDisposedException(nameof(IPackageDevelopmentSession), "The owning App development session was retired.");
            }
        }

        private void ValidateIdentity(DevPackageOwnerLeaseResponse lease)
        {
            if (lease.Packages.Count != 1 || lease.Packages[0].PackageId != PackageId)
                throw new InvalidDataException("The development package identity changed. Dispose this session and load the rebuilt folder again.");
        }

        private static bool IsSourceIdentity(string value)
            => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

        private CancellationTokenSource BeginOperation(CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            service.RequireAccess(token);
            return CancellationTokenSource.CreateLinkedTokenSource(token, service._lifetime.Token, _lifetime.Token, service._publication.RevocationToken);
        }

        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                if (_disposal is null || _disposal.IsFaulted || _disposal.IsCanceled) _disposal = DisposeCoreAsync();
                return new(_disposal);
            }
        }

        private async Task DisposeCoreAsync()
        {
            _lifetime.Cancel();
            await _gate.WaitAsync().ConfigureAwait(false);
            try { await service.Backend.ReleaseOwnerAsync(owner, CancellationToken.None).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
    }
}
