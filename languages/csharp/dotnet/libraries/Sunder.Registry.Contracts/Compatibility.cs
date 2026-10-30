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

    public RegistryClientCompatibilityProfile(IReadOnlyList<int> manifestVersions, IReadOnlyList<string> runtimeTargetKinds,
        string workerProtocol, IReadOnlyList<string> workerCapabilities, string sdkBaseline, string rid, string sdkVersionRange)
        : this([new(manifestVersions, runtimeTargetKinds, workerProtocol, workerCapabilities, sdkBaseline, sdkVersionRange)], rid) { }

    public const int MaximumProfiles = 8;
    public const int MaximumManifestVersions = 8;
    public const int MaximumRuntimeTargetKinds = 8;
    public const int MaximumWorkerCapabilities = 64;
    public const int MaximumIdentifierLength = 128;
    public const int MaximumProfileQueryLength = 16_384;
    public const string ProfilesQueryParameter = "profiles";
    public const string RidQueryParameter = "rid";

    public IReadOnlyList<RegistrySupportedPackageProfile> Profiles { get; }
    public string Rid { get; }
}

/// <summary>One coherent SDK baseline, Worker protocol and capability set.</summary>
/// <param name="WorkerProtocol">The exact worker protocol capability identifier.</param>
/// <param name="WorkerCapabilities">The complete supported worker capability set, including the protocol and SDK baseline.</param>
/// <param name="SdkBaseline">The exact SDK baseline capability identifier.</param>
/// <param name="SdkVersionRange">The SDK versions this profile actually implements.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistrySupportedPackageProfile(
    IReadOnlyList<int> ManifestVersions,
    IReadOnlyList<string> RuntimeTargetKinds,
    string WorkerProtocol,
    IReadOnlyList<string> WorkerCapabilities,
    string SdkBaseline,
    string SdkVersionRange)
{
    public IReadOnlyList<int> ManifestVersions { get; }
        = Array.AsReadOnly((ManifestVersions ?? []).ToArray());

    public IReadOnlyList<string> RuntimeTargetKinds { get; }
        = Array.AsReadOnly((RuntimeTargetKinds ?? []).ToArray());

    public string WorkerProtocol { get; }
        = WorkerProtocol ?? string.Empty;

    public IReadOnlyList<string> WorkerCapabilities { get; }
        = Array.AsReadOnly((WorkerCapabilities ?? []).ToArray());

    public string SdkBaseline { get; }
        = SdkBaseline ?? string.Empty;

    public string SdkVersionRange { get; }
        = SdkVersionRange ?? string.Empty;
}
