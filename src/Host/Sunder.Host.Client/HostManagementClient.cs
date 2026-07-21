using System.Net.Http.Headers;
using System.Net.Http.Json;
using Sunder.Host.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.Host.Client;

public sealed class HostManagementClient : IDisposable
{
    private readonly Func<RuntimeConnectionInfo?> _getConnection;
    private readonly HttpClient _client;

    public HostManagementClient(
        Func<RuntimeConnectionInfo?> getConnection,
        HttpMessageHandler? handler = null)
    {
        _getConnection = getConnection ?? throw new ArgumentNullException(nameof(getConnection));
        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public Task<HostHandshakeResponse> GetHandshakeAsync(CancellationToken cancellationToken = default)
        => GetRequiredAsync<HostHandshakeResponse>("handshake", cancellationToken);

    public Task<HostRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => GetRequiredAsync<HostRuntimeStatus>("v1/status", cancellationToken);

    public Task<HostRuntimeStatus> StartRuntimeAsync(
        HostLifecycleRequest request,
        CancellationToken cancellationToken = default)
        => SubmitAndWaitAsync(
            "v1/runtime/start",
            HostOperationKinds.RuntimeStart,
            request,
            cancellationToken);

    public Task<HostRuntimeStatus> StopRuntimeAsync(
        HostLifecycleRequest request,
        CancellationToken cancellationToken = default)
        => SubmitAndWaitAsync(
            "v1/runtime/stop",
            HostOperationKinds.RuntimeStop,
            request,
            cancellationToken);

    public Task<HostRuntimeStatus> RestartRuntimeAsync(
        HostLifecycleRequest request,
        CancellationToken cancellationToken = default)
        => SubmitAndWaitAsync(
            "v1/runtime/restart",
            HostOperationKinds.RuntimeRestart,
            request,
            cancellationToken);

    public Task<HostRuntimeStatus> StartRuntimeLegacyAsync(
        HostLifecycleRequest request,
        CancellationToken cancellationToken = default)
        => PostAsync("v1/runtime/start", request, cancellationToken);

    public Task<HostRuntimeStatus> StopRuntimeLegacyAsync(
        HostLifecycleRequest request,
        CancellationToken cancellationToken = default)
        => PostAsync("v1/runtime/stop", request, cancellationToken);

    public Task<HostRuntimeStatus> RestartRuntimeLegacyAsync(
        HostLifecycleRequest request,
        CancellationToken cancellationToken = default)
        => PostAsync("v1/runtime/restart", request, cancellationToken);

    public Task<HostLifecycleSubmission> SubmitStartRuntimeAsync(
        HostLifecycleRequest request,
        CancellationToken cancellationToken = default)
        => SubmitAsync("v1/runtime/start", HostOperationKinds.RuntimeStart, request, cancellationToken);

    public Task<HostLifecycleSubmission> SubmitStopRuntimeAsync(
        HostLifecycleRequest request,
        CancellationToken cancellationToken = default)
        => SubmitAsync("v1/runtime/stop", HostOperationKinds.RuntimeStop, request, cancellationToken);

    public Task<HostLifecycleSubmission> SubmitRestartRuntimeAsync(
        HostLifecycleRequest request,
        CancellationToken cancellationToken = default)
        => SubmitAsync("v1/runtime/restart", HostOperationKinds.RuntimeRestart, request, cancellationToken);

    public Task<HostOperationDescriptor> GetOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException("Host lifecycle operation ID is required.", nameof(operationId));
        }
        return GetRequiredAsync<HostOperationDescriptor>(
            $"v1/operations/{Uri.EscapeDataString(operationId)}",
            cancellationToken);
    }

    public async Task<HostOperationDescriptor> WaitForOperationAsync(
        HostOperationDescriptor operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        while (operation.State is HostOperationState.Accepted or HostOperationState.Running)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            var updated = await GetOperationAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(updated.OperationId, operation.OperationId, StringComparison.Ordinal)
                || updated.MutationId != operation.MutationId
                || !string.Equals(updated.Kind, operation.Kind, StringComparison.Ordinal)
                || updated.ExpectedDeploymentGeneration != operation.ExpectedDeploymentGeneration)
            {
                throw new InvalidDataException("Sunder Host changed immutable lifecycle operation identity fields.");
            }
            operation = updated;
        }
        return operation;
    }

    public async Task ShutdownHostAsync(CancellationToken cancellationToken = default)
        => await ShutdownHostAsync("v1/shutdown", cancellationToken).ConfigureAwait(false);

    public async Task ShutdownHostForUninstallAsync(CancellationToken cancellationToken = default)
        => await ShutdownHostAsync("v1/shutdown?mode=uninstall", cancellationToken).ConfigureAwait(false);

    private async Task ShutdownHostAsync(string path, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, path);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<T> GetRequiredAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("Sunder Host returned an empty response.");
    }

    private async Task<HostRuntimeStatus> PostAsync(
        string path,
        HostLifecycleRequest payload,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(payload);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HostRuntimeStatus>(cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Sunder Host returned an empty lifecycle response.");
    }

    private async Task<HostRuntimeStatus> SubmitAndWaitAsync(
        string path,
        string expectedKind,
        HostLifecycleRequest request,
        CancellationToken cancellationToken)
    {
        var submission = await SubmitAsync(path, expectedKind, request, cancellationToken).ConfigureAwait(false);
        var operation = await WaitForOperationAsync(submission.Operation, cancellationToken).ConfigureAwait(false);
        if (operation.State != HostOperationState.Succeeded)
        {
            throw new InvalidOperationException(
                $"Host lifecycle operation '{operation.OperationId}' failed"
                + (operation.FailureCode is null ? string.Empty : $" ({operation.FailureCode})")
                + $": {operation.Message ?? operation.State.ToString()}.");
        }
        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HostLifecycleSubmission> SubmitAsync(
        string path,
        string expectedKind,
        HostLifecycleRequest payload,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(payload);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var submission = await response.Content.ReadFromJsonAsync<HostLifecycleSubmission>(
                             cancellationToken: cancellationToken).ConfigureAwait(false)
                         ?? throw new InvalidDataException("Sunder Host returned an empty lifecycle submission.");
        if (string.IsNullOrWhiteSpace(submission.Operation.OperationId)
            || submission.Operation.MutationId != payload.MutationId
            || !string.Equals(submission.Operation.Kind, expectedKind, StringComparison.Ordinal)
            || submission.Operation.ExpectedDeploymentGeneration != payload.ExpectedDeploymentGeneration)
        {
            throw new InvalidDataException("Sunder Host returned a lifecycle operation that does not match the request.");
        }
        var expectedLocation = $"/api/host/v1/operations/{Uri.EscapeDataString(submission.Operation.OperationId)}";
        var location = response.Headers.Location;
        var actualPath = location?.IsAbsoluteUri == true ? location.AbsolutePath : location?.OriginalString;
        if (!string.Equals(actualPath, expectedLocation, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Sunder Host returned an invalid lifecycle operation location.");
        }
        return submission;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var connection = _getConnection()
            ?? throw new InvalidOperationException("Authenticated Sunder Host connection information is not available.");
        var request = new HttpRequestMessage(
            method,
            new Uri(RuntimeConnectionInfo.Normalize(connection.RuntimeUrl), $"api/host/{path}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.BearerToken);
        return request;
    }

    public void Dispose() => _client.Dispose();
}
