using Sunder.Runtime.Client;

namespace Sunder.App.Services;

internal sealed class RuntimeResourcePolicyStatusSource(
    RuntimeClientTransport transport) : IRuntimeResourcePolicyStatusSource
{
    public async ValueTask<RuntimeResourcePolicyEffectiveStatus> GetCurrentEffectivePolicyAsync(
        CancellationToken cancellationToken = default)
    {
        var handshake = await transport.RefreshHandshakeAsync(cancellationToken).ConfigureAwait(false);
        if (!handshake.SupportedFeatures.Contains(
                RuntimeClientResourceProfile.FeatureName,
                StringComparer.Ordinal))
        {
            return RuntimeResourcePolicyEffectiveStatus.Unavailable(
                "This Runtime version does not report its effective resource policy yet.");
        }

        var profile = transport.CurrentResourceProfile;
        if (profile is null || profile.RuntimeInstanceId != handshake.RuntimeInstanceId)
        {
            return RuntimeResourcePolicyEffectiveStatus.Unavailable(
                "The running Runtime did not provide a current effective resource policy.");
        }
        return RuntimeResourcePolicyEffectiveStatus.Available(profile.EffectivePolicy);
    }
}
