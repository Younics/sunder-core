using System.Threading.Tasks;
using Sunder.Sdk.Services;

namespace Example.Collision;

[SunderService(SunderServiceExposure.Public)]
public interface OrderService
{
    [SunderMethod]
    Task<OrderCreateRequest> CreateAsync(string name, long count);
}

[SunderValue("OrderCreateRequest")]
public sealed record OrderCreateRequest
{
    public required string Name { get; init; }
    public required long Count { get; init; }
}
