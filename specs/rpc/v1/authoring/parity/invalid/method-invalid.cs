using Sunder.Sdk.Rpc;

namespace Example.Invalid;

[SunderRpcService(SunderRpcExposure.Public)]
public interface BadMethodService
{
    [SunderRpcMethod]
    string Run();
}
