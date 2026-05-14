using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Sunder.App.Services;

public enum CliInstallPlatform
{
    Windows,
    MacOS,
    Linux,
}

public sealed record CliInstallationOptions
{
    public string AppBaseDirectory { get; init; } = AppContext.BaseDirectory;

    public string? LocalApplicationDataPath { get; init; }

    public string? UserProfilePath { get; init; }

    public CliInstallPlatform? Platform { get; init; }

    public bool UpdateUserPath { get; init; } = true;
}

public sealed record CliInstallationPaths(
    string BundledCliDirectory,
    string? BundledCliPath,
    string InstalledCliDirectory,
    string InstalledCliPath,
    string ShimDirectory,
    string ShimPath);

public sealed record CliInstallationStatus(
    CliInstallationPaths Paths,
    string PlatformName,
    bool IsBundledCliAvailable,
    bool IsInstalled,
    bool IsShimInstalled,
    bool IsShimCurrent,
    bool IsShimDirectoryOnCurrentProcessPath,
    bool IsShimDirectoryOnUserPath,
    bool RequiresTerminalRestart,
    bool RequiresManualPathConfiguration,
    bool CanInstallOrRepair,
    bool IsFullyInstalled,
    string Summary,
    string? Warning,
    string PathInstructions);

public sealed record CliInstallationResult(
    CliInstallationStatus Status,
    bool InstalledOrUpdatedCli,
    bool CreatedOrUpdatedShim,
    bool UpdatedUserPath,
    IReadOnlyList<string> Warnings);

public static class CliStartupNotificationPolicy
{
    public static bool TryCreateWarning(CliInstallationResult result, out string warning)
    {
        if (!string.IsNullOrWhiteSpace(result.Status.Warning))
        {
            warning = result.Status.Warning;
            return true;
        }

        warning = string.Empty;
        return false;
    }
}

public interface ICliEnvironmentVariableStore
{
    string? GetProcessEnvironmentVariable(string name);

    string? GetUserEnvironmentVariable(string name);

    void SetUserEnvironmentVariable(string name, string? value);

    void BroadcastEnvironmentChanged();
}

public sealed class CliInstallationService
{
    private const string PackageName = "Sunder";
    private const string CliDirectoryName = "Cli";
    private const string CommandName = "sunder";
    private const string PathVariableName = "PATH";
    private static readonly string[] DotnetAssemblyFileNames = ["sunder.dll", "Sunder.Cli.dll"];

    private readonly CliInstallationOptions _options;
    private readonly ICliEnvironmentVariableStore _environment;
    private readonly CliInstallPlatform _platform;

    public CliInstallationService(
        CliInstallationOptions? options = null,
        ICliEnvironmentVariableStore? environment = null)
    {
        _options = options ?? new CliInstallationOptions();
        _environment = environment ?? SystemCliEnvironmentVariableStore.Instance;
        _platform = _options.Platform ?? GetCurrentPlatform();
    }

