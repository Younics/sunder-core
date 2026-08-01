using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Build.Framework;
using Sunder.Package.Format;

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
    [Required] public string TargetFramework { get; set; } = string.Empty;
    public string? SdkPackageVersion { get; set; }
    public string? AssetsDirectory { get; set; }
    public ITaskItem[] SdkCapabilities { get; set; } = [];
    public ITaskItem[] ReferencePaths { get; set; } = [];
    public ITaskItem[] RuntimeCopyLocalPaths { get; set; } = [];
    public ITaskItem[] AuthoredAssemblyPaths { get; set; } = [];
    public ITaskItem[] DynamicAccessAcknowledgements { get; set; } = [];
    public ITaskItem[] RuntimeIdentifiers { get; set; } = [];
    public ITaskItem[] PackageTargets { get; set; } = [];
    public ITaskItem[] PackageViews { get; set; } = [];
    public ITaskItem[] ContractBundles { get; set; } = [];
    public ITaskItem[] UsesContracts { get; set; } = [];
    public ITaskItem[] RpcProviders { get; set; } = [];

    [Output]
    public ITaskItem[] ResolvedTargets { get; private set; } = [];

    [Output]
    public ITaskItem[] ResolvedContractFiles { get; private set; } = [];

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

        var assets = new PackageAssetDiscovery(ProjectDirectory, AssetsDirectory);
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

        var targets = PackageTargetBuilder.Build(
            metadata.ImplementedRoles,
            RuntimeIdentifiers,
            PackageTargets,
            PackageViews,
            metadata.Id,
            EntryAssembly,
            TargetFramework,
            sdkPackageVersion,
            metadata.RequiredSdkCapabilities,
            Log);
        if (targets is null)
        {
            return false;
        }
        var contracts = PackageContractBuilder.Build(
            ManifestOutputPath,
            targets,
            ContractBundles,
            UsesContracts,
            RpcProviders,
            Log);
        if (contracts is null) return false;

        var manifest = new SunderPackageManifest
        {
            ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion,
            ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
            Id = metadata.Id,
            Name = metadata.Name,
            Summary = string.IsNullOrWhiteSpace(metadata.Summary) ? null : metadata.Summary,
            Version = PackageVersion,
            Icon = string.IsNullOrWhiteSpace(metadata.Icon) ? null : PackageAssetDiscovery.NormalizePath(metadata.Icon),
            DependsOn = metadata.Dependencies,
            Targets = targets,
            ContractBundles = contracts.Bundles,
            UsesContracts = contracts.Uses,
            Provides = contracts.Providers,
        };

        var manifestDirectory = Path.GetDirectoryName(ManifestOutputPath);
        if (!string.IsNullOrWhiteSpace(manifestDirectory))
        {
            Directory.CreateDirectory(manifestDirectory);
        }
        PackageManifestSerializer.Write(ManifestOutputPath, manifest, JsonOptions);
        ResolvedTargets = PackageTargetBuilder.ToTaskItems(targets);
        ResolvedContractFiles = contracts.Files;
        Log.LogMessage(MessageImportance.High, $"Generated Sunder package manifest at {ManifestOutputPath}");
        return !Log.HasLoggedErrors;
    }
}
