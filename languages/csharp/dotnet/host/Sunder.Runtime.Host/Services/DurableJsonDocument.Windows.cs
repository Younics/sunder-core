using System.ComponentModel;
using System.Runtime.InteropServices;
using Sunder.Runtime.LocalState;

namespace Sunder.Runtime.Host.Services;

internal static partial class DurableJsonDocument
{
    private static void MoveWindows(string sourcePath, string destinationPath, bool replaceExisting)
    {
        const uint moveFileReplaceExisting = 0x1;
        const uint moveFileWriteThrough = 0x8;
        var flags = moveFileWriteThrough | (replaceExisting ? moveFileReplaceExisting : 0);
        if (!MoveFileEx(RuntimeOwnedFileSystem.GetWindowsNativePath(sourcePath), RuntimeOwnedFileSystem.GetWindowsNativePath(destinationPath), flags))
        {
            throw new IOException(
                $"Could not durably move '{sourcePath}' to '{destinationPath}'.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingPath, string newPath, uint flags);
}
