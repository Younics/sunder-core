// Loaded by the test reporter's PowerShell job owner; not product/runtime code.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Sunder.TestTools
{
    public static class WindowsTestProcessJob
    {
        private const uint KillOnJobClose = 0x2000;
        private const uint CreateSuspended = 0x4;
        private const uint CreateNoWindow = 0x08000000;
        private const uint WaitTimeout = 258;
        private const uint DuplicateSameAccess = 2;
        private const int ExtendedLimitInformation = 9;
        private const int BasicAccountingInformation = 1;
        private const int StartUseStdHandles = 0x100;

        public sealed class Result
        {
            public uint? ProcessId { get; set; }
            public int? Status { get; set; }
            public string Error { get; set; }
            public string Message { get; set; }
            public bool CleanupComplete { get; set; }
        }

        public static Result Run(string command, string[] arguments, string directory, long timeout, string cancelPath)
        {
            var result = new Result();
            IntPtr job = IntPtr.Zero;
            var process = new ProcessInformation();
            var handles = new IntPtr[3];
            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                Require(job != IntPtr.Zero);
                var limits = new ExtendedLimits();
                limits.BasicLimitInformation.LimitFlags = KillOnJobClose; // No breakaway.
                Require(SetInformationJobObject(job, ExtendedLimitInformation, ref limits, Marshal.SizeOf(typeof(ExtendedLimits))));
                var startup = new StartupInfo();
                startup.Size = Marshal.SizeOf(typeof(StartupInfo));
                startup.Flags = StartUseStdHandles;
                for (var index = 0; index < handles.Length; index++)
                    Require(DuplicateHandle(GetCurrentProcess(), GetStdHandle(-10 - index), GetCurrentProcess(),
                        out handles[index], 0, true, DuplicateSameAccess));
                startup.StandardInput = handles[0];
                startup.StandardOutput = handles[1];
                startup.StandardError = handles[2];
                var line = new StringBuilder(Quote(command));
                foreach (var argument in arguments) line.Append(' ').Append(Quote(argument));
                // Assign while suspended: even a fast launcher cannot create an
                // uncontained testhost/build process before ownership is established.
                Require(CreateProcess(null, line, IntPtr.Zero, IntPtr.Zero, true, CreateNoWindow | CreateSuspended,
                    IntPtr.Zero, directory, ref startup, out process));
                result.ProcessId = process.ProcessId;
                Require(AssignProcessToJobObject(job, process.Process));
                Require(ResumeThread(process.Thread) != uint.MaxValue);
                var clock = Stopwatch.StartNew();
                uint wait;
                while ((wait = WaitForSingleObject(process.Process, 50)) == WaitTimeout)
                {
                    if (clock.ElapsedMilliseconds < timeout && !File.Exists(cancelPath)) continue;
                    result.Error = File.Exists(cancelPath) ? "ECANCELED" : "ETIMEDOUT";
                    result.Message = "Owned test command stopped after " + clock.ElapsedMilliseconds + " ms.";
                    break;
                }
                Require(wait == 0 || wait == WaitTimeout);
                if (wait == 0)
                {
                    uint code;
                    Require(GetExitCodeProcess(process.Process, out code));
                    result.Status = unchecked((int)code);
                }
            }
            catch (Exception error)
            {
                result.Error = "EJOB";
                result.Message = error.ToString();
            }
            finally
            {
                try
                {
                    // Also terminate leftover compiler/test children on normal
                    // root exit; never carry them into the next project.
                    if (process.Process != IntPtr.Zero)
                    {
                        if (WaitForSingleObject(process.Process, 0) == WaitTimeout)
                            Require(TerminateProcess(process.Process, 1)); // covers failed job assignment too.
                    }
                    if (job != IntPtr.Zero)
                    {
                        Require(TerminateJobObject(job, 1));
                        var drain = Stopwatch.StartNew();
                        do
                        {
                            BasicAccounting accounting;
                            Require(QueryInformationJobObject(job, BasicAccountingInformation, out accounting, Marshal.SizeOf(typeof(BasicAccounting)), IntPtr.Zero));
                            if (accounting.ActiveProcesses == 0) { result.CleanupComplete = true; break; }
                            Thread.Sleep(25);
                        } while (drain.ElapsedMilliseconds < 10000);
                    }
                    else result.CleanupComplete = process.Process == IntPtr.Zero;
                    if (process.Process != IntPtr.Zero)
                        result.CleanupComplete &= WaitForSingleObject(process.Process, 10000) == 0;
                }
                catch (Exception cleanup)
                {
                    result.CleanupComplete = false;
                    result.Error = result.Error ?? "ECLEANUP";
                    result.Message += "\nCleanup: " + cleanup;
                }
                foreach (var handle in handles) if (handle != IntPtr.Zero) CloseHandle(handle);
                if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
                if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
                if (job != IntPtr.Zero) CloseHandle(job);
            }
            return result;
        }

        private static string Quote(string value)
        {
            var quoted = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                quoted.Append(character);
                slashes = 0;
            }
            return quoted.Append('\\', slashes * 2).Append('"').ToString();
        }

        private static void Require(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }

        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
        { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet;
          public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters
        { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
        { public BasicLimits BasicLimitInformation; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [StructLayout(LayoutKind.Sequential)] private struct BasicAccounting
        { public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
        { public int Size; public string Reserved, Desktop, Title; public int X, Y, Width, Height, XChars, YChars, Fill, Flags;
          public short ShowWindow, ReservedSize; public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError; }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation
        { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int info, ref ExtendedLimits limits, int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int info, out BasicAccounting accounting, int size, IntPtr length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")] private static extern bool CreateProcess(string application, StringBuilder line,
            IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int kind);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr source, IntPtr handle, IntPtr target, out IntPtr duplicate, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    }
}
