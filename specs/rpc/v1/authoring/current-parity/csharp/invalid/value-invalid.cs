using System.Threading.Tasks;
using Sunder.Sdk.Services;

namespace Example.Invalid;

[SunderService(SunderServiceExposure.Public)]
public interface BadValueService
{
    [SunderMethod]
    Task<BadValue> CheckAsync(BadValue request);
}

public sealed record BadValue
{
    public string Name { get; init; } = string.Empty;
}
