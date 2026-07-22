using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Build.Framework;
using Sunder.Package.Format;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Build.Tasks;

/// <summary>MSBuild task for V1 package manifest generation.</summary>
public sealed class GenerateSunderPackageManifestTask : Microsoft.Build.Utilities.Task
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [Required] public string TargetAssemblyPath { get; set; } = string.Empty;
    [Required] public string ManifestOutputPath { get; set; } = string.Empty;
    [Required] public string EntryAssembly { get; set; } = string.Empty;
    [Required] public string PackageVersion { get; set; } = string.Empty;
    [Required] public string ProjectDirectory { get; set; } = string.Empty;
    public string? TargetFramework { get; set; }
    public string? SdkPackageVersion { get; set; }
    public ITaskItem[] SdkCapabilities { get; set; } = [];
    public ITaskItem[] ReferencePaths { get; set; } = [];
    public ITaskItem[] RuntimeCopyLocalPaths { get; set; } = [];
    public ITaskItem[] AuthoredAssemblyPaths { get; set; } = [];
    public ITaskItem[] DynamicAccessAcknowledgements { get; set; } = [];

    public override bool Execute()
    {
        if (!File.Exists(TargetAssemblyPath))
        {
            Log.LogError($"Sunder package entry assembly was not found at '{TargetAssemblyPath}'.");
            return false;
        }

        var sdkPackageVersion = ResolvedSdkPackageVersion.Resolve(ReferencePaths, SdkPackageVersion, Log);
        if (sdkPackageVersion is null)
        {
            return false;
        }
        var capabilityInference = new PackageCapabilityInference(
            SdkCapabilities.Select(static item => item.ItemSpec).ToArray(),
            ReferencePaths.Select(static item => item.ItemSpec).ToArray(),
            RuntimeCopyLocalPaths.Select(static item => item.ItemSpec).ToArray(),
            AuthoredAssemblyPaths.Select(static item => item.ItemSpec).ToArray(),
            DynamicAccessAcknowledgements.Select(static item => item.ItemSpec).ToArray(),
            ProjectDirectory);
        var metadata = new PackageMetadataDecoder(
            TargetAssemblyPath,
            new PackageDependencyExtractor(PackageVersion),
            capabilityInference,
            Log,
            RuntimeCopyLocalPaths.Select(static item => item.ItemSpec).ToArray()).Decode();
        if (metadata is null)
        {
            return false;
        }
        if (!capabilityInference.IsComplete)
        {
            foreach (var diagnostic in capabilityInference.Diagnostics)
            {
                Log.LogError($"Sunder SDK capability inference is incomplete: {diagnostic}");
            }
            return false;
        }

        var assets = new PackageAssetDiscovery(ProjectDirectory);
        if (!new PackageManifestValidator(
                PackageVersion,
                EntryAssembly,
                sdkPackageVersion,
                ProjectDirectory,
                assets,
                Log).Validate(metadata))
        {
            return false;
        }

        var manifest = new SunderPackageManifest
        {
            ManifestVersion = 1,
            Id = metadata.Id,
            Name = metadata.Name,
            Summary = string.IsNullOrWhiteSpace(metadata.Summary) ? null : metadata.Summary,
            Version = PackageVersion,
            EntryAssembly = EntryAssembly,
            HostRoles = metadata.HostRoles,
            Icon = string.IsNullOrWhiteSpace(metadata.Icon) ? null : PackageAssetDiscovery.NormalizePath(metadata.Icon),
            DependsOn = metadata.Dependencies.Count == 0 ? null : metadata.Dependencies,
            SdkApiVersion = SunderSdkApiVersions.Current,
            SdkPackageVersion = sdkPackageVersion,
            RequiredSdkCapabilities = metadata.RequiredSdkCapabilities.Count == 0 ? null : metadata.RequiredSdkCapabilities,
            TargetFramework = string.IsNullOrWhiteSpace(TargetFramework) ? null : TargetFramework,
        };

        var manifestDirectory = Path.GetDirectoryName(ManifestOutputPath);
        if (!string.IsNullOrWhiteSpace(manifestDirectory))
        {
            Directory.CreateDirectory(manifestDirectory);
        }
        PackageManifestSerializer.Write(ManifestOutputPath, manifest, JsonOptions);
        Log.LogMessage(MessageImportance.High, $"Generated Sunder package manifest at {ManifestOutputPath}");
        return !Log.HasLoggedErrors;
    }
}
