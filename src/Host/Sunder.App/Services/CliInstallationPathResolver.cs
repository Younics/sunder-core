namespace Sunder.App.Services;

internal sealed class CliInstallationPathResolver(CliInstallationOptions options, CliInstallPlatform platform)
{
    private const string PackageName = "Sunder";
    private const string CliDirectoryName = "Cli";

    public CliInstallationPaths CreateBasePaths()
    {
        var appBaseDirectory = Path.GetFullPath(options.AppBaseDirectory);
        var userProfilePath = ResolveUserProfilePath();
        var localApplicationDataPath = ResolveLocalApplicationDataPath(userProfilePath);
        var bundledCliDirectory = Path.Combine(appBaseDirectory, CliDirectoryName);
        var installedCliDirectory = platform switch
        {
            CliInstallPlatform.Windows => Path.Combine(localApplicationDataPath, PackageName, "cli"),
            CliInstallPlatform.MacOS => Path.Combine(userProfilePath, "Library", "Application Support", PackageName, "cli"),
            _ => Path.Combine(userProfilePath, ".local", "share", PackageName, "cli"),
        };
        var shimDirectory = platform == CliInstallPlatform.Windows
            ? Path.Combine(localApplicationDataPath, PackageName, "bin")
            : Path.Combine(userProfilePath, ".local", "bin");
        var executableFileName = CliShim.GetExecutableFileName(platform);

        return new CliInstallationPaths(
            bundledCliDirectory,
            BundledCliPath: null,
            installedCliDirectory,
            Path.Combine(installedCliDirectory, executableFileName),
            shimDirectory,
            Path.Combine(shimDirectory, platform == CliInstallPlatform.Windows ? "sunder.cmd" : "sunder"));
    }

    private string ResolveUserProfilePath()
    {
        if (!string.IsNullOrWhiteSpace(options.UserProfilePath))
        {
            return Path.GetFullPath(options.UserProfilePath);
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
        if (!string.IsNullOrWhiteSpace(options.LocalApplicationDataPath))
        {
            return Path.GetFullPath(options.LocalApplicationDataPath);
        }

        var localApplicationDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationDataPath))
        {
            return localApplicationDataPath;
        }

        return Path.Combine(userProfilePath, "AppData", "Local");
    }
}
