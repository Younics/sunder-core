using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Sunder.Runtime.Host.Services;

internal sealed record ExecutionRuntimeGuardianPayloadReportV1(
    int SchemaVersion,
    Guid Generation,
    int ProcessId,
    string ProcessBirthIdentity)
{
    public const int CurrentSchemaVersion = 2;
}

internal sealed class SystemExecutionRuntimeProcessContainmentController(
    IExecutionRuntimeProcessController processes) : IExecutionRuntimeProcessContainmentController
{
    private enum UnixAnchorProbe
    {
        Gone,
        Member,
        Detached,
        IdentityMismatch,
        Unavailable,
    }

    internal const string WindowsJobKind = "windows-job-v1";
    internal const string UnixProcessGroupKind = "unix-process-group-v1";
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TerminalObservationTimeout = TimeSpan.FromMilliseconds(250);

    public async Task<IExecutionRuntimePendingContainedProcess> StartAsync(
        string leaseId,
        Guid generation,
        ProcessStartInfo payloadStartInfo,
        string payloadReportPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        ArgumentNullException.ThrowIfNull(payloadStartInfo);
        if (generation == Guid.Empty)
        {
            throw new ArgumentException("A non-empty containment generation is required.", nameof(generation));
        }
        if (!Path.IsPathFullyQualified(payloadStartInfo.FileName)
            || !Path.IsPathFullyQualified(payloadStartInfo.WorkingDirectory)
            || !Path.IsPathFullyQualified(payloadReportPath))
        {
            throw new ArgumentException("Contained process launch paths must be absolute.");
        }

        File.Delete(payloadReportPath);
        var jobName = OperatingSystem.IsWindows()
            ? $@"Local\Sunder.ExecutionRuntime.{leaseId}"
            : null;
        SafeFileHandle? jobHandle = null;
        Process? guardian = null;
        var containmentEstablished = false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                jobHandle = WindowsJob.Create(jobName!);
            }
            else if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                throw new PlatformNotSupportedException(
                    "Execution-runtime process containment is unavailable on this operating system.");
            }

            var guardianStartInfo = CreateGuardianStartInfo(
                generation,
                payloadStartInfo,
                payloadReportPath);
            guardian = new Process
            {
                StartInfo = guardianStartInfo,
                EnableRaisingEvents = true,
            };
            if (!guardian.Start())
            {
                throw new IOException("The execution-runtime process guardian did not start.");
            }

            if (jobHandle is not null)
            {
                WindowsJob.Assign(jobHandle, guardian.SafeHandle);
                containmentEstablished = true;
            }
            else
            {
                await WaitForUnixProcessGroupAsync(guardian, cancellationToken).ConfigureAwait(false);
                containmentEstablished = true;
            }

            var observation = processes.Observe(guardian);
            var pending = new SystemPendingContainedProcess(
                this,
                processes,
                generation,
                jobName is null ? UnixProcessGroupKind : WindowsJobKind,
                jobName ?? guardian.Id.ToString(CultureInfo.InvariantCulture),
                observation,
                payloadStartInfo.FileName,
                payloadReportPath,
                guardian,
                jobHandle);
            guardian = null;
            jobHandle = null;
            return pending;
        }
        catch
        {
            if (jobHandle is not null && containmentEstablished)
            {
                TryTerminateWindowsJob(jobHandle);
            }
            else if (guardian is { HasExited: false }
                     && containmentEstablished
                     && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            {
                TryTerminateUnixGroup(guardian.Id);
            }
            if (guardian is { HasExited: false })
            {
                try
                {
                    guardian.Kill(entireProcessTree: true);
                    await guardian.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                {
                }
            }
            guardian?.Dispose();
            jobHandle?.Dispose();
            throw;
        }
    }

    public ExecutionRuntimeContainmentProbe Probe(ExecutionRuntimeProcessLeaseV1 lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!lease.HasContainment)
        {
            return ExecutionRuntimeContainmentProbe.Empty;
        }

        if (OperatingSystem.IsWindows())
        {
            if (!string.Equals(lease.ContainmentKind, WindowsJobKind, StringComparison.Ordinal))
            {
                return ExecutionRuntimeContainmentProbe.IdentityMismatch;
            }
            using var job = WindowsJob.TryOpen(lease.ContainmentIdentity!, out var unavailable);
            if (job is null)
            {
                if (unavailable)
                {
                    return ExecutionRuntimeContainmentProbe.Unavailable;
                }
                var missingJobGuardianProbe = ProbeGuardian(lease);
                return missingJobGuardianProbe is ExecutionRuntimeProcessProbe.Missing
                    or ExecutionRuntimeProcessProbe.BirthMismatch
                    ? ExecutionRuntimeContainmentProbe.Empty
                    : ExecutionRuntimeContainmentProbe.Unavailable;
            }
            if (WindowsJob.GetActiveProcessCount(job) == 0)
            {
                return ExecutionRuntimeContainmentProbe.Empty;
            }
            var guardianProbe = MapActiveGuardianProbe(lease);
            if (guardianProbe is ExecutionRuntimeContainmentProbe.Active
                or ExecutionRuntimeContainmentProbe.Unavailable
                || !lease.IsAttached)
            {
                return guardianProbe;
            }
            var payloadProbe = processes.Probe(
                lease.ProcessId!.Value,
                lease.ProcessBirthIdentity!,
                lease.ExecutablePath!);
            if (payloadProbe == ExecutionRuntimeProcessProbe.Unavailable)
            {
                return ExecutionRuntimeContainmentProbe.Unavailable;
            }
            return payloadProbe == ExecutionRuntimeProcessProbe.Matching
                   && WindowsJob.ContainsProcess(job, lease.ProcessId.Value)
                ? ExecutionRuntimeContainmentProbe.Active
                : ExecutionRuntimeContainmentProbe.IdentityMismatch;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return ExecutionRuntimeContainmentProbe.Unavailable;
        }
        if (!string.Equals(lease.ContainmentKind, UnixProcessGroupKind, StringComparison.Ordinal)
            || !int.TryParse(
                lease.ContainmentIdentity,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var processGroupId)
            || processGroupId != lease.ContainmentProcessId)
        {
            return ExecutionRuntimeContainmentProbe.IdentityMismatch;
        }
        var guardian = ProbeUnixAnchor(
            processGroupId,
            lease.ContainmentProcessId!.Value,
            lease.ContainmentProcessBirthIdentity!,
            lease.ContainmentExecutablePath!);
        var payload = lease.IsAttached
            ? ProbeUnixAnchor(
                processGroupId,
                lease.ProcessId!.Value,
                lease.ProcessBirthIdentity!,
                lease.ExecutablePath!)
            : UnixAnchorProbe.Gone;
        if (guardian == UnixAnchorProbe.Unavailable || payload == UnixAnchorProbe.Unavailable)
        {
            return ExecutionRuntimeContainmentProbe.Unavailable;
        }
        if (guardian is UnixAnchorProbe.Detached or UnixAnchorProbe.IdentityMismatch
            || payload is UnixAnchorProbe.Detached or UnixAnchorProbe.IdentityMismatch)
        {
            return ExecutionRuntimeContainmentProbe.IdentityMismatch;
        }
        try
        {
            var groupAlive = UnixProcessGroups.IsAlive(processGroupId);
            if (guardian == UnixAnchorProbe.Member || payload == UnixAnchorProbe.Member)
            {
                return groupAlive
                    ? ExecutionRuntimeContainmentProbe.Active
                    : ExecutionRuntimeContainmentProbe.Unavailable;
            }
            return groupAlive
                ? ExecutionRuntimeContainmentProbe.IdentityMismatch
                : ExecutionRuntimeContainmentProbe.Empty;
        }
        catch (Win32Exception)
        {
            return ExecutionRuntimeContainmentProbe.Unavailable;
        }
    }

    public async Task<bool> TerminateAndDrainAsync(
        ExecutionRuntimeProcessLeaseV1 lease,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var probe = Probe(lease);
        if (probe == ExecutionRuntimeContainmentProbe.Empty)
        {
            return true;
        }
        if (probe != ExecutionRuntimeContainmentProbe.Active)
        {
            return await ConfirmEmptyAfterTerminalRaceAsync(lease).ConfigureAwait(false);
        }

        if (OperatingSystem.IsWindows())
        {
            using var job = WindowsJob.TryOpen(lease.ContainmentIdentity!, out _);
            if (job is null)
            {
                return await ConfirmEmptyAfterTerminalRaceAsync(lease).ConfigureAwait(false);
            }
            await TerminateWindowsJobAsync(job, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            if (!await TerminateUnixGroupAsync(lease, CancellationToken.None).ConfigureAwait(false))
            {
                return await ConfirmEmptyAfterTerminalRaceAsync(lease).ConfigureAwait(false);
            }
        }
        if (!await WaitForAttachedProcessExitAsync(lease).ConfigureAwait(false))
        {
            return await ConfirmEmptyAfterTerminalRaceAsync(lease).ConfigureAwait(false);
        }
        return await ConfirmEmptyAfterTerminalRaceAsync(lease).ConfigureAwait(false);
    }

    private async Task<bool> ConfirmEmptyAfterTerminalRaceAsync(ExecutionRuntimeProcessLeaseV1 lease)
    {
        var started = Stopwatch.GetTimestamp();
        do
        {
            if (Probe(lease) == ExecutionRuntimeContainmentProbe.Empty)
            {
                return true;
            }
            await Task.Delay(25, CancellationToken.None).ConfigureAwait(false);
        }
        while (Stopwatch.GetElapsedTime(started) < TerminalObservationTimeout);
        return Probe(lease) == ExecutionRuntimeContainmentProbe.Empty;
    }

    private async Task<bool> WaitForAttachedProcessExitAsync(ExecutionRuntimeProcessLeaseV1 lease)
    {
        if (!lease.IsAttached)
        {
            return true;
        }
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var probe = processes.Probe(
                lease.ProcessId!.Value,
                lease.ProcessBirthIdentity!,
                lease.ExecutablePath!);
            if (probe is ExecutionRuntimeProcessProbe.Missing or ExecutionRuntimeProcessProbe.BirthMismatch)
            {
                return true;
            }
            if (probe is ExecutionRuntimeProcessProbe.AttachmentMismatch
                or ExecutionRuntimeProcessProbe.Unavailable
                || Stopwatch.GetElapsedTime(started) >= TerminationTimeout)
            {
                return false;
            }
            await Task.Delay(25, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static ProcessStartInfo CreateGuardianStartInfo(
        Guid generation,
        ProcessStartInfo payload,
        string payloadReportPath)
    {
        var guardianExecutable = GetGuardianExecutablePath();
        var startInfo = new ProcessStartInfo
        {
            FileName = guardianExecutable,
            WorkingDirectory = payload.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment.Clear();
        foreach (var pair in payload.Environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
        startInfo.ArgumentList.Add(ExecutionRuntimeProcessGuardian.InvocationArgument);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(generation.ToString("N"));
        startInfo.ArgumentList.Add(payloadReportPath);
        startInfo.ArgumentList.Add(payload.FileName);
        startInfo.ArgumentList.Add(payload.ArgumentList.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var argument in payload.ArgumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static string GetGuardianExecutablePath()
    {
        var assemblyPath = typeof(SystemExecutionRuntimeProcessContainmentController).Assembly.Location;
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            throw new InvalidOperationException("The Runtime Host assembly location is unavailable.");
        }
        var executable = Path.Combine(
            Path.GetDirectoryName(assemblyPath)!,
            OperatingSystem.IsWindows() ? "Sunder.Runtime.Host.exe" : "Sunder.Runtime.Host");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                "The bundled Runtime Host process guardian executable is unavailable.",
                executable);
        }
        return executable;
    }

    private static async Task WaitForUnixProcessGroupAsync(
        Process guardian,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (guardian.HasExited)
            {
                throw new IOException("The execution-runtime process guardian exited before containment was established.");
            }
            if (UnixProcessGroups.GetProcessGroupId(guardian.Id) == guardian.Id)
            {
                return;
            }
            if (Stopwatch.GetElapsedTime(started) >= LaunchTimeout)
            {
                throw new TimeoutException("The execution-runtime process guardian did not establish its process group in time.");
            }
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private ExecutionRuntimeContainmentProbe MapActiveGuardianProbe(
        ExecutionRuntimeProcessLeaseV1 lease)
        => ProbeGuardian(lease) switch
        {
            ExecutionRuntimeProcessProbe.Matching => ExecutionRuntimeContainmentProbe.Active,
            ExecutionRuntimeProcessProbe.Unavailable => ExecutionRuntimeContainmentProbe.Unavailable,
            _ => ExecutionRuntimeContainmentProbe.IdentityMismatch,
        };

    private ExecutionRuntimeProcessProbe ProbeGuardian(ExecutionRuntimeProcessLeaseV1 lease)
        => processes.Probe(
            lease.ContainmentProcessId!.Value,
            lease.ContainmentProcessBirthIdentity!,
            lease.ContainmentExecutablePath!);

    private async Task<bool> TerminateUnixGroupAsync(
        ExecutionRuntimeProcessLeaseV1 lease,
        CancellationToken cancellationToken)
    {
        var processGroupId = lease.ContainmentProcessId!.Value;
        if (!HasVerifiedUnixGroupAnchor(lease, processGroupId))
        {
            return false;
        }
        UnixProcessGroups.Signal(processGroupId, UnixProcessGroups.SignalKill);
        return await WaitForUnixGroupExitAsync(processGroupId, cancellationToken).ConfigureAwait(false);
    }

    private UnixAnchorProbe ProbeUnixAnchor(
        int processGroupId,
        int processId,
        string processBirthIdentity,
        string executablePath)
    {
        var process = processes.Probe(processId, processBirthIdentity, executablePath);
        if (process is ExecutionRuntimeProcessProbe.Missing or ExecutionRuntimeProcessProbe.BirthMismatch)
        {
            return UnixAnchorProbe.Gone;
        }
        if (process == ExecutionRuntimeProcessProbe.AttachmentMismatch)
        {
            return UnixAnchorProbe.IdentityMismatch;
        }
        if (process == ExecutionRuntimeProcessProbe.Unavailable)
        {
            return UnixAnchorProbe.Unavailable;
        }
        try
        {
            var currentGroup = UnixProcessGroups.GetProcessGroupId(processId);
            return currentGroup switch
            {
                -1 => UnixAnchorProbe.Gone,
                _ when currentGroup == processGroupId => UnixAnchorProbe.Member,
                _ => UnixAnchorProbe.Detached,
            };
        }
        catch (Win32Exception)
        {
            return UnixAnchorProbe.Unavailable;
        }
    }

    private bool HasVerifiedUnixGroupAnchor(
        ExecutionRuntimeProcessLeaseV1 lease,
        int processGroupId)
    {
        try
        {
            if (ProbeGuardian(lease) == ExecutionRuntimeProcessProbe.Matching
                && UnixProcessGroups.GetProcessGroupId(lease.ContainmentProcessId!.Value) == processGroupId)
            {
                return true;
            }
            return lease.IsAttached
                   && processes.Probe(
                       lease.ProcessId!.Value,
                       lease.ProcessBirthIdentity!,
                       lease.ExecutablePath!) == ExecutionRuntimeProcessProbe.Matching
                   && UnixProcessGroups.GetProcessGroupId(lease.ProcessId.Value) == processGroupId;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForUnixGroupExitAsync(
        int processGroupId,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (UnixProcessGroups.IsAlive(processGroupId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= TerminationTimeout)
            {
                return false;
            }
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    private static async Task TerminateWindowsJobAsync(
        SafeFileHandle job,
        CancellationToken cancellationToken)
    {
        WindowsJob.Terminate(job);
        var started = Stopwatch.GetTimestamp();
        while (WindowsJob.GetActiveProcessCount(job) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= TerminationTimeout)
            {
                throw new TimeoutException("The execution-runtime Windows Job did not terminate in time.");
            }
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void TryTerminateWindowsJob(SafeFileHandle job)
    {
        try
        {
            WindowsJob.Terminate(job);
        }
        catch (Exception exception) when (exception is Win32Exception or ObjectDisposedException)
        {
        }
    }

    private static void TryTerminateUnixGroup(int processGroupId)
    {
        try
        {
            UnixProcessGroups.Signal(processGroupId, UnixProcessGroups.SignalKill);
        }
        catch (Win32Exception)
        {
        }
    }

    private sealed class SystemPendingContainedProcess :
        IExecutionRuntimePendingContainedProcess,
        IExecutionRuntimeContainedProcess
    {
        private readonly SystemExecutionRuntimeProcessContainmentController _controller;
        private readonly IExecutionRuntimeProcessController _processes;
        private readonly Guid _generation;
        private readonly string _expectedPayloadExecutable;
        private readonly string _payloadReportPath;
        private readonly Process _guardian;
        private readonly SafeFileHandle? _job;
        private readonly object _terminationGate = new();
        private Task? _termination;
        private Process? _payload;
        private ExecutionRuntimeProcessObservation? _payloadObservation;
        private int _disposed;

        public SystemPendingContainedProcess(
            SystemExecutionRuntimeProcessContainmentController controller,
            IExecutionRuntimeProcessController processes,
            Guid generation,
            string kind,
            string identity,
            ExecutionRuntimeProcessObservation containmentProcess,
            string expectedPayloadExecutable,
            string payloadReportPath,
            Process guardian,
            SafeFileHandle? job)
        {
            _controller = controller;
            _processes = processes;
            _generation = generation;
            Kind = kind;
            Identity = identity;
            ContainmentProcess = containmentProcess;
            _expectedPayloadExecutable = Path.GetFullPath(expectedPayloadExecutable);
            _payloadReportPath = payloadReportPath;
            _guardian = guardian;
            _job = job;
        }

        public string Kind { get; }
        public string Identity { get; }
        public ExecutionRuntimeProcessObservation ContainmentProcess { get; }
        public Process Process => _payload
                                  ?? throw new InvalidOperationException("The contained payload has not started.");
        public Stream StandardInput => _guardian.StandardInput.BaseStream;
        public Stream StandardOutput => _guardian.StandardOutput.BaseStream;
        public Stream StandardError => _guardian.StandardError.BaseStream;

        public async Task<IExecutionRuntimeContainedProcess> AuthorizePayloadAsync(
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_payload is not null)
            {
                throw new InvalidOperationException("The execution-runtime payload was already authorized.");
            }

            var authorization = Encoding.ASCII.GetBytes(_generation.ToString("N") + "\n");
            await _guardian.StandardInput.BaseStream.WriteAsync(authorization, cancellationToken)
                .ConfigureAwait(false);
            await _guardian.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            var started = Stopwatch.GetTimestamp();
            while (!File.Exists(_payloadReportPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_guardian.HasExited)
                {
                    throw new IOException("The execution-runtime process guardian exited before starting its payload.");
                }
                if (Stopwatch.GetElapsedTime(started) >= LaunchTimeout)
                {
                    throw new TimeoutException("The execution-runtime process guardian did not report its payload in time.");
                }
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }

            var report = await ReadPayloadReportAsync(_payloadReportPath, cancellationToken).ConfigureAwait(false);
            if (report.SchemaVersion != ExecutionRuntimeGuardianPayloadReportV1.CurrentSchemaVersion
                || report.Generation != _generation
                || report.ProcessId <= 0
                || string.IsNullOrWhiteSpace(report.ProcessBirthIdentity))
            {
                throw new InvalidDataException("The execution-runtime guardian payload report is invalid.");
            }

            Process payload;
            try
            {
                payload = Process.GetProcessById(report.ProcessId);
            }
            catch (ArgumentException exception)
            {
                var diagnostic = _guardian.HasExited
                    ? await _guardian.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false)
                    : string.Empty;
                throw new IOException(
                    $"The execution-runtime payload exited before durable attachment. {diagnostic}".Trim(),
                    exception);
            }
            payload.EnableRaisingEvents = true;
            var observation = _processes.Observe(payload);
            if (!string.Equals(
                    observation.BirthIdentity,
                    report.ProcessBirthIdentity,
                    StringComparison.Ordinal))
            {
                payload.Dispose();
                throw new InvalidDataException(
                    "The execution-runtime guardian payload PID no longer has its reported birth identity.");
            }
            var containmentMatches = _job is not null
                ? WindowsJob.ContainsProcess(_job, report.ProcessId)
                : UnixProcessGroups.GetProcessGroupId(report.ProcessId) == ContainmentProcess.ProcessId;
            if (!PathsEqual(observation.ExecutablePath, _expectedPayloadExecutable)
                || !containmentMatches)
            {
                try
                {
                    if (!payload.HasExited)
                    {
                        payload.Kill();
                        await payload.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally
                {
                    payload.Dispose();
                }
                throw new InvalidDataException(
                    "The execution-runtime guardian reported a payload outside its exact containment identity.");
            }
            _payload = payload;
            _payloadObservation = observation;
            return this;
        }

        public Task TerminateAndDrainAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_terminationGate)
            {
                if (_termination is null || _termination.IsFaulted || _termination.IsCanceled)
                {
                    _termination = TerminateCoreAsync(cancellationToken);
                }
                return _termination;
            }
        }

        private async Task TerminateCoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_job is not null)
            {
                await TerminateWindowsJobAsync(_job, CancellationToken.None).ConfigureAwait(false);
                if (_payload is { HasExited: false })
                {
                    await _payload.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                if (!_guardian.HasExited)
                {
                    await _guardian.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                return;
            }

            var payload = _payloadObservation;
            var lease = new ExecutionRuntimeProcessLeaseV1(
                ExecutionRuntimeProcessLeaseV1.CurrentSchemaVersion,
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid(),
                "containment.guardian",
                "containment",
                Guid.NewGuid(),
                new string('a', 64),
                DateTimeOffset.UtcNow,
                ProcessStartRequestedAtUtc: DateTimeOffset.UtcNow,
                ContainmentGeneration: _generation,
                ContainmentKind: Kind,
                ContainmentIdentity: Identity,
                ContainmentProcessId: ContainmentProcess.ProcessId,
                ContainmentProcessBirthIdentity: ContainmentProcess.BirthIdentity,
                ContainmentProcessStartedAtUtc: ContainmentProcess.StartedAtUtc,
                ContainmentExecutablePath: ContainmentProcess.ExecutablePath,
                ContainmentAttachedAtUtc: DateTimeOffset.UtcNow,
                ProcessId: payload?.ProcessId,
                ProcessBirthIdentity: payload?.BirthIdentity,
                ProcessStartedAtUtc: payload?.StartedAtUtc,
                ExecutablePath: payload?.ExecutablePath,
                RuntimeRootPath: payload is null ? null : Path.GetDirectoryName(payload.ExecutablePath),
                AttachedAtUtc: payload is null ? null : DateTimeOffset.UtcNow);
            if (!await _controller.TerminateAndDrainAsync(lease, cancellationToken).ConfigureAwait(false))
            {
                throw new IOException("The execution-runtime process containment could not be drained.");
            }
            if (_payload is { HasExited: false })
            {
                await _payload.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            if (!_guardian.HasExited)
            {
                await _guardian.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }
            _payload?.Dispose();
            _guardian.Dispose();
            _job?.Dispose();
            return ValueTask.CompletedTask;
        }

        private static async Task<ExecutionRuntimeGuardianPayloadReportV1> ReadPayloadReportAsync(
            string path,
            CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > 16 * 1024)
            {
                throw new InvalidDataException("The execution-runtime guardian payload report has an invalid size.");
            }
            return await JsonSerializer.DeserializeAsync<ExecutionRuntimeGuardianPayloadReportV1>(
                       stream,
                       ExecutionRuntimeStore.JsonOptions,
                       cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidDataException("The execution-runtime guardian payload report is empty.");
        }

        private static bool PathsEqual(string left, string right)
            => string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static class WindowsJob
    {
        private const uint JobObjectQuery = 0x0004;
        private const uint JobObjectTerminate = 0x0008;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectBasicAccountingInformationClass = 1;
        private const int JobObjectExtendedLimitInformationClass = 9;
        private const int ErrorFileNotFound = 2;
        private const int ErrorInvalidHandle = 6;
        private const int ErrorAlreadyExists = 183;

        public static SafeFileHandle Create(string name)
        {
            var handle = CreateJobObject(IntPtr.Zero, name);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create a Worker Job.");
            }
            if (Marshal.GetLastWin32Error() == ErrorAlreadyExists)
            {
                handle.Dispose();
                throw new IOException($"Execution-runtime Worker Job '{name}' already exists.");
            }

            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
                if (!SetInformationJobObject(
                        handle,
                        JobObjectExtendedLimitInformationClass,
                        buffer,
                        (uint)size))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows could not configure the Worker Job lifetime policy.");
                }
            }
            catch
            {
                handle.Dispose();
                throw;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return handle;
        }

        public static SafeFileHandle? TryOpen(string name, out bool unavailable)
        {
            var handle = OpenJobObject(JobObjectQuery | JobObjectTerminate, false, name);
            if (!handle.IsInvalid)
            {
                unavailable = false;
                return handle;
            }
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            unavailable = error is not (ErrorFileNotFound or ErrorInvalidHandle);
            return null;
        }

        public static void Assign(SafeFileHandle job, SafeProcessHandle process)
        {
            if (!AssignProcessToJobObject(job, process))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not assign the execution-runtime guardian to its Worker Job.");
            }
        }

        public static int GetActiveProcessCount(SafeFileHandle job)
        {
            var size = Marshal.SizeOf<JobObjectBasicAccountingInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!QueryInformationJobObject(
                        job,
                        JobObjectBasicAccountingInformationClass,
                        buffer,
                        (uint)size,
                        out _))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows could not inspect the execution-runtime Worker Job.");
                }
                return checked((int)Marshal.PtrToStructure<JobObjectBasicAccountingInformation>(buffer).ActiveProcesses);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public static bool ContainsProcess(SafeFileHandle job, int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!IsProcessInJob(process.SafeHandle, job, out var inJob))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows could not verify execution-runtime Worker Job membership.");
                }
                return inJob;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public static void Terminate(SafeFileHandle job)
        {
            if (!TerminateJobObject(job, 1))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not terminate the execution-runtime Worker Job.");
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle OpenJobObject(uint desiredAccess, bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            SafeFileHandle job,
            int informationClass,
            IntPtr information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryInformationJobObject(
            SafeFileHandle job,
            int informationClass,
            IntPtr information,
            uint informationLength,
            out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(
            SafeFileHandle job,
            SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsProcessInJob(
            SafeProcessHandle process,
            SafeFileHandle job,
            [MarshalAs(UnmanagedType.Bool)] out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicAccountingInformation
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }
    }
}

internal static class UnixProcessGroups
{
    public const int SignalKill = 9;
    private const int ErrorNoSuchProcess = 3;

    public static int CreateSession()
    {
        var result = setsid();
        return result >= 0
            ? result
            : throw new Win32Exception(Marshal.GetLastPInvokeError(), "The process guardian could not create a Unix session.");
    }

    public static int GetParentProcessId() => getppid();

    public static int GetProcessGroupId(int processId)
    {
        var result = getpgid(processId);
        if (result >= 0)
        {
            return result;
        }
        var error = Marshal.GetLastPInvokeError();
        return error == ErrorNoSuchProcess
            ? -1
            : throw new Win32Exception(error, "The execution-runtime process group could not be inspected.");
    }

    public static bool IsAlive(int processGroupId)
        => HasMemberOtherThan(processGroupId, excludedProcessId: null);

    public static bool HasMemberOtherThan(int processGroupId, int? excludedProcessId)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != excludedProcessId
                        && GetProcessGroupId(process.Id) == processGroupId
                        && !process.HasExited)
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorNoSuchProcess)
                {
                }
            }
        }
        return false;
    }

    public static void Signal(int processGroupId, int signal)
    {
        if (kill(-processGroupId, signal) == 0)
        {
            return;
        }
        var error = Marshal.GetLastPInvokeError();
        if (error != ErrorNoSuchProcess)
        {
            throw new Win32Exception(error, "The execution-runtime process group could not be signaled.");
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc")]
    private static extern int getppid();

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int processId);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);
}
