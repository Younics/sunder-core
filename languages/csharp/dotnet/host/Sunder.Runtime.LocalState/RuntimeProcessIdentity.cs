using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Sunder.Runtime.LocalState;

[StructLayout(LayoutKind.Sequential)]
internal struct RuntimeAuditToken
{
    public uint AuditUserId;
    public uint EffectiveUserId;
    public uint EffectiveGroupId;
    public uint RealUserId;
    public uint RealGroupId;
    public uint ProcessId;
    public uint AuditSessionId;
    public uint ProcessVersion;
}

internal static class RuntimeProcessIdentity
{
    internal static RuntimeAuditToken CurrentAuditToken()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        uint count = 8;
        if (task_info(mach_task_self(), 15, out var token, ref count) != 0 || count != 8 || token.ProcessId != Environment.ProcessId)
            throw new InvalidDataException("The process audit identity is unavailable.");
        return token;
    }

    internal static string Birth(Process process, RuntimeAuditToken? audit = null)
    {
        if (OperatingSystem.IsMacOS())
        {
            var token = audit ?? (process.Id == Environment.ProcessId ? CurrentAuditToken() : throw new InvalidDataException("A kernel audit token is required."));
            if (token.ProcessId != process.Id) throw new InvalidDataException("Audit process identity differs.");
            return $"darwin:{RuntimeContinuousClock.Read().BootSessionId}:{token.ProcessVersion.ToString(CultureInfo.InvariantCulture)}";
        }
        if (OperatingSystem.IsLinux())
        {
            var stat = ReadBounded($"/proc/{process.Id}/stat", 8192);
            var fields = System.Text.Encoding.UTF8.GetString(stat).Split(')').Last().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length <= 19 || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out _)) throw new InvalidDataException("Invalid process start identity.");
            return $"linux:{RuntimeContinuousClock.Read().BootSessionId}:{fields[19]}";
        }
        if (OperatingSystem.IsWindows()) return "windows:" + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        throw new PlatformNotSupportedException();
    }

    internal static string ExecutableHash(Process process)
    {
        var path = process.MainModule?.FileName ?? throw new InvalidDataException("Process executable identity is unavailable.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static string BuildIdentity(Assembly assembly) => $"{assembly.GetName().Name}/{assembly.GetName().Version}/{assembly.ManifestModule.ModuleVersionId:N}";

    internal static string ReadBuildIdentity(string assemblyPath, string expectedName)
    {
        using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is <= 0 or > 64 * 1024 * 1024) throw new InvalidDataException("App assembly exceeds the identity bound.");
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var definition = metadata.GetAssemblyDefinition();
        if (metadata.GetString(definition.Name) != expectedName) throw new InvalidDataException("The process is not the expected App build.");
        return $"{expectedName}/{definition.Version}/{metadata.GetGuid(metadata.GetModuleDefinition().Mvid):N}";
    }

    internal static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var output = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var count = stream.Read(chunk, 0, Math.Min(chunk.Length, checked(maximum + 1 - (int)output.Length)));
            if (count == 0) return output.ToArray();
            output.Write(chunk, 0, count);
            if (output.Length > maximum) throw new InvalidDataException("Process metadata exceeds its bound.");
        }
    }

    [DllImport("libSystem.dylib")] private static extern uint mach_task_self();
    [DllImport("libSystem.dylib")] private static extern int task_info(uint task, int flavor, out RuntimeAuditToken token, ref uint count);
}
