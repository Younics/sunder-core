using System.Text.Json.Serialization;

namespace Sunder.Registry.Contracts;

/// <summary>
/// The complete package compatibility profiles implemented by a client. A target
/// must satisfy one whole profile, never the union of capabilities across profiles.
/// GET resolution repeats the profiles parameter with one JSON profile per value.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryClientCompatibilityProfile
{
    [JsonConstructor]
    public RegistryClientCompatibilityProfile(IReadOnlyList<RegistrySupportedPackageProfile> profiles, string rid)
    {
        Profiles = Array.AsReadOnly((profiles ?? []).ToArray());
        Rid = rid ?? string.Empty;
    }

    public const int MaximumProfiles = 8;
    public const int MaximumManifestVersions = 8;
    public const int MaximumSdkMajors = 8;
    public const int MaximumTargetFrameworks = 8;
    public const int MaximumHostCapabilities = 64;
    public const int MaximumIdentifierLength = 128;
    public const int MaximumProfileQueryLength = 16_384;
    public const string ProfilesQueryParameter = "profiles";
    public const string RidQueryParameter = "rid";

    public IReadOnlyList<RegistrySupportedPackageProfile> Profiles { get; }
    public string Rid { get; }
}

/// <summary>One complete role/kind implementation, with independent SDK-major, protocol, and shared-framework support.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistrySupportedPackageProfile(
    string Role,
    string Kind,
    IReadOnlyList<int> ManifestVersions,
    IReadOnlyList<int> SdkMajors,
    IReadOnlyList<string> HostCapabilities,
    IReadOnlyList<string> TargetFrameworks,
    string? AvaloniaVersion = null)
{
    public string Role { get; } = Role ?? string.Empty;
    public string Kind { get; } = Kind ?? string.Empty;
    public IReadOnlyList<int> ManifestVersions { get; }
        = Array.AsReadOnly((ManifestVersions ?? []).ToArray());

    public IReadOnlyList<int> SdkMajors { get; } = Array.AsReadOnly((SdkMajors ?? []).ToArray());
    public IReadOnlyList<string> HostCapabilities { get; } = Array.AsReadOnly((HostCapabilities ?? []).ToArray());
    public IReadOnlyList<string> TargetFrameworks { get; } = Array.AsReadOnly((TargetFrameworks ?? []).ToArray());
}
