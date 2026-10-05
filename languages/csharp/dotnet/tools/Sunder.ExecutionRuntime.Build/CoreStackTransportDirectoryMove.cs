using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sunder.ExecutionRuntime.Build;

internal static class CoreStackTransportDirectoryMove
{
    // The per-call native seam permits unavailable-platform tests without changing process-wide P/Invoke resolution.
    internal static void Move(string source, string destination, Func<int>? nativeRename = null)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (OperatingSystem.IsWindows() && nativeRename is null)
        {
            Directory.Move(source, destination);
            return;
        }

        try
        {
            const int atCurrentWorkingDirectory = -100;
            const uint renameNoReplace = 1;
            const uint renameExclusive = 4;
            var result = nativeRename is not null ? nativeRename()
                : OperatingSystem.IsLinux() ? RenameAt2(atCurrentWorkingDirectory, source, atCurrentWorkingDirectory, destination, renameNoReplace)
                : OperatingSystem.IsMacOS() ? RenameExclusive(source, destination, renameExclusive)
                : throw new PlatformNotSupportedException("This OS has no configured atomic no-replace directory rename.");
            var error = Marshal.GetLastPInvokeError();
            if (result != 0)
                throw new IOException($"Atomic Core transport directory publication failed (errno {error}); no fallback was attempted.",
                    new Win32Exception(error));
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException
            or BadImageFormatException or PlatformNotSupportedException)
        {
            throw new IOException("Atomic no-replace directory publication is unavailable; no fallback was attempted.", exception);
        }
    }

    [DllImport("libc", EntryPoint = "renameat2", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int RenameAt2(int sourceDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source, int destinationDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "renamex_np", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int RenameExclusive([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);
}
