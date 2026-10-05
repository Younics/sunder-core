using System.Threading.Tasks;
using Sunder.Sdk.Rpc;

namespace Example.Invalid;

[SunderRpcService(SunderRpcExposure.Public)]
public interface RenamedService
{
    [SunderRpcMethod]
    Task<BaselineValue> RunAsync(BaselineValue request);
}

[SunderRpcDefinition("BaselineValue")]
public sealed record BaselineValue
{
    public required bool Value { get; init; }
}
