using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;

[assembly: SunderPackage(Id = "example.generated", Name = "Implementation-First RPC Fixture")]

namespace Example.Generated;

internal static class Program
{
    public static Task Main()
        => SunderWorker.RunAsync(_ => new SunderWorkerOptions(
        [
            new GreetingService().CreateSunderRpcProviderRegistration(),
        ]));
}

[SunderRpcService(SunderRpcExposure.Package)]
public sealed partial class GreetingService
{
    [SunderRpcMethod]
    public ValueTask<GreetingResult> GreetAsync(
        GreetingRequest request,
        SunderRpcInvocationContext context,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new GreetingResult { Message = request.Name });
}

public sealed record GreetingRequest
{
    public required string Name { get; init; }
}

public sealed record GreetingResult
{
    public required string Message { get; init; }
}
