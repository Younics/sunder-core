using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using static Sunder.Runtime.Host.Services.PackageProtocolMapper;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionPreparer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public PreparedRuntimePackage? PrepareDevPackage(
        int index,
        string folder,
        string sessionFolder,
        PackageSessionFileMaterializer fileMaterializer,
        ICollection<string> errors)
    {
        if (!Directory.Exists(folder))
        {
            errors.Add($"Dev package folder '{folder}' does not exist.");
            return null;
        }

        var manifestPath = Path.Combine(folder, "sunder-package.json");
        if (!File.Exists(manifestPath))
        {
            errors.Add($"Dev package folder '{folder}' does not contain sunder-package.json.");
            return null;
        }

        var shadowFolder = Path.Combine(sessionFolder, $"{index:D2}-{SanitizeFolderName(Path.GetFileName(folder))}");
        fileMaterializer.MaterializeDirectory(folder, shadowFolder);
        return PrepareMaterializedPackage(
            folder,
            new RuntimePackageSource(string.Empty, PackageSourceKind.Dev, folder, shadowFolder),
            shadowFolder,
            errors);
    }

    public PreparedRuntimePackage? PrepareInstalledPackage(
        int index,
        InstalledPackageRecord package,
        string sessionFolder,
        PackageSessionFileMaterializer fileMaterializer,
        ICollection<string> errors)
    {
        if (!Directory.Exists(package.InstallPath))
        {
            errors.Add($"Installed package folder '{package.InstallPath}' does not exist.");
            return null;
        }

        if (!File.Exists(package.ManifestPath))
        {
            errors.Add($"Installed package '{package.PackageId}' does not contain manifest/sunder-package.json.");
            return null;
        }

        var shadowFolder = Path.Combine(sessionFolder, $"{index:D2}-{SanitizeFolderName(package.PackageId)}");
        Directory.CreateDirectory(shadowFolder);
        var shadowManifestPath = Path.Combine(shadowFolder, "sunder-package.json");
        File.Copy(package.ManifestPath, shadowManifestPath, overwrite: true);
        if (Directory.Exists(package.LibraryFolder))
        {
            fileMaterializer.MaterializeDirectory(package.LibraryFolder, Path.Combine(shadowFolder, "lib"));
        }

        var assetFolder = Path.Combine(package.InstallPath, "payload", "assets");
        if (Directory.Exists(assetFolder))
        {
            fileMaterializer.MaterializeDirectory(assetFolder, Path.Combine(shadowFolder, "assets"));
        }

        return PrepareMaterializedPackage(
            package.InstallPath,
            new RuntimePackageSource(package.PackageId, PackageSourceKind.Installed, package.InstallPath, shadowFolder),
            shadowFolder,
            errors);
    }

    public static RuntimePackageActivationState ToActivationState(InstalledPackageRecord package)
    {
        var manifest = JsonSerializer.Deserialize<SunderPackageManifest>(File.ReadAllText(package.ManifestPath), JsonOptions)
            ?? throw new InvalidDataException($"Installed package '{package.PackageId}' has an invalid manifest.");
        return new(
            package.PackageId,
            package.Name,
            package.Version,
            ToHostRoles(manifest.HostRoles ?? []),
            package.Icon);
    }

    private static PreparedRuntimePackage? PrepareMaterializedPackage(
        string sourceFolder,
        RuntimePackageSource source,
        string shadowFolder,
        ICollection<string> errors)
    {
        var shadowManifestPath = Path.Combine(shadowFolder, "sunder-package.json");
        SunderPackageManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SunderPackageManifest>(File.ReadAllText(shadowManifestPath), JsonOptions);
        }
        catch (Exception exception)
        {
            errors.Add($"Failed to parse '{shadowManifestPath}': {exception.Message}");
            return null;
        }

        var validationErrors = SunderPackageManifestValidator.Validate(
                manifest,
                shadowFolder,
                SunderPackageManifestLayout.Activation)
            .Concat(manifest is null ? [] : SunderSdkCompatibilityProfile.Validate(manifest))
            .ToArray();
        if (validationErrors.Length > 0)
        {
            foreach (var validationError in validationErrors) errors.Add(validationError);
            return null;
        }

        var libraryFolder = Path.Combine(shadowFolder, "lib");
        var dependencies = (manifest!.DependsOn ?? [])
            .Where(static dependency => !string.IsNullOrWhiteSpace(dependency.PackageId)
                                        && !string.IsNullOrWhiteSpace(dependency.VersionRange))
            .Select(static dependency => new PackageDependencyDescriptor(
                dependency.PackageId!,
                dependency.VersionRange!))
            .ToArray();
        var hostRoles = ToHostRoles(manifest.HostRoles!);
        var preparedSource = source with
        {
            PackageId = manifest.Id!,
            HostRoles = hostRoles,
            Dependencies = dependencies,
        };
        return new PreparedRuntimePackage(
            sourceFolder,
            preparedSource,
            shadowFolder,
            libraryFolder,
            manifest.Id!,
            manifest.Version!,
            hostRoles,
            new RuntimePackageActivationState(manifest.Id!, manifest.Name!, manifest.Version!, hostRoles, manifest.Icon),
            Path.Combine(libraryFolder, manifest.EntryAssembly!),
            dependencies);
    }

    internal static PackageHostRoles ToHostRoles(IReadOnlyList<string> roles)
    {
        if (roles.Count == 1 && string.Equals(roles[0], SunderPackageFormat.ContractOnlyHostRole, StringComparison.Ordinal))
        {
            return PackageHostRoles.ContractOnly;
        }
        if (roles.Count == 0
            || roles.Contains(SunderPackageFormat.ContractOnlyHostRole, StringComparer.Ordinal)
            || roles.Any(role => role is not SunderPackageFormat.AppHostRole and not SunderPackageFormat.RuntimeHostRole))
        {
            throw new InvalidDataException("Package manifest declares invalid hostRoles metadata.");
        }
        var value = PackageHostRoles.ContractOnly;
        if (roles.Contains(SunderPackageFormat.AppHostRole, StringComparer.Ordinal)) value |= PackageHostRoles.App;
        if (roles.Contains(SunderPackageFormat.RuntimeHostRole, StringComparer.Ordinal)) value |= PackageHostRoles.Runtime;
        return value;
    }

    private static string SanitizeFolderName(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return "package";
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(folderName.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
    }
}
