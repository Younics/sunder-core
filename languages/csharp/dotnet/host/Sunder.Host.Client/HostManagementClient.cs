using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Sunder.Host.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.Host.Client;

public sealed class HostManagementClient : IDisposable
{
    private const long MaxJsonResponseBytes = 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
    };
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
        _ = await SendAsync(
            request,
            static (response, _) =>
            {
                response.EnsureSuccessStatusCode();
                return Task.FromResult(true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> GetRequiredAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        return await SendAsync(
            request,
            static async (response, token) =>
            {
                response.EnsureSuccessStatusCode();
                return await ReadRequiredJsonAsync<T>(response.Content, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HostLifecycleSubmission> SubmitAsync(
        string path,
        string expectedKind,
        HostLifecycleRequest payload,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(payload);
        return await SendAsync(
            request,
            async (response, token) =>
            {
                response.EnsureSuccessStatusCode();
                var submission = await ReadRequiredJsonAsync<HostLifecycleSubmission>(
                    response.Content,
                    token).ConfigureAwait(false);
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
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(
        HttpRequestMessage request,
        Func<HttpResponseMessage, CancellationToken, Task<T>> readAsync,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token).ConfigureAwait(false);
            EnsureResponseOrigin(request, response);
            return await readAsync(response, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Sunder Host request exceeded the {RequestTimeout} timeout.", exception);
        }
    }

    private static async Task<T> ReadRequiredJsonAsync<T>(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } contentLength
            && contentLength > MaxJsonResponseBytes)
        {
            throw new InvalidDataException(
                $"Sunder Host response exceeds the {MaxJsonResponseBytes} byte limit.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var remaining = MaxJsonResponseBytes - destination.Length;
            var readLength = (int)Math.Min(buffer.Length, remaining + 1);
            var read = await source.ReadAsync(
                buffer.AsMemory(0, readLength),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return destination.Length == 0
                    ? throw new InvalidDataException("Sunder Host returned an empty response.")
                    : JsonSerializer.Deserialize<T>(destination.ToArray(), JsonOptions)
                      ?? throw new InvalidDataException("Sunder Host returned an empty response.");
            }
            if (read > remaining)
            {
                throw new InvalidDataException(
                    $"Sunder Host response exceeds the {MaxJsonResponseBytes} byte limit.");
            }
            destination.Write(buffer, 0, read);
        }
    }

    private static void EnsureResponseOrigin(HttpRequestMessage request, HttpResponseMessage response)
    {
        var expected = request.RequestUri;
        var actual = response.RequestMessage?.RequestUri ?? expected;
        if (expected is not null
            && actual is not null
            && string.Equals(expected.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(expected.Host, actual.Host, StringComparison.OrdinalIgnoreCase)
            && expected.Port == actual.Port
            && string.IsNullOrEmpty(actual.UserInfo))
        {
            return;
        }

        throw new InvalidDataException("Sunder Host response changed to an untrusted origin.");
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
