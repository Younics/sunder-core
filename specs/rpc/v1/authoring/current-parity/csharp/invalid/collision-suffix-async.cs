using System.Threading.Tasks;
using Sunder.Sdk.Services;

namespace Example.Collision;

[SunderService(SunderServiceExposure.Public)]
public interface RunService
{
    [SunderMethod]
    Task<CollisionValue> Run(CollisionValue request);

    [SunderMethod]
    Task<CollisionValue> RunAsync(CollisionValue request);
}

[SunderValue("CollisionValue")]
public sealed record CollisionValue
{
    public required bool Value { get; init; }
}
