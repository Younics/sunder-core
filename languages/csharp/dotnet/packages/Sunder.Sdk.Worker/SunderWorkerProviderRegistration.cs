using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Worker;

/// <summary>Binds one manifest-declared provider identity to its activation-scoped RPC handler.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderWorkerProviderRegistration
{
    /// <summary>Creates a provider registration.</summary>
    public SunderWorkerProviderRegistration(
        string providerId,
        string contractId,
        string contractVersion,
        string contractSha256,
        ISunderRpcServiceHandler handler)
    {
        ArgumentNullException.ThrowIfNull(contractVersion);
        ArgumentNullException.ThrowIfNull(contractSha256);
        if (!PackageId.TryParse(providerId, out _))
        {
            throw new ArgumentException("The provider id must be a canonical package-style identifier.", nameof(providerId));
        }
        if (!PackageId.TryParse(contractId, out _))
        {
            throw new ArgumentException("The contract id must be a canonical package-style identifier.", nameof(contractId));
        }
        if (contractVersion.Length > 128 || !SemanticVersion.TryParse(contractVersion, out _))
        {
            throw new ArgumentException("The contract version must be a bounded strict semantic version.", nameof(contractVersion));
        }
        if (contractSha256.Length != 64
            || contractSha256.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("The contract SHA-256 must be 64 lowercase hexadecimal characters.", nameof(contractSha256));
        }

        ArgumentNullException.ThrowIfNull(handler);
        ProviderId = providerId;
        ContractId = contractId;
        ContractVersion = contractVersion;
        ContractSha256 = contractSha256;
        Handler = handler;
    }

    /// <summary>Gets the manifest-declared provider identifier.</summary>
    public string ProviderId { get; }

    /// <summary>Gets the bundled contract identifier.</summary>
    public string ContractId { get; }

    /// <summary>Gets the exact bundled contract semantic version.</summary>
    public string ContractVersion { get; }

    /// <summary>Gets the lowercase canonical descriptor SHA-256.</summary>
    public string ContractSha256 { get; }

    /// <summary>Gets the activation-scoped provider handler.</summary>
    public ISunderRpcServiceHandler Handler { get; }
}
