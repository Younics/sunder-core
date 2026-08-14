using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed class PackageCapabilityConsentViewModel
{
    public PackageCapabilityConsentViewModel(IReadOnlyList<PackageCapabilityConsent> requirements)
    {
        Items = requirements
            .Select(static requirement => new PackageCapabilityConsentItemViewModel(
                requirement.PackageId,
                requirement.PackageVersion,
                requirement.ManifestSha256,
                string.Join(", ", requirement.Capabilities)))
            .ToArray();
    }

    public IReadOnlyList<PackageCapabilityConsentItemViewModel> Items { get; }
}

public sealed record PackageCapabilityConsentItemViewModel(
    string PackageId,
    string PackageVersion,
    string ManifestSha256,
    string CapabilitiesText);
