using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Collects desktop App contributions during one package activation.</summary>
/// <remarks>Every registration is attributed to the currently activating package. The App retains registrations and contribution instances until deactivation. Registration is single-threaded and valid only during module activation.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.ContributionsV1)]
public interface ISunderAppContributionRegistry
{
}
