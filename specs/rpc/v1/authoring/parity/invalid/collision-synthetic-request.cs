using System.Threading.Tasks;
using Sunder.Sdk.Rpc;

namespace Example.Collision;

[SunderRpcService(SunderRpcExposure.Public)]
public interface OrderService
{
    [SunderRpcMethod]
    Task<OrderCreateRequest> CreateAsync(string name, long count);
}

[SunderRpcDefinition("OrderCreateRequest")]
public sealed record OrderCreateRequest
{
    public required string Name { get; init; }
    public required long Count { get; init; }
}
