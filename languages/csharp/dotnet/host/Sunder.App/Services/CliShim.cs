namespace Sunder.App.Services;

internal static class CliShim
{
    private const string CommandName = "sunder";

    public static string GetExecutableFileName(CliInstallPlatform platform)
        => platform == CliInstallPlatform.Windows ? $"{CommandName}.exe" : CommandName;

    public static string CreateContent(CliFileDescriptor installedCli, CliInstallPlatform platform)
    {
        if (platform == CliInstallPlatform.Windows)
        {
            return installedCli.RequiresDotnet
                ? $"@echo off{Environment.NewLine}dotnet \"{EscapeBatchPath(installedCli.FilePath)}\" %*{Environment.NewLine}"
                : $"@echo off{Environment.NewLine}\"{EscapeBatchPath(installedCli.FilePath)}\" %*{Environment.NewLine}";
        }

        return installedCli.RequiresDotnet
            ? $"#!/usr/bin/env sh\nexec dotnet \"{EscapePosixDoubleQuotedPath(installedCli.FilePath)}\" \"$@\"\n"
            : $"#!/usr/bin/env sh\nexec \"{EscapePosixDoubleQuotedPath(installedCli.FilePath)}\" \"$@\"\n";
    }

    public static void TryMakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(
                path,
                mode
                | UnixFileMode.UserRead
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherExecute);
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string EscapeBatchPath(string path)
        => path.Replace("%", "%%", StringComparison.Ordinal);

    private static string EscapePosixDoubleQuotedPath(string path)
        => path
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);
}
