using System.Threading.Tasks;
using Sunder.Sdk.Rpc;

namespace Example.Collision;

[SunderRpcService(SunderRpcExposure.Public)]
public interface CaseService
{
    [SunderRpcMethod]
    Task<STATUS> ConvertAsync(Status request);
}

[SunderRpcDefinition("Status")]
public sealed record Status
{
    public required bool Value { get; init; }
}

[SunderRpcDefinition("STATUS")]
public sealed record STATUS
{
    public required bool Value { get; init; }
}
