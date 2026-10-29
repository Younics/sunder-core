using Sunder.Runtime.Client;

namespace Sunder.App.Services;

// Deliberately not IDisposable/IAsyncDisposable: the service container borrows this
// process owner. Supervision must outlive ordinary services and their cleanup.
internal sealed class AppProcessPolicyLifetime
{
    private readonly RuntimeClientTransport _gateway;
    private readonly AppRuntimeProcessTransport _transport;
    private readonly object _gate = new();
    private Task? _exit;

    internal AppProcessPolicySupervisor Supervisor { get; }
    internal RuntimeClientTransport PackageTransport => _transport.Transport;

    internal AppProcessPolicyLifetime(RuntimeConnectionState connection, Action shutdownApp)
    {
        // Do not borrow the container-owned gateway: it can already be disposed
        // while ordinary cleanup still needs authenticated heartbeat proof.
        _gateway = new RuntimeClientTransport(connection.GetConnectionInfo);
        _transport = new AppRuntimeProcessTransport(_gateway);
        Supervisor = new AppProcessPolicySupervisor(_transport, shutdownApp);
    }

    internal AppProcessPolicyLifetime(RuntimeClientTransport gateway, AppRuntimeProcessTransport transport,
        AppProcessPolicySupervisor supervisor)
    {
        _gateway = gateway;
        _transport = transport;
        Supervisor = supervisor;
    }

    internal void BeginShutdown() => Supervisor.BeginShutdown();

    internal Task StopForExitAsync()
    {
        lock (_gate) return _exit ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        try { await Supervisor.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { _transport.Dispose(); }
            finally { _gateway.Dispose(); }
        }
    }
}
