using System;
using System.Threading.Tasks;
using Sunder.Sdk.Services;

namespace Example.Invalid;

[SunderService(SunderServiceExposure.Public)]
public interface CompleteHandlerService
{
    [SunderMethod]
    Task<HandlerResult> FirstAsync(HandlerResult request);

    [SunderMethod]
    Task<HandlerResult> SecondAsync(HandlerResult request);
}

[SunderServiceProvider(typeof(CompleteHandlerService))]
public sealed partial class IncompleteProvider : CompleteHandlerService
{
    public Task<HandlerResult> FirstAsync(HandlerResult request) => throw new NotImplementedException();
}

public sealed record HandlerResult
{
    public required bool Ok { get; init; }
}
