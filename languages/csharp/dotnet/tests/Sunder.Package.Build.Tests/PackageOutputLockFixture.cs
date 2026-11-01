using System.Text.Json;
using Sunder.Package.Build.Tasks;

namespace Sunder.Package.Build.Tests;

internal static class PackageOutputLockFixture
{
    internal static TemporaryProcessMarker CreateOwnerDirectory(
        string directory,
        int pid,
        string hostname,
        DateTime processStartedAt,
        string? linuxBootIdOverride = null,
        string? linuxPidNamespaceOverride = null)
    {
        var platform = OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsWindows() ? "win32"
            : OperatingSystem.IsMacOS() ? "darwin"
            : throw new PlatformNotSupportedException();
        string? linuxBootId = null;
        string? linuxPidNamespace = null;
        if (OperatingSystem.IsLinux())
        {
            linuxBootId = linuxBootIdOverride
                ?? File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim().ToLowerInvariant();
            linuxPidNamespace = linuxPidNamespaceOverride
                ?? new FileInfo("/proc/self/ns/pid").LinkTarget
                ?? throw new InvalidDataException("Linux PID namespace identity is unavailable.");
        }
        var processStartedAtValue = processStartedAt.ToUniversalTime().ToString("O");
        var processMarkerToken = Guid.NewGuid().ToString("D");
        var sunderHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sunder");
        var generatedOutput = Path.Combine(sunderHome, "generated-output");
        var processMarkerRoot = Path.Combine(generatedOutput, "processes");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(processMarkerRoot);
        else
            foreach (var markerDirectory in new[] { sunderHome, generatedOutput, processMarkerRoot })
                Directory.CreateDirectory(markerDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var processMarkerPath = Path.Combine(processMarkerRoot, processMarkerToken + ".json");
        File.WriteAllText(processMarkerPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            token = processMarkerToken,
            pid,
            hostname,
            processStartedAt = processStartedAtValue,
            platform,
            linuxBootId,
            linuxPidNamespace,
            createdAt = DateTime.UtcNow,
        }));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, GeneratedOutputLock.OwnerFileName), JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            token = Guid.NewGuid().ToString("D"),
            pid,
            hostname,
            processStartedAt = processStartedAtValue,
            platform,
            processMarkerPath,
            processMarkerToken,
            linuxBootId,
            linuxPidNamespace,
            acquiredAt = DateTime.UtcNow.AddMinutes(-1),
        }));
        return new TemporaryProcessMarker(processMarkerPath);
    }

    internal sealed class TemporaryProcessMarker(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Delete()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }

        public void Dispose() => Delete();
    }
}
