using System.Threading.Tasks;
using Sunder.Sdk.Services;

namespace Example.Collision;

[SunderService(SunderServiceExposure.Public)]
public interface CaseService
{
    [SunderMethod]
    Task<STATUS> ConvertAsync(Status request);
}

[SunderValue("Status")]
public sealed record Status
{
    public required bool Value { get; init; }
}

[SunderValue("STATUS")]
public sealed record STATUS
{
    public required bool Value { get; init; }
}
