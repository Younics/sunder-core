using System.Threading.Tasks;
using Sunder.Sdk.Rpc;

namespace Example.Collision;

[SunderRpcService(SunderRpcExposure.Public)]
public interface Status
{
    [SunderRpcMethod]
    Task<CollisionValue> ReadAsync(CollisionValue request);
}

[SunderRpcService(SunderRpcExposure.Public)]
public interface StatusService
{
    [SunderRpcMethod]
    Task<CollisionValue> ReadAsync(CollisionValue request);
}

[SunderRpcDefinition("CollisionValue")]
public sealed record CollisionValue
{
    public required bool Value { get; init; }
}
