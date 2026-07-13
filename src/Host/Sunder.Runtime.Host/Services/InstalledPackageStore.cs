using System.Text.Encodings.Web;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed class InstalledPackageStore(RuntimePackagePaths paths)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<IReadOnlyList<InstalledPackageRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.StateFilePath))
        {
            return [];
        }

        InstalledPackageStateFile? state;
        try
        {
            state = JsonSerializer.Deserialize<InstalledPackageStateFile>(
                await File.ReadAllTextAsync(paths.StateFilePath, cancellationToken),
                JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Installed package catalog '{paths.StateFilePath}' is invalid.", ex);
        }

        if (state?.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Installed package catalog '{paths.StateFilePath}' has an unsupported schema version.");
        }

        ValidateCatalog(state.Packages);
        return state.Packages.ToArray();
    }

    public async Task<InstalledPackageRecord?> GetAsync(string packageId, CancellationToken cancellationToken = default)
        => (await ListAsync(cancellationToken)).FirstOrDefault(package =>
            string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    internal async Task WriteAsync(
        IReadOnlyList<InstalledPackageRecord> packages,
        CancellationToken cancellationToken = default)
    {
        ValidateCatalog(packages);
        Directory.CreateDirectory(paths.CatalogRootPath);
        var state = new InstalledPackageStateFile(
            1,
            packages.OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase).ToArray());
        await DurableJsonDocument.WriteAsync(paths.StateFilePath, state, JsonOptions, cancellationToken);
    }

    internal void ValidateCatalog(IReadOnlyList<InstalledPackageRecord> packages)
    {
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            if (!PackageId.TryParse(package.PackageId, out _))
            {
                throw new InvalidDataException($"Installed package catalog contains invalid package id '{package.PackageId}'.");
            }

            if (!SemanticVersion.TryParse(package.Version, out _))
            {
                throw new InvalidDataException($"Installed package '{package.PackageId}' has invalid version '{package.Version}'.");
            }

            if (!packageIds.Add(package.PackageId))
            {
                throw new InvalidDataException($"Installed package catalog contains duplicate package id '{package.PackageId}'.");
            }

            if (!paths.IsCanonicalInstalledPath(package.PackageId, package.Version, package.InstallPath))
            {
                throw new InvalidDataException(
                    $"Installed package '{package.PackageId}' path '{package.InstallPath}' is outside its current versioned package root.");
            }

            foreach (var dependency in package.DependsOn)
            {
                if (!PackageId.TryParse(dependency.PackageId, out _)
                    || !PackageVersionRange.TryParse(dependency.VersionRange, out _))
                {
                    throw new InvalidDataException(
                        $"Installed package '{package.PackageId}' has invalid dependency '{dependency.PackageId}' range '{dependency.VersionRange}'.");
                }
            }
        }
    }

    public InstalledPackageDescriptor ToDescriptor(InstalledPackageRecord package)
    {
        var icon = string.IsNullOrWhiteSpace(package.Icon)
            ? null
            : new PackageIconDescriptor(null, package.Icon);
        var dependencies = package.DependsOn
            .Select(dependency => new PackageDependencyDescriptor(dependency.PackageId, dependency.VersionRange))
            .ToArray();
        return new InstalledPackageDescriptor(
            package.PackageId,
            package.Name,
            package.Version,
            package.Summary,
            icon,
            package.IsEnabled,
            dependencies,
            package.InstalledAtUtc,
            package.IsEnabled ? null : "Disabled");
    }

    public async Task<string?> TryResolvePackageAssetPathAsync(
        string packageId,
        string assetPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(assetPath))
        {
            return null;
        }

        var package = await GetAsync(packageId, cancellationToken);
        return package is null
            ? null
            : PackageAssetPathResolver.TryResolveInstalledAssetPath(package.InstallPath, assetPath);
    }
}
