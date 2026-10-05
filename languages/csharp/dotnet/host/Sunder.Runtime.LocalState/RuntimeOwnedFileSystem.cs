using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sunder.Runtime.LocalState;

internal static class RuntimeOwnedFileSystem
{
    private const uint FileTypeMask = 0xF000;
    private const uint DirectoryType = 0x4000;
    private const uint RegularFileType = 0x8000;
    private const uint SymbolicLinkType = 0xA000;
    private const uint GroupOrOtherWriteMask = 0x0012;

    internal static void ValidateMutableDirectory(string mountRootPath, string path)
        => ValidateMutableEntry(mountRootPath, path, DirectoryType, requireSingleLink: false);

    internal static void ValidateMutableRegularFile(string mountRootPath, string path)
        => ValidateMutableEntry(mountRootPath, path, RegularFileType, requireSingleLink: true);

    internal static string GetMutableDirectoryIdentity(string path)
    {
        var fullPath = Path.GetFullPath(path);
        ValidateNoReparsePointAncestors(fullPath);
        return GetMutableDirectoryIdentity(fullPath, fullPath);
    }

    internal static string GetMutableDirectoryIdentity(string mountRootPath, string path)
        => GetMutableEntryIdentity(mountRootPath, path, DirectoryType, requireSingleLink: false);

    internal static string GetMutableRegularFileIdentity(string mountRootPath, string path)
        => GetMutableEntryIdentity(mountRootPath, path, RegularFileType, requireSingleLink: true);

    internal static void ValidateMutableDirectoryIdentity(string path, string expectedIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedIdentity);
        if (!string.Equals(GetMutableDirectoryIdentity(path), expectedIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Runtime-owned directory '{path}' no longer has its expected physical identity.");
        }
    }

    internal static void ValidateMutableDirectoryIdentity(
        string mountRootPath,
        string path,
        string expectedIdentity)
        => ValidateMutableEntryIdentity(
            mountRootPath,
            path,
            expectedIdentity,
            DirectoryType,
            requireSingleLink: false);

    internal static void ValidateMutableRegularFileIdentity(
        string mountRootPath,
        string path,
        string expectedIdentity)
        => ValidateMutableEntryIdentity(
            mountRootPath,
            path,
            expectedIdentity,
            RegularFileType,
            requireSingleLink: true);

