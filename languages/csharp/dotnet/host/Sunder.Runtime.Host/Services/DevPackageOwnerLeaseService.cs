using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class DevPackageOwnerLeaseService
{
    private readonly PackageSessionLifecycleService _sessions;
    private readonly RuntimeSessionOwner _runtimeSession;
    private readonly DevPackageWatchService _watcher;
    private readonly RuntimeProtocolDescriptor _protocol;
    private readonly RuntimeLifecyclePolicyOptions _policy;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DevPackageOwnerLeaseService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, OwnerLease> _owners = new(StringComparer.Ordinal);

    public DevPackageOwnerLeaseService(
        PackageSessionLifecycleService sessions,
        RuntimeSessionOwner runtimeSession,
        DevPackageWatchService watcher,
        RuntimeProtocolDescriptor protocol,
        RuntimeLifecyclePolicyOptions policy,
        TimeProvider timeProvider,
        ILogger<DevPackageOwnerLeaseService> logger)
    {
        _sessions = sessions;
        _runtimeSession = runtimeSession;
        _watcher = watcher;
        _protocol = protocol;
        _policy = policy;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<DevPackageOwnerLeaseResponse> ReplaceAsync(
        string ownerId,
        DevPackageOwnerMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateOwnerId(ownerId);
        ValidateToken(request.OwnerToken);
        ValidateMutationId(request.MutationId);
        ValidateRuntimeInstance(request.RuntimeInstanceId);
        await RequireReadyRuntimeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_owners.TryGetValue(ownerId, out var existing))
            {
                Authenticate(existing, request.OwnerToken);
                if (existing.ExpiresAtUtc <= now)
                {
                    await ReleaseCoreAsync(ownerId, cancellationToken).ConfigureAwait(false);
                    throw new RuntimeAuthenticationException("The dev package owner lease has expired.");
                }
                if (request.Revision == existing.Revision
                    && string.Equals(request.MutationId, existing.MutationId, StringComparison.Ordinal))
                {
                    if (!FoldersEqual(existing.Folders, request.Folders))
                    {
                        throw new RuntimeConflictException(
                            $"Dev package owner mutation '{request.MutationId}' was already used with different folders.");
                    }
                    var renewed = Renew(existing, now);
                    _owners[ownerId] = renewed;
                    await _watcher.SynchronizeAsync(_sessions.GetDevWatchTargets(), cancellationToken).ConfigureAwait(false);
                    return ToResponse(renewed);
                }

                if (request.Revision != existing.Revision + 1)
                {
                    throw new RuntimeConflictException(
                        $"Dev package owner '{ownerId}' expected revision {existing.Revision + 1}, but received {request.Revision}.");
                }
            }
            else if (request.Revision != 1)
            {
                throw new RuntimeConflictException(
                    $"New dev package owner '{ownerId}' must begin at revision 1.");
            }

            ValidateProcessIdentity(request.ProcessId, request.ProcessStartedAtUtc);
            var packages = await _sessions.ReplaceAppDevPackageOwnerAsync(
                ownerId,
                request.Folders,
                cancellationToken).ConfigureAwait(false);
            var committedAt = _timeProvider.GetUtcNow();
            var lease = new OwnerLease(
                ownerId,
                request.OwnerToken,
                request.MutationId,
                request.Revision,
                committedAt + _policy.DevPackageOwnerLeaseLifetime,
                request.ProcessId,
                request.ProcessStartedAtUtc,
                request.Folders,
                packages);
            _owners[ownerId] = lease;
            await _watcher.SynchronizeAsync(_sessions.GetDevWatchTargets(), cancellationToken).ConfigureAwait(false);
            return ToResponse(lease);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DevPackageOwnerLeaseResponse> HeartbeatAsync(
        string ownerId,
        DevPackageOwnerHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateOwnerId(ownerId);
        ValidateToken(request.OwnerToken);
        ValidateRuntimeInstance(request.RuntimeInstanceId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lease = GetAuthenticated(ownerId, request.OwnerToken);
            if (lease.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                await ReleaseCoreAsync(ownerId, cancellationToken).ConfigureAwait(false);
                throw new RuntimeAuthenticationException("The dev package owner lease has expired.");
            }

            lease = Renew(lease, _timeProvider.GetUtcNow());
            _owners[ownerId] = lease;
            return ToResponse(lease);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseAsync(
        string ownerId,
        DevPackageOwnerReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateOwnerId(ownerId);
        ValidateToken(request.OwnerToken);
        ValidateRuntimeInstance(request.RuntimeInstanceId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_owners.TryGetValue(ownerId, out var lease))
            {
                return;
            }
            Authenticate(lease, request.OwnerToken);
            await ReleaseCoreAsync(ownerId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReapExpiredAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            foreach (var lease in _owners.Values
                         .Where(lease => lease.ExpiresAtUtc <= now || !IsClientProcessAlive(lease))
                         .ToArray())
            {
                try
                {
                    await ReleaseCoreAsync(lease.OwnerId, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Released expired dev package owner {OwnerId}", lease.OwnerId);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "Failed to release expired dev package owner {OwnerId}", lease.OwnerId);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal bool ContainsOwner(string ownerId) => _owners.ContainsKey(ownerId);

    private async Task RequireReadyRuntimeAsync(CancellationToken cancellationToken)
    {
        await _runtimeSession.WaitForBootstrapAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = _runtimeSession.GetSnapshot();
        if (snapshot.BootstrapState != RuntimeBootstrapState.Ready)
        {
            throw new RuntimeUnavailableException("The Runtime did not complete installed package bootstrap successfully.");
        }
    }

    private async Task ReleaseCoreAsync(string ownerId, CancellationToken cancellationToken)
    {
        await _sessions.ReleaseAppDevPackageOwnerAsync(ownerId, cancellationToken).ConfigureAwait(false);
        _owners.Remove(ownerId);
        await _watcher.SynchronizeAsync(_sessions.GetDevWatchTargets(), cancellationToken).ConfigureAwait(false);
    }

    private OwnerLease GetAuthenticated(string ownerId, string token)
    {
        if (!_owners.TryGetValue(ownerId, out var lease))
        {
            throw new RuntimeAuthenticationException("Dev package owner credentials are invalid.");
        }

        Authenticate(lease, token);
        return lease;
    }

    private static void Authenticate(OwnerLease lease, string token)
    {
        var expected = Encoding.UTF8.GetBytes(lease.OwnerToken);
        var supplied = Encoding.UTF8.GetBytes(token);
        if (expected.Length != supplied.Length
            || !CryptographicOperations.FixedTimeEquals(expected, supplied))
        {
            throw new RuntimeAuthenticationException("Dev package owner credentials are invalid.");
        }
    }

    private OwnerLease Renew(OwnerLease lease, DateTimeOffset now)
        => lease with { ExpiresAtUtc = now + _policy.DevPackageOwnerLeaseLifetime };

    private DevPackageOwnerLeaseResponse ToResponse(OwnerLease lease)
        => new(
            _protocol.RuntimeInstanceId,
            lease.OwnerId,
            lease.MutationId,
            lease.Revision,
            lease.ExpiresAtUtc,
            _sessions.Generation,
            lease.Packages);

    private void ValidateRuntimeInstance(Guid runtimeInstanceId)
    {
        if (runtimeInstanceId == Guid.Empty || runtimeInstanceId != _protocol.RuntimeInstanceId)
        {
            throw new RuntimeConflictException("The dev package owner request targets a stale Runtime instance.");
        }
    }

    private static void ValidateOwnerId(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId)
            || ownerId.Length > 128
            || ownerId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new RuntimeValidationException("A valid dev package owner id is required.");
        }
    }

    private static void ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 512)
        {
            throw new RuntimeAuthenticationException("Dev package owner credentials are invalid.");
        }
    }

    private static void ValidateMutationId(string mutationId)
    {
        if (string.IsNullOrWhiteSpace(mutationId) || mutationId.Length > 128)
        {
            throw new RuntimeValidationException("A valid dev package owner mutation id is required.");
        }
    }

    private static void ValidateProcessIdentity(int? processId, DateTimeOffset? processStartedAtUtc)
    {
        if (processId is <= 0 || processId.HasValue != processStartedAtUtc.HasValue)
        {
            throw new RuntimeValidationException(
                "Dev package owner process id and process start identity must either both be supplied or both be omitted.");
        }
    }

    private static bool IsClientProcessAlive(OwnerLease lease)
    {
        if (lease.ProcessId is not { } processId || lease.ProcessStartedAtUtc is not { } expectedStart)
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return !process.HasExited && (actualStart - expectedStart).Duration() < TimeSpan.FromSeconds(2);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static bool FoldersEqual(
        IReadOnlyList<DevPackageOwnerFolder> left,
        IReadOnlyList<DevPackageOwnerFolder> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        return left
            .OrderBy(static folder => folder.Folder, StringComparer.Ordinal)
            .Zip(right.OrderBy(static folder => folder.Folder, StringComparer.Ordinal))
            .All(pair => pair.First.Watch == pair.Second.Watch
                         && string.Equals(
                             Path.GetFullPath(pair.First.Folder),
                             Path.GetFullPath(pair.Second.Folder),
                             OperatingSystem.IsWindows()
                                 ? StringComparison.OrdinalIgnoreCase
                                 : StringComparison.Ordinal));
    }

    private sealed record OwnerLease(
        string OwnerId,
        string OwnerToken,
        string MutationId,
        long Revision,
        DateTimeOffset ExpiresAtUtc,
        int? ProcessId,
        DateTimeOffset? ProcessStartedAtUtc,
        IReadOnlyList<DevPackageOwnerFolder> Folders,
        IReadOnlyList<DevPackageOwnerPackage> Packages);
}

internal sealed class DevPackageOwnerLeaseReaper(
    DevPackageOwnerLeaseService owners,
    RuntimeLifecyclePolicyOptions policy,
    ILogger<DevPackageOwnerLeaseReaper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(policy.DevPackageOwnerSweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await owners.ReapExpiredAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Dev package owner lease reaper failed");
            }
        }
    }
}
