using System.Diagnostics;
using System.Text;

namespace Sunder.App.Services;

internal static class LinuxShellAssociationService
{
    private const string DesktopFileId = "com.younics.sunder.desktop";
    private const string StackMimeType = "application/vnd.sunder.stack";
    private const string MimePackageFileName = "com.younics.sunder-stack.xml";

    public static void RegisterForCurrentUser()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var executablePath = GetExecutablePath();
        var dataHome = GetDataHome();
        if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(dataHome))
        {
            return;
        }

        try
        {
            var applicationsDirectory = Path.Combine(dataHome, "applications");
            var mimeRoot = Path.Combine(dataHome, "mime");
            var mimePackagesDirectory = Path.Combine(mimeRoot, "packages");

            Directory.CreateDirectory(applicationsDirectory);
            Directory.CreateDirectory(mimePackagesDirectory);

            File.WriteAllText(
                Path.Combine(applicationsDirectory, DesktopFileId),
                BuildDesktopFile(executablePath),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            File.WriteAllText(
                Path.Combine(mimePackagesDirectory, MimePackageFileName),
                BuildMimePackage(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            RunBestEffort("update-desktop-database", applicationsDirectory);
            RunBestEffort("update-mime-database", mimeRoot);
            RunBestEffort("xdg-mime", "default", DesktopFileId, StackMimeType);
            RunBestEffort("xdg-mime", "default", DesktopFileId, "x-scheme-handler/sunder");
        }
        catch
        {
            // Shell association setup is best effort and should not block app startup.
        }
    }

    private static string? GetExecutablePath()
    {
        var appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrWhiteSpace(appImagePath) && File.Exists(appImagePath))
        {
            return Path.GetFullPath(appImagePath);
        }

        var processPath = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath)
            ? Path.GetFullPath(processPath)
            : null;
    }

    private static string? GetDataHome()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(dataHome) && Path.IsPathFullyQualified(dataHome))
        {
            return dataHome;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? null
            : Path.Combine(userProfile, ".local", "share");
    }

    private static string BuildDesktopFile(string executablePath)
        => $"""
           [Desktop Entry]
           Type=Application
           Name=Sunder
           Comment=Local-first desktop package platform
           Exec={EscapeDesktopExecPath(executablePath)} %U
           Terminal=false
           Categories=Utility;
           MimeType={StackMimeType};x-scheme-handler/sunder;
           StartupWMClass=Sunder.App
           """;

    private static string BuildMimePackage()
        => $"""
           <?xml version="1.0" encoding="UTF-8"?>
           <mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
             <mime-type type="{StackMimeType}">
               <comment>Sunder Stack</comment>
               <glob pattern="*.sunderstack"/>
             </mime-type>
           </mime-info>
           """;

    private static string EscapeDesktopExecPath(string path)
        => "\"" + path
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal) + "\"";

    private static void RunBestEffort(string fileName, params string[] arguments)
    {
        try
        {
            using var process = Process.Start(CreateProcessStartInfo(fileName, arguments));
            if (process is null)
            {
                return;
            }

            if (!process.WaitForExit(milliseconds: 5000))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Missing XDG utilities should not prevent Sunder from starting.
        }
    }

    private static ProcessStartInfo CreateProcessStartInfo(string fileName, string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
