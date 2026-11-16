using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class SystemExecutionRuntimeProcessContainmentController
{
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
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create a Worker Job.");
            if (Marshal.GetLastWin32Error() == ErrorAlreadyExists)
            {
                handle.Dispose();
                throw new IOException($"Execution-runtime Worker Job '{name}' already exists.");
            }

            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not configure the Worker Job lifetime policy.");
            }
            catch { handle.Dispose(); throw; }
            finally { Marshal.FreeHGlobal(buffer); }
            return handle;
        }

        public static SafeFileHandle? TryOpen(string name, out bool unavailable)
        {
            var handle = OpenJobObject(JobObjectQuery | JobObjectTerminate, false, name);
            if (!handle.IsInvalid) { unavailable = false; return handle; }
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            unavailable = error is not (ErrorFileNotFound or ErrorInvalidHandle);
            return null;
        }

        public static void Assign(SafeFileHandle job, SafeProcessHandle process)
        {
            if (!AssignProcessToJobObject(job, process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not assign the execution-runtime guardian to its Worker Job.");
        }

        public static int GetActiveProcessCount(SafeFileHandle job)
        {
            var size = Marshal.SizeOf<JobObjectBasicAccountingInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!QueryInformationJobObject(job, JobObjectBasicAccountingInformationClass, buffer, (uint)size, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not inspect the execution-runtime Worker Job.");
                return checked((int)Marshal.PtrToStructure<JobObjectBasicAccountingInformation>(buffer).ActiveProcesses);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public static bool ContainsProcess(SafeFileHandle job, int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!IsProcessInJob(process.SafeHandle, job, out var inJob))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not verify execution-runtime Worker Job membership.");
                return inJob;
            }
            catch (ArgumentException) { return false; }
        }

        public static void Terminate(SafeFileHandle job)
        {
            if (!TerminateJobObject(job, 1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not terminate the execution-runtime Worker Job.");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle OpenJobObject(uint desiredAccess, bool inheritHandle, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint informationLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint informationLength, out uint returnLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
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