    internal static string GetCanonicalPhysicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
                   ?? throw new InvalidDataException($"Path '{path}' has no filesystem root.");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, segment);
            if (!Path.Exists(candidate) && !IsReparsePoint(candidate))
            {
                current = candidate;
                continue;
            }
            FileSystemInfo entry = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : new FileInfo(candidate);
            current = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
            if (OperatingSystem.IsWindows())
            {
                current = GetWindowsFinalPath(current);
            }
        }
        return Path.GetFullPath(current);
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowsFinalPath(string path)
    {
        const uint fileShareRead = 0x0001;
        const uint fileShareWrite = 0x0002;
        const uint fileShareDelete = 0x0004;
        const uint openExisting = 3;
        const uint fileFlagBackupSemantics = 0x02000000;
        using var handle = CreateFileW(
            path,
            desiredAccess: 0,
            fileShareRead | fileShareWrite | fileShareDelete,
            0,
            openExisting,
            fileFlagBackupSemantics,
            0);
        if (handle.IsInvalid)
        {
            throw new IOException(
                $"Could not canonicalize physical path '{path}'.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, flags: 0);
        if (length == 0 || length >= buffer.Capacity)
        {
            throw new IOException(
                $"Could not canonicalize physical path '{path}'.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        var finalPath = buffer.ToString();
        const string extendedUncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        if (finalPath.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + finalPath[extendedUncPrefix.Length..];
        }
        return finalPath.StartsWith(extendedPrefix, StringComparison.Ordinal)
            ? finalPath[extendedPrefix.Length..]
            : finalPath;
    }

    internal static void ValidateNoReparsePointAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (IsReparsePoint(current) && !IsTrustedMacOsSystemAlias(path, current))
            {
                throw new InvalidDataException(
                    $"Runtime-owned path '{path}' contains a symbolic link or reparse-point component: '{current}'.");
            }
        }
    }

    private static void ValidateMutableEntry(
        string mountRootPath,
        string path,
        uint expectedType,
        bool requireSingleLink)
    {
        var mountRoot = Inspect(mountRootPath);
        var entry = Inspect(path);
        if ((mountRoot.Mode & FileTypeMask) != DirectoryType
            || (entry.Mode & FileTypeMask) != expectedType)
        {
            throw new InvalidDataException(
                $"Runtime-owned path '{path}' is not the expected physical file type.");
        }
        if (mountRoot.MountIdentity != entry.MountIdentity)
        {
            throw new InvalidDataException(
                $"Runtime-owned path '{path}' crosses a filesystem mount boundary.");
        }
        if (OperatingSystem.IsWindows())
        {
            ValidateWindowsOwner(path, expectedType == DirectoryType);
        }
        else if (entry.OwnerUserId != getuid())
        {
            throw new InvalidDataException(
                $"Runtime-owned path '{path}' is not owned by the current user.");
        }
        if (requireSingleLink && entry.LinkCount != 1)
        {
            throw new InvalidDataException(
                $"Runtime-owned file '{path}' has aliases outside its unique deletion identity.");
        }
    }

    private static string GetMutableEntryIdentity(
        string mountRootPath,
        string path,
        uint expectedType,
        bool requireSingleLink)
    {
        ValidateMutableEntry(mountRootPath, path, expectedType, requireSingleLink);
        return Inspect(path).Identity;
    }

    private static void ValidateMutableEntryIdentity(
        string mountRootPath,
        string path,
        string expectedIdentity,
        uint expectedType,
        bool requireSingleLink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedIdentity);
        var actualIdentity = GetMutableEntryIdentity(
            mountRootPath,
            path,
            expectedType,
            requireSingleLink);
        if (!string.Equals(actualIdentity, expectedIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Runtime-owned path '{path}' no longer has its expected physical identity.");
        }
    }

    private static PhysicalEntry Inspect(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            return InspectWindows(fullPath);
        }
        if (OperatingSystem.IsLinux())
        {
            return InspectLinux(fullPath);
        }
        if (OperatingSystem.IsMacOS())
        {
            return InspectMacOs(fullPath);
        }
        throw new PlatformNotSupportedException(
            "Runtime-owned physical path validation supports Windows, Linux, and macOS only.");
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
        using var handle = CreateFileW(
            path,
            fileReadAttributes,
            fileShareRead | fileShareWrite | fileShareDelete,
            0,
            openExisting,
            fileFlagOpenReparsePoint | fileFlagBackupSemantics,
            0);
        if (handle.IsInvalid)
        {
            throw new IOException(
                $"Could not inspect Runtime-owned path '{path}'.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException(
                $"Could not inspect Runtime-owned path '{path}'.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        if (!GetFileInformationByHandleEx(
                handle,
                fileInformationClass: 18,
                out var fileIdInformation,
                (uint)Marshal.SizeOf<WindowsFileIdInformation>()))
        {
            throw new IOException(
                $"Could not obtain the stable Runtime-owned identity for '{path}'.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        var attributes = (FileAttributes)information.FileAttributes;
        var mode = (attributes & FileAttributes.ReparsePoint) != 0
            ? 0u
            : (attributes & FileAttributes.Directory) != 0
                ? DirectoryType
                : RegularFileType;
        return new PhysicalEntry(
            mode,
            information.NumberOfLinks,
            0,
            unchecked((long)fileIdInformation.VolumeSerialNumber),
            $"windows:{fileIdInformation.VolumeSerialNumber:x16}:" +
            $"{fileIdInformation.FileIdLow:x16}{fileIdInformation.FileIdHigh:x16}");
    }

    private static PhysicalEntry InspectLinux(string path)
    {
        const int atFileSystemWorkingDirectory = -100;
        const int atSymlinkNoFollow = 0x100;
        const int atNoAutomount = 0x800;
        const uint statxType = 0x0001;
        const uint statxMode = 0x0002;
        const uint statxLinkCount = 0x0004;
        const uint statxUserId = 0x0008;
        const uint statxInode = 0x0100;
        const uint statxMountId = 0x1000;
        const uint requested = statxType | statxMode | statxLinkCount | statxUserId | statxInode | statxMountId;
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            Marshal.Copy(new byte[256], 0, buffer, 256);
            if (statx(
                    atFileSystemWorkingDirectory,
                    path,
                    atSymlinkNoFollow | atNoAutomount,
                    requested,
                    buffer) != 0)
            {
                throw new IOException(
                    $"Could not inspect Runtime-owned path '{path}'.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
            var returnedMask = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            if ((returnedMask & requested) != requested)
            {
                throw new PlatformNotSupportedException(
                    "Linux did not return the physical path identity required for safe Runtime mutation.");
            }
            var mountIdentity = Marshal.ReadInt64(buffer, 144);
            var fileIdentity = unchecked((ulong)Marshal.ReadInt64(buffer, 32));
            return new PhysicalEntry(
                Mode: unchecked((ushort)Marshal.ReadInt16(buffer, 28)),
                LinkCount: unchecked((uint)Marshal.ReadInt32(buffer, 16)),
                OwnerUserId: unchecked((uint)Marshal.ReadInt32(buffer, 20)),
                MountIdentity: mountIdentity,
                Identity: $"linux:{unchecked((ulong)mountIdentity):x16}:{fileIdentity:x16}");
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new PlatformNotSupportedException(
                "Linux statx support is required for safe Runtime-owned path mutation.",
                exception);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static PhysicalEntry InspectMacOs(string path)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (lstat64(path, buffer) != 0)
            {
                throw new IOException(
                    $"Could not inspect Runtime-owned path '{path}'.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
            var mountIdentity = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            var fileIdentity = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
            return new PhysicalEntry(
                Mode: unchecked((ushort)Marshal.ReadInt16(buffer, 4)),
                LinkCount: unchecked((ushort)Marshal.ReadInt16(buffer, 6)),
                OwnerUserId: unchecked((uint)Marshal.ReadInt32(buffer, 16)),
                MountIdentity: mountIdentity,
                Identity: $"darwin:{mountIdentity:x8}:{fileIdentity:x16}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private readonly record struct PhysicalEntry(
        uint Mode,
        uint LinkCount,
        uint OwnerUserId,
        long MountIdentity,
        string Identity);

    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsOwner(string path, bool directory)
    {
        var identity = WindowsIdentity.GetCurrent().User
                       ?? throw new IOException("The current Windows user has no security identifier.");
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
        if (!identity.Equals(security.GetOwner(typeof(SecurityIdentifier))))
        {
            throw new InvalidDataException(
                $"Runtime-owned path '{path}' is not owned by the current Windows user.");
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null
                   || new DirectoryInfo(path).LinkTarget is not null
                   || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool IsTrustedMacOsSystemAlias(string requestedPath, string componentPath)
    {
        if (!OperatingSystem.IsMacOS()
            || string.Equals(Path.GetFullPath(requestedPath), componentPath, StringComparison.Ordinal)
            || !string.Equals(componentPath, "/var", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var alias = InspectMacOs(componentPath);
            var resolvedTarget = new DirectoryInfo(componentPath)
                .ResolveLinkTarget(returnFinalTarget: true)?
                .FullName;
            if ((alias.Mode & FileTypeMask) != SymbolicLinkType
                || alias.OwnerUserId != 0
                || !string.Equals(resolvedTarget, "/private/var", StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var trustedDirectory in new[] { "/", "/private", "/private/var" })
            {
                var entry = InspectMacOs(trustedDirectory);
                if ((entry.Mode & FileTypeMask) != DirectoryType
                    || entry.OwnerUserId != 0
                    || (entry.Mode & GroupOrOtherWriteMask) != 0)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileTime
    {
        internal uint Low;
        internal uint High;
    }

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
    private struct WindowsFileIdInformation
    {
        internal ulong VolumeSerialNumber;
        internal ulong FileIdLow;
        internal ulong FileIdHigh;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int statx(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        nint buffer);

    [DllImport("libc", EntryPoint = "lstat64", SetLastError = true)]
    private static extern int lstat64(string path, nint buffer);

    [DllImport("libc")]
    private static extern uint getuid();

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out WindowsFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out WindowsFileIdInformation information,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags);
}
