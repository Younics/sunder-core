using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Tests;

internal static class CanonicalPackageTestBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string WriteExplodedPackage(
        string root,
        string packageId,
        string version,
        string entryAssemblySource,
        IReadOnlyList<string>? roles = null,
        IReadOnlyList<InstalledPackageDependencyRecord>? dependencies = null,
        IReadOnlyList<string>? runtimeIdentifiers = null,
        string appTargetKind = SunderPackageFormat.AvaloniaTargetKind,
        string runtimeTargetKind = SunderPackageFormat.DotnetTargetKind,
        string? appEntryPoint = null,
        string? runtimeEntryPoint = null,
        IReadOnlyList<SunderPackageWebViewManifest?>? appViews = null,
        IReadOnlyList<SunderPackageContractBundleManifest?>? contractBundles = null,
        IReadOnlyList<SunderPackageContractUseManifest?>? usesContracts = null)
    {
        roles ??= [SunderPackageFormat.AppHostRole, SunderPackageFormat.RuntimeHostRole];
        runtimeIdentifiers ??= [RuntimeInformation.RuntimeIdentifier];
        Directory.CreateDirectory(Path.Combine(root, "manifest"));
        var libraryFolder = Path.Combine(root, "payload", "shared", "lib");
        Directory.CreateDirectory(libraryFolder);
        var entryAssemblyName = Path.GetFileName(entryAssemblySource);
        File.Copy(entryAssemblySource, Path.Combine(libraryFolder, entryAssemblyName), overwrite: true);

        var targets = roles
            .SelectMany(role => runtimeIdentifiers.Select(rid => new SunderPackageTargetManifest
            {
                Role = role,
                Rid = rid,
                Kind = role == SunderPackageFormat.AppHostRole
                    ? appTargetKind
                    : runtimeTargetKind,
                EntryPoint = role == SunderPackageFormat.AppHostRole
                    ? appEntryPoint ?? $"lib/{entryAssemblyName}"
                    : runtimeEntryPoint ?? $"lib/{entryAssemblyName}",
                TargetFramework = "net10.0",
                SdkVersion = "1.1.0",
                RequiredHostCapabilities = ["sdk-baseline-1-1.v1", "core.v1"],
                Views = role == SunderPackageFormat.AppHostRole ? appViews : null,
            }))
            .Cast<SunderPackageTargetManifest?>()
            .ToArray();
        var manifest = new SunderPackageManifest
        {
            ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion,
            ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
            Id = packageId,
            Name = packageId,
            Version = version,
            DependsOn = (dependencies ?? [])
                .Select(dependency => new SunderPackageDependencyManifest
                {
                    PackageId = dependency.PackageId,
                    VersionRange = dependency.VersionRange,
                })
                .ToArray(),
            ContractBundles = contractBundles ?? [],
            UsesContracts = usesContracts ?? [],
            Targets = targets,
        };
        var manifestPath = Path.Combine(root, "manifest", "sunder-package.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        WriteContentIndex(root);
        return root;
    }

    public static InstalledPackageRecord CreateInstalledRecord(
        string root,
        string packageId,
        string version,
        bool isEnabled = true,
        IReadOnlyList<InstalledPackageDependencyRecord>? dependencies = null)
    {
        var validation = SunderPackageArchiveInspector.ValidateExtractedPackageAsync(root)
            .GetAwaiter()
            .GetResult();
        if (!validation.Success || validation.Manifest is null || validation.ContentIndex?.Files is null)
        {
            throw new InvalidDataException(string.Join(" | ", validation.Errors));
        }
        return new InstalledPackageRecord(
            packageId,
            validation.Manifest.Name!,
            validation.Manifest.Summary,
            version,
            validation.Manifest.Icon,
            root,
            Path.Combine(root, "manifest", "sunder-package.json"),
            ComputeContentIdentity(root),
            validation.ContentIndex.Files
                .Select(entry => new InstalledPackageContentRecord(entry!.Path!, entry.Sha256!, entry.Size))
                .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
                .ToArray(),
            dependencies ?? [],
            isEnabled,
            DateTimeOffset.UtcNow);
    }

    public static async Task<string> CreateArchiveAsync(string sourceRoot)
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sunderpkg");
        await Task.Run(() => ZipFile.CreateFromDirectory(sourceRoot, archivePath));
        return archivePath;
    }

    public static RuntimePackageTargetSource CreateTargetSource(
        string root,
        PackageSourceKind sourceKind,
        string role = SunderPackageFormat.AppHostRole,
        string? rid = null)
    {
        var manifest = JsonSerializer.Deserialize<SunderPackageManifest>(
                           File.ReadAllText(Path.Combine(root, "manifest", "sunder-package.json")))
                       ?? throw new InvalidDataException("Test package manifest could not be read.");
        var contentIndex = JsonSerializer.Deserialize<SunderPackageContentIndex>(
                               File.ReadAllText(Path.Combine(root, "manifest", "content-index.json")))
                           ?? throw new InvalidDataException("Test package content index could not be read.");
        var key = new SunderPackageTargetKey(role, rid ?? RuntimeInformation.RuntimeIdentifier);
        if (!SunderPackageTargetResolver.TryResolveTarget(manifest, key, out var target))
        {
            throw new InvalidDataException($"Test package does not declare target '{key}'.");
        }

        var source = new RuntimePackageSource(
            manifest.Id!,
            sourceKind,
            root,
            root,
            role == SunderPackageFormat.AppHostRole ? PackageHostRoles.App : PackageHostRoles.Runtime,
            Manifest: manifest,
            ContentIndex: contentIndex,
            SelectedTargetKey: key,
            SelectedTarget: target);
        return new RuntimePackageTargetSource(source, key, target!);
    }

    public static void WriteContentIndex(string root)
    {
        var indexPath = Path.Combine(root, "manifest", "content-index.json");
        var entries = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !PathsEqual(path, indexPath))
            .Select(path =>
            {
                using var stream = File.OpenRead(path);
                return new SunderPackageContentIndexEntry(
                    Path.GetRelativePath(root, path).Replace('\\', '/'),
                    Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
                    stream.Length);
            })
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        File.WriteAllText(
            indexPath,
            JsonSerializer.Serialize(
                new SunderPackageContentIndex(SunderPackageFormat.CurrentContentIndexVersion, entries),
                JsonOptions));
    }

    private static string ComputeContentIdentity(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in new[]
                 {
                     Path.Combine(root, "manifest", "sunder-package.json"),
                     Path.Combine(root, "manifest", "content-index.json"),
                 })
        {
            using var stream = File.OpenRead(path);
            hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
