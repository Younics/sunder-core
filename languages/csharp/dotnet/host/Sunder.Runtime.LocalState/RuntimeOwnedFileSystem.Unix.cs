using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sunder.Runtime.LocalState;

internal static partial class RuntimeOwnedFileSystem
{
    private const uint SymbolicLinkType = 0xA000;
    private const uint GroupOrOtherWriteMask = 0x0012;

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
            if (statx(atFileSystemWorkingDirectory, path, atSymlinkNoFollow | atNoAutomount, requested, buffer) != 0)
                throw new IOException($"Could not inspect Runtime-owned path '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));
            var returnedMask = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            if ((returnedMask & requested) != requested)
                throw new PlatformNotSupportedException("Linux did not return the physical path identity required for safe Runtime mutation.");
            var mountIdentity = Marshal.ReadInt64(buffer, 144);
            var fileIdentity = unchecked((ulong)Marshal.ReadInt64(buffer, 32));
            return new PhysicalEntry(Mode: unchecked((ushort)Marshal.ReadInt16(buffer, 28)),
                LinkCount: unchecked((uint)Marshal.ReadInt32(buffer, 16)), OwnerUserId: unchecked((uint)Marshal.ReadInt32(buffer, 20)),
                MountIdentity: mountIdentity, Identity: $"linux:{unchecked((ulong)mountIdentity):x16}:{fileIdentity:x16}");
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new PlatformNotSupportedException("Linux statx support is required for safe Runtime-owned path mutation.", exception);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static PhysicalEntry InspectMacOs(string path)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (lstat64(path, buffer) != 0)
                throw new IOException($"Could not inspect Runtime-owned path '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));
            var mountIdentity = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            var fileIdentity = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
            return new PhysicalEntry(Mode: unchecked((ushort)Marshal.ReadInt16(buffer, 4)),
                LinkCount: unchecked((ushort)Marshal.ReadInt16(buffer, 6)), OwnerUserId: unchecked((uint)Marshal.ReadInt32(buffer, 16)),
                MountIdentity: mountIdentity, Identity: $"darwin:{mountIdentity:x8}:{fileIdentity:x16}");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool IsTrustedMacOsSystemAlias(string requestedPath, string componentPath)
    {
        if (!OperatingSystem.IsMacOS() || string.Equals(Path.GetFullPath(requestedPath), componentPath, StringComparison.Ordinal)
            || !string.Equals(componentPath, "/var", StringComparison.Ordinal)) return false;
        try
        {
            var alias = InspectMacOs(componentPath);
            var resolvedTarget = new DirectoryInfo(componentPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if ((alias.Mode & FileTypeMask) != SymbolicLinkType || alias.OwnerUserId != 0
                || !string.Equals(resolvedTarget, "/private/var", StringComparison.Ordinal)) return false;
            foreach (var trustedDirectory in new[] { "/", "/private", "/private/var" })
            {
                var entry = InspectMacOs(trustedDirectory);
                if ((entry.Mode & FileTypeMask) != DirectoryType || entry.OwnerUserId != 0
                    || (entry.Mode & GroupOrOtherWriteMask) != 0) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int statx(int directoryFileDescriptor, string path, int flags, uint mask, nint buffer);
    [DllImport("libc", EntryPoint = "lstat64", SetLastError = true)] private static extern int lstat64(string path, nint buffer);
    [DllImport("libc")] private static extern uint getuid();
}
