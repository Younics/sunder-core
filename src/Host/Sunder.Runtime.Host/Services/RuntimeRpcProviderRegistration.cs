using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed record RuntimeRpcProviderRegistration(
    string ProviderId,
    string ContractId,
    string ContractVersion,
    string ContractSha256,
    SunderRpcContractDescriptor Contract,
    ISunderRpcServiceHandler Handler,
    SunderPackageProviderManifest Manifest);
