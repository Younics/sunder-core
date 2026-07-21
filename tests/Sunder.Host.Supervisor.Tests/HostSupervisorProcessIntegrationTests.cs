using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    [Fact]
    public async Task SupervisorProcess_GatewaysToNestedRuntimeAndOwnsResetDrain()
    {
        var root = CreateTempDirectory();
        var hostStateRoot = Path.Combine(root, "host");
        var runtimeStateRoot = Path.Combine(root, "runtime");
        var externalConnectionPath = Path.Combine(root, "connection", "host.json");
        var workerConnectionPath = Path.Combine(hostStateRoot, "connection", "runtime-worker.json");
        var externalToken = $"integration-external-{Guid.NewGuid():N}";
        using var process = StartSupervisor(
            typeof(RuntimeHostStartupOptions).Assembly.Location,
            hostStateRoot,
            runtimeStateRoot,
            externalConnectionPath,
            externalToken);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
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
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
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
            await TerminateProcessAsync(process);
            var diagnostics = process.HasExited
                ? $"stdout:\n{await standardOutput}\nstderr:\n{await standardError}"
                : "Supervisor process did not exit; output streams remain open.";
            throw new InvalidOperationException(
                $"Real Supervisor/Runtime integration failed.\n{diagnostics}",
                exception);
        }
        finally
        {
            await TerminateProcessAsync(process);
            TryDeleteDirectory(root);
        }
    }

    private static Process StartSupervisor(
        string runtimeHostPath,
        string hostStateRoot,
        string runtimeStateRoot,
        string externalConnectionPath,
        string externalToken)
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

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Failed to launch the real Sunder Host Supervisor process.");
    }

    private static async Task<RuntimeConnectionInfo> WaitForConnectionAsync(
        string path,
        Process process,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connection = RuntimeConnectionInfoStore.Load(path);
            if (connection is not null)
            {
                return connection;
            }
            ThrowIfExited(process, $"publish connection information to '{path}'");
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private static async Task<HostRuntimeStatus> WaitForHostStateAsync(
        HttpClient client,
        HostRuntimeState expected,
        Process process,
        CancellationToken cancellationToken)
    {
        HostRuntimeStatus? latest = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            latest = await client.GetFromJsonAsync<HostRuntimeStatus>(
                "api/host/v1/status",
                cancellationToken);
            if (latest?.State == expected)
            {
                return latest;
            }
            if (latest?.State is HostRuntimeState.Failed or HostRuntimeState.CrashLoop)
            {
                throw new InvalidOperationException(
                    $"Runtime worker entered '{latest.State}': {latest.FailureCode} {latest.FailureMessage}");
            }
            ThrowIfExited(process, $"reach Host Runtime state '{expected}'");
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private static void ThrowIfExited(Process process, string operation)
    {
        if (process.HasExited)
        {
            throw new InvalidOperationException(
                $"Sunder Host Supervisor exited with code {process.ExitCode} before it could {operation}.");
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

    private static async Task TerminateProcessAsync(Process process)
    {
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "sunder-host-process-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
