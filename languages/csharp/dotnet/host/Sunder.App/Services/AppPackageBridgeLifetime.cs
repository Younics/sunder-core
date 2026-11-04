using Sunder.App.Bridge;

namespace Sunder.App.Services;

// Resolved last by activation, so DI retires package-owned services before the scoped App services.
internal sealed class AppPackageBridgeLifetime : IAsyncDisposable
{
    private readonly object _gate = new();
    private Task? _disposal;
    internal AppPackageBridgeHost? Host { get; set; }
    internal IAppPackageActivationV1? Activation { get; set; }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        try { if (Activation is { } activation) await activation.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            Activation = null;
            if (Host is { } host) await host.DisposeAsync().ConfigureAwait(false);
            Host = null;
        }
    }
}
