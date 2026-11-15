using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sunder.Runtime.LocalState;

internal static partial class RuntimeOwnedFileSystem
{
    [SupportedOSPlatform("windows")]
    private static void CreateWindowsDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetOwner(identity.User ?? throw new IOException("The current Windows user has no security identifier."));
        security.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateWindowsFile(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetOwner(identity.User ?? throw new IOException("The current Windows user has no security identifier."));
        security.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.FullControl, AccessControlType.Allow));
        var rights = access switch
        {
            FileAccess.Read => FileSystemRights.Read,
            FileAccess.Write => FileSystemRights.Write,
            FileAccess.ReadWrite => FileSystemRights.Read | FileSystemRights.Write,
            _ => throw new ArgumentOutOfRangeException(nameof(access)),
        };
        return new FileInfo(path).Create(mode, rights | FileSystemRights.Synchronize, share, bufferSize, options, security);
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowsFinalPath(string path)
    {
        const uint fileShareRead = 0x0001;
        const uint fileShareWrite = 0x0002;
        const uint fileShareDelete = 0x0004;
        const uint openExisting = 3;
        const uint fileFlagBackupSemantics = 0x02000000;
        using var handle = CreateFileW(GetWindowsNativePath(path), desiredAccess: 0,
            fileShareRead | fileShareWrite | fileShareDelete, 0, openExisting, fileFlagBackupSemantics, 0);
        if (handle.IsInvalid)
            throw new IOException($"Could not canonicalize physical path '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, flags: 0);
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException($"Could not canonicalize physical path '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));
        var finalPath = buffer.ToString();
        const string extendedUncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        if (finalPath.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + finalPath[extendedUncPrefix.Length..];
        return finalPath.StartsWith(extendedPrefix, StringComparison.Ordinal) ? finalPath[extendedPrefix.Length..] : finalPath;
    }

    internal static string GetWindowsNativePath(string path)
    {
        // Managed IO supports long paths without machine policy changes. Raw
        // Win32 opens need the same extended-length spelling explicitly.
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal)) return fullPath;
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;
    }

    [SupportedOSPlatform("windows")]
    private static PhysicalEntry InspectWindows(string path)
    {
        const uint fileReadAttributes = 0x0080;
        const uint fileShareRead = 0x0001;
        const uint fileShareWrite = 0x0002;
        const uint fileShareDelete = 0x0004;
        const uint openExisting = 3;
        const uint fileFlagOpenReparsePoint = 0x00200000;
        const uint fileFlagBackupSemantics = 0x02000000;
        using var handle = CreateFileW(GetWindowsNativePath(path), fileReadAttributes,
            fileShareRead | fileShareWrite | fileShareDelete, 0, openExisting, fileFlagOpenReparsePoint | fileFlagBackupSemantics, 0);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
            throw new IOException($"Could not inspect Runtime-owned path '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));
        if (!GetFileInformationByHandleEx(handle, fileInformationClass: 18, out var fileIdInformation, (uint)Marshal.SizeOf<WindowsFileIdInformation>()))
            throw new IOException($"Could not obtain the stable Runtime-owned identity for '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));
        var attributes = (FileAttributes)information.FileAttributes;
        var mode = (attributes & FileAttributes.ReparsePoint) != 0 ? 0u
            : (attributes & FileAttributes.Directory) != 0 ? DirectoryType : RegularFileType;
        return new PhysicalEntry(mode, information.NumberOfLinks, 0, unchecked((long)fileIdInformation.VolumeSerialNumber),
            $"windows:{fileIdInformation.VolumeSerialNumber:x16}:{fileIdInformation.FileIdLow:x16}{fileIdInformation.FileIdHigh:x16}");
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsOwner(string path, bool directory)
    {
        var identity = WindowsIdentity.GetCurrent().User ?? throw new IOException("The current Windows user has no security identifier.");
        FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
        if (!identity.Equals(security.GetOwner(typeof(SecurityIdentifier))))
            throw new InvalidDataException($"Runtime-owned path '{path}' is not owned by the current Windows user.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileTime { internal uint Low; internal uint High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        internal uint FileAttributes;
        internal WindowsFileTime CreationTime;
        internal WindowsFileTime LastAccessTime;
        internal WindowsFileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileIdInformation { internal ulong VolumeSerialNumber; internal ulong FileIdLow; internal ulong FileIdHigh; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out WindowsFileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass,
        out WindowsFileIdInformation information, uint bufferSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);
}
