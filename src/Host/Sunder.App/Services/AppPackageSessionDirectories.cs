using System.Diagnostics;

namespace Sunder.App.Services;

internal static class AppPackageSessionDirectories
{
    private static readonly string SessionRootPath = Path.Combine(
        Path.GetTempPath(),
        "Sunder.App",
        "package-sessions");

    public static string CreateSessionFolder()
    {
        var sessionFolder = Path.Combine(
            SessionRootPath,
            $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sessionFolder);
        return sessionFolder;
    }

    public static void CleanupStaleSessions()
        => CleanupStaleSessions(SessionRootPath);

    internal static void CleanupStaleSessions(string sessionRootPath)
    {
        if (!Directory.Exists(sessionRootPath))
        {
            return;
        }

        foreach (var sessionFolder in Directory.EnumerateDirectories(sessionRootPath))
        {
            if (TryReadOwnerProcessId(Path.GetFileName(sessionFolder), out var processId)
                && IsProcessRunning(processId))
            {
                continue;
            }

            TryDeleteDirectory(sessionFolder);
        }
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
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to delete a stale app package session folder.", ex);
        }
    }
}
