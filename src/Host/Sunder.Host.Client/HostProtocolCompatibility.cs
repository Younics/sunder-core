using Sunder.Host.Contracts;

namespace Sunder.Host.Client;

public static class HostProtocolCompatibility
{
    public static bool IsCompatible(HostHandshakeResponse? handshake)
        => GetIncompatibility(handshake) is null;

    public static string? GetIncompatibility(HostHandshakeResponse? handshake, params string[] requiredFeatures)
    {
        ArgumentNullException.ThrowIfNull(requiredFeatures);
        if (handshake is null)
        {
            return "Host did not return a handshake.";
        }
        if (!string.Equals(handshake.ProtocolIdentity, HostProtocol.Identity, StringComparison.Ordinal))
        {
            return $"Host protocol identity '{handshake.ProtocolIdentity}' is not supported.";
        }
        if (handshake.ProtocolRevision < handshake.MinimumSupportedRevision
            || handshake.ProtocolRevision > handshake.MaximumSupportedRevision
            || handshake.MinimumSupportedRevision > HostProtocol.CurrentRevision
            || handshake.MaximumSupportedRevision < HostProtocol.CurrentRevision)
        {
            return $"Host protocol revision {handshake.ProtocolRevision} with supported range "
                   + $"{handshake.MinimumSupportedRevision}-{handshake.MaximumSupportedRevision} is incompatible with client revision {HostProtocol.CurrentRevision}.";
        }
        if (handshake.HostId == Guid.Empty || handshake.SupervisorInstanceId == Guid.Empty)
        {
            return "Host handshake did not provide valid host and process identities.";
        }
        foreach (var feature in requiredFeatures)
        {
            if (string.IsNullOrWhiteSpace(feature))
            {
                throw new ArgumentException("Required Host protocol features cannot be empty.", nameof(requiredFeatures));
            }
            if (!handshake.SupportedFeatures.Contains(feature, StringComparer.Ordinal))
            {
                return $"Host does not support required feature '{feature}'.";
            }
        }
        return null;
    }
}
