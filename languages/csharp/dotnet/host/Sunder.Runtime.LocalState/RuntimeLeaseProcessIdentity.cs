using System.Diagnostics;
using System.Globalization;

namespace Sunder.Runtime.LocalState;

// Shared by Runtime and projection leases. This is not the App audit identity or
// the .NET/Node build-lock format; those intentionally have different guarantees.
internal static class RuntimeLeaseProcessIdentity
{
    internal static string Capture(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            var stat = File.ReadAllText($"/proc/{process.Id}/stat");
            var closingParenthesis = stat.LastIndexOf(')');
            if (closingParenthesis < 0)
                throw new InvalidDataException($"Process {process.Id} has invalid Linux start metadata.");
            var fields = stat[(closingParenthesis + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length <= 19 || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException($"Process {process.Id} has invalid Linux start metadata.");
            var bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim().ToLowerInvariant();
            if (!Guid.TryParseExact(bootId, "D", out _)) throw new InvalidDataException("The Linux boot identity is invalid.");
            return $"linux:{bootId}:{fields[19]}";
        }

        var ticks = process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        if (OperatingSystem.IsWindows()) return "windows:" + ticks;
        if (OperatingSystem.IsMacOS()) return "darwin:" + ticks;
        throw new PlatformNotSupportedException("Process birth identity is unavailable on this operating system.");
    }
}
