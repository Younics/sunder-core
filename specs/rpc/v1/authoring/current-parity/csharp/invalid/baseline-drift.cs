using System.Threading.Tasks;
using Sunder.Sdk.Services;

namespace Example.Invalid;

[SunderService(SunderServiceExposure.Public)]
public interface RenamedService
{
    [SunderMethod]
    Task<BaselineValue> RunAsync(BaselineValue request);
}

[SunderValue("BaselineValue")]
public sealed record BaselineValue
{
    public required bool Value { get; init; }
}
