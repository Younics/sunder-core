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
    private const string PathVariableName = "PATH";
    private static readonly string[] DotnetAssemblyFileNames = ["sunder.dll", "Sunder.Cli.dll"];

    private readonly CliInstallationOptions _options;
    private readonly ICliEnvironmentVariableStore _environment;
    private readonly CliInstallPlatform _platform;
    private readonly CliInstallationPathResolver _pathResolver;
    private readonly SemaphoreSlim _operationSemaphore = new(1, 1);

    public CliInstallationService(
        CliInstallationOptions? options = null,
        ICliEnvironmentVariableStore? environment = null)
    {
        _options = options ?? new CliInstallationOptions();
        _environment = environment ?? SystemCliEnvironmentVariableStore.Instance;
        _platform = _options.Platform ?? GetCurrentPlatform();
        _pathResolver = new CliInstallationPathResolver(_options, _platform);
    }

    public Task<CliInstallationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => RunSerializedAsync(CreateStatus, cancellationToken);

    public Task<CliInstallationResult> EnsureInstalledAsync(CancellationToken cancellationToken = default)
        => RunSerializedAsync(EnsureInstalled, cancellationToken);

    public Task<CliInstallationStatus> UninstallAsync(CancellationToken cancellationToken = default)
        => RunSerializedAsync(Uninstall, cancellationToken);

    private CliInstallationResult EnsureInstalled()
    {
        var warnings = new List<string>();
        var paths = _pathResolver.CreateBasePaths();
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
        var installedOrUpdatedCli = CliFileSystem.SyncDirectoryIfNeeded(
            paths.BundledCliDirectory,
            paths.InstalledCliDirectory,
            IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        if (!bundledCli.RequiresDotnet)
        {
            CliShim.TryMakeExecutable(installedCliPath);
        }

        var installedCli = new CliFileDescriptor(installedCliPath, bundledCli.RequiresDotnet);
        var shimContent = CliShim.CreateContent(installedCli, _platform);
        var createdOrUpdatedShim = WriteTextIfChanged(paths.ShimPath, shimContent);
        CliShim.TryMakeExecutable(paths.ShimPath);

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
    }

    private CliInstallationStatus Uninstall()
    {
        var paths = _pathResolver.CreateBasePaths();
        CliFileSystem.DeleteDirectoryIfExists(paths.InstalledCliDirectory);
        CliFileSystem.DeleteFileIfExists(paths.ShimPath);
        CliFileSystem.DeleteDirectoryIfEmpty(paths.ShimDirectory);

        return CreateStatus();
    }

    private async Task<T> RunSerializedAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _operationSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }

    private CliInstallationStatus CreateStatus()
    {
        var paths = _pathResolver.CreateBasePaths();
        var bundledCli = ResolveBundledCli(paths.BundledCliDirectory);
        var installedCli = ResolveInstalledCli(paths.InstalledCliDirectory);
        var installedCliPath = installedCli?.FilePath
            ?? Path.Combine(paths.InstalledCliDirectory, bundledCli is null ? CliShim.GetExecutableFileName(_platform) : Path.GetFileName(bundledCli.FilePath));
        paths = paths with
        {
            BundledCliPath = bundledCli?.FilePath,
            InstalledCliPath = installedCliPath,
        };

        var isShimInstalled = File.Exists(paths.ShimPath);
        var isShimCurrent = installedCli is not null
            && isShimInstalled
            && IsTextFileContentEqual(paths.ShimPath, CliShim.CreateContent(installedCli, _platform));
        var isShimDirectoryOnCurrentProcessPath = CliPathList.Contains(
            _environment.GetProcessEnvironmentVariable(PathVariableName),
            paths.ShimDirectory,
            IsWindows);
        var isShimDirectoryOnUserPath = IsWindows
            && CliPathList.Contains(_environment.GetUserEnvironmentVariable("Path"), paths.ShimDirectory, IsWindows);
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

    private CliFileDescriptor? ResolveBundledCli(string bundledCliDirectory)
        => ResolveCliFile(bundledCliDirectory);

    private CliFileDescriptor? ResolveInstalledCli(string installedCliDirectory)
        => ResolveCliFile(installedCliDirectory);

    private CliFileDescriptor? ResolveCliFile(string directory)
    {
        var executablePath = Path.Combine(directory, CliShim.GetExecutableFileName(_platform));
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

        if (CliPathList.Contains(_environment.GetProcessEnvironmentVariable(PathVariableName), shimDirectory, IsWindows)
            || CliPathList.Contains(_environment.GetUserEnvironmentVariable("Path"), shimDirectory, IsWindows))
        {
            return false;
        }

        var currentUserPath = _environment.GetUserEnvironmentVariable("Path")
            ?? _environment.GetUserEnvironmentVariable(PathVariableName);
        var updatedPath = CliPathList.Append(currentUserPath, shimDirectory, IsWindows);
        _environment.SetUserEnvironmentVariable("Path", updatedPath);
        _environment.BroadcastEnvironmentChanged();
        return true;
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

    private static bool IsTextFileContentEqual(string path, string content)
        => File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal);

}
