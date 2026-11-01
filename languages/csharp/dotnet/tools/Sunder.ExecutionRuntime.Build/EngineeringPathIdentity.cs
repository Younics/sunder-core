using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sunder.ExecutionRuntime.Build;

// Physical identity is stable across writes and child creation. CreationTimeUtc
// is not: Unix filesystems without birth time synthesize it from mutable times.
internal static class EngineeringPathIdentity
{
    internal static string Read(string path, bool directory, SafeFileHandle? file = null)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var opened = file is null ? CreateFile(path, 0x80, 7, 0, 3, 0x02200000, 0) : null;
                var handle = file ?? opened!;
                if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, buffer, 8)) throw InspectionError(path);
                var attributes = (FileAttributes)Marshal.ReadInt32(buffer);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                    || ((attributes & FileAttributes.Directory) != 0) != directory)
                    throw new IOException("Engineering identity requires a non-link regular file or directory.");
                if (!GetFileInformationByHandleEx(handle, 18, buffer, 24)) throw InspectionError(path);
                return $"windows:{Marshal.ReadInt64(buffer, 0):x16}:{Marshal.ReadInt64(buffer, 8):x16}:{Marshal.ReadInt64(buffer, 16):x16}";
            }

            uint mode;
            string identity;
            if (OperatingSystem.IsLinux())
            {
                // statx has one fixed ABI on both supported Linux architectures.
                if (Statx(file is null ? -100 : file.DangerousGetHandle().ToInt32(), file is null ? path : "",
                        file is null ? 0x900 : 0x1000, 0x101, buffer) != 0) throw InspectionError(path);
                if ((Marshal.ReadInt32(buffer) & 0x101) != 0x101)
                    throw new IOException("Linux did not return the required engineering path identity.");
                mode = unchecked((ushort)Marshal.ReadInt16(buffer, 28));
                identity = $"linux:{Marshal.ReadInt32(buffer, 136):x8}:{Marshal.ReadInt32(buffer, 140):x8}:{Marshal.ReadInt64(buffer, 32):x16}";
            }
            else if (OperatingSystem.IsMacOS())
            {
                if ((file is null ? LStat64(path, buffer) : FStat64(file, buffer)) != 0) throw InspectionError(path);
                mode = unchecked((ushort)Marshal.ReadInt16(buffer, 4));
                identity = $"darwin:{Marshal.ReadInt32(buffer, 0):x8}:{Marshal.ReadInt64(buffer, 8):x16}";
            }
            else throw new PlatformNotSupportedException("Engineering path identity requires Windows, Linux or macOS.");

            if ((mode & 0xF000) != (directory ? 0x4000u : 0x8000u))
                throw new IOException("Engineering identity requires a non-link regular file or directory.");
            return identity;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IOException InspectionError(string path)
        => new($"Could not inspect engineering path identity '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, nint buffer);
    [DllImport("libc", EntryPoint = "lstat64", SetLastError = true)]
    private static extern int LStat64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint buffer);
    [DllImport("libc", EntryPoint = "fstat64", SetLastError = true)]
    private static extern int FStat64(SafeFileHandle file, nint buffer);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, nint buffer, uint size);
}
