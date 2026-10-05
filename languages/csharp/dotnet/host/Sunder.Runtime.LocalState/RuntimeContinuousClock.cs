using System.Runtime.InteropServices;
using System.Text;

namespace Sunder.Runtime.LocalState;

internal sealed record RuntimeContinuousClockReading(string BootSessionId, string Source, long Frequency, long Ticks)
{
    internal bool SameEpoch(RuntimeContinuousClockReading other) => BootSessionId == other.BootSessionId && Source == other.Source && Frequency == other.Frequency;
}

internal static class RuntimeContinuousClock
{
    internal static RuntimeContinuousClockReading Read()
    {
        if (OperatingSystem.IsLinux())
        {
            if (clock_gettime(7, out var time) != 0 || time.Seconds < 0 || time.Nanoseconds is < 0 or >= 1_000_000_000)
                throw new InvalidDataException("The suspend-aware clock is unavailable.");
            return new(BootId(File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim()), "linux.clock-boottime.v1", 10_000_000,
                checked(time.Seconds * 10_000_000 + time.Nanoseconds / 100));
        }
        if (OperatingSystem.IsMacOS())
        {
            var bytes = new byte[128]; nuint size = (nuint)bytes.Length;
            if (sysctlbyname("kern.bootsessionuuid", bytes, ref size, IntPtr.Zero, 0) != 0 || size is 0 or > 128
                || mach_timebase_info(out var scale) != 0 || scale.Numerator == 0 || scale.Denominator == 0)
                throw new InvalidDataException("The native boot/clock identity is unavailable.");
            return new(BootId(Encoding.ASCII.GetString(bytes, 0, checked((int)size)).TrimEnd('\0')), "darwin.mach-continuous.v1", 10_000_000,
                checked((long)((UInt128)mach_continuous_time() * scale.Numerator / ((UInt128)scale.Denominator * 100))));
        }
        if (OperatingSystem.IsWindows())
        {
            if (NtQuerySystemInformation(90, out var boot, (uint)Marshal.SizeOf<BootEnvironment>(), out _) != 0 || boot.Identifier == Guid.Empty)
                throw new InvalidDataException("The native boot identity is unavailable.");
            QueryInterruptTimePrecise(out var ticks);
            return new(boot.Identifier.ToString("D"), "windows.interrupt-time.v1", 10_000_000, checked((long)ticks));
        }
        throw new PlatformNotSupportedException("No suspend-aware clock is available.");
    }

    private static string BootId(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id.ToString("D") : throw new InvalidDataException("Invalid native boot identity.");
    [StructLayout(LayoutKind.Sequential)] private struct Timespec { public long Seconds; public long Nanoseconds; }
    [StructLayout(LayoutKind.Sequential)] private struct Timebase { public uint Numerator; public uint Denominator; }
    [StructLayout(LayoutKind.Sequential)] private struct BootEnvironment { public Guid Identifier; public uint FirmwareType; public ulong Flags; }
    [DllImport("libc", SetLastError = true)] private static extern int clock_gettime(int clock, out Timespec value);
    [DllImport("libc", SetLastError = true)] private static extern int sysctlbyname(string name, byte[] value, ref nuint size, IntPtr newValue, nuint newSize);
    [DllImport("libSystem.dylib")] private static extern ulong mach_continuous_time();
    [DllImport("libSystem.dylib")] private static extern int mach_timebase_info(out Timebase value);
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int kind, out BootEnvironment value, uint size, out uint returned);
    [DllImport("kernel32.dll")] private static extern void QueryInterruptTimePrecise(out ulong value);
}
