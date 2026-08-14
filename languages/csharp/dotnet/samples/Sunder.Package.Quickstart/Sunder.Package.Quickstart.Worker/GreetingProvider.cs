using System.Globalization;
using Microsoft.Extensions.Logging;
using Sunder.Package.Quickstart.Protocol.Generated;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Quickstart.Worker;

internal sealed class GreetingProvider(SunderWorkerContext context) : IGreetingProvider, IDisposable
{
    private const string CountKey = "greeting.count";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = context.Logging.LoggerFactory.CreateLogger<GreetingProvider>();

    public async ValueTask<GreetResponse> CreateAsync(
        SunderRpcInvocationContext invocation,
        GreetRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await context.State.GetValueAsync(CountKey, cancellationToken).ConfigureAwait(false);
            var count = long.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var previous)
                ? checked(previous + 1)
                : 1;
            await context.State.SetValueAsync(
                CountKey,
                count.ToString(CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Created greeting number {InvocationCount}", count);
            return new GreetResponse
            {
                Message = $"Hello, {request.Name}!",
                InvocationCount = count,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
