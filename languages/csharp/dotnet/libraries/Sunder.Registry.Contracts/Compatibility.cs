namespace Sunder.Registry.Contracts;

/// <summary>
/// Declares the package compatibility boundary of the client resolving an installable package version.
/// GET resolution endpoints use the JSON property names as query parameter names, repeating collection values.
/// </summary>
/// <param name="WorkerProtocol">The exact worker protocol capability identifier.</param>
/// <param name="WorkerCapabilities">The complete supported worker capability set, including the protocol and SDK baseline.</param>
/// <param name="SdkBaseline">The exact SDK baseline capability identifier.</param>
public sealed record RegistryClientCompatibilityProfile(
    IReadOnlyList<int> ManifestVersions,
    IReadOnlyList<string> RuntimeTargetKinds,
    string WorkerProtocol,
    IReadOnlyList<string> WorkerCapabilities,
    string SdkBaseline,
    string Rid)
{
    public const int MaximumManifestVersions = 8;
    public const int MaximumRuntimeTargetKinds = 8;
    public const int MaximumWorkerCapabilities = 64;
    public const int MaximumIdentifierLength = 128;
    public const string ManifestVersionsQueryParameter = "manifestVersions";
    public const string RuntimeTargetKindsQueryParameter = "runtimeTargetKinds";
    public const string WorkerProtocolQueryParameter = "workerProtocol";
    public const string WorkerCapabilitiesQueryParameter = "workerCapabilities";
    public const string SdkBaselineQueryParameter = "sdkBaseline";
    public const string RidQueryParameter = "rid";

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

    public string Rid { get; }
        = Rid ?? string.Empty;
}
