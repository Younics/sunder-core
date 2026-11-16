using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sunder.Runtime.Host.Services;

internal static partial class DurableJsonDocument
{
    private static void SyncUnixDirectory(string path)
    {
        var descriptor = open(path, 0);
        if (descriptor < 0)
        {
            throw new IOException(
                $"Could not open durable document directory '{path}' for synchronization.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        try
        {
            if (fsync(descriptor) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is not (22 or 30 or 45 or 95))
                {
                    throw new IOException(
                        $"Could not synchronize durable document directory '{path}'.",
                        new Win32Exception(error));
                }
            }
        }
        finally { close(descriptor); }
    }

    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int descriptor);
    [DllImport("libc")] private static extern int close(int descriptor);
}
