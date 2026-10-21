using System.Threading.Tasks;
using Sunder.Sdk.Services;

namespace Example.Invalid;

[SunderService(SunderServiceExposure.Public)]
public interface GenericService
{
    [SunderMethod]
    Task<T> EchoAsync<T>(T request);
}
