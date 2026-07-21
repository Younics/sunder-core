using System.Net;
using System.Net.Sockets;
using Sunder.Host.Client;
using Sunder.Host.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed class RuntimeHealthProbe : IDisposable
{
    private readonly RuntimeConnectionState _connectionState;
    private readonly RuntimeClientTransport _transport;
    private readonly RuntimeManagementClient _management;
    private readonly HostManagementClient _hostManagement;
    private readonly bool _ownsTransport;

    public RuntimeHealthProbe(
        RuntimeConnectionState connectionState,
        RuntimeClientTransport? transport = null,
        HttpMessageHandler? hostHandler = null)
    {
        _connectionState = connectionState;
        _ownsTransport = transport is null;
        _transport = transport ?? new RuntimeClientTransport(
            connectionState.GetConnectionInfo,
            policy: new RuntimeClientPolicyOptions { RequestTimeout = TimeSpan.FromSeconds(2) });
        _management = new RuntimeManagementClient(_transport);
        _hostManagement = new HostManagementClient(connectionState.GetConnectionInfo, hostHandler);
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
                ? await _transport.ProbeHandshakeAsync(deadline.Token).ConfigureAwait(false)
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
        if (!HasMatchingConnection(runtimeUrl))
        {
            return;
        }
        if (await TryShutdownSupervisorAsync(runtimeUrl, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await _transport.ShutdownWithoutProtocolNegotiationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Direct Runtime replacement verifies that the old process actually stopped.
        }
    }

    public async Task<bool> IsRuntimeHealthyAsync(Uri runtimeUrl, CancellationToken cancellationToken)
        => await CanConnectAsync(runtimeUrl, cancellationToken);

    public async Task<bool> TryEnsureSupervisedRuntimeStartedAsync(
        Uri runtimeUrl,
        CancellationToken cancellationToken)
    {
        if (!HasMatchingConnection(runtimeUrl))
        {
            return false;
        }

        var handshake = await TryGetHostHandshakeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
        if (handshake is null)
        {
            return false;
        }

        if (HostProtocolCompatibility.GetIncompatibility(
                handshake,
                HostProtocolFeatures.RuntimeLifecycleV1) is { } incompatibility)
        {
            throw new InvalidOperationException(incompatibility);
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var durable = SupportsDurableOperations(handshake);
            var mutationId = Guid.NewGuid();
            while (true)
            {
                var status = await _hostManagement.GetStatusAsync(deadline.Token).ConfigureAwait(false);
                if (durable && status.ActiveOperationId is not null)
                {
                    await _hostManagement.WaitForOperationAsync(
                        await _hostManagement.GetOperationAsync(status.ActiveOperationId, deadline.Token).ConfigureAwait(false),
                        deadline.Token).ConfigureAwait(false);
                    continue;
                }
                if (status.State == HostRuntimeState.Ready)
                {
                    return true;
                }
                if (IsTransitional(status.State))
                {
                    if (!durable)
                    {
                        return true;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token).ConfigureAwait(false);
                    continue;
                }

                var request = new HostLifecycleRequest(mutationId, status.DeploymentGeneration);
                try
                {
                    if (durable)
                    {
                        var submission = await _hostManagement.SubmitStartRuntimeAsync(request, deadline.Token).ConfigureAwait(false);
                        await WaitForSuccessfulOperationAsync(submission.Operation, deadline.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await _hostManagement.StartRuntimeLegacyAsync(request, deadline.Token).ConfigureAwait(false);
                    }
                    return true;
                }
                catch (HttpRequestException exception) when (
                    durable && exception.StatusCode == HttpStatusCode.Conflict)
                {
                    // Startup reconciliation may have advanced the generation while submission waited.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Sunder Host at '{runtimeUrl}' could not start its Runtime worker.",
                exception);
        }
    }

    public async Task<bool> TryStopSupervisedRuntimeAsync(
        Uri runtimeUrl,
        CancellationToken cancellationToken)
    {
        if (!HasMatchingConnection(runtimeUrl))
        {
            return false;
        }

        var handshake = await TryGetHostHandshakeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
        if (handshake is null)
        {
            return false;
        }

        if (HostProtocolCompatibility.GetIncompatibility(
                handshake,
                HostProtocolFeatures.RuntimeLifecycleV1) is { } incompatibility)
        {
            throw new InvalidOperationException(incompatibility);
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            var durable = SupportsDurableOperations(handshake);
            var mutationId = Guid.NewGuid();
            while (true)
            {
                var status = await _hostManagement.GetStatusAsync(deadline.Token).ConfigureAwait(false);
                if (durable && status.ActiveOperationId is not null)
                {
                    await _hostManagement.WaitForOperationAsync(
                        await _hostManagement.GetOperationAsync(status.ActiveOperationId, deadline.Token).ConfigureAwait(false),
                        deadline.Token).ConfigureAwait(false);
                    continue;
                }
                if (status.State == HostRuntimeState.Stopped
                    && status.DesiredState == HostRuntimeDesiredState.Stopped)
                {
                    return true;
                }
                if (durable && IsTransitional(status.State))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token).ConfigureAwait(false);
                    continue;
                }

                var request = new HostLifecycleRequest(mutationId, status.DeploymentGeneration);
                try
                {
                    if (durable)
                    {
                        var submission = await _hostManagement.SubmitStopRuntimeAsync(request, deadline.Token).ConfigureAwait(false);
                        await WaitForSuccessfulOperationAsync(submission.Operation, deadline.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await _hostManagement.StopRuntimeLegacyAsync(request, deadline.Token).ConfigureAwait(false);
                    }
                    return true;
                }
                catch (HttpRequestException exception) when (
                    durable && exception.StatusCode == HttpStatusCode.Conflict)
                {
                    // Startup reconciliation may have advanced the generation while submission waited.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Sunder Host at '{runtimeUrl}' could not stop its Runtime worker.",
                exception);
        }
    }

    private async Task<bool> TryShutdownSupervisorAsync(
        Uri runtimeUrl,
        CancellationToken cancellationToken)
    {
        var handshake = await TryGetHostHandshakeAsync(runtimeUrl, cancellationToken).ConfigureAwait(false);
        if (handshake is null)
        {
            return false;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await _hostManagement.ShutdownHostAsync(deadline.Token).ConfigureAwait(false);
        return true;
    }

    internal async Task<HostHandshakeResponse?> TryGetHostHandshakeAsync(
        Uri runtimeUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            return await _hostManagement.GetHandshakeAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception exception)
        {
            if (!await CanConnectAsync(runtimeUrl, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            throw new InvalidOperationException(
                $"The service at '{runtimeUrl}' was reachable but its Sunder Host identity could not be verified.",
                exception);
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

    private static bool SupportsDurableOperations(HostHandshakeResponse handshake)
        => handshake.SupportedFeatures.Contains(
            HostProtocolFeatures.DurableOperationsV1,
            StringComparer.Ordinal);

    private static bool IsTransitional(HostRuntimeState state)
        => state is HostRuntimeState.Starting
            or HostRuntimeState.Draining
            or HostRuntimeState.Stopping
            or HostRuntimeState.Restarting
            or HostRuntimeState.Updating
            or HostRuntimeState.RollingBack;

    private async Task WaitForSuccessfulOperationAsync(
        HostOperationDescriptor operation,
        CancellationToken cancellationToken)
    {
        operation = await _hostManagement.WaitForOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        if (operation.State != HostOperationState.Succeeded)
        {
            throw new InvalidOperationException(
                $"Host lifecycle operation '{operation.OperationId}' failed"
                + (operation.FailureCode is null ? string.Empty : $" ({operation.FailureCode})")
                + $": {operation.Message ?? operation.State.ToString()}.");
        }
    }

    public void Dispose()
    {
        _hostManagement.Dispose();
        _management.Dispose();
        if (_ownsTransport)
        {
            _transport.Dispose();
        }
    }
}
