using System.Diagnostics;

namespace Sunder.Runtime.Host.Services;

internal static class RuntimePackageSessionDirectories
{
    private static int _cleanupScheduled;
    private static readonly string SessionRootPath = Path.Combine(
        Path.GetTempPath(),
        "Sunder.Runtime.Host",
        "V1");

    public static string CreateInstalledSessionFolder()
        => CreateSessionFolder("installed-sessions");

    public static void CleanupStaleSessions()
    {
        CleanupStaleSessions(Path.Combine(SessionRootPath, "dev-sessions"));
        CleanupStaleSessions(Path.Combine(SessionRootPath, "installed-sessions"));
    }

    public static void ScheduleStaleSessionCleanup()
    {
        if (Interlocked.Exchange(ref _cleanupScheduled, 1) != 0) return;
        _ = Task.Run(CleanupStaleSessions);
    }

    internal static void CleanupStaleSessions(string sessionKindRootPath)
    {
        if (!Directory.Exists(sessionKindRootPath))
        {
            return;
        }

        foreach (var sessionFolder in Directory.EnumerateDirectories(sessionKindRootPath))
        {
            if (TryReadOwnerProcessId(Path.GetFileName(sessionFolder), out var processId)
                && IsProcessRunning(processId))
            {
                continue;
            }

            TryDeleteDirectory(sessionFolder);
        }
    }

    private static string CreateSessionFolder(string sessionKind)
    {
        var sessionFolder = Path.Combine(
            SessionRootPath,
            sessionKind,
            $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sessionFolder);
        return sessionFolder;
    }

    private static bool TryReadOwnerProcessId(string folderName, out int processId)
    {
        processId = 0;
        var parts = folderName.Split('-', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && int.TryParse(parts[1], out processId);
    }

    private static bool IsProcessRunning(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // A failed stale-session cleanup should not block runtime startup.
        }
    }
}
