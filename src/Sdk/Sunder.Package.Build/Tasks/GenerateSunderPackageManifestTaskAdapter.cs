using Microsoft.Build.Framework;

namespace Sunder.Package.Build.Tasks;

/// <summary>MSBuild adapter for V1 package manifest generation.</summary>
public sealed class GenerateSunderPackageManifestTask : Microsoft.Build.Utilities.Task
{
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

    public string? SdkApiVersion { get; set; }

    public string? SdkPackageVersion { get; set; }

    public ITaskItem[] SdkCapabilities { get; set; } = [];

    public ITaskItem[] ReferencePaths { get; set; } = [];

    public ITaskItem[] RuntimeCopyLocalPaths { get; set; } = [];

    public override bool Execute()
    {
        var generator = new PackageManifestGenerator
        {
            BuildEngine = BuildEngine,
            HostObject = HostObject,
            TargetAssemblyPath = TargetAssemblyPath,
            ManifestOutputPath = ManifestOutputPath,
            EntryAssembly = EntryAssembly,
            PackageVersion = PackageVersion,
            ProjectDirectory = ProjectDirectory,
            TargetFramework = TargetFramework,
            SdkVersion = SdkVersion,
            SdkApiVersion = SdkApiVersion,
            SdkPackageVersion = SdkPackageVersion,
            SdkCapabilities = SdkCapabilities,
            ReferencePaths = ReferencePaths,
            RuntimeCopyLocalPaths = RuntimeCopyLocalPaths,
        };
        return generator.Execute();
    }
}
