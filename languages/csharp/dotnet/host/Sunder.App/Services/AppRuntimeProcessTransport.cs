using System.Diagnostics;
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
    private readonly RuntimePolicyHeartbeatClient _heartbeatGateway;
    private readonly RuntimePolicyHeartbeatClient _heartbeatWorker;
    private Guid? _runtimeInstanceId;
    private string _handshakePhase = "idle";
    internal RuntimeClientTransport Transport { get; }
    internal RuntimeClientTransport PolicyTransport { get; }
    internal string HandshakePhase => Volatile.Read(ref _handshakePhase);
    internal string DiagnosticPhase => HandshakePhase is { } phase && phase.StartsWith("gateway-", StringComparison.Ordinal)
        ? $"{phase}/{_heartbeatGateway.DiagnosticPhase}" : $"{HandshakePhase}/{_heartbeatWorker.DiagnosticPhase}";

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
        var heartbeatGatewayHandler = handlerFactory is null
            ? (HttpMessageHandler)new RuntimePolicyHttpMessageHandler(gateway.GetConnectionInfo)
            : new IpcHandler(gateway.GetConnectionInfo, handlerFactory, allowTcp: true);
        var heartbeatWorkerHandler = handlerFactory is null
            ? (HttpMessageHandler)new RuntimePolicyHttpMessageHandler(Connection, requireIpc: true)
            : new IpcHandler(Connection, handlerFactory);
        handlerFactory ??= RuntimeIpcHttpMessageHandlerFactory.Create;
        Transport = new RuntimeClientTransport(Connection, new IpcHandler(Connection, handlerFactory), gateway.Policy);
        // Package RPC negotiation and long-lived calls must not queue supervision
        // behind their connection/cache work. Both channels retain the same private
        // discovery, credential and OS-peer checks.
        PolicyTransport = new RuntimeClientTransport(Connection, new IpcHandler(Connection, handlerFactory), gateway.Policy);
        _heartbeatGateway = new(gateway.GetConnectionInfo, heartbeatGatewayHandler, gateway.Policy);
        _heartbeatWorker = new(Connection, heartbeatWorkerHandler, gateway.Policy);
    }

    internal RuntimeHandshakeResponse HeartbeatHandshake(CancellationToken token)
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            Volatile.Write(ref _handshakePhase, "gateway-handshake");
            var current = _heartbeatGateway.Handshake(token, started);
            if (_runtimeInstanceId is { } expected && current.RuntimeInstanceId != expected)
            {
                Volatile.Write(ref _handshakePhase, "idle");
                return current;
            }
            Volatile.Write(ref _handshakePhase, "private-handshake");
            var direct = _heartbeatWorker.Handshake(token, started);
            if (direct.RuntimeInstanceId != current.RuntimeInstanceId)
                throw new InvalidOperationException(PackagePolicyErrorCodes.AppProcessSupervisionRequired);
            _runtimeInstanceId = current.RuntimeInstanceId;
            Volatile.Write(ref _handshakePhase, "idle");
            return current;
        }
        catch
        {
            Volatile.Write(ref _handshakePhase, _handshakePhase == "gateway-handshake"
                ? "gateway-handshake-failed" : "private-handshake-failed");
            throw;
        }
    }

    internal RuntimeAppPolicySessionStatus Heartbeat(RuntimeAppPolicySessionRequest session, CancellationToken token)
        => _heartbeatWorker.Heartbeat(session, token);

    internal async Task<RuntimeHandshakeResponse> HandshakeAsync(CancellationToken token)
    {
        try
        {
            Volatile.Write(ref _handshakePhase, "gateway-handshake");
            var current = await _gateway.ProbeHandshakeAsync(token).ConfigureAwait(false);
            // Let the process watchdog observe a changed Host immediately, even if its
            // new private connection is not yet available or the old socket still exists.
            if (_runtimeInstanceId is { } expected && current.RuntimeInstanceId != expected)
            {
                Volatile.Write(ref _handshakePhase, "idle");
                return current;
            }
            Volatile.Write(ref _handshakePhase, "private-handshake");
            var direct = await PolicyTransport.ProbeHandshakeAsync(token).ConfigureAwait(false);
            if (direct.RuntimeInstanceId != current.RuntimeInstanceId)
                throw new InvalidOperationException(PackagePolicyErrorCodes.AppProcessSupervisionRequired);
            _runtimeInstanceId = current.RuntimeInstanceId;
            Volatile.Write(ref _handshakePhase, "idle");
            return current;
        }
        catch
        {
            Volatile.Write(ref _handshakePhase, _handshakePhase == "gateway-handshake"
                ? "gateway-handshake-failed" : "private-handshake-failed");
            throw;
        }
    }

    public void Dispose()
    {
        _heartbeatWorker.Dispose();
        _heartbeatGateway.Dispose();
        PolicyTransport.Dispose();
        Transport.Dispose();
    }

    private sealed class IpcHandler(Func<RuntimeConnectionInfo?> connection, Func<RuntimeIpcEndpoint, HttpMessageHandler> handlerFactory,
        bool allowTcp = false) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private RuntimeConnectionInfo? _current;
        private HttpMessageInvoker? _client;
        private bool _disposed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Client(request).SendAsync(request, token);

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken token)
            => Client(request).Send(request, token);

        private HttpMessageInvoker Client(HttpRequestMessage request)
        {
            HttpMessageInvoker client;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var selected = connection();
                if (selected is null || selected.IpcEndpoint is null && !allowTcp
                    || request.RequestUri is null || !selected.CanSendTo(request.RequestUri)
                    || request.Headers.Authorization?.Scheme != "Bearer" || request.Headers.Authorization.Parameter != selected.BearerToken)
                    throw new InvalidOperationException(PackagePolicyErrorCodes.AppProcessSupervisionRequired);
                if (_current != selected)
                {
                    _client?.Dispose();
                    _client = new HttpMessageInvoker(selected.IpcEndpoint is { } endpoint ? handlerFactory(endpoint)
                        : new RuntimePolicyHttpMessageHandler(connection));
                    _current = selected;
                }
                client = _client!;
            }
            return client;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) lock (_gate) { _disposed = true; _client?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
