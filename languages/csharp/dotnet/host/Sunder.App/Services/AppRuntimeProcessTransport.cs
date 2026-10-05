using Sunder.Host.Client;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.App.Services;

// Package permits and App RPC must reach Runtime with the App's OS peer identity.
// The public TCP gateway is still used for shell management and availability.
internal sealed class AppRuntimeProcessTransport : IDisposable
{
    private readonly RuntimeClientTransport _gateway;
    private Guid? _runtimeInstanceId;
    internal RuntimeClientTransport Transport { get; }

    internal AppRuntimeProcessTransport(RuntimeClientTransport gateway)
        : this(gateway, connection => connection.IpcEndpoint is not null
            ? connection : HostConnectionInfoStore.LoadLocalRuntimeWorkerFor(connection)) { }

    internal AppRuntimeProcessTransport(RuntimeClientTransport gateway, Func<RuntimeConnectionInfo, RuntimeConnectionInfo?> loadWorker,
        Func<RuntimeIpcEndpoint, HttpMessageHandler>? handlerFactory = null)
    {
        _gateway = gateway;
        RuntimeConnectionInfo? Connection()
        {
            var gatewayConnection = gateway.GetConnectionInfo();
            var worker = gatewayConnection is null ? null : loadWorker(gatewayConnection);
            return worker?.IpcEndpoint is not null && worker.RuntimeUrl == RuntimeIpcEndpoint.LogicalRuntimeUrl ? worker : null;
        }
        Transport = new RuntimeClientTransport(Connection, new IpcHandler(Connection, handlerFactory ?? RuntimeIpcHttpMessageHandlerFactory.Create), gateway.Policy);
    }

    internal async Task<RuntimeHandshakeResponse> HandshakeAsync(CancellationToken token)
    {
        var current = await _gateway.ProbeHandshakeAsync(token).ConfigureAwait(false);
        // Let the process watchdog observe a changed Host immediately, even if its
        // new private connection is not yet available or the old socket still exists.
        if (_runtimeInstanceId is { } expected && current.RuntimeInstanceId != expected) return current;
        var direct = await Transport.ProbeHandshakeAsync(token).ConfigureAwait(false);
        if (direct.RuntimeInstanceId != current.RuntimeInstanceId)
            throw new InvalidOperationException(PackagePolicyErrorCodes.AppProcessSupervisionRequired);
        _runtimeInstanceId = current.RuntimeInstanceId;
        return current;
    }

    public void Dispose() => Transport.Dispose();

    private sealed class IpcHandler(Func<RuntimeConnectionInfo?> connection, Func<RuntimeIpcEndpoint, HttpMessageHandler> handlerFactory) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private RuntimeConnectionInfo? _current;
        private HttpMessageInvoker? _client;
        private bool _disposed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            HttpMessageInvoker client;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var selected = connection();
                if (selected?.IpcEndpoint is null || request.RequestUri is null || !selected.CanSendTo(request.RequestUri)
                    || request.Headers.Authorization?.Scheme != "Bearer" || request.Headers.Authorization.Parameter != selected.BearerToken)
                    throw new InvalidOperationException(PackagePolicyErrorCodes.AppProcessSupervisionRequired);
                if (_current != selected)
                {
                    _client?.Dispose();
                    _client = new HttpMessageInvoker(handlerFactory(selected.IpcEndpoint));
                    _current = selected;
                }
                client = _client!;
            }
            return client.SendAsync(request, token);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) lock (_gate) { _disposed = true; _client?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
