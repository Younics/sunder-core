namespace Sunder.Runtime.Contracts;

public sealed record PackageCapabilityConsent(
    string PackageId,
    string PackageVersion,
    string ManifestSha256,
    IReadOnlyList<string> Capabilities)
{
    public IReadOnlyList<string> Capabilities { get; }
        = RuntimeContractCollections.Freeze(Capabilities);
}
