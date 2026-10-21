using System.Threading.Tasks;
using Sunder.Sdk.Services;

namespace Example.Collision;

[SunderService(SunderServiceExposure.Public)]
public interface Status
{
    [SunderMethod]
    Task<CollisionValue> ReadAsync(CollisionValue request);
}

[SunderService(SunderServiceExposure.Public)]
public interface StatusService
{
    [SunderMethod]
    Task<CollisionValue> ReadAsync(CollisionValue request);
}

[SunderValue("CollisionValue")]
public sealed record CollisionValue
{
    public required bool Value { get; init; }
}
