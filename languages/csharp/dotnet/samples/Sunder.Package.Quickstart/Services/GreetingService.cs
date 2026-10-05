using System.Globalization;
using Microsoft.Extensions.Logging;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Quickstart;

[SunderRpcService(SunderRpcExposure.Package, ContractId = "docs.sunder.quickstart.greeting", ServiceId = "greeting")]
[SunderRpcProvider(ProviderId = "docs.sunder.quickstart.greeting")]
public sealed partial class GreetingService(IPackageContext context) : IDisposable
{
    private const string CountKey = "greeting.count";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = context.Logging.LoggerFactory.CreateLogger<GreetingService>();

    [SunderRpcMethod]
    public async ValueTask<GreetResponse> CreateAsync(
        GreetRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await context.Storage.State.GetValueAsync(CountKey, cancellationToken).ConfigureAwait(false);
            var count = long.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var previous)
                ? checked(previous + 1)
                : 1;
            await context.Storage.State.SetValueAsync(
                CountKey,
                count.ToString(CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Created greeting number {InvocationCount}", count);
            return new GreetResponse
            {
                Message = new() { Value = $"Hello, {request.Name.Value}!" },
                InvocationCount = new() { Value = count },
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

public sealed record GreetRequest
{
    public required GreetingName Name { get; init; }
}

public sealed record GreetResponse
{
    public required GreetingMessage Message { get; init; }
    public required GreetingCount InvocationCount { get; init; }
}

[SunderRpcDefinition("Name"), SunderRpcTransparent, SunderRpcFacet(MinLength = 1, MaxLength = 120)]
public sealed record GreetingName
{
    public required string Value { get; init; }
}

[SunderRpcDefinition("Message"), SunderRpcTransparent, SunderRpcFacet(MinLength = 1, MaxLength = 256)]
public sealed record GreetingMessage
{
    public required string Value { get; init; }
}

[SunderRpcDefinition("InvocationCount"), SunderRpcTransparent, SunderRpcFacet(Minimum = "1", Maximum = "9007199254740991")]
public sealed record GreetingCount
{
    public required long Value { get; init; }
}
