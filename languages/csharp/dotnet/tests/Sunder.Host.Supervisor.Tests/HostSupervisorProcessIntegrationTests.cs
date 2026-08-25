using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Sunder.Core.TestSupport;
using Sunder.Host.Contracts;
using Sunder.Host.Supervisor;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Host.Supervisor.Tests;

public sealed class HostSupervisorProcessIntegrationTests
{
    private const string CorrelationHeader = "X-Correlation-ID";

    [Fact]
    public async Task SupervisorProcess_GatewaysToNestedRuntimeAndOwnsResetDrain()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-host-process-tests");
        var root = scope.RootPath;
        var hostStateRoot = Path.Combine(root, "host");
        var runtimeStateRoot = Path.Combine(root, "runtime");
        var externalConnectionPath = Path.Combine(root, "connection", "host.json");
        var workerConnectionPath = Path.Combine(hostStateRoot, "connection", "runtime-worker.json");
        var externalToken = $"integration-external-{Guid.NewGuid():N}";
        var deploymentIdentity = HostDeploymentIdentityVerifier.Compute(
            Path.GetDirectoryName(typeof(HostStartupOptions).Assembly.Location)!);
        await using var process = StartSupervisor(
            typeof(RuntimeHostStartupOptions).Assembly.Location,
            hostStateRoot,
            runtimeStateRoot,
            externalConnectionPath,
            externalToken,
            deploymentIdentity);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        try
        {
            var connection = await WaitForConnectionAsync(
                externalConnectionPath,
                process,
                timeout.Token);
            Assert.Equal(externalToken, connection.BearerToken);

            using (var anonymous = new HttpClient { BaseAddress = connection.RuntimeUrl })
            using (var unauthorized = await anonymous.GetAsync("api/host/handshake", timeout.Token))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            }

