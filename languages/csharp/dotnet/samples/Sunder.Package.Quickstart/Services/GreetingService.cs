using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;

namespace Sunder.Package.Quickstart;

[SunderService]
public sealed partial class GreetingService(IPackageContext context) : IDisposable
{
    private const string CountKey = "greeting.count";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = context.Logging.LoggerFactory.CreateLogger<GreetingService>();

    public async ValueTask<GreetingResult> GreetAsync(
        [SunderValueConstraints(MinLength = 1, MaxLength = 120)] string name,
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
            return new GreetingResult
            {
                Message = $"Hello, {name}!",
                InvocationCount = count,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await context.Storage.State.SetValueAsync(CountKey, "0", cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async IAsyncEnumerable<GreetingResult> GreetManyAsync(
        IReadOnlyList<string> names,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var name in names)
            yield return await GreetAsync(name, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _gate.Dispose();
}

public sealed record GreetingResult
{
    [SunderValueConstraints(MinLength = 1, MaxLength = 256)]
    public required string Message { get; init; }
    [SunderValueConstraints(Minimum = "1", Maximum = "9007199254740991")]
    public required long InvocationCount { get; init; }
}
