using System.Threading.Tasks;
using Sunder.Sdk.Rpc;

namespace Example.Boundary;

[SunderRpcService(SunderRpcExposure.Package)]
public interface ContractAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaService
{
    [SunderRpcMethod]
    Task<BoundaryValue> RunAsync(BoundaryValue request);
}

[SunderRpcDefinition("BoundaryValue")]
public sealed record BoundaryValue
{
    public required bool Ok { get; init; }
}
