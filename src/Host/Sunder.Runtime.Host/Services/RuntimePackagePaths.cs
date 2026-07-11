using Sunder.Runtime.Client;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackagePaths
{
    public RuntimePackagePaths()
        : this(RuntimeLocalState.GetV1RootPath())
    {
    }

    public RuntimePackagePaths(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
        CatalogRootPath = Path.Combine(RootPath, "catalog");
        InstalledRootPath = Path.Combine(RootPath, "installed");
        StagingRootPath = Path.Combine(RootPath, "staging");
        TransactionRootPath = Path.Combine(RootPath, "transactions");
        TombstoneRootPath = Path.Combine(RootPath, "tombstones");
        TransferRootPath = Path.Combine(RootPath, "transfers");
        PackageDataRootPath = Path.Combine(RootPath, "package-data");
        RegistryCredentialRootPath = Path.Combine(RootPath, "credentials", "registry");
        RegistryCredentialFilePath = Path.Combine(RegistryCredentialRootPath, "credentials.enc.json");
        StateFilePath = Path.Combine(CatalogRootPath, "installed-packages.json");
        SchemaFilePath = Path.Combine(RootPath, RuntimeLocalState.SchemaFileName);
        LeaseFilePath = Path.Combine(RootPath, RuntimeLocalState.LeaseFileName);
    }

    public string RootPath { get; }

    public string CatalogRootPath { get; }

    public string InstalledRootPath { get; }

    public string StagingRootPath { get; }

    public string TransactionRootPath { get; }

    public string TombstoneRootPath { get; }

    public string TransferRootPath { get; }

    public string PackageDataRootPath { get; }

    public string RegistryCredentialRootPath { get; }

    public string RegistryCredentialFilePath { get; }

    public string StateFilePath { get; }

    public string SchemaFilePath { get; }

    public string LeaseFilePath { get; }

    public string CreateStagingPath() => Path.Combine(StagingRootPath, Guid.NewGuid().ToString("N"));

    public string GetInstalledPackagePath(string packageId, string version)
        => Path.Combine(InstalledRootPath, packageId, version);

    public bool IsCanonicalInstalledPath(string packageId, string version, string path)
    {
        var expected = Path.GetFullPath(GetInstalledPackagePath(packageId, version));
        var candidate = Path.GetFullPath(path);
        return string.Equals(expected, candidate, PathComparison);
    }

    public bool IsWithinPackageRoot(string path)
    {
        var root = RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        return candidate.StartsWith(root, PathComparison);
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
