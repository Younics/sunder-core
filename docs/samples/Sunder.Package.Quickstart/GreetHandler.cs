using Microsoft.Extensions.Logging;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Quickstart;

public sealed class GreetHandler(IPackageContext context)
    : IPackageRuntimeOperationHandler<GreetRequest, GreetResponse>, IDisposable
{
    private const string CountKey = "greeting.count";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = context.Logging.LoggerFactory.CreateLogger<GreetHandler>();

    public async ValueTask<GreetResponse> HandleAsync(
        GreetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stored = await context.Storage.State.GetValueAsync(CountKey, cancellationToken);
            var count = int.TryParse(stored, out var previous) ? checked(previous + 1) : 1;
            await context.Storage.State.SetValueAsync(CountKey, count.ToString(), cancellationToken);
            _logger.LogInformation("Created greeting number {InvocationCount}", count);
            return new GreetResponse($"Hello, {request.Name}!", count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
