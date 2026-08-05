using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Sunder.App.Services;

[SupportedOSPlatform("windows")]
internal sealed class WindowsBreakawayRuntimeLauncher : RuntimePersistentLauncherBase
{
    private const string Backend = "windows-job";
    private const string ServiceName = "sunder-host";
    private const string DefaultJobName = @"Local\Younics.Sunder.Host";
    private const string DefaultLaunchMutexName = @"Local\Younics.Sunder.Host.Launch";
    private const int MarkerVersion = 1;
    private const uint CreateSuspended = 0x00000004;
    private const uint DetachedProcess = 0x00000008;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateDefaultErrorMode = 0x04000000;
    private const uint JobObjectLimitBreakawayOk = 0x00000800;
    private const uint JobObjectLimitSilentBreakawayOk = 0x00001000;
    private const uint Synchronize = 0x00100000;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint ProcessTerminate = 0x00000001;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint Infinite = 0xffffffff;
    private const int JobObjectBasicAccountingInformationClass = 1;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private static readonly IntPtr ProcThreadAttributeJobList = new(0x0002000D);
    private static readonly TimeSpan ReconciliationTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<int, WindowsProcessLease> _processes = new();
    private readonly string _markerPath;
    private readonly string _jobName;
    private readonly string _launchMutexName;
    private IntPtr _jobHandle;

    public WindowsBreakawayRuntimeLauncher(
        string? markerPath = null,
        string? jobName = null,
        string? launchMutexName = null)
    {
        _markerPath = Path.GetFullPath(markerPath ?? GetDefaultMarkerPath());
        _jobName = jobName ?? DefaultJobName;
        _launchMutexName = launchMutexName ?? DefaultLaunchMutexName;
    }

