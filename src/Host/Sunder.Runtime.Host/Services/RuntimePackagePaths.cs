namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackagePaths
{
    public RuntimePackagePaths()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sunder",
            "runtime",
            "packages"))
    {
    }

    public RuntimePackagePaths(string rootPath)
    {
        RootPath = rootPath;
        InstalledRootPath = Path.Combine(rootPath, "installed");
        StagingRootPath = Path.Combine(rootPath, "staging");
        StateFilePath = Path.Combine(rootPath, "installed-packages.json");
    }

    public string RootPath { get; }

    public string InstalledRootPath { get; }

    public string StagingRootPath { get; }

    public string StateFilePath { get; }

    public string CreateStagingPath() => Path.Combine(StagingRootPath, Guid.NewGuid().ToString("N"));

    public string GetInstalledPackagePath(string packageId, string version)
        => Path.Combine(InstalledRootPath, packageId, version);
}
