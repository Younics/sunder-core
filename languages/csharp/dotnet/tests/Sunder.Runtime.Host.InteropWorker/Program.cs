using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;

[assembly: SunderPackage(Id = "test.worker.interop", Name = "Runtime Host Interop Worker")]

await SunderWorker.RunAsync(context =>
{
    var descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(
        Path.Combine(context.ContentRootPath, "contracts", "example.rpc.json")));
    return new SunderWorkerOptions(
    [
        new SunderWorkerProviderRegistration(
            "sunder.interop.dotnet.provider",
            descriptor.ContractId,
            descriptor.Version,
            descriptor.Sha256,
            descriptor.AbiSha256!,
            new InteropRpcHandler(descriptor)),
    ])
    {
        OnActivated = async (activationContext, cancellationToken) =>
        {
            if (activationContext.PackageId == "sunder.interop.dotnet.caller")
            {
                await RunDotNetToNodeInteropAsync(activationContext, cancellationToken);
            }
        },
    };
});

static async Task RunDotNetToNodeInteropAsync(
    SunderWorkerContext context,
    CancellationToken cancellationToken)
{
    var catalog = await context.Rpc.DiscoverAsync("example.messages", cancellationToken);
    var provider = catalog.Providers.Single(candidate => candidate.PackageId == "sunder.sea.smoke");
    var request = JsonSerializer.SerializeToElement(new { message = ".NET to Node" });
    var unary = await context.Rpc.InvokeAsync(
        provider.Endpoint,
        "messages",
        "send",
        request,
        cancellationToken: cancellationToken);
    var events = new List<string>();
    await foreach (var item in context.Rpc.SubscribeAsync(
                       provider.Endpoint,
                       "messages",
                       "watch",
                       request,
                       cancellationToken: cancellationToken))
    {
        events.Add(item.GetProperty("message").GetString()!);
    }
    var data = await context.Rpc.InvokeAsync(
        provider.Endpoint,
        "messages",
        "data",
        request,
        cancellationToken: cancellationToken);
    string? faultCode = null;
    try
    {
        await context.Rpc.InvokeAsync(
            provider.Endpoint,
            "messages",
            "fault",
            request,
            cancellationToken: cancellationToken);
    }
    catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Domain)
    {
        faultCode = exception.Error.Code;
    }
    if (unary.GetProperty("message").GetString() != "native SEA unary"
        || !events.SequenceEqual(["native SEA stream 1", "native SEA stream 2"])
        || data.GetProperty("message").GetString() != "shared Worker V3 package data"
        || faultCode != "example.rejected")
    {
        throw new InvalidOperationException(".NET to Node Runtime Host RPC conformance failed.");
    }
    await context.State.SetValueAsync(
        "interop.dotnet-to-node",
        JsonSerializer.Serialize(new
        {
            unary = unary.GetProperty("message").GetString(),
            stream = events,
            data = data.GetProperty("message").GetString(),
            faultCode,
        }),
        cancellationToken);
}

sealed class InteropRpcHandler : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
{
    public InteropRpcHandler(SunderRpcContractDescriptor descriptor)
        => DeclaredErrors = descriptor.Services.ToDictionary(
            static service => service.ServiceId,
            static service => (IReadOnlyDictionary<string, IReadOnlyList<string>>)service.Methods.ToDictionary(
                static method => method.MethodId,
                static method => method.Errors,
                StringComparer.Ordinal),
            StringComparer.Ordinal);

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors { get; }

    public ValueTask<JsonElement> InvokeUnaryAsync(
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (serviceId != "messages")
        {
            return ValueTask.FromException<JsonElement>(new InvalidOperationException("Unexpected service."));
        }
        if (methodId == "fault")
        {
            return ValueTask.FromException<JsonElement>(new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.Domain,
                "example.rejected",
                "rejected")));
        }
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(new
        {
            message = methodId == "data" ? "shared Worker V3 package data" : "native .NET unary",
        }));
    }

    public async IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (serviceId != "messages" || methodId != "watch")
        {
            throw new InvalidOperationException("Unexpected stream method.");
        }
        foreach (var message in new[] { "native .NET stream 1", "native .NET stream 2" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return JsonSerializer.SerializeToElement(new { message });
            await Task.Yield();
        }
    }
}
