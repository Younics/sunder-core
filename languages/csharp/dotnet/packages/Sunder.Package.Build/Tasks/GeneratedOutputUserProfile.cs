using System.Runtime.InteropServices;

namespace Sunder.Package.Build.Tasks;

/// <summary>The OS account home, not a build's isolated HOME/configuration directory.</summary>
internal static class GeneratedOutputUserProfile
{
    internal static string Home { get; } = ReadHome();

    private static string ReadHome()
    {
        var home = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : ReadUnixHome();
        if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home))
            throw new IOException("Could not establish the OS account home for generated output locking.");
        return GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(home);
    }

    private static string ReadUnixHome()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Generated output account identity is unavailable on this operating system.");
        // POSIX account lookup ignores HOME. getpwuid_r keeps the returned strings
        // owned by this call instead of borrowing libc's shared passwd buffer.
        for (var size = 4096; size <= 1024 * 1024; size *= 2)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                int error;
                IntPtr result;
                IntPtr home;
                if (OperatingSystem.IsMacOS())
                {
                    error = GetDarwinAccount(GetEffectiveUserId(), out var account, buffer, (nuint)size, out result);
                    home = account.Home;
                }
                else
                {
                    error = GetLinuxAccount(GetEffectiveUserId(), out var account, buffer, (nuint)size, out result);
                    home = account.Home;
                }
                if (error == 34) continue; // ERANGE on Linux and macOS.
                if (error != 0 || result == IntPtr.Zero || home == IntPtr.Zero)
                    throw new IOException($"Could not read the OS account home for generated output locking (error {error}).");
                return Marshal.PtrToStringUTF8(home) ?? throw new IOException("The OS account has no home directory.");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("The OS account record exceeds the generated output lookup bound.");
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "getpwuid_r")]
    private static extern int GetLinuxAccount(uint uid, out LinuxAccount account, IntPtr buffer, nuint length, out IntPtr result);

    [DllImport("libc", EntryPoint = "getpwuid_r")]
    private static extern int GetDarwinAccount(uint uid, out DarwinAccount account, IntPtr buffer, nuint length, out IntPtr result);

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxAccount
    {
        internal IntPtr Name, Password;
        internal uint Uid, Gid;
        internal IntPtr Description, Home, Shell;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinAccount
    {
        internal IntPtr Name, Password;
        internal uint Uid, Gid;
        internal long Change;
        internal IntPtr Class, Description, Home, Shell;
        internal long Expire;
        internal int Fields;
    }
}
