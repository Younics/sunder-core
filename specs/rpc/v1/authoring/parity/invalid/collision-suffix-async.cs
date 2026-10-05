using System.Threading.Tasks;
using Sunder.Sdk.Rpc;

namespace Example.Collision;

[SunderRpcService(SunderRpcExposure.Public)]
public interface RunService
{
    [SunderRpcMethod]
    Task<CollisionValue> Run(CollisionValue request);

    [SunderRpcMethod]
    Task<CollisionValue> RunAsync(CollisionValue request);
}

[SunderRpcDefinition("CollisionValue")]
public sealed record CollisionValue
{
    public required bool Value { get; init; }
}
