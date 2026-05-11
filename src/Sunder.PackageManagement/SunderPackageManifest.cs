using System.Text.Json.Serialization;

namespace Sunder.PackageManagement;

public sealed class SunderPackageManifest
{
    [JsonPropertyName("manifestVersion")]
    public int? ManifestVersion { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("entryAssembly")]
    public string? EntryAssembly { get; init; }

    [JsonPropertyName("icon")]
    public string? Icon { get; init; }

    [JsonPropertyName("dependsOn")]
    public IReadOnlyList<SunderPackageDependencyManifest>? DependsOn { get; init; }

    [JsonPropertyName("sdkApiVersion")]
    public int? SdkApiVersion { get; init; }

    [JsonPropertyName("sdkPackageVersion")]
    public string? SdkPackageVersion { get; init; }

    [JsonPropertyName("requiredSdkCapabilities")]
    public IReadOnlyList<string>? RequiredSdkCapabilities { get; init; }

    [JsonPropertyName("sdkVersion")]
    public string? SdkVersion { get; init; }

    [JsonPropertyName("targetFramework")]
    public string? TargetFramework { get; init; }
}

public sealed class SunderPackageDependencyManifest
{
    [JsonPropertyName("packageId")]
    public string? PackageId { get; init; }

    [JsonPropertyName("versionRange")]
    public string? VersionRange { get; init; }
}
