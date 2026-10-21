using System.Threading.Tasks;
using Sunder.Sdk.Services;

namespace Example.PackageIdentity;

[SunderService(SunderServiceExposure.Package)]
public interface XService
{
    [SunderMethod]
    Task<BoundaryValue> RunAsync(BoundaryValue request);
}

[SunderValue("BoundaryValue")]
public sealed record BoundaryValue
{
    public required bool Ok { get; init; }
}
