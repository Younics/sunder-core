using System.Reflection;
using System.Runtime.Loader;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Build.Tasks;

public sealed class GenerateSunderPackageManifestTask : Microsoft.Build.Utilities.Task
{
    private static readonly Regex PackageIdRegex = new("^[a-z0-9]+(\\.[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SemVerRegex = new("^\\d+\\.\\d+\\.\\d+([-.+][0-9A-Za-z.-]+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [Required]
    public string TargetAssemblyPath { get; set; } = string.Empty;

    [Required]
    public string ManifestOutputPath { get; set; } = string.Empty;

    [Required]
    public string EntryAssembly { get; set; } = string.Empty;

    [Required]
    public string PackageVersion { get; set; } = string.Empty;

    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    public string? TargetFramework { get; set; }

    public string? SdkVersion { get; set; }

    public override bool Execute()
    {
        if (!File.Exists(TargetAssemblyPath))
        {
            Log.LogError($"Sunder package entry assembly was not found at '{TargetAssemblyPath}'.");
            return false;
        }

        var metadata = ReadPackageMetadata();
        if (metadata is null)
        {
            return false;
        }

        if (!ValidateMetadata(metadata))
        {
            return false;
        }

        var manifest = new GeneratedSunderPackageManifest(
            ManifestVersion: 1,
            Id: metadata.Id,
            Name: metadata.Name,
            Summary: string.IsNullOrWhiteSpace(metadata.Summary) ? null : metadata.Summary,
            Version: PackageVersion,
            EntryAssembly: EntryAssembly,
            Icon: string.IsNullOrWhiteSpace(metadata.Icon) ? null : NormalizePath(metadata.Icon),
            DependsOn: metadata.Dependencies.Count == 0 ? null : metadata.Dependencies,
            SdkVersion: string.IsNullOrWhiteSpace(SdkVersion) ? null : SdkVersion,
            TargetFramework: string.IsNullOrWhiteSpace(TargetFramework) ? null : TargetFramework);

        var manifestDirectory = Path.GetDirectoryName(ManifestOutputPath);
        if (!string.IsNullOrWhiteSpace(manifestDirectory))
        {
            Directory.CreateDirectory(manifestDirectory);
        }

        File.WriteAllText(ManifestOutputPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine);
        Log.LogMessage(MessageImportance.High, $"Generated Sunder package manifest at {ManifestOutputPath}");
        return !Log.HasLoggedErrors;
    }

    private SunderPackageMetadata? ReadPackageMetadata()
    {
        var assemblyDirectory = Path.GetDirectoryName(TargetAssemblyPath)!;
        var loadContext = new PackageMetadataLoadContext(assemblyDirectory);
        try
        {
            using var stream = File.OpenRead(TargetAssemblyPath);
            var assembly = loadContext.LoadFromStream(stream);
            var attributes = assembly.GetCustomAttributesData();
            var packageAttributes = attributes
                .Where(attribute => attribute.AttributeType.FullName == typeof(SunderPackageAttribute).FullName)
                .ToArray();

            if (packageAttributes.Length == 0)
            {
                Log.LogError($"Sunder package assembly '{TargetAssemblyPath}' must declare one SunderPackage attribute.");
                return null;
            }

            if (packageAttributes.Length > 1)
            {
                Log.LogError($"Sunder package assembly '{TargetAssemblyPath}' declares multiple SunderPackage attributes.");
                return null;
            }

            var packageAttribute = packageAttributes[0];
            var dependencies = attributes
                .Where(attribute => attribute.AttributeType.FullName == typeof(SunderPackageDependencyAttribute).FullName)
                .Select(attribute => new GeneratedSunderPackageDependency(
                    PackageId: GetNamedString(attribute, nameof(SunderPackageDependencyAttribute.PackageId)) ?? string.Empty,
                    VersionRange: GetNamedString(attribute, nameof(SunderPackageDependencyAttribute.VersionRange)) ?? string.Empty))
                .ToArray();

            return new SunderPackageMetadata(
                Id: GetNamedString(packageAttribute, nameof(SunderPackageAttribute.Id)) ?? string.Empty,
                Name: GetNamedString(packageAttribute, nameof(SunderPackageAttribute.Name)) ?? string.Empty,
                Summary: GetNamedString(packageAttribute, nameof(SunderPackageAttribute.Summary)),
                Icon: GetNamedString(packageAttribute, nameof(SunderPackageAttribute.Icon)),
                Dependencies: dependencies);
        }
        catch (ReflectionTypeLoadException ex)
        {
            Log.LogError($"Failed to inspect Sunder package metadata in '{TargetAssemblyPath}': {ex.Message}");
            foreach (var loaderException in ex.LoaderExceptions.Where(static exception => exception is not null))
            {
                Log.LogError(loaderException!.Message);
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: false);
            return null;
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private bool ValidateMetadata(SunderPackageMetadata metadata)
    {
        ValidatePackageId(metadata.Id, "package id");

        if (string.IsNullOrWhiteSpace(metadata.Name))
        {
            Log.LogError("Sunder package metadata must include Name.");
        }

        if (string.IsNullOrWhiteSpace(PackageVersion) || !SemVerRegex.IsMatch(PackageVersion))
        {
            Log.LogError($"Sunder package version '{PackageVersion}' must be SemVer-compatible.");
        }

        if (string.IsNullOrWhiteSpace(EntryAssembly))
        {
            Log.LogError("Sunder package entry assembly name is required.");
        }

        if (!string.IsNullOrWhiteSpace(metadata.Icon))
        {
            ValidateRelativePath(metadata.Icon, "package icon");
            if (!PackageAssetExists(metadata.Icon))
            {
                Log.LogError($"Sunder package icon '{metadata.Icon}' does not exist under '{ProjectDirectory}'.");
            }
        }

        var seenDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in metadata.Dependencies)
        {
            ValidatePackageId(dependency.PackageId, "dependency package id");
            if (!seenDependencies.Add(dependency.PackageId))
            {
                Log.LogError($"Sunder package dependency '{dependency.PackageId}' is declared more than once.");
            }

            if (string.IsNullOrWhiteSpace(dependency.VersionRange))
            {
                Log.LogError($"Sunder package dependency '{dependency.PackageId}' must include VersionRange.");
            }
        }

        return !Log.HasLoggedErrors;
    }

    private void ValidatePackageId(string packageId, string label)
    {
        if (string.IsNullOrWhiteSpace(packageId) || !PackageIdRegex.IsMatch(packageId))
        {
            Log.LogError($"Sunder {label} '{packageId}' must use lowercase dot-separated ASCII identifiers.");
        }
    }

    private void ValidateRelativePath(string path, string label)
    {
        if (Path.IsPathRooted(path))
        {
            Log.LogError($"Sunder {label} path '{path}' must be relative.");
            return;
        }

        var normalizedSegments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (normalizedSegments.Any(static segment => segment == ".."))
        {
            Log.LogError($"Sunder {label} path '{path}' must not contain parent directory traversal.");
        }
    }

    private bool PackageAssetExists(string assetPath)
    {
        var normalizedPath = assetPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (File.Exists(Path.Combine(ProjectDirectory, normalizedPath)))
        {
            return true;
        }

        const string assetsPrefix = "assets/";
        var forwardPath = assetPath.Replace('\\', '/');
        if (!forwardPath.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sourceAssetPath = Path.Combine(
            ProjectDirectory,
            "Assets",
            forwardPath[assetsPrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(sourceAssetPath);
    }

    private static string? GetNamedString(CustomAttributeData attribute, string name)
        => attribute.NamedArguments.FirstOrDefault(argument => argument.MemberName == name).TypedValue.Value as string;

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private sealed record SunderPackageMetadata(
        string Id,
        string Name,
        string? Summary,
        string? Icon,
        IReadOnlyList<GeneratedSunderPackageDependency> Dependencies);

    private sealed record GeneratedSunderPackageManifest(
        int ManifestVersion,
        string Id,
        string Name,
        string? Summary,
        string Version,
        string EntryAssembly,
        string? Icon,
        IReadOnlyList<GeneratedSunderPackageDependency>? DependsOn,
        string? SdkVersion,
        string? TargetFramework);

    private sealed record GeneratedSunderPackageDependency(string PackageId, string VersionRange);

    private sealed class PackageMetadataLoadContext(string assemblyDirectory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var sdkAssembly = typeof(SunderPackageAttribute).Assembly;
            if (AssemblyName.ReferenceMatchesDefinition(assemblyName, sdkAssembly.GetName()))
            {
                return sdkAssembly;
            }

            if (assemblyName.Name is null)
            {
                return null;
            }

            var candidatePath = Path.Combine(assemblyDirectory, assemblyName.Name + ".dll");
            return File.Exists(candidatePath) ? LoadFromAssemblyPath(candidatePath) : null;
        }
    }
}
