using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Sunder.Package.Projections;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed record ExecutionRuntimeLeaseOwner(
    string PackageId,
    string GenerationId,
    Guid ActivationId);

internal sealed record ExecutionRuntimeProcessLeaseV1(
    int SchemaVersion,
    string LeaseId,
    Guid HostInstanceId,
    string PackageId,
    string GenerationId,
    Guid ActivationId,
    string ContentIdentity,
    DateTimeOffset ReservedAtUtc,
    DateTimeOffset? ProcessStartRequestedAtUtc = null,
    Guid? ContainmentGeneration = null,
    string? ContainmentKind = null,
    string? ContainmentIdentity = null,
    int? ContainmentProcessId = null,
    string? ContainmentProcessBirthIdentity = null,
    DateTimeOffset? ContainmentProcessStartedAtUtc = null,
    string? ContainmentExecutablePath = null,
    DateTimeOffset? ContainmentAttachedAtUtc = null,
    int? ProcessId = null,
    string? ProcessBirthIdentity = null,
    DateTimeOffset? ProcessStartedAtUtc = null,
    string? ExecutablePath = null,
    string? RuntimeRootPath = null,
    DateTimeOffset? AttachedAtUtc = null)
{
    public const int CurrentSchemaVersion = 2;

    public bool HasContainment => ContainmentProcessId is not null;
    public bool IsAttached => ProcessId is not null;
}

internal enum ExecutionRuntimeProcessProbe
{
    Missing,
    BirthMismatch,
    AttachmentMismatch,
    Matching,
    Unavailable,
}

internal sealed record ExecutionRuntimeProcessObservation(
    int ProcessId,
    string BirthIdentity,
    DateTimeOffset StartedAtUtc,
    string ExecutablePath);

internal enum ExecutionRuntimeContainmentProbe
{
    Empty,
    Active,
    IdentityMismatch,
    Unavailable,
}

internal interface IExecutionRuntimeProcessController
{
    ExecutionRuntimeProcessObservation Observe(Process process);

    ExecutionRuntimeProcessProbe Probe(
        int processId,
        string birthIdentity,
        string executablePath);

}

internal interface IExecutionRuntimeContainedProcess : IAsyncDisposable
{
    Process Process { get; }

    Stream StandardInput { get; }

    Stream StandardOutput { get; }

    Stream StandardError { get; }

    Task TerminateAndDrainAsync(CancellationToken cancellationToken = default);
}

internal interface IExecutionRuntimeProcessContainmentController
{
    Task<IExecutionRuntimePendingContainedProcess> StartAsync(
        string leaseId,
        Guid generation,
        ProcessStartInfo payloadStartInfo,
        string payloadReportPath,
        CancellationToken cancellationToken);

    ExecutionRuntimeContainmentProbe Probe(ExecutionRuntimeProcessLeaseV1 lease);

    Task<bool> TerminateAndDrainAsync(
        ExecutionRuntimeProcessLeaseV1 lease,
        CancellationToken cancellationToken);
}

internal interface IExecutionRuntimePendingContainedProcess : IAsyncDisposable
{
    string Kind { get; }

    string Identity { get; }

    ExecutionRuntimeProcessObservation ContainmentProcess { get; }

    Task<IExecutionRuntimeContainedProcess> AuthorizePayloadAsync(
        CancellationToken cancellationToken = default);

    Task TerminateAndDrainAsync(CancellationToken cancellationToken = default);
}

internal interface IExecutionRuntimeProcessLease : IAsyncDisposable
{
    string LeaseId { get; }

    string RuntimeRootPath { get; }

    Task<IExecutionRuntimeContainedProcess> LaunchAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken = default,
        PackageProjectionAcquisition? packageProjection = null);

    Task ReleaseAfterProcessTreeTerminationAsync(CancellationToken cancellationToken = default);
}

