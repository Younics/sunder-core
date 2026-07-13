using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public static class RuntimeProtocolCompatibility
{
    public static bool IsCompatible(RuntimeHandshakeResponse? handshake)
        => GetIncompatibility(handshake) is null;

    public static string? GetIncompatibility(RuntimeHandshakeResponse? handshake)
    {
        if (handshake is null)
        {
            return "Runtime did not return a handshake.";
        }
        if (!string.Equals(handshake.ProtocolIdentity, RuntimeProtocol.Identity, StringComparison.Ordinal))
        {
            return $"Runtime protocol identity '{handshake.ProtocolIdentity}' is not supported.";
        }
        if (handshake.ProtocolRevision < handshake.MinimumSupportedRevision
            || handshake.ProtocolRevision > handshake.MaximumSupportedRevision
            || handshake.MinimumSupportedRevision > RuntimeProtocol.CurrentRevision
            || handshake.MaximumSupportedRevision < RuntimeProtocol.CurrentRevision)
        {
            return $"Runtime protocol revision {handshake.ProtocolRevision} with supported range "
                   + $"{handshake.MinimumSupportedRevision}-{handshake.MaximumSupportedRevision} is incompatible with client revision {RuntimeProtocol.CurrentRevision}.";
        }
        if (handshake.RuntimeInstanceId == Guid.Empty)
        {
            return "Runtime handshake did not provide a valid instance id.";
        }
        if (handshake.SupportedFeatures is null
            || !handshake.SupportedFeatures.Contains(RuntimeProtocolFeatures.VersionedApiV1, StringComparer.Ordinal))
        {
            return $"Runtime does not support required feature '{RuntimeProtocolFeatures.VersionedApiV1}'.";
        }
        if (handshake.Product is null
            || string.IsNullOrWhiteSpace(handshake.Product.ProductName)
            || string.IsNullOrWhiteSpace(handshake.Product.ProductVersion))
        {
            return "Runtime handshake did not provide product version diagnostics.";
        }
        return null;
    }
}
