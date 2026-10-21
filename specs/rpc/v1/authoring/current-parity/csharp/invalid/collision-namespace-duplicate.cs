using System.Threading.Tasks;
using Sunder.Sdk.Services;

namespace Example.Collision
{
    [SunderValue("CollisionValue")]
    public sealed record CollisionValue
    {
        public required bool Value { get; init; }
    }
}

namespace Example.Collision.First
{
    [SunderService(SunderServiceExposure.Public)]
    public interface StatusService
    {
        [SunderMethod]
        Task<Example.Collision.CollisionValue> RunAsync(Example.Collision.CollisionValue request);
    }
}

namespace Example.Collision.Second
{
    [SunderService(SunderServiceExposure.Public)]
    public interface StatusService
    {
        [SunderMethod]
        Task<Example.Collision.CollisionValue> RunAsync(Example.Collision.CollisionValue request);
    }
}