internal sealed class ExecutionRuntimeLeaseRegistry
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreFences = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly ExecutionRuntimeStoreLayout _layout;
    private readonly Guid _hostInstanceId;
    private readonly TimeProvider _timeProvider;
    private readonly IExecutionRuntimeProcessController _processes;
    private readonly IExecutionRuntimeProcessContainmentController _containment;
    private readonly ExecutionRuntimePhaseTelemetry _telemetry;

    public ExecutionRuntimeLeaseRegistry(
        ExecutionRuntimeStoreLayout layout,
        Guid hostInstanceId,
        TimeProvider timeProvider,
        IExecutionRuntimeProcessController? processes = null,
        IExecutionRuntimeProcessContainmentController? containment = null,
        ExecutionRuntimePhaseTelemetry? telemetry = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _hostInstanceId = hostInstanceId != Guid.Empty
            ? hostInstanceId
            : throw new ArgumentException("A non-empty Runtime Host instance id is required.", nameof(hostInstanceId));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _processes = processes ?? new SystemExecutionRuntimeProcessController();
        _containment = containment ?? new SystemExecutionRuntimeProcessContainmentController(_processes);
        _telemetry = telemetry ?? ExecutionRuntimePhaseTelemetry.Disabled;
    }

    public Guid HostInstanceId => _hostInstanceId;

    public async Task<IExecutionRuntimeProcessLease> ReserveAsync(
        ExecutionRuntimeLeaseOwner owner,
        string contentIdentity,
        string runtimeRootPath,
        string executablePath,
        CancellationToken cancellationToken)
        => await ReserveAsync(
                owner,
                contentIdentity,
                runtimeRootPath,
                executablePath,
                identity: null,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<IExecutionRuntimeProcessLease> ReserveAsync(
        ExecutionRuntimeLeaseOwner owner,
        string contentIdentity,
        string runtimeRootPath,
        string executablePath,
        ExecutionRuntimeIdentity? identity,
        CancellationToken cancellationToken)
    {
        using var phase = _telemetry.BeginPhase(ExecutionRuntimePhaseNames.Lease, identity);
        try
        {
            ValidateOwner(owner);
            ExecutionRuntimeStoreLayout.ValidateSha256(contentIdentity, nameof(contentIdentity));
            var expectedRoot = _layout.GetContentRootPath(contentIdentity);
            var root = Path.GetFullPath(runtimeRootPath);
            var executable = Path.GetFullPath(executablePath);
            if (!PathsEqual(root, expectedRoot))
            {
                throw new InvalidDataException("The execution-runtime lease root does not match its content identity.");
            }

            using var fence = await EnterFenceAsync(cancellationToken).ConfigureAwait(false);
            if (!Directory.Exists(root) || !File.Exists(executable))
            {
                throw new InvalidDataException("The execution-runtime root or launcher disappeared before lease reservation.");
            }
            PackageStorePathGuard.EnsureSafe(root, _layout.ContentRootPath);
            PackageStorePathGuard.EnsureSafe(executable, root);

            var leaseId = Guid.NewGuid().ToString("N");
            var document = new ExecutionRuntimeProcessLeaseV1(
                ExecutionRuntimeProcessLeaseV1.CurrentSchemaVersion,
                leaseId,
                _hostInstanceId,
                owner.PackageId,
                owner.GenerationId,
                owner.ActivationId,
                contentIdentity,
                _timeProvider.GetUtcNow());
            var path = _layout.GetLeasePath(leaseId);
            Validate(document, path);
            if (File.Exists(path))
            {
                throw new IOException($"Execution-runtime lease '{leaseId}' already exists.");
            }
            await DurableJsonDocument.WriteAsync(
                    path,
                    document,
                    ExecutionRuntimeStore.JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            phase.Succeeded();
            return new ExecutionRuntimeProcessLease(this, document, root, executable);
        }
        catch (OperationCanceledException)
        {
            phase.Canceled();
            throw;
        }
        catch (Exception exception)
        {
            phase.Failed(exception);
            throw;
        }
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        using var fence = await EnterFenceAsync(cancellationToken).ConfigureAwait(false);
        if (File.Exists(_layout.LeaseRecoveryFailurePath))
        {
            throw new InvalidDataException(
                "Execution-runtime process lease recovery remains blocked by quarantined ownership evidence. Run the data-safe Runtime reset before restarting.");
        }
        ReconcileAtomicWriteCandidatesUnderFence();
        ValidateLeaseDirectoryUnderFence();
        foreach (var path in EnumerateLeaseFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionRuntimeProcessLeaseV1 lease;
            try
            {
                lease = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
                Validate(lease, path);
            }
            catch (Exception exception) when (exception is IOException
                                                or JsonException
                                                or InvalidDataException
                                                or ArgumentException)
            {
                QuarantineInvalidLeaseUnderFence(path);
                throw new InvalidDataException(
                    $"Execution-runtime lease '{path}' is invalid; startup cannot safely recover process ownership.",
                    exception);
            }

            if (lease.HostInstanceId == _hostInstanceId)
            {
                continue;
            }
            if (!lease.HasContainment)
            {
                DurableJsonDocument.Delete(path);
                DeletePayloadReportsUnderFence(lease.LeaseId);
                continue;
            }

            if (lease.HasContainment)
            {
                var containmentProbe = _containment.Probe(lease);
                if (containmentProbe == ExecutionRuntimeContainmentProbe.Active)
                {
                    var terminated = await _containment.TerminateAndDrainAsync(lease, cancellationToken)
                        .ConfigureAwait(false);
                    if (!terminated || _containment.Probe(lease) != ExecutionRuntimeContainmentProbe.Empty)
                    {
                        throw new IOException(
                            $"Verified orphan Worker containment for lease '{lease.LeaseId}' could not be terminated and drained.");
                    }
                }
                else if (containmentProbe == ExecutionRuntimeContainmentProbe.Unavailable)
                {
                    throw new IOException(
                        $"Worker containment identity for execution-runtime lease '{lease.LeaseId}' could not be verified.");
                }
                else if (containmentProbe == ExecutionRuntimeContainmentProbe.IdentityMismatch)
                {
                    throw new IOException(
                        $"Worker containment for execution-runtime lease '{lease.LeaseId}' no longer matches its durable identity.");
                }

                DurableJsonDocument.Delete(path);
                DeletePayloadReportsUnderFence(lease.LeaseId);
                continue;
            }
        }
    }

    public async Task<IReadOnlySet<string>> ReadActiveContentIdentitiesUnderFenceAsync(
        CancellationToken cancellationToken)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in EnumerateLeaseFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            Validate(lease, path);
            identities.Add(lease.ContentIdentity);
        }
        return identities;
    }

    internal async Task<IReadOnlyList<ExecutionRuntimeProcessLeaseV1>> ReadLeasesUnderFenceAsync(CancellationToken token)
    {
        var result = new List<ExecutionRuntimeProcessLeaseV1>();
        foreach (var path in EnumerateLeaseFiles())
        {
            var lease = await ReadAsync(path, token);
            Validate(lease, path);
            result.Add(lease);
        }
        return result;
    }

    public bool HasActiveLeaseUnderFence(string contentIdentity)
    {
        ExecutionRuntimeStoreLayout.ValidateSha256(contentIdentity, nameof(contentIdentity));
        foreach (var path in EnumerateLeaseFiles())
        {
            var lease = Read(path);
            Validate(lease, path);
            if (string.Equals(lease.ContentIdentity, contentIdentity, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public async ValueTask<StoreFenceLease> EnterFenceAsync(CancellationToken cancellationToken)
    {
        var gate = StoreFences.GetOrAdd(_layout.RootPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fileLock = await RuntimeFileLock.AcquireExclusiveAsync(
                    _layout.StoreFencePath,
                    cancellationToken)
                .ConfigureAwait(false);
            return new StoreFenceLease(gate, fileLock);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    public StoreFenceLease EnterFence()
    {
        var gate = StoreFences.GetOrAdd(_layout.RootPath, static _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            return new StoreFenceLease(gate, RuntimeFileLock.AcquireExclusive(_layout.StoreFencePath));
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private async Task<IExecutionRuntimeContainedProcess> LaunchContainedAsync(
        ExecutionRuntimeProcessLeaseV1 reservation,
        string runtimeRootPath,
        string executablePath,
        ProcessStartInfo startInfo,
        PackageProjectionAcquisition? packageProjection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.UseShellExecute
            || !Path.IsPathFullyQualified(startInfo.FileName)
            || !Path.IsPathFullyQualified(startInfo.WorkingDirectory)
            || !PathsEqual(startInfo.FileName, executablePath))
        {
            throw new InvalidDataException(
                "The execution-runtime process launch does not use its exact shell-free leased executable.");
        }

        if (packageProjection is not null)
        {
            await packageProjection.BindToProcessTreeLeaseAsync(
                    reservation.LeaseId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var generation = Guid.NewGuid();
        await BeginContainedLaunchAsync(reservation, generation, cancellationToken).ConfigureAwait(false);
        var payloadReportPath = _layout.GetLeasePayloadReportPath(reservation.LeaseId);
        IExecutionRuntimePendingContainedProcess? pending = null;
        try
        {
            pending = await _containment.StartAsync(
                    reservation.LeaseId,
                    generation,
                    startInfo,
                    payloadReportPath,
                    cancellationToken)
                .ConfigureAwait(false);
            await AttachContainmentAsync(
                    reservation,
                    generation,
                    pending,
                    cancellationToken)
                .ConfigureAwait(false);
            var guardian = ToProjectionProcessIdentity(pending.ContainmentProcess);
            if (packageProjection is not null)
            {
                await packageProjection.AttachToProcessTreeAsync(
                        reservation.LeaseId,
                        [guardian],
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            var contained = await pending.AuthorizePayloadAsync(cancellationToken).ConfigureAwait(false);
            var worker = await AttachContainedProcessAsync(
                    reservation,
                    runtimeRootPath,
                    executablePath,
                    contained.Process,
                    cancellationToken)
                .ConfigureAwait(false);
            if (packageProjection is not null)
            {
                await packageProjection.AttachToProcessTreeAsync(
                        reservation.LeaseId,
                        [guardian, ToProjectionProcessIdentity(worker)],
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            DeletePayloadReportsUnderFence(reservation.LeaseId);
            return contained;
        }
        catch
        {
            if (pending is not null)
            {
                try
                {
                    await pending.TerminateAndDrainAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    await pending.DisposeAsync().ConfigureAwait(false);
                }
            }
            await ReleaseFailedContainedLaunchAsync(reservation).ConfigureAwait(false);
            throw;
        }
    }

    private async Task BeginContainedLaunchAsync(
        ExecutionRuntimeProcessLeaseV1 reservation,
        Guid generation,
        CancellationToken cancellationToken)
    {
        using var fence = await EnterFenceAsync(cancellationToken).ConfigureAwait(false);
        var path = _layout.GetLeasePath(reservation.LeaseId);
        var current = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        Validate(current, path);
        if (current.ProcessStartRequestedAtUtc is not null
            || current.HasContainment
            || current.IsAttached
            || !SameReservation(current, reservation))
        {
            throw new InvalidDataException(
                "The execution-runtime lease reservation changed before contained process start.");
        }
        var starting = current with
        {
            ProcessStartRequestedAtUtc = _timeProvider.GetUtcNow(),
            ContainmentGeneration = generation,
        };
        Validate(starting, path);
        await DurableJsonDocument.WriteAsync(path, starting, ExecutionRuntimeStore.JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task AttachContainmentAsync(
        ExecutionRuntimeProcessLeaseV1 reservation,
        Guid generation,
        IExecutionRuntimePendingContainedProcess pending,
        CancellationToken cancellationToken)
    {
        var observation = pending.ContainmentProcess;
        ExecutionRuntimeProcessBirthIdentity.Validate(observation.BirthIdentity);
        if (observation.ProcessId <= 0
            || observation.StartedAtUtc == default
            || !Path.IsPathFullyQualified(observation.ExecutablePath))
        {
            throw new InvalidDataException("The execution-runtime containment process identity is invalid.");
        }

        using var fence = await EnterFenceAsync(cancellationToken).ConfigureAwait(false);
        var path = _layout.GetLeasePath(reservation.LeaseId);
        var current = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        Validate(current, path);
        if (current.HasContainment
            || current.IsAttached
            || current.ContainmentGeneration != generation
            || !SameReservation(current, reservation))
        {
            throw new InvalidDataException(
                "The execution-runtime lease reservation changed before containment attachment.");
        }
        var attached = current with
        {
            ContainmentKind = pending.Kind,
            ContainmentIdentity = pending.Identity,
            ContainmentProcessId = observation.ProcessId,
            ContainmentProcessBirthIdentity = observation.BirthIdentity,
            ContainmentProcessStartedAtUtc = observation.StartedAtUtc.ToUniversalTime(),
            ContainmentExecutablePath = Path.GetFullPath(observation.ExecutablePath),
            ContainmentAttachedAtUtc = _timeProvider.GetUtcNow(),
        };
        Validate(attached, path);
        await DurableJsonDocument.WriteAsync(path, attached, ExecutionRuntimeStore.JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ExecutionRuntimeProcessObservation> AttachContainedProcessAsync(
        ExecutionRuntimeProcessLeaseV1 reservation,
        string runtimeRootPath,
        string executablePath,
        Process process,
        CancellationToken cancellationToken)
    {
        var observation = _processes.Observe(process);
        ExecutionRuntimeProcessBirthIdentity.Validate(observation.BirthIdentity);
        if (observation.ProcessId <= 0
            || observation.StartedAtUtc == default
            || !PathsEqual(observation.ExecutablePath, executablePath))
        {
            throw new InvalidDataException(
                "The contained Worker process does not match its exact execution-runtime launcher identity.");
        }

        using var fence = await EnterFenceAsync(cancellationToken).ConfigureAwait(false);
        var path = _layout.GetLeasePath(reservation.LeaseId);
        var current = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        Validate(current, path);
        if (!current.HasContainment || current.IsAttached || !SameReservation(current, reservation))
        {
            throw new InvalidDataException("The execution-runtime containment changed before Worker attachment.");
        }
        var attached = current with
        {
            ProcessId = observation.ProcessId,
            ProcessBirthIdentity = observation.BirthIdentity,
            ProcessStartedAtUtc = observation.StartedAtUtc.ToUniversalTime(),
            ExecutablePath = Path.GetFullPath(executablePath),
            RuntimeRootPath = Path.GetFullPath(runtimeRootPath),
            AttachedAtUtc = _timeProvider.GetUtcNow(),
        };
        Validate(attached, path);
        await DurableJsonDocument.WriteAsync(path, attached, ExecutionRuntimeStore.JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return observation;
    }

    private static PackageProjectionProcessIdentity ToProjectionProcessIdentity(
        ExecutionRuntimeProcessObservation process)
        => new(process.ProcessId, process.BirthIdentity, process.StartedAtUtc);

    private async Task ReleaseFailedContainedLaunchAsync(ExecutionRuntimeProcessLeaseV1 reservation)
    {
        using var fence = await EnterFenceAsync(CancellationToken.None).ConfigureAwait(false);
        var path = _layout.GetLeasePath(reservation.LeaseId);
        if (!File.Exists(path))
        {
            DeletePayloadReportsUnderFence(reservation.LeaseId);
            return;
        }
        var current = await ReadAsync(path, CancellationToken.None).ConfigureAwait(false);
        Validate(current, path);
        if (!SameReservation(current, reservation))
        {
            throw new InvalidDataException("The failed execution-runtime launch no longer owns its lease.");
        }
        if (current.HasContainment && _containment.Probe(current) != ExecutionRuntimeContainmentProbe.Empty)
        {
            throw new IOException("Failed execution-runtime process containment could not be proven empty.");
        }
        DurableJsonDocument.Delete(path);
        DeletePayloadReportsUnderFence(reservation.LeaseId);
    }

    private async Task ReleaseReservationAsync(
        ExecutionRuntimeProcessLeaseV1 reservation,
        CancellationToken cancellationToken)
    {
        using var fence = await EnterFenceAsync(cancellationToken).ConfigureAwait(false);
        var path = _layout.GetLeasePath(reservation.LeaseId);
        if (!File.Exists(path))
        {
            return;
        }
        var current = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        Validate(current, path);
        if (!SameReservation(current, reservation))
        {
            throw new InvalidDataException("The execution-runtime lease reservation is no longer owned by this launch.");
        }
        if (current.IsAttached || current.HasContainment)
        {
            throw new InvalidOperationException(
                "A contained execution-runtime lease cannot be released without process-tree termination proof.");
        }
        DurableJsonDocument.Delete(path);
    }

    private async Task ReleaseAfterTerminationAsync(
        ExecutionRuntimeProcessLeaseV1 reservation,
        CancellationToken cancellationToken)
    {
        using var fence = await EnterFenceAsync(cancellationToken).ConfigureAwait(false);
        var path = _layout.GetLeasePath(reservation.LeaseId);
        if (!File.Exists(path))
        {
            return;
        }
        var current = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        Validate(current, path);
        if (!SameReservation(current, reservation))
        {
            throw new InvalidDataException("The execution-runtime lease is no longer owned by this Worker.");
        }
        if (current.HasContainment)
        {
            var containmentProbe = _containment.Probe(current);
            if (containmentProbe == ExecutionRuntimeContainmentProbe.Active)
            {
                throw new InvalidOperationException(
                    $"Worker containment for lease '{current.LeaseId}' is still active; its execution-runtime lease cannot be released.");
            }
            if (containmentProbe == ExecutionRuntimeContainmentProbe.Unavailable)
            {
                throw new IOException(
                    $"Worker containment termination for lease '{current.LeaseId}' could not be verified.");
            }
            if (containmentProbe == ExecutionRuntimeContainmentProbe.IdentityMismatch)
            {
                throw new IOException(
                    $"Worker containment for lease '{current.LeaseId}' still exists with an unexpected identity.");
            }
        }
        DurableJsonDocument.Delete(path);
        DeletePayloadReportsUnderFence(reservation.LeaseId);
    }

    private async Task<ExecutionRuntimeProcessLeaseV1> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        PackageStorePathGuard.EnsureSafe(path, _layout.LeaseRootPath);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > 64 * 1024)
        {
            throw new InvalidDataException($"Execution-runtime lease '{path}' has an invalid size.");
        }
        return await JsonSerializer.DeserializeAsync<ExecutionRuntimeProcessLeaseV1>(
                   stream,
                   ExecutionRuntimeStore.JsonOptions,
                   cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException($"Execution-runtime lease '{path}' is empty.");
    }

    private ExecutionRuntimeProcessLeaseV1 Read(string path)
    {
        PackageStorePathGuard.EnsureSafe(path, _layout.LeaseRootPath);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > 64 * 1024)
        {
            throw new InvalidDataException($"Execution-runtime lease '{path}' has an invalid size.");
        }
        return JsonSerializer.Deserialize<ExecutionRuntimeProcessLeaseV1>(
                   stream,
                   ExecutionRuntimeStore.JsonOptions)
               ?? throw new InvalidDataException($"Execution-runtime lease '{path}' is empty.");
    }

    private void Validate(ExecutionRuntimeProcessLeaseV1 lease, string path)
    {
        if (lease.SchemaVersion != ExecutionRuntimeProcessLeaseV1.CurrentSchemaVersion
            || !Guid.TryParseExact(lease.LeaseId, "N", out _)
            || lease.HostInstanceId == Guid.Empty
            || lease.ActivationId == Guid.Empty
            || lease.ReservedAtUtc == default
            || lease.ProcessStartRequestedAtUtc is { } startRequestedAtUtc
                && startRequestedAtUtc < lease.ReservedAtUtc
            || lease.ContainmentGeneration == Guid.Empty
            || (lease.ProcessStartRequestedAtUtc is null) != (lease.ContainmentGeneration is null)
            || !string.Equals(path, _layout.GetLeasePath(lease.LeaseId), PathComparison))
        {
            throw new InvalidDataException($"Execution-runtime lease '{path}' is invalid.");
        }
        ValidateOwner(new ExecutionRuntimeLeaseOwner(lease.PackageId, lease.GenerationId, lease.ActivationId));
        ExecutionRuntimeStoreLayout.ValidateSha256(lease.ContentIdentity, nameof(lease.ContentIdentity));

        var containmentCount = new object?[]
        {
            lease.ContainmentKind,
            lease.ContainmentIdentity,
            lease.ContainmentProcessId,
            lease.ContainmentProcessBirthIdentity,
            lease.ContainmentProcessStartedAtUtc,
            lease.ContainmentExecutablePath,
            lease.ContainmentAttachedAtUtc,
        }.Count(static value => value is not null);
        if (containmentCount is not (0 or 7))
        {
            throw new InvalidDataException(
                $"Execution-runtime lease '{path}' has a partial process-containment attachment.");
        }
        if (lease.HasContainment)
        {
            if (lease.ProcessStartRequestedAtUtc is null
                || lease.ContainmentGeneration is null
                || lease.ContainmentProcessId <= 0
                || lease.ContainmentProcessStartedAtUtc == default
                || lease.ContainmentAttachedAtUtc == default
                || lease.ContainmentAttachedAtUtc < lease.ReservedAtUtc
                || !Path.IsPathFullyQualified(lease.ContainmentExecutablePath!))
            {
                throw new InvalidDataException(
                    $"Execution-runtime lease '{path}' has invalid process-containment metadata.");
            }
            ExecutionRuntimeProcessBirthIdentity.Validate(lease.ContainmentProcessBirthIdentity!);
            if (string.Equals(
                    lease.ContainmentKind,
                    SystemExecutionRuntimeProcessContainmentController.UnixProcessGroupKind,
                    StringComparison.Ordinal))
            {
                if (!int.TryParse(
                        lease.ContainmentIdentity,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var processGroupId)
                    || processGroupId != lease.ContainmentProcessId)
                {
                    throw new InvalidDataException(
                        $"Execution-runtime lease '{path}' has an invalid Unix process-group identity.");
                }
            }
            else if (string.Equals(
                         lease.ContainmentKind,
                         SystemExecutionRuntimeProcessContainmentController.WindowsJobKind,
                         StringComparison.Ordinal))
            {
                var expectedJobName = $@"Local\Sunder.ExecutionRuntime.{lease.LeaseId}";
                if (!string.Equals(lease.ContainmentIdentity, expectedJobName, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Execution-runtime lease '{path}' has an invalid Windows Job identity.");
                }
            }
            else
            {
                throw new InvalidDataException(
                    $"Execution-runtime lease '{path}' has an unknown process-containment kind.");
            }
        }

        var attachmentCount = new object?[]
        {
            lease.ProcessId,
            lease.ProcessBirthIdentity,
            lease.ProcessStartedAtUtc,
            lease.ExecutablePath,
            lease.RuntimeRootPath,
            lease.AttachedAtUtc,
        }.Count(static value => value is not null);
        if (attachmentCount is not (0 or 6))
        {
            throw new InvalidDataException(
                $"Execution-runtime lease '{path}' has a partial process attachment.");
        }
        if (!lease.IsAttached)
        {
            return;
        }
        if (!lease.HasContainment
            || lease.ProcessStartRequestedAtUtc is null
            || lease.ProcessId <= 0
            || lease.ProcessStartedAtUtc == default
            || lease.AttachedAtUtc == default
            || lease.AttachedAtUtc < lease.ReservedAtUtc
            || !Path.IsPathFullyQualified(lease.RuntimeRootPath!)
            || !Path.IsPathFullyQualified(lease.ExecutablePath!))
        {
            throw new InvalidDataException($"Execution-runtime lease '{path}' has invalid process metadata.");
        }
        ExecutionRuntimeProcessBirthIdentity.Validate(lease.ProcessBirthIdentity!);
        var expectedRoot = _layout.GetContentRootPath(lease.ContentIdentity);
        if (!PathsEqual(expectedRoot, lease.RuntimeRootPath!))
        {
            throw new InvalidDataException($"Execution-runtime lease '{path}' names the wrong immutable root.");
        }
        _layout.EnsureContained(lease.ExecutablePath!, expectedRoot);
    }

    private void ReconcileAtomicWriteCandidatesUnderFence()
    {
        foreach (var group in Directory.EnumerateFiles(_layout.LeaseRootPath, "*.json.tmp-*")
                     .GroupBy(static path => path[..path.LastIndexOf(".tmp-", StringComparison.Ordinal)], PathComparer))
        {
            var finalPath = group.Key;
            ExecutionRuntimeProcessLeaseV1? final = null;
            if (File.Exists(finalPath))
            {
                try
                {
                    final = Read(finalPath);
                    Validate(final, finalPath);
                }
                catch (Exception exception) when (exception is IOException
                                                    or JsonException
                                                    or InvalidDataException
                                                    or ArgumentException)
                {
                    QuarantineInvalidLeaseUnderFence(finalPath);
                    throw new InvalidDataException(
                        $"Execution-runtime lease '{finalPath}' is invalid.",
                        exception);
                }
            }

            var candidates = new List<(string Path, ExecutionRuntimeProcessLeaseV1 Lease)>();
            foreach (var candidatePath in group)
            {
                try
                {
                    PackageStorePathGuard.EnsureSafe(candidatePath, _layout.LeaseRootPath);
                    var candidate = Read(candidatePath);
                    Validate(candidate, finalPath);
                    candidates.Add((candidatePath, candidate));
                }
                catch (Exception exception) when (exception is IOException
                                                    or JsonException
                                                    or InvalidDataException
                                                    or ArgumentException)
                {
                    ExecutionRuntimeStore.TryDeleteFile(candidatePath);
                }
            }

            var recoverable = candidates
                .Where(candidate => (candidate.Lease.IsAttached || candidate.Lease.HasContainment)
                                    && (final is null || SameReservation(candidate.Lease, final)))
                .OrderByDescending(static candidate => candidate.Lease.AttachedAtUtc
                                                       ?? candidate.Lease.ContainmentAttachedAtUtc)
                .FirstOrDefault();
            if (recoverable.Lease is not null
                && (final is null
                    || !final.IsAttached && !final.HasContainment
                    || recoverable.Lease.IsAttached && !final.IsAttached))
            {
                File.Move(recoverable.Path, finalPath, overwrite: true);
                DurableJsonDocument.SyncDirectory(_layout.LeaseRootPath);
            }
            foreach (var candidate in candidates)
            {
                ExecutionRuntimeStore.TryDeleteFile(candidate.Path);
            }
        }
    }

    private IReadOnlyList<string> EnumerateLeaseFiles()
    {
        var paths = Directory.EnumerateFiles(
                _layout.LeaseRootPath,
                "*.json",
                SearchOption.TopDirectoryOnly)
            .ToArray();
        foreach (var path in paths)
        {
            PackageStorePathGuard.EnsureSafe(path, _layout.LeaseRootPath);
        }
        return paths;
    }

    private void ValidateLeaseDirectoryUnderFence()
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     _layout.LeaseRootPath,
                     "*",
                     SearchOption.TopDirectoryOnly).ToArray())
        {
            var fileName = Path.GetFileName(path);
            var expectedLease = File.Exists(path)
                                && fileName.EndsWith(".json", StringComparison.Ordinal)
                                && Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _);
            if (expectedLease)
            {
                continue;
            }
            if (Directory.Exists(path) && PathsEqual(path, _layout.LockRootPath))
            {
                PackageStorePathGuard.EnsureSafe(path, _layout.LeaseRootPath);
                continue;
            }
            var payloadSeparator = fileName.IndexOf(".payload", StringComparison.Ordinal);
            if (File.Exists(path)
                && payloadSeparator > 0
                && Guid.TryParseExact(fileName.AsSpan(0, payloadSeparator), "N", out var leaseId))
            {
                var leasePath = _layout.GetLeasePath(leaseId.ToString("N"));
                if (File.Exists(leasePath))
                {
                    continue;
                }
                ExecutionRuntimeStore.TryDeleteFile(path);
                continue;
            }
            QuarantineInvalidLeaseUnderFence(path);
            throw new InvalidDataException(
                $"Unknown execution-runtime lease entry '{path}' was quarantined; startup cannot safely infer process ownership.");
        }
    }

    private void QuarantineInvalidLeaseUnderFence(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }
        PackageStorePathGuard.EnsureSafe(path, _layout.LeaseRootPath);
        RecordRecoveryFailureUnderFence(path);
        Directory.CreateDirectory(_layout.QuarantineRootPath);
        var destination = _layout.CreateQuarantinePath("invalid-lease", _timeProvider.GetUtcNow());
        if (Directory.Exists(path))
        {
            Directory.Move(path, destination);
        }
        else
        {
            File.Move(path, destination);
        }
    }

    private void RecordRecoveryFailureUnderFence(string path)
    {
        if (File.Exists(_layout.LeaseRecoveryFailurePath))
        {
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(
            $"Execution-runtime lease ownership could not be recovered from '{Path.GetFileName(path)}'.{Environment.NewLine}");
        using (var stream = new FileStream(
                   _layout.LeaseRecoveryFailurePath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.Read,
                   bufferSize: 4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        DurableJsonDocument.SyncDirectory(_layout.LockRootPath);
    }

    private static void ValidateOwner(ExecutionRuntimeLeaseOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!PackageId.TryParse(owner.PackageId, out _)
            || string.IsNullOrWhiteSpace(owner.GenerationId)
            || owner.GenerationId.Length > 256
            || owner.GenerationId.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_')
            || owner.ActivationId == Guid.Empty)
        {
            throw new ArgumentException("Execution-runtime lease ownership identity is invalid.", nameof(owner));
        }
    }

    private static bool SameReservation(
        ExecutionRuntimeProcessLeaseV1 left,
        ExecutionRuntimeProcessLeaseV1 right)
        => string.Equals(left.LeaseId, right.LeaseId, StringComparison.Ordinal)
           && left.HostInstanceId == right.HostInstanceId
           && string.Equals(left.PackageId, right.PackageId, StringComparison.Ordinal)
           && string.Equals(left.GenerationId, right.GenerationId, StringComparison.Ordinal)
           && left.ActivationId == right.ActivationId
           && string.Equals(left.ContentIdentity, right.ContentIdentity, StringComparison.Ordinal)
           && left.ReservedAtUtc == right.ReservedAtUtc;

    private void DeletePayloadReportsUnderFence(string leaseId)
    {
        var reportPath = _layout.GetLeasePayloadReportPath(leaseId);
        ExecutionRuntimeStore.TryDeleteFile(reportPath);
        foreach (var candidatePath in Directory.EnumerateFiles(
                     _layout.LeaseRootPath,
                     Path.GetFileName(reportPath) + ".tmp-*",
                     SearchOption.TopDirectoryOnly))
        {
            PackageStorePathGuard.EnsureSafe(candidatePath, _layout.LeaseRootPath);
            ExecutionRuntimeStore.TryDeleteFile(candidatePath);
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal sealed class StoreFenceLease(
        SemaphoreSlim gate,
        RuntimeFileLock fileLock) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                fileLock.Dispose();
                gate.Release();
            }
        }
    }

    private sealed class ExecutionRuntimeProcessLease(
        ExecutionRuntimeLeaseRegistry registry,
        ExecutionRuntimeProcessLeaseV1 reservation,
        string runtimeRootPath,
        string executablePath) : IExecutionRuntimeProcessLease
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _startRequested;
        private bool _attached;
        private bool _released;

        public string LeaseId => reservation.LeaseId;

        public string RuntimeRootPath => runtimeRootPath;

        public async Task<IExecutionRuntimeContainedProcess> LaunchAsync(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken = default,
            PackageProjectionAcquisition? packageProjection = null)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_released, this);
                if (_startRequested || _attached)
                {
                    throw new InvalidOperationException(
                        "The execution-runtime lease was already used for process start.");
                }
                try
                {
                    var contained = await registry.LaunchContainedAsync(
                            reservation,
                            runtimeRootPath,
                            executablePath,
                            startInfo,
                            packageProjection,
                            cancellationToken)
                        .ConfigureAwait(false);
                    _startRequested = true;
                    _attached = true;
                    return contained;
                }
                catch
                {
                    _startRequested = true;
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task ReleaseAfterProcessTreeTerminationAsync(
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_released)
                {
                    return;
                }
                await registry.ReleaseAfterTerminationAsync(reservation, cancellationToken)
                    .ConfigureAwait(false);
                _released = true;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_released)
                {
                    return;
                }
                if (_attached || _startRequested)
                {
                    throw new InvalidOperationException(
                        "A started execution-runtime lease requires process-tree termination proof.");
                }
                await registry.ReleaseReservationAsync(reservation, CancellationToken.None)
                    .ConfigureAwait(false);
                _released = true;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}

internal static class ExecutionRuntimeProcessBirthIdentity
{
    public static string Capture(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            var stat = File.ReadAllText($"/proc/{process.Id}/stat");
            var closingParenthesis = stat.LastIndexOf(')');
            if (closingParenthesis < 0)
            {
                throw new InvalidDataException($"Process {process.Id} has invalid Linux start metadata.");
            }
            var fields = stat[(closingParenthesis + 1)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length <= 19
                || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                throw new InvalidDataException($"Process {process.Id} has invalid Linux start metadata.");
            }
            var bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim().ToLowerInvariant();
            if (!Guid.TryParseExact(bootId, "D", out _))
            {
                throw new InvalidDataException("The Linux boot identity is invalid.");
            }
            return $"linux:{bootId}:{fields[19]}";
        }

        var startedAtUtc = process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        if (OperatingSystem.IsWindows())
        {
            return "windows:" + startedAtUtc;
        }
        if (OperatingSystem.IsMacOS())
        {
            return "darwin:" + startedAtUtc;
        }
        throw new PlatformNotSupportedException(
            "Execution-runtime process birth identity is unavailable on this operating system.");
    }

    public static void Validate(string value)
    {
        if (OperatingSystem.IsLinux())
        {
            var parts = value.Split(':');
            if (parts.Length == 3
                && string.Equals(parts[0], "linux", StringComparison.Ordinal)
                && Guid.TryParseExact(parts[1], "D", out _)
                && ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return;
            }
        }
        else
        {
            var prefix = OperatingSystem.IsWindows() ? "windows:" : "darwin:";
            if (value.StartsWith(prefix, StringComparison.Ordinal)
                && long.TryParse(value.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks > 0)
            {
                return;
            }
        }
        throw new InvalidDataException("The execution-runtime process birth identity is invalid.");
    }

    internal static string CreateSyntheticForCurrentPlatform(long value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        if (OperatingSystem.IsLinux())
        {
            return $"linux:00000000-0000-0000-0000-000000000001:{value.ToString(CultureInfo.InvariantCulture)}";
        }
        return (OperatingSystem.IsWindows() ? "windows:" : "darwin:")
               + value.ToString(CultureInfo.InvariantCulture);
    }
}

internal sealed class SystemExecutionRuntimeProcessController : IExecutionRuntimeProcessController
{
    public ExecutionRuntimeProcessObservation Observe(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Worker process {process.Id} exited before identity capture.");
            }
            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new InvalidDataException($"Worker process {process.Id} executable identity is unavailable.");
            }
            return new ExecutionRuntimeProcessObservation(
                process.Id,
                ExecutionRuntimeProcessBirthIdentity.Capture(process),
                new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
                Path.GetFullPath(executablePath));
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or System.ComponentModel.Win32Exception
                                           or NotSupportedException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            throw new IOException("The started Worker process identity could not be captured.", exception);
        }
    }

    public ExecutionRuntimeProcessProbe Probe(
        int processId,
        string birthIdentity,
        string executablePath)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return ExecutionRuntimeProcessProbe.Missing;
            }
            var actual = ExecutionRuntimeProcessBirthIdentity.Capture(process);
            if (!string.Equals(actual, birthIdentity, StringComparison.Ordinal))
            {
                return ExecutionRuntimeProcessProbe.BirthMismatch;
            }
            var actualExecutablePath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(actualExecutablePath)
                   && PathsEqual(actualExecutablePath, executablePath)
                ? ExecutionRuntimeProcessProbe.Matching
                : ExecutionRuntimeProcessProbe.AttachmentMismatch;
        }
        catch (ArgumentException)
        {
            return ExecutionRuntimeProcessProbe.Missing;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or System.ComponentModel.Win32Exception
                                           or NotSupportedException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            return ExecutionRuntimeProcessProbe.Unavailable;
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
