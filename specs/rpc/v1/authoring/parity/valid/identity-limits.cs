using System;
using System.Threading.Tasks;
using Sunder.Sdk.Rpc;

namespace Example.Boundary;

[SunderRpcService(SunderRpcExposure.Package)]
public interface ContractAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaService
{
    [SunderRpcMethod]
    Task<BoundaryValue> RunAsync(BoundaryValue request);
}

[SunderRpcService(SunderRpcExposure.Package)]
public sealed partial class ProviderAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaService
{
    [SunderRpcMethod]
    public Task<BoundaryValue> RunAsync(BoundaryValue request) => throw new NotImplementedException();
}

[SunderRpcDefinition("BoundaryValue")]
public sealed record BoundaryValue
{
    public required bool Ok { get; init; }
}
