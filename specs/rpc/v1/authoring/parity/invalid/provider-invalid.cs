using System;
using System.Threading.Tasks;
using Sunder.Sdk.Rpc;

namespace Example.Invalid;

[SunderRpcService(SunderRpcExposure.Package)]
[SunderRpcProvider(Audience = SunderRpcProviderAudience.Public)]
public sealed partial class PrivateProviderService
{
    [SunderRpcMethod]
    public ValueTask<ProviderResult> RunAsync() => throw new NotImplementedException();
}

public sealed record ProviderResult
{
    public required bool Ok { get; init; }
}
