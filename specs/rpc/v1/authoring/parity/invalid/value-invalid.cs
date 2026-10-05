using System.Threading.Tasks;
using Sunder.Sdk.Rpc;

namespace Example.Invalid;

[SunderRpcService(SunderRpcExposure.Public)]
public interface BadValueService
{
    [SunderRpcMethod]
    Task<BadValue> CheckAsync(BadValue request);
}

public sealed record BadValue
{
    public string Name { get; init; } = string.Empty;
}
