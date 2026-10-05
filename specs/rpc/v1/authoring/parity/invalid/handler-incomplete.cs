using System;
using System.Threading.Tasks;
using Sunder.Sdk.Rpc;

namespace Example.Invalid;

[SunderRpcService(SunderRpcExposure.Public)]
public interface CompleteHandlerService
{
    [SunderRpcMethod]
    Task<HandlerResult> FirstAsync(HandlerResult request);

    [SunderRpcMethod]
    Task<HandlerResult> SecondAsync(HandlerResult request);
}

[SunderRpcProvider(typeof(CompleteHandlerService))]
public sealed partial class IncompleteProvider : CompleteHandlerService
{
    public Task<HandlerResult> FirstAsync(HandlerResult request) => throw new NotImplementedException();
}

public sealed record HandlerResult
{
    public required bool Ok { get; init; }
}
