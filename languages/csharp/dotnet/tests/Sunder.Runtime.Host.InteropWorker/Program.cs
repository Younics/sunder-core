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
    return new SunderWorkerOptions(context.PackageId is
        "sunder.interop.dotnet.caller" or
        "sunder.interop.dotnet.self.caller" or
        "sunder.interop.dotnet.runtimebacked.caller"
        ? []
        :
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
                await RunInteropAsync(
                    activationContext,
                    "sunder.node.runtimebacked",
                    "runtime-backed Node unary",
                    ["runtime-backed Node stream 1", "runtime-backed Node stream 2"],
                    "interop.dotnet-to-node",
                    cancellationToken);
            }
            else if (activationContext.PackageId == "sunder.interop.dotnet.runtimebacked.caller")
            {
                await RunInteropAsync(
                    activationContext,
                    "sunder.node.runtimebacked",
                    "runtime-backed Node unary",
                    ["runtime-backed Node stream 1", "runtime-backed Node stream 2"],
                    "interop.dotnet-to-runtime-backed-node",
                    cancellationToken);
            }
            else if (activationContext.PackageId == "sunder.interop.dotnet.self.caller")
            {
                await RunInteropAsync(
                    activationContext,
                    "test.worker.interop",
                    "native .NET unary",
                    ["native .NET stream 1", "native .NET stream 2"],
                    "interop.dotnet-to-dotnet",
                    cancellationToken);
            }
        },
    };
});

static async Task RunInteropAsync(
    SunderWorkerContext context,
    string providerPackageId,
    string expectedUnary,
    IReadOnlyList<string> expectedStream,
    string stateKey,
    CancellationToken cancellationToken)
{
    var catalog = await context.Rpc.DiscoverAsync("example.messages", cancellationToken);
    var provider = catalog.Providers.Single(candidate => candidate.PackageId == providerPackageId);
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
    JsonElement? faultDetails = null;
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
        faultDetails = exception.Error.Details;
    }
    var faultReason = faultDetails is { } details
        ? details.GetProperty("reason").GetString()
        : null;
    var faultRetryable = faultDetails is { } retryDetails
        ? retryDetails.GetProperty("retryable").GetBoolean()
        : (bool?)null;
    if (unary.GetProperty("message").GetString() != expectedUnary
        || !events.SequenceEqual(expectedStream)
        || data.GetProperty("message").GetString() != "shared Worker V1 package data"
        || faultCode != "example.rejected"
        || faultReason != "conformance rejection"
        || faultRetryable is not false)
    {
        throw new InvalidOperationException(".NET to Node Runtime Host RPC conformance failed.");
    }
    await context.State.SetValueAsync(
        stateKey,
        JsonSerializer.Serialize(new
        {
            unary = unary.GetProperty("message").GetString(),
            stream = events,
            data = data.GetProperty("message").GetString(),
            faultCode,
            faultDetails,
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
                "rejected",
                details: JsonSerializer.SerializeToElement(new
                {
                    reason = "conformance rejection",
                    retryable = false,
                }))));
        }
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(new
        {
            message = methodId == "data" ? "shared Worker V1 package data" : "native .NET unary",
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
