using Sunder.Sdk.Rpc;

namespace Sunder.Package.Template;

[SunderRpcService(SunderRpcExposure.Package, ContractId = "SUNDER_PACKAGE_ID_CSHARP.sample", ServiceId = "sample")]
[SunderRpcProvider(ProviderId = "SUNDER_PACKAGE_ID_CSHARP.sample")]
public sealed partial class SampleService
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<SampleResponse> GreetAsync(
        SampleRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new SampleResponse
        {
            Message = new() { Value = $"Hello, {request.Name.Value}!" },
        });
    }
}

public sealed record SampleRequest
{
    public required SampleText Name { get; init; }
}

public sealed record SampleResponse
{
    public required SampleText Message { get; init; }
}

[SunderRpcDefinition("String"), SunderRpcTransparent, SunderRpcFacet(MinLength = 0, MaxLength = 4096)]
public sealed record SampleText
{
    public required string Value { get; init; }
}
