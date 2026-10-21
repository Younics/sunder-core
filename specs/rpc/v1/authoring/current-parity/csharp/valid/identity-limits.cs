using System;
using System.Threading.Tasks;
using Sunder.Sdk.Services;

namespace Example.Boundary;

[SunderService(SunderServiceExposure.Package)]
public interface ContractAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaService
{
    Task<BoundaryValue> RunAsync(BoundaryValue request);
}

[SunderService(SunderServiceExposure.Package)]
public sealed partial class ProviderAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaService
{
    public Task<BoundaryValue> RunAsync(BoundaryValue request) => throw new NotImplementedException();
}

[SunderValue("BoundaryValue")]
public sealed record BoundaryValue
{
    public required bool Ok { get; init; }
}
