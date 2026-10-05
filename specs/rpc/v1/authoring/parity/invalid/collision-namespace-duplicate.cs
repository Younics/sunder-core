using System.Threading.Tasks;
using Sunder.Sdk.Rpc;

namespace Example.Collision
{
    [SunderRpcDefinition("CollisionValue")]
    public sealed record CollisionValue
    {
        public required bool Value { get; init; }
    }
}

namespace Example.Collision.First
{
    [SunderRpcService(SunderRpcExposure.Public)]
    public interface StatusService
    {
        [SunderRpcMethod]
        Task<Example.Collision.CollisionValue> RunAsync(Example.Collision.CollisionValue request);
    }
}

namespace Example.Collision.Second
{
    [SunderRpcService(SunderRpcExposure.Public)]
    public interface StatusService
    {
        [SunderRpcMethod]
        Task<Example.Collision.CollisionValue> RunAsync(Example.Collision.CollisionValue request);
    }
}
