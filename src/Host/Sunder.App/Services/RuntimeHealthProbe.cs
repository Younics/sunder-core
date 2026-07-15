using System.Net.Sockets;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed class RuntimeHealthProbe : IDisposable
{
    private readonly RuntimeConnectionState _connectionState;
    private readonly RuntimeClientTransport _transport;
    private readonly RuntimeManagementClient _management;
    private readonly bool _ownsTransport;

    public RuntimeHealthProbe(
        RuntimeConnectionState connectionState,
        RuntimeClientTransport? transport = null)
    {
        _connectionState = connectionState;
        _ownsTransport = transport is null;
        _transport = transport ?? new RuntimeClientTransport(
            connectionState.GetConnectionInfo,
            policy: new RuntimeClientPolicyOptions { RequestTimeout = TimeSpan.FromSeconds(2) });
        _management = new RuntimeManagementClient(_transport);
    }

    public async Task<SystemStatusResponse?> TryGetRuntimeStatusAsync(
        Uri runtimeUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            return HasMatchingConnection(runtimeUrl)
                ? await _management.GetSystemStatusAsync(deadline.Token).ConfigureAwait(false)
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task<RuntimeHandshakeResponse?> TryGetRuntimeHandshakeAsync(
        Uri runtimeUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            return HasMatchingConnection(runtimeUrl)
                ? await _transport.NegotiateAsync(deadline.Token).ConfigureAwait(false)
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task ShutdownRuntimeAsync(Uri runtimeUrl, CancellationToken cancellationToken)
    {
        try
        {
            if (HasMatchingConnection(runtimeUrl))
            {
                await _management.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Startup verifies that the old host actually stopped before launching the bundled host.
        }
    }

    public async Task<bool> IsRuntimeHealthyAsync(Uri runtimeUrl, CancellationToken cancellationToken)
    {
        if (_connectionState.ConnectionInfo is null)
        {
            return await CanConnectAsync(runtimeUrl, cancellationToken);
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            return HasMatchingConnection(runtimeUrl)
                   && await _management.IsRuntimeHealthyAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CanConnectAsync(Uri runtimeUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(runtimeUrl.Host, runtimeUrl.Port, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private bool HasMatchingConnection(Uri runtimeUrl)
        => _connectionState.ConnectionInfo?.Matches(runtimeUrl) == true;

    public void Dispose()
    {
        _management.Dispose();
        if (_ownsTransport)
        {
            _transport.Dispose();
        }
    }
}
