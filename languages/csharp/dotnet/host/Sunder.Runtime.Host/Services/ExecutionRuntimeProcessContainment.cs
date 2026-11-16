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

internal sealed partial class SystemExecutionRuntimeProcessContainmentController(
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
        ExecutionRuntimeProcessOutput? standardOutput = null;
        ExecutionRuntimeProcessOutput? standardError = null;
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

            if (OperatingSystem.IsWindows())
            {
                standardOutput = new ExecutionRuntimeProcessOutput(guardian.StandardOutput.BaseStream, "sunder-worker-stdout");
                standardError = new ExecutionRuntimeProcessOutput(guardian.StandardError.BaseStream, "sunder-worker-stderr");
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
                jobHandle,
                standardOutput,
                standardError);
            guardian = null;
            jobHandle = null;
            standardOutput = null;
            standardError = null;
            return pending;
        }
        catch
        {
            standardOutput?.Stop();
            standardError?.Stop();
            if (jobHandle is not null && containmentEstablished)
            {
                TryTerminateWindowsJob(jobHandle);
            }
            else if (containmentEstablished
                     && guardian is { HasExited: false }
                     && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            {
                TryTerminateUnixGroup(guardian.Id);
            }
            if (guardian is not null)
            {
                try
                {
                    // Start can fail before the Process has an associated handle.
                    // Probe only inside cleanup's guarded block, never mask the
                    // native launch error with HasExited's InvalidOperationException.
                    if (!guardian.HasExited)
                    {
                        guardian.Kill(entireProcessTree: true);
                        await guardian.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                {
                }
            }
            guardian?.Dispose();
            jobHandle?.Dispose();
            if (standardOutput is not null) await standardOutput.Drained.ConfigureAwait(false);
            if (standardError is not null) await standardError.Drained.ConfigureAwait(false);
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
        ExecutionRuntimeGuardianEnvironment.Configure(
            startInfo.Environment,
            RuntimeEnvironment.GetRuntimeDirectory());
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
                throw new ExecutionRuntimeGuardianLaunchException(guardian.ExitCode);
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

    internal static async Task<ExecutionRuntimeGuardianPayloadReportV1> ReadPayloadReportAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            // The published name can be observed before the guardian's atomic
            // rename closes its DELETE handle. Keep that rename compatible while
            // retaining this exact read handle and still denying concurrent writes.
            FileShare.Read | FileShare.Delete,
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
        private readonly ExecutionRuntimeProcessOutput? _standardOutput;
        private readonly ExecutionRuntimeProcessOutput? _standardError;
        private readonly object _terminationGate = new();
        private Task? _termination;
        private Task? _disposal;
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
            SafeFileHandle? job,
            ExecutionRuntimeProcessOutput? standardOutput,
            ExecutionRuntimeProcessOutput? standardError)
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
            _standardOutput = standardOutput;
            _standardError = standardError;
        }

        public string Kind { get; }
        public string Identity { get; }
        public ExecutionRuntimeProcessObservation ContainmentProcess { get; }
        public Process Process => _payload
                                  ?? throw new InvalidOperationException("The contained payload has not started.");
        public Stream StandardInput => _guardian.StandardInput.BaseStream;
        public Stream StandardOutput => _standardOutput?.Stream ?? _guardian.StandardOutput.BaseStream;
        public Stream StandardError => _standardError?.Stream ?? _guardian.StandardError.BaseStream;

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
                    throw new ExecutionRuntimeGuardianLaunchException(_guardian.ExitCode);
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
                var diagnostic = string.Empty;
                if (_guardian.HasExited)
                {
                    using var reader = new StreamReader(StandardError, _guardian.StandardError.CurrentEncoding,
                        detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                    diagnostic = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                }
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
            lock (_terminationGate)
            {
                return new ValueTask(_disposal ??= DisposeCoreAsync());
            }
        }

        private async Task DisposeCoreAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            _standardOutput?.Stop();
            _standardError?.Stop();
            _payload?.Dispose();
            _guardian.Dispose();
            _job?.Dispose();
            // Closing the exact containment ends native pipe reads. Do not detach
            // their tasks or call a canceled consumer read proof of pump retirement.
            if (_standardOutput is not null) await _standardOutput.Drained.ConfigureAwait(false);
            if (_standardError is not null) await _standardError.Drained.ConfigureAwait(false);
        }

        private static bool PathsEqual(string left, string right)
            => string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

}
