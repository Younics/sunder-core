using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed class InstalledPackageStore(RuntimePackagePaths paths)
{
    private const int CurrentSchemaVersion = 3;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
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

        if (state?.SchemaVersion is not 1 and not 2 and not CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Installed package catalog '{paths.StateFilePath}' has an unsupported schema version.");
        }

        if (state.Packages is null)
        {
            throw new InvalidDataException($"Installed package catalog '{paths.StateFilePath}' is missing packages.");
        }
        var packages = NormalizeCatalogForCompatibility(state.Packages);
        ValidateCatalog(packages);
        if (state.SchemaVersion != CurrentSchemaVersion)
        {
            await WriteAsync(packages, cancellationToken);
        }
        return packages;
    }

    public async Task<InstalledPackageRecord?> GetAsync(string packageId, CancellationToken cancellationToken = default)
        => (await ListAsync(cancellationToken)).FirstOrDefault(package =>
            string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    internal async Task WriteAsync(
        IReadOnlyList<InstalledPackageRecord> packages,
        CancellationToken cancellationToken = default)
    {
        packages = NormalizeCatalogForCompatibility(packages);
        ValidateCatalog(packages);
        Directory.CreateDirectory(paths.CatalogRootPath);
        var state = new InstalledPackageStateFile(
            CurrentSchemaVersion,
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

            ValidateProvenance(package);

            if (!paths.IsCanonicalInstalledPath(package.PackageId, package.Version, package.InstallPath))
            {
                throw new InvalidDataException(
                    $"Installed package '{package.PackageId}' path '{package.InstallPath}' is outside its current versioned package root.");
            }

            var expectedManifestPath = Path.GetFullPath(Path.Combine(
                package.InstallPath,
                SunderPackageFormat.ManifestPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathsEqual(expectedManifestPath, package.ManifestPath))
            {
                throw new InvalidDataException(
                    $"Installed package '{package.PackageId}' manifest path '{package.ManifestPath}' is not canonical.");
            }
            if (!IsCanonicalHash(package.ContentIdentity))
            {
                throw new InvalidDataException($"Installed package '{package.PackageId}' has an invalid content identity.");
            }
            if (package.ContentInventory is null
                || package.ContentInventory.Count == 0
                || package.ContentInventory.Count > SunderPackageFormat.MaxContentIndexEntries)
            {
                throw new InvalidDataException($"Installed package '{package.PackageId}' has an invalid content inventory.");
            }
            var inventoryPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var content in package.ContentInventory)
            {
                if (content is null
                    || !ArchiveRelativePath.TryParse(
                        content.Path,
                        SunderPackageFormat.MaxArchivePathLength,
                        SunderPackageFormat.MaxArchivePathDepth,
                        out var contentPath,
                        out _)
                    || !SunderPackageFormat.IsAllowedArchivePath(contentPath.ToString())
                    || SunderPackageFormat.IsContentIndexPath(contentPath.ToString())
                    || !inventoryPaths.Add(contentPath.ToString())
                    || !IsCanonicalHash(content.Sha256)
                    || content.Size < 0)
                {
                    throw new InvalidDataException($"Installed package '{package.PackageId}' has an invalid content inventory entry.");
                }
            }
            if (!inventoryPaths.Contains(SunderPackageFormat.ManifestPath))
            {
                throw new InvalidDataException($"Installed package '{package.PackageId}' content inventory is missing its manifest.");
            }

            if (package.DependsOn is null)
            {
                throw new InvalidDataException($"Installed package '{package.PackageId}' is missing dependencies.");
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

    internal static IReadOnlyList<InstalledPackageRecord> NormalizeCatalogForCompatibility(
        IReadOnlyList<InstalledPackageRecord> packages)
        => packages.Select(static package => package.Provenance is null
                ? package with { Provenance = InstalledPackageProvenanceRecord.Unknown }
                : package)
            .ToArray();

    public InstalledPackageDescriptor ToDescriptor(InstalledPackageRecord package)
    {
        var validation = SunderPackageArchiveInspector.ValidateExtractedPackageAsync(package.InstallPath)
            .GetAwaiter()
            .GetResult();
        if (!validation.Success || validation.Manifest is null)
        {
            throw new InvalidDataException(
                $"Installed package '{package.PackageId}' is invalid: {string.Join(" | ", validation.Errors)}");
        }
        var manifest = validation.Manifest;
        if (!string.Equals(manifest.Id, package.PackageId, StringComparison.Ordinal)
            || !string.Equals(manifest.Version, package.Version, StringComparison.Ordinal)
            || !string.Equals(manifest.Name, package.Name, StringComparison.Ordinal)
            || !string.Equals(manifest.Summary, package.Summary, StringComparison.Ordinal)
            || !string.Equals(manifest.Icon, package.Icon, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Installed package '{package.PackageId}' catalog identity does not match its strict manifest.");
        }
        if (validation.ContentIndex is null
            || !PackageSessionPreparer.InventoryMatches(validation.ContentIndex, package.ContentInventory)
            || !string.Equals(
                PackageSessionPreparer.ComputeContentIdentity(package.InstallPath),
                package.ContentIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Installed package '{package.PackageId}' content identity or inventory does not match its strict package content.");
        }
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
            PackageTargetSelection.GetHostRoles(manifest),
            package.Summary,
            icon,
            package.IsEnabled,
            dependencies,
            package.InstalledAtUtc,
            package.IsEnabled ? null : "Disabled",
            (manifest.UsesContracts ?? [])
                .Where(static use => use is not null)
                .Select(static use => new PackageRpcContractUseDescriptor(
                    use!.ContractId!,
                    use.VersionRange!,
                    use.Required!.Value,
                    (use.Actions ?? []).Where(static action => action is not null).Select(static action => action!).ToArray()))
                .ToArray(),
            (package.Provenance ?? InstalledPackageProvenanceRecord.Unknown).ToDescriptor());
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

    private static bool IsCanonicalHash(string? value)
        => value is { Length: 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateProvenance(InstalledPackageRecord package)
    {
        var provenance = package.Provenance
            ?? throw new InvalidDataException($"Installed package '{package.PackageId}' is missing provenance.");
        if (!Enum.IsDefined(provenance.SourceKind) || !Enum.IsDefined(provenance.VersionPolicy))
        {
            throw new InvalidDataException($"Installed package '{package.PackageId}' has invalid provenance.");
        }

        if (provenance.SourceKind == InstalledPackageSourceKind.Registry)
        {
            Uri normalizedOrigin;
            try
            {
                normalizedOrigin = RegistryOrigin.Normalize(provenance.RegistryOrigin ?? string.Empty);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException($"Installed package '{package.PackageId}' has an invalid Registry origin.", exception);
            }

            if (!string.Equals(normalizedOrigin.AbsoluteUri, provenance.RegistryOrigin, StringComparison.Ordinal)
                || !PackageId.TryParse(provenance.SourcePackageId, out _)
                || !string.Equals(provenance.SourcePackageId, package.PackageId, StringComparison.Ordinal)
                || !IsCanonicalHash(provenance.SourceIdentity)
                || provenance.VersionRange is not null && !PackageVersionRange.TryParse(provenance.VersionRange, out _))
            {
                throw new InvalidDataException($"Installed package '{package.PackageId}' has invalid Registry provenance.");
            }

            var validSelection = provenance.VersionPolicy switch
            {
                InstalledPackageVersionPolicy.FollowTag => !string.IsNullOrWhiteSpace(provenance.RequestedTag)
                    && provenance.RequestedVersion is null,
                InstalledPackageVersionPolicy.ExplicitVersion => SemanticVersion.TryParse(provenance.RequestedVersion, out _)
                    && provenance.RequestedTag is null
                    && provenance.VersionRange is null,
                InstalledPackageVersionPolicy.TransitiveDependency => provenance.RequestedTag is null
                    && provenance.RequestedVersion is null
                    && provenance.VersionRange is null
                    && !provenance.IncludePrerelease,
                _ => false,
            };
            if (!validSelection)
            {
                throw new InvalidDataException($"Installed package '{package.PackageId}' has an invalid Registry version policy.");
            }
            return;
        }

        if (provenance.VersionPolicy != InstalledPackageVersionPolicy.Unmanaged
            || provenance.RegistryOrigin is not null
            || provenance.SourcePackageId is not null
            || provenance.RequestedTag is not null
            || provenance.RequestedVersion is not null
            || provenance.VersionRange is not null
            || provenance.IncludePrerelease
            || provenance.SourceKind == InstalledPackageSourceKind.LocalArchive && !IsCanonicalHash(provenance.SourceIdentity)
            || provenance.SourceKind == InstalledPackageSourceKind.Unknown && provenance.SourceIdentity is not null
            || provenance.SourceKind == InstalledPackageSourceKind.Development && string.IsNullOrWhiteSpace(provenance.SourceIdentity))
        {
            throw new InvalidDataException($"Installed package '{package.PackageId}' has invalid unmanaged provenance.");
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
