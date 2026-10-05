using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Sunder.Package.Projections;

internal enum ExecutionRuntimeProcessTreeLeaseProbe
{
    Missing,
    NoContainment,
    Empty,
    Active,
    IdentityMismatch,
    Unavailable,
}

internal static class ExecutionRuntimeProcessTreeLease
{
    private enum UnixAnchorProbe
    {
        Gone,
        Member,
        Detached,
        Unavailable,
    }

    private const int CurrentSchemaVersion = 2;
    private const int MaximumLeaseBytes = 64 * 1024;
    private const string UnixProcessGroupKind = "unix-process-group-v1";
    private const string WindowsJobKind = "windows-job-v1";

    public static ExecutionRuntimeProcessTreeLeaseProbe Probe(
        string path,
        string expectedLeaseId,
        Func<int, string, PackageProjectionProcessProbe> probeProcess)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedLeaseId);
        ArgumentNullException.ThrowIfNull(probeProcess);

        ExecutionRuntimeProcessLeaseEvidence lease;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Execution-runtime process-tree lease '{path}' must not be a link or reparse point.");
            }
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumLeaseBytes)
            {
                throw new InvalidDataException(
                    $"Execution-runtime process-tree lease '{path}' has an invalid size.");
            }
            lease = JsonSerializer.Deserialize<ExecutionRuntimeProcessLeaseEvidence>(
                        stream,
                        PackageProjectionJson.Options)
                    ?? throw new InvalidDataException(
                        $"Execution-runtime process-tree lease '{path}' is empty.");
        }
        catch (FileNotFoundException)
        {
            return ExecutionRuntimeProcessTreeLeaseProbe.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return ExecutionRuntimeProcessTreeLeaseProbe.Missing;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Execution-runtime process-tree lease '{path}' is invalid.",
                exception);
        }

        Validate(lease, expectedLeaseId, path);
        if (!lease.HasContainment)
        {
            return ExecutionRuntimeProcessTreeLeaseProbe.NoContainment;
        }
        if (OperatingSystem.IsWindows())
        {
            return ProbeWindowsJob(lease, probeProcess);
        }
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return ProbeUnixProcessGroup(lease, probeProcess);
        }
        return ExecutionRuntimeProcessTreeLeaseProbe.Unavailable;
    }

    private static ExecutionRuntimeProcessTreeLeaseProbe ProbeWindowsJob(
        ExecutionRuntimeProcessLeaseEvidence lease,
        Func<int, string, PackageProjectionProcessProbe> probeProcess)
    {
        if (!string.Equals(lease.ContainmentKind, WindowsJobKind, StringComparison.Ordinal)
            || !string.Equals(
                lease.ContainmentIdentity,
                $@"Local\Sunder.ExecutionRuntime.{lease.LeaseId}",
                StringComparison.Ordinal))
        {
            return ExecutionRuntimeProcessTreeLeaseProbe.IdentityMismatch;
        }

        using var job = WindowsJob.TryOpen(lease.ContainmentIdentity!, out var unavailable);
        if (job is null)
        {
            if (unavailable)
            {
                return ExecutionRuntimeProcessTreeLeaseProbe.Unavailable;
            }
            return probeProcess(lease.ContainmentProcessId!.Value, lease.ContainmentProcessBirthIdentity!) is
                PackageProjectionProcessProbe.Missing or PackageProjectionProcessProbe.BirthMismatch
                ? ExecutionRuntimeProcessTreeLeaseProbe.Empty
                : ExecutionRuntimeProcessTreeLeaseProbe.Unavailable;
        }

        int activeProcesses;
        try
        {
            activeProcesses = WindowsJob.GetActiveProcessCount(job);
        }
        catch (Win32Exception)
        {
            return ExecutionRuntimeProcessTreeLeaseProbe.Unavailable;
        }
        if (activeProcesses == 0)
        {
            return ExecutionRuntimeProcessTreeLeaseProbe.Empty;
        }

        var guardian = probeProcess(
            lease.ContainmentProcessId!.Value,
            lease.ContainmentProcessBirthIdentity!);
        if (guardian == PackageProjectionProcessProbe.Matching)
        {
            return ExecutionRuntimeProcessTreeLeaseProbe.Active;
        }
        if (guardian == PackageProjectionProcessProbe.Unavailable)
        {
            return ExecutionRuntimeProcessTreeLeaseProbe.Unavailable;
        }
        if (!lease.IsAttached)
        {
            return ExecutionRuntimeProcessTreeLeaseProbe.IdentityMismatch;
        }

        var payload = probeProcess(lease.ProcessId!.Value, lease.ProcessBirthIdentity!);
        if (payload == PackageProjectionProcessProbe.Unavailable)
        {
            return ExecutionRuntimeProcessTreeLeaseProbe.Unavailable;
        }
        if (payload != PackageProjectionProcessProbe.Matching)
        {
            return ExecutionRuntimeProcessTreeLeaseProbe.IdentityMismatch;
        }
        try
        {
            return WindowsJob.ContainsProcess(job, lease.ProcessId.Value)
                ? ExecutionRuntimeProcessTreeLeaseProbe.Active
                : ExecutionRuntimeProcessTreeLeaseProbe.IdentityMismatch;
        }
        catch (Win32Exception)
        {
            return ExecutionRuntimeProcessTreeLeaseProbe.Unavailable;
        }
    }

    private static ExecutionRuntimeProcessTreeLeaseProbe ProbeUnixProcessGroup(
        ExecutionRuntimeProcessLeaseEvidence lease,
        Func<int, string, PackageProjectionProcessProbe> probeProcess)
    {
        if (!string.Equals(lease.ContainmentKind, UnixProcessGroupKind, StringComparison.Ordinal)
            || !int.TryParse(
                lease.ContainmentIdentity,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var processGroupId)
            || processGroupId != lease.ContainmentProcessId)
        {
            return ExecutionRuntimeProcessTreeLeaseProbe.IdentityMismatch;
        }

        var guardian = ProbeUnixAnchor(
            processGroupId,
            lease.ContainmentProcessId!.Value,
            lease.ContainmentProcessBirthIdentity!,
            probeProcess);
        var payload = lease.IsAttached
            ? ProbeUnixAnchor(
                processGroupId,
                lease.ProcessId!.Value,
                lease.ProcessBirthIdentity!,
                probeProcess)
            : UnixAnchorProbe.Gone;
        if (guardian == UnixAnchorProbe.Unavailable || payload == UnixAnchorProbe.Unavailable)
        {
            return ExecutionRuntimeProcessTreeLeaseProbe.Unavailable;
        }
        if (guardian == UnixAnchorProbe.Detached || payload == UnixAnchorProbe.Detached)
        {
            return ExecutionRuntimeProcessTreeLeaseProbe.IdentityMismatch;
        }
        var groupState = UnixProcessGroup.Probe(processGroupId);
        if (guardian == UnixAnchorProbe.Member || payload == UnixAnchorProbe.Member)
        {
            return groupState == ExecutionRuntimeProcessTreeLeaseProbe.Active
                ? ExecutionRuntimeProcessTreeLeaseProbe.Active
                : ExecutionRuntimeProcessTreeLeaseProbe.Unavailable;
        }
        return groupState == ExecutionRuntimeProcessTreeLeaseProbe.Empty
            ? ExecutionRuntimeProcessTreeLeaseProbe.Empty
            : groupState == ExecutionRuntimeProcessTreeLeaseProbe.Active
                ? ExecutionRuntimeProcessTreeLeaseProbe.IdentityMismatch
                : ExecutionRuntimeProcessTreeLeaseProbe.Unavailable;
    }

    private static UnixAnchorProbe ProbeUnixAnchor(
        int processGroupId,
        int processId,
        string processBirthIdentity,
        Func<int, string, PackageProjectionProcessProbe> probeProcess)
    {
        var process = probeProcess(processId, processBirthIdentity);
        if (process == PackageProjectionProcessProbe.Unavailable)
        {
            return UnixAnchorProbe.Unavailable;
        }
        if (process is PackageProjectionProcessProbe.Missing or PackageProjectionProcessProbe.BirthMismatch)
        {
            return UnixAnchorProbe.Gone;
        }
        try
        {
            return UnixProcessGroup.GetProcessGroupId(processId) == processGroupId
                ? UnixAnchorProbe.Member
                : UnixAnchorProbe.Detached;
        }
        catch (Win32Exception)
        {
            return UnixAnchorProbe.Unavailable;
        }
    }

    private static void Validate(
        ExecutionRuntimeProcessLeaseEvidence lease,
        string expectedLeaseId,
        string path)
    {
        if (lease.SchemaVersion != CurrentSchemaVersion
            || !string.Equals(lease.LeaseId, expectedLeaseId, StringComparison.Ordinal)
            || !Guid.TryParseExact(lease.LeaseId, "N", out _)
            || lease.HostInstanceId == Guid.Empty
            || string.IsNullOrWhiteSpace(lease.PackageId)
            || string.IsNullOrWhiteSpace(lease.GenerationId)
            || lease.ActivationId == Guid.Empty
            || lease.ContentIdentity.Length != 64
            || lease.ContentIdentity.Any(static value => !char.IsAsciiHexDigit(value))
            || lease.ReservedAtUtc == default
            || lease.ProcessStartRequestedAtUtc is { } startRequestedAtUtc
                && startRequestedAtUtc < lease.ReservedAtUtc
            || lease.ContainmentGeneration == Guid.Empty
            || (lease.ProcessStartRequestedAtUtc is null) != (lease.ContainmentGeneration is null))
        {
            throw new InvalidDataException(
                $"Execution-runtime process-tree lease '{path}' is invalid.");
        }

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
        var attachmentCount = new object?[]
        {
            lease.ProcessId,
            lease.ProcessBirthIdentity,
            lease.ProcessStartedAtUtc,
            lease.ExecutablePath,
            lease.RuntimeRootPath,
            lease.AttachedAtUtc,
        }.Count(static value => value is not null);
        if (containmentCount is not (0 or 7)
            || attachmentCount is not (0 or 6)
            || !lease.HasContainment && lease.IsAttached)
        {
            throw new InvalidDataException(
                $"Execution-runtime process-tree lease '{path}' has partial process ownership evidence.");
        }
        if (!lease.HasContainment)
        {
            return;
        }
        if (lease.ProcessStartRequestedAtUtc is null
            || lease.ContainmentGeneration is null
            || lease.ContainmentProcessId <= 0
            || string.IsNullOrWhiteSpace(lease.ContainmentProcessBirthIdentity)
            || lease.ContainmentProcessStartedAtUtc == default
            || string.IsNullOrWhiteSpace(lease.ContainmentExecutablePath)
            || !Path.IsPathFullyQualified(lease.ContainmentExecutablePath)
            || lease.ContainmentAttachedAtUtc == default
            || lease.ContainmentAttachedAtUtc < lease.ReservedAtUtc)
        {
            throw new InvalidDataException(
                $"Execution-runtime process-tree lease '{path}' has invalid containment evidence.");
        }
        if (!lease.IsAttached)
        {
            return;
        }
        if (lease.ProcessId <= 0
            || string.IsNullOrWhiteSpace(lease.ProcessBirthIdentity)
            || lease.ProcessStartedAtUtc == default
            || string.IsNullOrWhiteSpace(lease.ExecutablePath)
            || !Path.IsPathFullyQualified(lease.ExecutablePath)
            || string.IsNullOrWhiteSpace(lease.RuntimeRootPath)
            || !Path.IsPathFullyQualified(lease.RuntimeRootPath)
            || lease.AttachedAtUtc == default
            || lease.AttachedAtUtc < lease.ReservedAtUtc)
        {
            throw new InvalidDataException(
                $"Execution-runtime process-tree lease '{path}' has invalid process evidence.");
        }
    }

    private sealed record ExecutionRuntimeProcessLeaseEvidence(
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
        public bool HasContainment => ContainmentProcessId is not null;
        public bool IsAttached => ProcessId is not null;
    }

    private static class UnixProcessGroup
    {
        private const int ErrorNoSuchProcess = 3;
        private const int ErrorPermissionDenied = 1;

        public static ExecutionRuntimeProcessTreeLeaseProbe Probe(int processGroupId)
        {
            if (kill(-processGroupId, 0) == 0)
            {
                return ExecutionRuntimeProcessTreeLeaseProbe.Active;
            }
            return Marshal.GetLastPInvokeError() switch
            {
                ErrorNoSuchProcess => ExecutionRuntimeProcessTreeLeaseProbe.Empty,
                ErrorPermissionDenied => ExecutionRuntimeProcessTreeLeaseProbe.Active,
                _ => ExecutionRuntimeProcessTreeLeaseProbe.Unavailable,
            };
        }

        public static int GetProcessGroupId(int processId)
        {
            var groupId = getpgid(processId);
            if (groupId < 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            return groupId;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int processId, int signal);

        [DllImport("libc", SetLastError = true)]
        private static extern int getpgid(int processId);
    }

    private static class WindowsJob
    {
        private const uint JobObjectQuery = 0x0004;
        private const int JobObjectBasicAccountingInformationClass = 1;
        private const int ErrorFileNotFound = 2;
        private const int ErrorInvalidHandle = 6;

        public static SafeFileHandle? TryOpen(string name, out bool unavailable)
        {
            var handle = OpenJobObject(JobObjectQuery, false, name);
            if (!handle.IsInvalid)
            {
                unavailable = false;
                return handle;
            }
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            unavailable = error is not (ErrorFileNotFound or ErrorInvalidHandle);
            return null;
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
                        Marshal.GetLastPInvokeError(),
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
                        Marshal.GetLastPInvokeError(),
                        "Windows could not verify execution-runtime Worker Job membership.");
                }
                return inJob;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle OpenJobObject(uint desiredAccess, bool inheritHandle, string name);

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
        private static extern bool IsProcessInJob(
            SafeProcessHandle process,
            SafeFileHandle job,
            [MarshalAs(UnmanagedType.Bool)] out bool result);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicAccountingInformation
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodKernelTime;
            public uint TotalPageFaultCount;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
        }
    }
}
