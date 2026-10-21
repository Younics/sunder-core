using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;

[assembly: SunderPackage(Id = "compatibility.dotnet.probe", Name = "Compatibility .NET RPC probe")]

await SunderWorker.RunAsync(context =>
{
    var descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Path.Combine(context.ContentRootPath, "contracts/probe.rpc.json")));
    var lifetime = new CancellationTokenSource();
    Task? running = null;
    return new SunderWorkerOptions([new("compatibility.probe.provider", descriptor.ContractId, descriptor.Version,
        descriptor.Sha256, descriptor.AbiSha256!, new ProbeHandler(context))])
    {
        OnActivated = (worker, _) =>
        {
            running = Task.Run(async () =>
            {
                try { await ExecuteAsync(worker, lifetime.Token); }
                catch (Exception error) when (!lifetime.IsCancellationRequested)
                { await worker.State.SetValueAsync("compatibility.failure", error.ToString()); }
            });
            return ValueTask.CompletedTask;
        },
        OnShutdown = async (_, _) =>
        {
            lifetime.Cancel();
            if (running is not null) try { await running; } catch (OperationCanceledException) { }
            lifetime.Dispose();
        },
    };
});

static async Task ExecuteAsync(SunderWorkerContext context, CancellationToken token)
{
    string? nonce;
    while (string.IsNullOrEmpty(nonce = await context.State.GetValueAsync("compatibility.challenge", token)))
        await Task.Delay(25, token);
    var provider = (await context.Rpc.DiscoverAsync("compatibility.probe", token)).Providers.Single(x => x.PackageId == context.PackageId);
    var request = JsonSerializer.SerializeToElement(new { value = nonce });
    var unary = await context.Rpc.InvokeAsync(provider.Endpoint, "probe", "echo", request, cancellationToken: token);
    var stream = new List<string?>();
    await foreach (var value in context.Rpc.SubscribeAsync(provider.Endpoint, "probe", "finite", request, cancellationToken: token)) stream.Add(value.GetString());
    using var abort = CancellationTokenSource.CreateLinkedTokenSource(token);
    var started = false;
    var clientCancelled = false;
    try
    {
        await foreach (var value in context.Rpc.SubscribeAsync(provider.Endpoint, "probe", "wait", request, cancellationToken: abort.Token))
        {
            if (started || value.GetString() != nonce + ":started") throw new InvalidOperationException("Unexpected cancellation stream event.");
            started = true;
            abort.Cancel();
        }
        clientCancelled = abort.IsCancellationRequested;
    }
    catch (OperationCanceledException) when (abort.IsCancellationRequested) { clientCancelled = true; }
    catch (SunderRpcException error) when (abort.IsCancellationRequested && error.Error.Kind == SunderRpcErrorKind.Cancelled) { clientCancelled = true; }
    string? providerCancelled = null;
    for (var attempt = 0; attempt < 400; attempt++)
    {
        providerCancelled = await context.State.GetValueAsync("compatibility.provider-cancelled", token);
        if (providerCancelled == nonce) break;
        await Task.Delay(25, token);
    }
    if (!started || !clientCancelled || providerCancelled != nonce) throw new InvalidOperationException("Provider cancellation did not drain.");
    var afterCancel = await context.Rpc.InvokeAsync(provider.Endpoint, "probe", "echo", JsonSerializer.SerializeToElement(new { value = "after:" + nonce }), cancellationToken: token);
    await context.State.SetValueAsync("compatibility.report", JsonSerializer.Serialize(new { protocol = "sunder.compatibility.probe.v1", nonce,
        unary = unary.GetString(), stream, started, clientCancelled, providerCancelled, afterCancel = afterCancel.GetString() }), token);
}

sealed class ProbeHandler(SunderWorkerContext worker) : ISunderRpcServiceHandler
{
    public ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext context, string serviceId, string methodId,
        JsonElement request, CancellationToken cancellationToken = default)
    {
        if (serviceId != "probe" || methodId != "echo") throw new InvalidOperationException("Unexpected unary call.");
        return ValueTask.FromResult(JsonSerializer.SerializeToElement("echo:" + request.GetProperty("value").GetString()));
    }

    public async IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId, string methodId,
        JsonElement request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (serviceId != "probe") throw new InvalidOperationException("Unexpected service.");
        var nonce = request.GetProperty("value").GetString();
        if (methodId == "finite")
        {
            yield return JsonSerializer.SerializeToElement(nonce + ":1");
            yield return JsonSerializer.SerializeToElement(nonce + ":2");
            yield break;
        }
        if (methodId != "wait") throw new InvalidOperationException("Unexpected stream.");
        try
        {
            yield return JsonSerializer.SerializeToElement(nonce + ":started");
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested) await worker.State.SetValueAsync("compatibility.provider-cancelled", nonce!);
        }
    }
}
