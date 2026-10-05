using System.Threading.Tasks;
using Sunder.Sdk.Rpc;

namespace Example.Collision;

[SunderRpcService(SunderRpcExposure.Public)]
public interface ReservedService
{
    [SunderRpcMethod]
    Task<ReservedResult> RunAsync(string @await, string _await);
}

[SunderRpcDefinition("ReservedResult")]
public sealed record ReservedResult
{
    public required bool Ok { get; init; }
}
