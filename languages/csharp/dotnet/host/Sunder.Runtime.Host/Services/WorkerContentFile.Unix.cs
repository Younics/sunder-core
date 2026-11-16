using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sunder.Runtime.Host.Services;

internal static partial class WorkerContentFile
{
    // O_NONBLOCK prevents FIFO opens from hanging; O_NOFOLLOW rejects a leaf
    // substituted after the caller's attribute check. Linux flags differ by ABI.
    internal static int UnixOpenFlags(WorkerProcessPlatform platform, Architecture architecture)
    {
        if (architecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException($"Worker content files do not support the {architecture} Unix ABI.");

        return platform switch
        {
            WorkerProcessPlatform.MacOS => 0x0004 | 0x0100 | 0x01000000,
            WorkerProcessPlatform.Linux => 0x0800 | 0x80000
                | (architecture == Architecture.Arm64 ? 0x8000 : 0x20000),
            _ => throw new PlatformNotSupportedException("A supported Unix platform is required."),
        };
    }

    private static FileStream OpenUnixRead(string path)
    {
        var flags = UnixOpenFlags(WorkerProcessEnvironment.GetCurrentPlatform(), RuntimeInformation.ProcessArchitecture);
        var descriptor = OpenUnixFile(path, flags);
        if (descriptor < 0)
            throw new IOException("The worker content file could not be opened.",
                new Win32Exception(Marshal.GetLastPInvokeError()));

        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try { return new FileStream(handle, FileAccess.Read, 128 * 1024, isAsync: false); }
        catch { handle.Dispose(); throw; }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnixFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}
