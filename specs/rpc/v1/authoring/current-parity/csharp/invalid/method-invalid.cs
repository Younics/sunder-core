using Sunder.Sdk.Services;

namespace Example.Invalid;

[SunderService(SunderServiceExposure.Public)]
public interface BadMethodService
{
    [SunderMethod]
    string Run();
}
