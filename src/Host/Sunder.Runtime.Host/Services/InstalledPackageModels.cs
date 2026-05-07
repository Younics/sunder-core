using System.Text.Json.Serialization;

namespace Sunder.Runtime.Host.Services;

internal sealed record InstalledPackageStateFile(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("packages")] IReadOnlyList<InstalledPackageRecord> Packages);

internal sealed record InstalledPackageRecord(
    [property: JsonPropertyName("packageId")] string PackageId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("entryAssembly")] string EntryAssembly,
    [property: JsonPropertyName("icon")] string? Icon,
    [property: JsonPropertyName("dependsOn")] IReadOnlyList<InstalledPackageDependencyRecord> DependsOn,
    [property: JsonPropertyName("installPath")] string InstallPath,
    [property: JsonPropertyName("isEnabled")] bool IsEnabled,
    [property: JsonPropertyName("installedAtUtc")] DateTimeOffset InstalledAtUtc)
{
    public string ManifestPath => TryResolveManifestPath(InstallPath) ?? Path.Combine(InstallPath, "manifest", "sunder-package.json");

    public string LibraryFolder => TryResolveLibraryFolder(InstallPath) ?? Path.Combine(InstallPath, "payload", "lib");

    public string EntryAssemblyPath => Path.Combine(LibraryFolder, EntryAssembly);

    public static string? TryResolveManifestPath(string installPath)
    {
        var packagedManifestPath = Path.Combine(installPath, "manifest", "sunder-package.json");
        if (File.Exists(packagedManifestPath))
        {
            return packagedManifestPath;
        }

        var devManifestPath = Path.Combine(installPath, "sunder-package.json");
        return File.Exists(devManifestPath) ? devManifestPath : null;
    }

    public static string? TryResolveLibraryFolder(string installPath)
    {
        var packagedLibraryFolder = Path.Combine(installPath, "payload", "lib");
        if (Directory.Exists(packagedLibraryFolder))
        {
            return packagedLibraryFolder;
        }

        var devLibraryFolder = Path.Combine(installPath, "lib");
        return Directory.Exists(devLibraryFolder) ? devLibraryFolder : null;
    }
}

internal sealed record InstalledPackageDependencyRecord(
    [property: JsonPropertyName("packageId")] string PackageId,
    [property: JsonPropertyName("versionRange")] string VersionRange);
