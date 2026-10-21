using System.Threading.Tasks;
using Sunder.Sdk.Services;

namespace Example.Boundary;

[SunderService(SunderServiceExposure.Package)]
public interface ContractAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaService
{
    [SunderMethod]
    Task<BoundaryValue> RunAsync(BoundaryValue request);
}

[SunderValue("BoundaryValue")]
public sealed record BoundaryValue
{
    public required bool Ok { get; init; }
}
