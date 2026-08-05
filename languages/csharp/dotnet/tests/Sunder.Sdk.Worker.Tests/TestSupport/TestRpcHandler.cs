using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Worker.Tests.TestSupport;

internal sealed class TestRpcHandler : ISunderRpcServiceHandler
{
    public Func<SunderRpcInvocationContext, string, string, JsonElement, CancellationToken, ValueTask<JsonElement>> Unary
    {
        get;
        init;
    } = static (_, _, _, _, _) => ValueTask.FromException<JsonElement>(
        new NotSupportedException("Unary invocation is not configured."));

    public Func<SunderRpcInvocationContext, string, string, JsonElement, CancellationToken, IAsyncEnumerable<JsonElement>> Stream
    {
        get;
        init;
    } = static (_, _, _, _, _) => Empty();

    public ValueTask<JsonElement> InvokeUnaryAsync(
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken = default)
        => Unary(context, serviceId, methodId, request, cancellationToken);

    public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken = default)
        => Stream(context, serviceId, methodId, request, cancellationToken);

    private static async IAsyncEnumerable<JsonElement> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}