    public override Task<HostServiceLaunchReceipt> ReconcileAndLaunchAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var launchMutex = new Mutex(initiallyOwned: false, _launchMutexName);
        AcquireLaunchMutex(launchMutex, cancellationToken);
        var existingServiceDisplaced = false;
        try
        {
            var jobHandle = EnsureJobHandle();
            var existing = TryAdoptPersistedProcess(jobHandle);
            if (existing is not null)
            {
                if (!replaceExisting && existing.IsRunning)
                {
                    Track(existing);
                    return Task.FromResult(new HostServiceLaunchReceipt(
                        Backend,
                        ServiceName,
                        existing.ProcessId,
                        existing.Generation.ToString("N")));
                }
                existing.Dispose();
            }
            ClearTrackedProcesses();

            var activeProcesses = GetActiveJobProcessCount(jobHandle);
            if (activeProcesses > 0)
            {
                if (!replaceExisting)
                {
                    throw new InvalidOperationException(
                        "The Sunder Host Windows job still contains a process whose deployment identity could not be verified.");
                }
                TerminateAndDrainJob(
                    jobHandle,
                    cancellationToken,
                    () => existingServiceDisplaced = true);
            }
            DeleteMarker();

            var executable = RuntimeLauncherProcess.ResolveExecutable(startInfo.FileName);
            if (!Path.IsPathFullyQualified(executable))
            {
                throw new InvalidOperationException(
                    "The Sunder Host executable could not be resolved to an absolute path on Windows.");
            }
            var flags = CreateSuspended
                        | DetachedProcess
                        | CreateUnicodeEnvironment
                        | ExtendedStartupInfoPresent
                        | CreateDefaultErrorMode
                        | GetBreakawayCreationFlag();
            var commandLine = new StringBuilder(string.Join(
                ' ',
                new[] { executable }.Concat(startInfo.ArgumentList).Select(QuoteWindowsArgument)));
            var environment = CreateEnvironmentBlock(startInfo);
            ProcessInformation processInformation;
            try
            {
                processInformation = CreateProcessInJob(
                    executable,
                    commandLine,
                    flags,
                    environment,
                    startInfo.WorkingDirectory,
                    jobHandle);
            }
            finally
            {
                Marshal.FreeHGlobal(environment);
            }

            var processHandle = processInformation.Process;
            var threadHandle = processInformation.Thread;
            try
            {
                if (!IsProcessInJob(processHandle, jobHandle, out var inSunderJob))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows could not inspect the suspended Sunder Host process.");
                }
                if (!inSunderJob)
                {
                    throw new InvalidOperationException(
                        "Windows did not create the Sunder Host process inside its durable job.");
                }
                var creationTime = GetCreationTime(processHandle);
                var generation = Guid.NewGuid();
                var marker = new WindowsHostProcessMarker(
                    MarkerVersion,
                    generation,
                    processInformation.ProcessId,
                    creationTime,
                    executable,
                    "suspended");
                WriteMarker(marker);
                if (ResumeThread(threadHandle) == uint.MaxValue)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows could not resume the Sunder Host process.");
                }
                marker = marker with { Phase = "running" };
                WriteMarker(marker);
                var lease = new WindowsProcessLease(
                    processInformation.ProcessId,
                    processHandle,
                    creationTime,
                    executable,
                    generation);
                processHandle = IntPtr.Zero;
                Track(lease);
                return Task.FromResult(new HostServiceLaunchReceipt(
                    Backend,
                    ServiceName,
                    processInformation.ProcessId,
                    generation.ToString("N")));
            }
            catch
            {
                if (processHandle != IntPtr.Zero)
                {
                    TerminateProcess(processHandle, 1);
                }
                DeleteMarker();
                throw;
            }
            finally
            {
                CloseHandle(threadHandle);
                if (processHandle != IntPtr.Zero)
                {
                    CloseHandle(processHandle);
                }
            }
        }
        catch (Exception exception) when (
            existingServiceDisplaced && exception is not IHostServiceReplacementFailure)
        {
            throw HostServiceReplacementFailure.Wrap(exception);
        }
        finally
        {
            launchMutex.ReleaseMutex();
        }
    }

    public override Task<HostServiceObservation> ObserveAsync(
        HostServiceLaunchReceipt receipt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var launchMutex = new Mutex(initiallyOwned: false, _launchMutexName);
        AcquireLaunchMutex(launchMutex, cancellationToken);
        try
        {
            return Task.FromResult(Observe(receipt));
        }
        finally
        {
            launchMutex.ReleaseMutex();
        }
    }

    private HostServiceObservation Observe(HostServiceLaunchReceipt receipt)
    {
        if (receipt.ProcessId is not { } processId)
        {
            return HostServiceObservation.Unknown();
        }
        if (!_processes.TryGetValue(processId, out var lease))
        {
            lease = TryAdoptPersistedProcess(EnsureJobHandle());
            if (lease is null || lease.ProcessId != processId)
            {
                lease?.Dispose();
                return new HostServiceObservation(
                    HostServiceState.Absent,
                    processId,
                    result: "windows-process-not-found",
                    diagnosticPaths: [_markerPath]);
            }
            Track(lease);
        }

        var wait = WaitForSingleObject(lease.ProcessHandle, 0);
        if (wait == WaitTimeout)
        {
            return new HostServiceObservation(HostServiceState.Running, processId);
        }
        if (wait != WaitObject0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not observe the Sunder Host process.");
        }
        if (!GetExitCodeProcess(lease.ProcessHandle, out var rawExitCode))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not read the Sunder Host process exit code.");
        }

        RemoveTrackedProcess(lease);
        var exitCode = unchecked((int)rawExitCode);
        return new HostServiceObservation(
            exitCode == 0 ? HostServiceState.Stopped : HostServiceState.Failed,
            processId,
            exitCode,
            result: "windows-process-exit",
            diagnosticPaths: [_markerPath]);
    }

    public override Task<bool> TryStopAsync(
        HostServiceLaunchReceipt receipt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var launchMutex = new Mutex(initiallyOwned: false, _launchMutexName);
        AcquireLaunchMutex(launchMutex, cancellationToken);
        try
        {
            var jobHandle = EnsureJobHandle();
            var marker = ReadMarker();
            if (marker is null)
            {
                return Task.FromResult(GetActiveJobProcessCount(jobHandle) == 0);
            }
            if (!Guid.TryParseExact(receipt.InstanceId, "N", out var generation)
                || marker.Generation != generation)
            {
                return Task.FromResult(false);
            }
            if (GetActiveJobProcessCount(jobHandle) > 0)
            {
                TerminateAndDrainJob(jobHandle, cancellationToken);
            }
            ClearTrackedProcesses();
            DeleteMarkerIfMatches(generation);
            return Task.FromResult(true);
        }
        finally
        {
            launchMutex.ReleaseMutex();
        }
    }

    public override void Dispose()
    {
        foreach (var pair in _processes)
        {
            if (_processes.TryRemove(pair.Key, out var lease))
            {
                lease.Dispose();
            }
        }
        var jobHandle = Interlocked.Exchange(ref _jobHandle, IntPtr.Zero);
        if (jobHandle != IntPtr.Zero)
        {
            CloseHandle(jobHandle);
        }
    }

    private IntPtr EnsureJobHandle()
    {
        var current = Volatile.Read(ref _jobHandle);
        if (current != IntPtr.Zero)
        {
            return current;
        }
        var created = CreateJobObject(IntPtr.Zero, _jobName);
        if (created == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not open the Sunder Host job.");
        }
        var winner = Interlocked.CompareExchange(ref _jobHandle, created, IntPtr.Zero);
        if (winner != IntPtr.Zero)
        {
            CloseHandle(created);
            return winner;
        }
        return created;
    }

    private void Track(WindowsProcessLease lease)
    {
        if (_processes.TryAdd(lease.ProcessId, lease))
        {
            return;
        }
        lease.Dispose();
    }

    private void ClearTrackedProcesses()
    {
        foreach (var pair in _processes)
        {
            if (_processes.TryRemove(pair.Key, out var lease))
            {
                lease.Dispose();
            }
        }
    }

    private void RemoveTrackedProcess(WindowsProcessLease lease)
    {
        if (_processes.TryRemove(
                new KeyValuePair<int, WindowsProcessLease>(lease.ProcessId, lease)))
        {
            lease.Dispose();
        }
        DeleteMarkerIfMatches(lease.Generation);
    }

    private WindowsProcessLease? TryAdoptPersistedProcess(IntPtr jobHandle)
    {
        var marker = ReadMarker();
        if (marker is null || marker.Version != MarkerVersion || marker.ProcessId <= 0)
        {
            return null;
        }
        var processHandle = OpenProcess(
            Synchronize | ProcessQueryLimitedInformation | ProcessTerminate,
            false,
            marker.ProcessId);
        if (processHandle == IntPtr.Zero)
        {
            DeleteMarkerIfMatches(marker.Generation);
            return null;
        }
        try
        {
            if (GetCreationTime(processHandle) != marker.CreationTimeFileTimeUtc
                || !PathEquals(GetProcessImagePath(processHandle), marker.ExecutablePath)
                || !IsProcessInJob(processHandle, jobHandle, out var inJob)
                || !inJob)
            {
                DeleteMarkerIfMatches(marker.Generation);
                return null;
            }
            if (!string.Equals(marker.Phase, "running", StringComparison.Ordinal))
            {
                if (!TerminateProcess(processHandle, 1))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows could not terminate an interrupted Sunder Host launch.");
                }
                if (WaitForSingleObject(
                        processHandle,
                        (uint)ReconciliationTimeout.TotalMilliseconds) != WaitObject0)
                {
                    throw new TimeoutException(
                        "An interrupted Sunder Host launch did not terminate in time.");
                }
                DeleteMarkerIfMatches(marker.Generation);
                return null;
            }
            var lease = new WindowsProcessLease(
                marker.ProcessId,
                processHandle,
                marker.CreationTimeFileTimeUtc,
                marker.ExecutablePath,
                marker.Generation);
            processHandle = IntPtr.Zero;
            return lease;
        }
        finally
        {
            if (processHandle != IntPtr.Zero)
            {
                CloseHandle(processHandle);
            }
        }
    }

    private static uint GetBreakawayCreationFlag()
    {
        if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not inspect the Sunder App process job.");
        }
        if (!inJob)
        {
            return 0;
        }

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(
                    IntPtr.Zero,
                    JobObjectExtendedLimitInformationClass,
                    buffer,
                    (uint)size,
                    out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not inspect the enclosing Sunder App job.");
            }
            var information = Marshal.PtrToStructure<JobObjectExtendedLimitInformation>(buffer);
            if ((information.BasicLimitInformation.LimitFlags & JobObjectLimitSilentBreakawayOk) != 0)
            {
                return 0;
            }
            if ((information.BasicLimitInformation.LimitFlags & JobObjectLimitBreakawayOk) != 0)
            {
                return CreateBreakawayFromJob;
            }
            throw new InvalidOperationException(
                "The Sunder App is running inside a Windows job that does not allow a durable Host process. Launch Sunder outside that job.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int GetActiveJobProcessCount(IntPtr jobHandle)
    {
        var size = Marshal.SizeOf<JobObjectBasicAccountingInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(
                    jobHandle,
                    JobObjectBasicAccountingInformationClass,
                    buffer,
                    (uint)size,
                    out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not inspect the Sunder Host job.");
            }
            return checked((int)Marshal.PtrToStructure<JobObjectBasicAccountingInformation>(buffer).ActiveProcesses);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void TerminateAndDrainJob(
        IntPtr jobHandle,
        CancellationToken cancellationToken,
        Action? terminated = null)
    {
        if (!TerminateJobObject(jobHandle, 1))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not terminate the previous Sunder Host job.");
        }
        terminated?.Invoke();
        var started = Stopwatch.GetTimestamp();
        while (GetActiveJobProcessCount(jobHandle) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= ReconciliationTimeout)
            {
                throw new TimeoutException(
                    $"The previous Sunder Host Windows job did not stop within {ReconciliationTimeout.TotalSeconds:0} seconds.");
            }
            Thread.Sleep(25);
        }
    }

    private static void AcquireLaunchMutex(Mutex mutex, CancellationToken cancellationToken)
    {
        try
        {
            var signaled = WaitHandle.WaitAny(
                [mutex, cancellationToken.WaitHandle],
                ReconciliationTimeout);
            if (signaled == 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (signaled == WaitHandle.WaitTimeout)
            {
                throw new TimeoutException("Another Sunder process is still reconciling the Windows Host job.");
            }
        }
        catch (AbandonedMutexException)
        {
        }
    }

    private WindowsHostProcessMarker? ReadMarker()
    {
        EnsureMarkerFileIsSafe(allowMissing: true);
        if (!File.Exists(_markerPath))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<WindowsHostProcessMarker>(
                File.ReadAllBytes(_markerPath),
                JsonOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            AppSessionLog.WriteError("Ignored an invalid Windows Host process marker.", exception);
            return null;
        }
    }

    private void WriteMarker(WindowsHostProcessMarker marker)
    {
        var directory = Path.GetDirectoryName(_markerPath)!;
        Directory.CreateDirectory(directory);
        EnsureMarkerDirectoryIsSafe(directory);
        EnsureMarkerFileIsSafe(allowMissing: true);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_markerPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(marker, JsonOptions);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _markerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void DeleteMarkerIfMatches(Guid generation)
    {
        if (ReadMarker()?.Generation == generation)
        {
            DeleteMarker();
        }
    }

    private void DeleteMarker()
    {
        if (File.Exists(_markerPath))
        {
            EnsureMarkerFileIsSafe();
            File.Delete(_markerPath);
        }
    }

    private static long GetCreationTime(IntPtr processHandle)
    {
        if (!GetProcessTimes(
                processHandle,
                out var creationTime,
                out _,
                out _,
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not read the Sunder Host process creation time.");
        }
        return creationTime.ToLong();
    }

    private static string GetProcessImagePath(IntPtr processHandle)
    {
        var capacity = 32768;
        var builder = new StringBuilder(capacity);
        if (!QueryFullProcessImageName(processHandle, 0, builder, ref capacity))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not read the Sunder Host process image path.");
        }
        return builder.ToString();
    }

    private static IntPtr CreateEnvironmentBlock(ProcessStartInfo startInfo)
    {
        var block = string.Join('\0', RuntimeLauncherProcess.GetServiceEnvironment(startInfo)
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => $"{pair.Key}={pair.Value}")) + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }

    private static ProcessInformation CreateProcessInJob(
        string executable,
        StringBuilder commandLine,
        uint creationFlags,
        IntPtr environment,
        string? workingDirectory,
        IntPtr jobHandle)
    {
        nuint attributeListSize = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
        if (attributeListSize == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not size the Sunder Host process attribute list.");
        }
        var attributeList = Marshal.AllocHGlobal(checked((nint)attributeListSize));
        var jobHandleValue = Marshal.AllocHGlobal(IntPtr.Size);
        var attributeListInitialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not initialize the Sunder Host process attribute list.");
            }
            attributeListInitialized = true;
            Marshal.WriteIntPtr(jobHandleValue, jobHandle);
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributeJobList,
                    jobHandleValue,
                    (nuint)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not bind the Sunder Host process to its durable job at creation.");
            }
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() },
                AttributeList = attributeList,
            };
            if (!CreateProcessExtended(
                    executable,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    creationFlags,
                    environment,
                    workingDirectory,
                    ref startup,
                    out var processInformation))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not create an independent Sunder Host process.");
            }
            return processInformation;
        }
        finally
        {
            if (attributeListInitialized)
            {
                DeleteProcThreadAttributeList(attributeList);
            }
            Marshal.FreeHGlobal(jobHandleValue);
            Marshal.FreeHGlobal(attributeList);
        }
    }

    internal static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0 && value.All(character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return value;
        }

        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append(character);
                backslashes = 0;
                continue;
            }
            builder.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void EnsureMarkerDirectoryIsSafe(string directory)
    {
        var item = new DirectoryInfo(directory);
        if (item.LinkTarget is not null
            || (item.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The Windows Host launcher state directory is a reparse point.");
        }
    }

    private void EnsureMarkerFileIsSafe(bool allowMissing = false)
    {
        var item = new FileInfo(_markerPath);
        if (allowMissing && !item.Exists && item.LinkTarget is null)
        {
            return;
        }
        if (item.LinkTarget is not null
            || item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The Windows Host process marker is a reparse point.");
        }
    }

    private static string GetDefaultMarkerPath()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The current user's local application data directory is unavailable.");
        }
        return Path.Combine(localApplicationData, "Sunder", "host", "launcher", "windows-v1.json");
    }

    private sealed record WindowsHostProcessMarker(
        int Version,
        Guid Generation,
        int ProcessId,
        long CreationTimeFileTimeUtc,
        string ExecutablePath,
        string Phase);

    private sealed class WindowsProcessLease(
        int processId,
        IntPtr processHandle,
        long creationTimeFileTimeUtc,
        string executablePath,
        Guid generation) : IDisposable
    {
        private IntPtr _processHandle = processHandle;

        public int ProcessId { get; } = processId;

        public IntPtr ProcessHandle => Volatile.Read(ref _processHandle);

        public long CreationTimeFileTimeUtc { get; } = creationTimeFileTimeUtc;

        public string ExecutablePath { get; } = executablePath;

        public Guid Generation { get; } = generation;

        public bool IsRunning => WaitForSingleObject(ProcessHandle, 0) == WaitTimeout;

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _processHandle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr ReservedPointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public long ToLong() => ((long)HighDateTime << 32) | LowDateTime;
    }

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
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
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
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessExtended(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        uint flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        nuint size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        IntPtr job,
        int informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        uint flags,
        StringBuilder executableName,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        IntPtr process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
