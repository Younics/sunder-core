using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sunder.Runtime.Host.Services;

internal static class UnixProcessGroups
{
    public const int SignalKill = 9;
    public const int ErrorOperationNotPermitted = 1;
    private const int ErrorNoSuchProcess = 3;

    public static int CreateSession()
    {
        var result = setsid();
        return result >= 0 ? result
            : throw new Win32Exception(Marshal.GetLastPInvokeError(), "The process guardian could not create a Unix session.");
    }

    public static int GetParentProcessId() => getppid();

    public static int GetProcessGroupId(int processId)
    {
        var result = getpgid(processId);
        if (result >= 0) return result;
        var error = Marshal.GetLastPInvokeError();
        return error == ErrorNoSuchProcess ? -1
            : throw new Win32Exception(error, "The execution-runtime process group could not be inspected.");
    }

    public static bool IsAlive(int processGroupId)
        => HasMemberOtherThan(processGroupId, excludedProcessId: null);

    // Signal zero never delivers a signal. In particular, EPERM is not proof of absence.
    public static bool IsAbsent(int processGroupId)
        => kill(-processGroupId, 0) == -1 && Marshal.GetLastPInvokeError() == ErrorNoSuchProcess;

    public static bool HasMemberOtherThan(int processGroupId, int? excludedProcessId)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != excludedProcessId && GetProcessGroupId(process.Id) == processGroupId && !process.HasExited)
                        return true;
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorNoSuchProcess) { }
            }
        }
        return false;
    }

    public static void Signal(int processGroupId, int signal)
    {
        if (kill(-processGroupId, signal) == 0) return;
        var error = Marshal.GetLastPInvokeError();
        if (error != ErrorNoSuchProcess)
            throw new Win32Exception(error, "The execution-runtime process group could not be signaled.");
    }

    [DllImport("libc", SetLastError = true)] private static extern int setsid();
    [DllImport("libc")] private static extern int getppid();
    [DllImport("libc", SetLastError = true)] private static extern int getpgid(int processId);
    [DllImport("libc", SetLastError = true)] private static extern int kill(int processId, int signal);
}