    public Task<CliInstallationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(CreateStatus, cancellationToken);
    }

    public Task<CliInstallationResult> EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var warnings = new List<string>();
            var paths = CreateBasePaths();
            var bundledCli = ResolveBundledCli(paths.BundledCliDirectory);

            if (bundledCli is null)
            {
                var status = CreateStatus();
                if (!string.IsNullOrWhiteSpace(status.Warning))
                {
                    warnings.Add(status.Warning);
                }

                return new CliInstallationResult(
                    status,
                    InstalledOrUpdatedCli: false,
                    CreatedOrUpdatedShim: false,
                    UpdatedUserPath: false,
                    warnings);
            }

            Directory.CreateDirectory(paths.InstalledCliDirectory);
            Directory.CreateDirectory(paths.ShimDirectory);

            var installedCliPath = Path.Combine(paths.InstalledCliDirectory, Path.GetFileName(bundledCli.FilePath));
            var installedOrUpdatedCli = SyncCliDirectoryIfNeeded(paths.BundledCliDirectory, paths.InstalledCliDirectory);

            if (!bundledCli.RequiresDotnet)
            {
                TryMakeExecutable(installedCliPath);
            }

            var installedCli = new CliFileDescriptor(installedCliPath, bundledCli.RequiresDotnet);
            var shimContent = CreateShimContent(installedCli);
            var createdOrUpdatedShim = WriteTextIfChanged(paths.ShimPath, shimContent);
            TryMakeExecutable(paths.ShimPath);

            var updatedUserPath = EnsureShimDirectoryIsOnPath(paths.ShimDirectory);
            var statusAfterInstall = CreateStatus();

            if (!statusAfterInstall.IsFullyInstalled && !string.IsNullOrWhiteSpace(statusAfterInstall.Warning))
            {
                warnings.Add(statusAfterInstall.Warning);
            }

            return new CliInstallationResult(
                statusAfterInstall,
                installedOrUpdatedCli,
                createdOrUpdatedShim,
                updatedUserPath,
                warnings);
        }, cancellationToken);
    }

    public Task<CliInstallationStatus> UninstallAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var paths = CreateBasePaths();
            foreach (var candidate in GetKnownInstalledCliCandidates(paths.InstalledCliDirectory))
            {
                DeleteFileIfExists(candidate);
            }

            DeleteFileIfExists(paths.ShimPath);
            DeleteDirectoryIfEmpty(paths.InstalledCliDirectory);
            DeleteDirectoryIfEmpty(paths.ShimDirectory);

            return CreateStatus();
        }, cancellationToken);
    }

    private CliInstallationStatus CreateStatus()
    {
        var paths = CreateBasePaths();
        var bundledCli = ResolveBundledCli(paths.BundledCliDirectory);
        var installedCli = ResolveInstalledCli(paths.InstalledCliDirectory);
        var installedCliPath = installedCli?.FilePath
            ?? Path.Combine(paths.InstalledCliDirectory, bundledCli is null ? GetExecutableFileName() : Path.GetFileName(bundledCli.FilePath));
        paths = paths with
        {
            BundledCliPath = bundledCli?.FilePath,
            InstalledCliPath = installedCliPath,
        };

        var isShimInstalled = File.Exists(paths.ShimPath);
        var isShimCurrent = installedCli is not null
            && isShimInstalled
            && IsTextFileContentEqual(paths.ShimPath, CreateShimContent(installedCli));
        var isShimDirectoryOnCurrentProcessPath = PathListContains(
            _environment.GetProcessEnvironmentVariable(PathVariableName),
            paths.ShimDirectory);
        var isShimDirectoryOnUserPath = IsWindows
            && PathListContains(_environment.GetUserEnvironmentVariable("Path"), paths.ShimDirectory);
        var requiresTerminalRestart = false;
        var requiresManualPathConfiguration = false;
        var isFullyInstalled = installedCli is not null && isShimCurrent;
        var canInstallOrRepair = bundledCli is not null;
        var warning = CreateWarning(
            bundledCli,
            installedCli,
            isShimInstalled,
            isShimCurrent);

        return new CliInstallationStatus(
            paths,
            GetPlatformName(),
            bundledCli is not null,
            installedCli is not null,
            isShimInstalled,
            isShimCurrent,
            isShimDirectoryOnCurrentProcessPath,
            isShimDirectoryOnUserPath,
            requiresTerminalRestart,
            requiresManualPathConfiguration,
            canInstallOrRepair,
            isFullyInstalled,
            CreateSummary(isFullyInstalled, warning),
            warning,
            CreatePathInstructions(paths.ShimDirectory));
    }

    private CliInstallationPaths CreateBasePaths()
    {
        var appBaseDirectory = Path.GetFullPath(_options.AppBaseDirectory);
        var userProfilePath = ResolveUserProfilePath();
        var localApplicationDataPath = ResolveLocalApplicationDataPath(userProfilePath);
        var bundledCliDirectory = Path.Combine(appBaseDirectory, CliDirectoryName);
        var installedCliDirectory = _platform switch
        {
            CliInstallPlatform.Windows => Path.Combine(localApplicationDataPath, PackageName, "cli"),
            CliInstallPlatform.MacOS => Path.Combine(userProfilePath, "Library", "Application Support", PackageName, "cli"),
            _ => Path.Combine(userProfilePath, ".local", "share", PackageName, "cli"),
        };
        var shimDirectory = IsWindows
            ? Path.Combine(localApplicationDataPath, PackageName, "bin")
            : Path.Combine(userProfilePath, ".local", "bin");
        var executableFileName = GetExecutableFileName();

        return new CliInstallationPaths(
            bundledCliDirectory,
            BundledCliPath: null,
            installedCliDirectory,
            Path.Combine(installedCliDirectory, executableFileName),
            shimDirectory,
            Path.Combine(shimDirectory, IsWindows ? $"{CommandName}.cmd" : CommandName));
    }

    private string ResolveUserProfilePath()
    {
        if (!string.IsNullOrWhiteSpace(_options.UserProfilePath))
        {
            return Path.GetFullPath(_options.UserProfilePath);
        }

        var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfilePath))
        {
            return userProfilePath;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    private string ResolveLocalApplicationDataPath(string userProfilePath)
    {
        if (!string.IsNullOrWhiteSpace(_options.LocalApplicationDataPath))
        {
            return Path.GetFullPath(_options.LocalApplicationDataPath);
        }

        var localApplicationDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationDataPath))
        {
            return localApplicationDataPath;
        }

        return Path.Combine(userProfilePath, "AppData", "Local");
    }

    private CliFileDescriptor? ResolveBundledCli(string bundledCliDirectory)
        => ResolveCliFile(bundledCliDirectory);

    private CliFileDescriptor? ResolveInstalledCli(string installedCliDirectory)
        => ResolveCliFile(installedCliDirectory);

    private CliFileDescriptor? ResolveCliFile(string directory)
    {
        var executablePath = Path.Combine(directory, GetExecutableFileName());
        if (File.Exists(executablePath))
        {
            return new CliFileDescriptor(executablePath, RequiresDotnet: false);
        }

        foreach (var assemblyFileName in DotnetAssemblyFileNames)
        {
            var assemblyPath = Path.Combine(directory, assemblyFileName);
            if (File.Exists(assemblyPath))
            {
                return new CliFileDescriptor(assemblyPath, RequiresDotnet: true);
            }
        }

        return null;
    }

    private bool SyncCliDirectoryIfNeeded(string sourceDirectory, string destinationDirectory)
    {
        var changed = false;
        var expectedRelativePaths = new HashSet<string>(IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            expectedRelativePaths.Add(relativePath);

            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            var destinationParent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParent))
            {
                Directory.CreateDirectory(destinationParent);
            }

            if (File.Exists(destinationPath) && FilesHaveSameHash(sourcePath, destinationPath))
            {
                continue;
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);
            changed = true;
        }

        if (Directory.Exists(destinationDirectory))
        {
            foreach (var destinationPath in Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(destinationDirectory, destinationPath);
                if (expectedRelativePaths.Contains(relativePath))
                {
                    continue;
                }

                DeleteFileIfExists(destinationPath);
                changed = true;
            }

            DeleteEmptySubdirectories(destinationDirectory);
        }

        return changed;
    }

    private IEnumerable<string> GetKnownInstalledCliCandidates(string installedCliDirectory)
    {
        yield return Path.Combine(installedCliDirectory, GetExecutableFileName());
        foreach (var assemblyFileName in DotnetAssemblyFileNames)
        {
            yield return Path.Combine(installedCliDirectory, assemblyFileName);
        }
    }

    private string GetExecutableFileName()
        => IsWindows ? $"{CommandName}.exe" : CommandName;

    private string CreateShimContent(CliFileDescriptor installedCli)
    {
        if (IsWindows)
        {
            return installedCli.RequiresDotnet
                ? $"@echo off{Environment.NewLine}dotnet \"{EscapeBatchPath(installedCli.FilePath)}\" %*{Environment.NewLine}"
                : $"@echo off{Environment.NewLine}\"{EscapeBatchPath(installedCli.FilePath)}\" %*{Environment.NewLine}";
        }

        return installedCli.RequiresDotnet
            ? $"#!/usr/bin/env sh\nexec dotnet \"{EscapePosixDoubleQuotedPath(installedCli.FilePath)}\" \"$@\"\n"
            : $"#!/usr/bin/env sh\nexec \"{EscapePosixDoubleQuotedPath(installedCli.FilePath)}\" \"$@\"\n";
    }

    private static bool WriteTextIfChanged(string path, string content)
    {
        if (IsTextFileContentEqual(path, content))
        {
            return false;
        }

        File.WriteAllText(path, content);
        return true;
    }

    private bool EnsureShimDirectoryIsOnPath(string shimDirectory)
    {
        if (!IsWindows || !_options.UpdateUserPath)
        {
            return false;
        }

        if (PathListContains(_environment.GetProcessEnvironmentVariable(PathVariableName), shimDirectory)
            || PathListContains(_environment.GetUserEnvironmentVariable("Path"), shimDirectory))
        {
            return false;
        }

        var currentUserPath = _environment.GetUserEnvironmentVariable("Path")
            ?? _environment.GetUserEnvironmentVariable(PathVariableName);
        var updatedPath = AppendPathEntry(currentUserPath, shimDirectory);
        _environment.SetUserEnvironmentVariable("Path", updatedPath);
        _environment.BroadcastEnvironmentChanged();
        return true;
    }

    private bool PathListContains(string? pathValue, string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return false;
        }

        var separator = IsWindows ? ';' : ':';
        return pathValue
            .Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => PathsEqual(entry, expectedPath));
    }

    private string AppendPathEntry(string? pathValue, string pathEntry)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return pathEntry;
        }

        var trimmedPath = pathValue.Trim();
        var separator = IsWindows ? ';' : ':';
        if (trimmedPath.EndsWith(separator))
        {
            return $"{trimmedPath}{pathEntry}";
        }

        return $"{trimmedPath}{separator}{pathEntry}";
    }

    private bool PathsEqual(string path, string expectedPath)
    {
        var comparison = IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(NormalizePathForComparison(path), NormalizePathForComparison(expectedPath), comparison);
    }

    private static string NormalizePathForComparison(string path)
    {
        var normalized = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        try
        {
            normalized = Path.GetFullPath(normalized);
        }
        catch
        {
            // Keep the raw expanded value when the PATH entry is not a valid file-system path.
        }

        return normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string CreateWarning(
        CliFileDescriptor? bundledCli,
        CliFileDescriptor? installedCli,
        bool isShimInstalled,
        bool isShimCurrent)
    {
        if (bundledCli is null)
        {
            return "Bundled Sunder CLI was not found next to the app.";
        }

        if (installedCli is null)
        {
            return "Sunder CLI is not installed for this user.";
        }

        if (!isShimInstalled)
        {
            return "Sunder CLI command shim is missing.";
        }

        if (!isShimCurrent)
        {
            return "Sunder CLI command shim needs repair.";
        }

        return string.Empty;
    }

    private static string CreateSummary(bool isFullyInstalled, string? warning)
    {
        if (isFullyInstalled)
        {
            return "Sunder CLI is installed.";
        }

        if (!string.IsNullOrWhiteSpace(warning))
        {
            return warning;
        }

        return "Not installed.";
    }

    private string CreatePathInstructions(string shimDirectory)
    {
        if (IsWindows)
        {
            return $"Sunder uses this user PATH directory:{Environment.NewLine}{shimDirectory}{Environment.NewLine}{Environment.NewLine}If it was just added, close and reopen your terminal, then run: sunder --help";
        }

        return $"Add this line to your shell profile, then restart your terminal:{Environment.NewLine}export PATH=\"{shimDirectory}:$PATH\"{Environment.NewLine}{Environment.NewLine}After restarting, run: sunder --help";
    }

    private string GetPlatformName()
        => _platform switch
        {
            CliInstallPlatform.Windows => "Windows",
            CliInstallPlatform.MacOS => "macOS",
            _ => "Linux",
        };

    private bool IsWindows => _platform == CliInstallPlatform.Windows;

    private static CliInstallPlatform GetCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return CliInstallPlatform.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return CliInstallPlatform.MacOS;
        }

        return CliInstallPlatform.Linux;
    }

    private static bool FilesHaveSameHash(string firstPath, string secondPath)
        => string.Equals(ComputeSha256(firstPath), ComputeSha256(secondPath), StringComparison.OrdinalIgnoreCase);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsTextFileContentEqual(string path, string content)
        => File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal);

    private static string EscapeBatchPath(string path)
        => path.Replace("%", "%%", StringComparison.Ordinal);

    private static string EscapePosixDoubleQuotedPath(string path)
        => path
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);

    private static void TryMakeExecutable(string path)
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

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Uninstall/repair should best-effort clean stale CLI files.
        }
    }

    private static void DeleteDirectoryIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
            // Leaving an empty directory behind is preferable to failing app startup.
        }
    }

    private static void DeleteEmptySubdirectories(string rootDirectory)
    {
        foreach (var directory in Directory
            .EnumerateDirectories(rootDirectory, "*", SearchOption.AllDirectories)
            .OrderByDescending(directory => directory.Length))
        {
            DeleteDirectoryIfEmpty(directory);
        }
    }

    private sealed record CliFileDescriptor(string FilePath, bool RequiresDotnet);

    private sealed class SystemCliEnvironmentVariableStore : ICliEnvironmentVariableStore
    {
        public static readonly SystemCliEnvironmentVariableStore Instance = new();

        public string? GetProcessEnvironmentVariable(string name)
            => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);

        public string? GetUserEnvironmentVariable(string name)
            => OperatingSystem.IsWindows()
                ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                : null;

        public void SetUserEnvironmentVariable(string name, string? value)
        {
            if (OperatingSystem.IsWindows())
            {
                Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
            }
        }

        public void BroadcastEnvironmentChanged()
        {
            if (OperatingSystem.IsWindows())
            {
                WindowsEnvironmentBroadcaster.BroadcastEnvironmentChanged();
            }
        }
    }

    private static partial class WindowsEnvironmentBroadcaster
    {
        private const int HwndBroadcast = 0xffff;
        private const int WmSettingChange = 0x001a;
        private const int SmtoAbortIfHung = 0x0002;

        public static void BroadcastEnvironmentChanged()
        {
            try
            {
                SendMessageTimeout(
                    HwndBroadcast,
                    WmSettingChange,
                    UIntPtr.Zero,
                    "Environment",
                    SmtoAbortIfHung,
                    5000,
                    out _);
            }
            catch
            {
                // PATH changes still apply for new terminals even if broadcasting fails.
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Classic DllImport avoids requiring unsafe blocks in the app project.")]
        private static extern IntPtr SendMessageTimeout(
            int hWnd,
            int msg,
            UIntPtr wParam,
            string lParam,
            int flags,
            int timeout,
            out UIntPtr result);
    }
}
