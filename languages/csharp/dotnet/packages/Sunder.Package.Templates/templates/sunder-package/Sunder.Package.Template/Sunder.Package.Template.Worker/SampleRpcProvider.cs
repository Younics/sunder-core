using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Template.Protocol.Generated;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Template.Worker;

internal sealed class SampleRpcProvider : ISampleProvider, ISunderRpcServiceHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new SunderRpcV2JsonConverterFactory() },
    };

    public ValueTask<SampleResponse> GreetAsync(
        SunderRpcInvocationContext context,
        SampleRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new SampleResponse
        {
            Message = $"Hello, {request.Name}!",
        });

    public async ValueTask<JsonElement> InvokeUnaryAsync(
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(serviceId, "sample", StringComparison.Ordinal)
            || !string.Equals(methodId, "greet", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unknown RPC method '{serviceId}/{methodId}'.");
        }

        var typedRequest = request.Deserialize<SampleRequest>(JsonOptions)
            ?? throw new InvalidOperationException("The RPC request was null.");
        var response = await GreetAsync(context, typedRequest, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(response, JsonOptions);
    }

    public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException($"RPC method '{serviceId}/{methodId}' is not a server stream.");
}