            using var authenticated = new HttpClient
            {
                BaseAddress = connection.RuntimeUrl,
                Timeout = Timeout.InfiniteTimeSpan,
            };
            authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                connection.BearerToken);

            var hostHandshake = await authenticated.GetFromJsonAsync<HostHandshakeResponse>(
                "api/host/handshake",
                timeout.Token);
            Assert.NotNull(hostHandshake);
            Assert.Equal(HostProtocol.Identity, hostHandshake.ProtocolIdentity);
            Assert.Equal(deploymentIdentity, hostHandshake.DeploymentIdentity);
            var repeatedHandshake = await authenticated.GetFromJsonAsync<HostHandshakeResponse>(
                "api/host/handshake",
                timeout.Token);
            Assert.Equal(deploymentIdentity, repeatedHandshake?.DeploymentIdentity);

            var ready = await WaitForHostStateAsync(
                authenticated,
                HostRuntimeState.Ready,
                process,
                timeout.Token);
            Assert.Equal(HostRuntimeDesiredState.Running, ready.DesiredState);
            Assert.NotNull(ready.RuntimeInstanceId);

            var workerConnection = await WaitForConnectionAsync(
                workerConnectionPath,
                process,
                timeout.Token);
            Assert.Equal(RuntimeIpcEndpoint.LogicalRuntimeUrl, workerConnection.RuntimeUrl);
            Assert.NotEqual(connection.BearerToken, workerConnection.BearerToken);

            var runtimeHandshake = await authenticated.GetFromJsonAsync<RuntimeHandshakeResponse>(
                "api/handshake",
                timeout.Token);
            Assert.NotNull(runtimeHandshake);
            Assert.Equal(ready.RuntimeInstanceId, runtimeHandshake.RuntimeInstanceId);

            using var runtime = new RuntimeManagementClient(() => connection);
            var system = await runtime.GetSystemStatusAsync(timeout.Token);
            Assert.True(system.IsReady);
            Assert.Equal(RuntimeBootstrapState.Ready, system.State);

            var challenge = await runtime.PrepareResetAsync(timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(challenge.Challenge));
            Assert.True(challenge.ExpiresAtUtc > DateTimeOffset.UtcNow);

            var drain = await runtime.DrainForResetAsync(challenge.Challenge, timeout.Token);
            Assert.Equal(
                RuntimeV1StateDescriptor.ResetCategories.Select(static category => category.Id),
                drain.Categories);

            var stopped = await WaitForHostStateAsync(
                authenticated,
                HostRuntimeState.Stopped,
                process,
                timeout.Token);
            Assert.Equal(HostRuntimeDesiredState.Stopped, stopped.DesiredState);
            Assert.Null(stopped.RuntimeInstanceId);
            Assert.False(File.Exists(workerConnectionPath));

            using (var unavailable = await authenticated.GetAsync("api/handshake", timeout.Token))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
                Assert.Contains(
                    "host.runtime-unavailable",
                    await unavailable.Content.ReadAsStringAsync(timeout.Token),
                    StringComparison.Ordinal);
            }

            using (var startResponse = await authenticated.PostAsJsonAsync(
                       "api/host/v1/runtime/start",
                       new HostLifecycleRequest(Guid.NewGuid(), stopped.DeploymentGeneration),
                       timeout.Token))
            {
                Assert.Equal(HttpStatusCode.Accepted, startResponse.StatusCode);
                var submission = await startResponse.Content.ReadFromJsonAsync<HostLifecycleSubmission>(
                    timeout.Token);
                Assert.NotNull(submission);
                Assert.Equal(HostOperationKinds.RuntimeStart, submission.Operation.Kind);
            }

            var restarted = await WaitForHostStateAsync(
                authenticated,
                HostRuntimeState.Ready,
                process,
                timeout.Token);
            Assert.Equal(HostRuntimeDesiredState.Running, restarted.DesiredState);
            Assert.NotEqual(ready.RuntimeInstanceId, restarted.RuntimeInstanceId);
            var restartedWorkerConnection = await WaitForConnectionAsync(
                workerConnectionPath,
                process,
                timeout.Token);
            Assert.NotEqual(workerConnection.BearerToken, restartedWorkerConnection.BearerToken);

            using var shutdown = await authenticated.PostAsync(
                "api/host/v1/shutdown",
                content: null,
                timeout.Token);
            Assert.Equal(HttpStatusCode.NoContent, shutdown.StatusCode);
            var result = await process.WaitForExitAsync(TimeSpan.FromSeconds(30), timeout.Token);
            Assert.Equal(0, result.ExitCode);
            Assert.False(File.Exists(externalConnectionPath));
            Assert.False(File.Exists(workerConnectionPath));
            Assert.True(RuntimeLocalState.IsLeaseAvailable(runtimeStateRoot));
            using var lifecycleStore = new HostLifecycleStore(hostStateRoot);
            Assert.Equal(
                HostRuntimeDesiredState.Running,
                lifecycleStore.LoadOrCreate().DesiredState);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Real Supervisor/Runtime integration failed.{Environment.NewLine}{process.Diagnostics}",
                exception);
        }
    }

    [Fact]
    public async Task SupervisorProcess_ForwardsTransferClassesAndRejectsOversizedOrdinaryBodiesBeforeRuntimeProxy()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-host-upload-limit-tests");
        var root = scope.RootPath;
        var hostStateRoot = Path.Combine(root, "host");
        var runtimeStateRoot = Path.Combine(root, "runtime");
        var externalConnectionPath = Path.Combine(root, "connection", "host.json");
        var externalToken = $"integration-external-{Guid.NewGuid():N}";
        var deploymentIdentity = HostDeploymentIdentityVerifier.Compute(
            Path.GetDirectoryName(typeof(HostStartupOptions).Assembly.Location)!);
        await using var process = StartSupervisor(
            typeof(RuntimeHostStartupOptions).Assembly.Location,
            hostStateRoot,
            runtimeStateRoot,
            externalConnectionPath,
            externalToken,
            deploymentIdentity);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        try
        {
            var connection = await WaitForConnectionAsync(externalConnectionPath, process, timeout.Token);
            using var client = new HttpClient
            {
                BaseAddress = connection.RuntimeUrl,
                Timeout = Timeout.InfiniteTimeSpan,
            };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                connection.BearerToken);
            await WaitForHostStateAsync(client, HostRuntimeState.Ready, process, timeout.Token);

            var transferLength = RuntimeRequestSizeLimits.DefaultMaxRequestBodyBytes + 1;
            await AssertTransferAcceptedAsync(
                client,
                "api/v1/uploads/packages",
                transferLength,
                chunked: false,
                timeout.Token);
            await AssertTransferAcceptedAsync(
                client,
                "api/v1/uploads/stacks",
                transferLength,
                chunked: true,
                timeout.Token);
            await AssertTransferAcceptedAsync(
                client,
                "api/v1/uploads/stack-media",
                transferLength,
                chunked: false,
                timeout.Token);

            await AssertOversizedRequestAsync(client, chunked: false, timeout.Token);
            await AssertOversizedRequestAsync(client, chunked: true, timeout.Token);

            var status = await client.GetFromJsonAsync<HostRuntimeStatus>(
                "api/host/v1/status",
                timeout.Token);
            Assert.Equal(HostRuntimeState.Ready, status?.State);

            using var shutdown = await client.PostAsync("api/host/v1/shutdown", content: null, timeout.Token);
            Assert.Equal(HttpStatusCode.NoContent, shutdown.StatusCode);
            var result = await process.WaitForExitAsync(TimeSpan.FromSeconds(30), timeout.Token);
            Assert.Equal(0, result.ExitCode);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Real Supervisor request-limit integration failed.{Environment.NewLine}{process.Diagnostics}",
                exception);
        }
    }

    private static async Task AssertTransferAcceptedAsync(
        HttpClient client,
        string path,
        long length,
        bool chunked,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new SizedHttpContent(length, reportLength: !chunked),
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var upload = await response.Content.ReadFromJsonAsync<ContentUploadDescriptor>(cancellationToken);
        Assert.NotNull(upload);
        Assert.Equal(length, upload.Length);
    }

    private static async Task AssertOversizedRequestAsync(
        HttpClient client,
        bool chunked,
        CancellationToken cancellationToken)
    {
        var correlationId = $"supervisor-upload-{(chunked ? "chunked" : "fixed")}";
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            "api/v1/packages/missing/settings/values")
        {
            Content = new SizedHttpContent(
                RuntimeRequestSizeLimits.DefaultMaxRequestBodyBytes + 1,
                reportLength: !chunked),
        };
        request.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(correlationId, response.Headers.GetValues(CorrelationHeader).Single());
        Assert.Equal(
            "https://sunder.dev/problems/runtime.v1.upload-limit",
            problem.GetProperty("type").GetString());
        Assert.Equal("Upload limit exceeded", problem.GetProperty("title").GetString());
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, problem.GetProperty("status").GetInt32());
        Assert.Equal(
            $"Request body too large. The max request body size is {RuntimeRequestSizeLimits.DefaultMaxRequestBodyBytes} bytes.",
            problem.GetProperty("detail").GetString());
        Assert.Equal("runtime.v1.upload-limit", problem.GetProperty("code").GetString());
        Assert.Equal(correlationId, problem.GetProperty("correlationId").GetString());
    }

    private static CapturedProcess StartSupervisor(
        string runtimeHostPath,
        string hostStateRoot,
        string runtimeStateRoot,
        string externalConnectionPath,
        string externalToken,
        string deploymentIdentity)
    {
        var supervisorAssembly = typeof(HostStartupOptions).Assembly.Location;
        var startInfo = new ProcessStartInfo(ResolveDotnetHost())
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(supervisorAssembly)!,
        };
        startInfo.ArgumentList.Add(supervisorAssembly);
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add("http://127.0.0.1:0");
        startInfo.ArgumentList.Add("--runtime-host-path");
        startInfo.ArgumentList.Add(runtimeHostPath);
        startInfo.ArgumentList.Add("--host-state-root");
        startInfo.ArgumentList.Add(hostStateRoot);
        startInfo.ArgumentList.Add("--runtime-state-root");
        startInfo.ArgumentList.Add(runtimeStateRoot);
        startInfo.ArgumentList.Add("--worker-startup-timeout-seconds");
        startInfo.ArgumentList.Add("30");
        startInfo.ArgumentList.Add("--deployment-identity");
        startInfo.ArgumentList.Add(deploymentIdentity);

        foreach (var key in startInfo.Environment.Keys
                     .Where(static key => key.StartsWith("SUNDER_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            startInfo.Environment.Remove(key);
        }
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        startInfo.Environment["SUNDER_RUNTIME_BEARER_TOKEN"] = externalToken;
        startInfo.Environment["SUNDER_RUNTIME_CONNECTION_FILE"] = externalConnectionPath;

        return CapturedProcess.Start(startInfo);
    }

    private static async Task<RuntimeConnectionInfo> WaitForConnectionAsync(
        string path,
        CapturedProcess process,
        CancellationToken cancellationToken)
    {
        await FileSignal.WaitForAsync(
            path,
            TimeSpan.FromSeconds(30),
            () => process.Diagnostics,
            cancellationToken);
        return await Eventually.ObserveAsync(
            _ => ValueTask.FromResult(RuntimeConnectionInfoStore.Load(path)),
            static connection => connection is not null,
            TimeSpan.FromSeconds(2),
            describe: (connection, error) =>
                $"connection={connection}; error={error?.Message}; {process.Diagnostics}",
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException($"Connection signal '{path}' did not contain a connection document.");
    }

    private static async Task<HostRuntimeStatus> WaitForHostStateAsync(
        HttpClient client,
        HostRuntimeState expected,
        CapturedProcess process,
        CancellationToken cancellationToken)
    {
        var status = await Eventually.ObserveAsync(
            async token =>
            {
                ThrowIfExited(process, $"reach Host Runtime state '{expected}'");
                var latest = await client.GetFromJsonAsync<HostRuntimeStatus>(
                    "api/host/v1/status",
                    token);
                if (latest?.State is HostRuntimeState.Failed or HostRuntimeState.CrashLoop)
                {
                    throw new InvalidOperationException(
                        $"Runtime worker entered '{latest.State}': {latest.FailureCode} {latest.FailureMessage}");
                }
                return latest;
            },
            latest => latest?.State == expected,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromMilliseconds(100),
            (latest, error) => $"expected={expected}; latest={latest}; error={error?.Message}; {process.Diagnostics}",
            cancellationToken);
        return status ?? throw new InvalidOperationException($"Host status did not reach '{expected}'.");
    }

    private static void ThrowIfExited(CapturedProcess process, string operation)
    {
        if (process.HasExited)
        {
            throw new InvalidOperationException(
                $"Sunder Host Supervisor exited before it could {operation}.{Environment.NewLine}{process.Diagnostics}");
        }
    }

    private static string ResolveDotnetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var processPath = Environment.ProcessPath;
        return processPath is not null
            && string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase)
            ? processPath
            : "dotnet";
    }

    private sealed class SizedHttpContent(long length, bool reportLength) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[64 * 1024];
            var remaining = length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(buffer.Length, remaining);
                await stream.WriteAsync(buffer.AsMemory(0, count));
                remaining -= count;
            }
        }

        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = reportLength ? length : 0;
            return reportLength;
        }
    }

}
