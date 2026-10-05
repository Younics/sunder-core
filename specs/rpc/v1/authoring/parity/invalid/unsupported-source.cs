using System.Threading.Tasks;
using Sunder.Sdk.Rpc;

namespace Example.Invalid;

[SunderRpcService(SunderRpcExposure.Public)]
public interface GenericService
{
    [SunderRpcMethod]
    Task<T> EchoAsync<T>(T request);
}
