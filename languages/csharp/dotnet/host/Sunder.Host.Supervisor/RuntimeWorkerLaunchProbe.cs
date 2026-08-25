using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.Host.Supervisor;

internal static class RuntimeWorkerLaunchProbe
{
    private const int MaximumStartupDiagnosticBytes = 4096;
    private const string StartupDiagnosticPipeEnvironmentVariable = "SUNDER_RUNTIME_STARTUP_DIAGNOSTIC_PIPE";

    public static Process StartWorkerProcess(
        string? runtimeHostPath,
        string connectionInfoPath,
        string runtimeStateRoot,
        RuntimeResourcePolicyLaunchResolver resourcePolicyLaunchResolver,
        string workerToken,
        RuntimeIpcEndpoint endpoint,
        string supervisorLifetimePipeHandle,
        string startupDiagnosticPipeHandle)
    {
        var startInfo = CreateWorkerStartInfo(
            runtimeHostPath,
            connectionInfoPath,
            runtimeStateRoot,
            resourcePolicyLaunchResolver,
            workerToken,
            endpoint,
            supervisorLifetimePipeHandle,
            startupDiagnosticPipeHandle);
        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Failed to start Sunder.Runtime.Host.");
    }

    internal static ProcessStartInfo CreateWorkerStartInfo(
        string? runtimeHostPath,
        string connectionInfoPath,
        string runtimeStateRoot,
        RuntimeResourcePolicyLaunchResolver resourcePolicyLaunchResolver,
        string workerToken,
        RuntimeIpcEndpoint endpoint,
        string supervisorLifetimePipeHandle,
        string startupDiagnosticPipeHandle)
    {
        if (runtimeHostPath is null)
        {
            throw new FileNotFoundException("Sunder.Runtime.Host is not installed in this Host distribution.");
        }

        var isDotnetAssembly = string.Equals(Path.GetExtension(runtimeHostPath), ".dll", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo(isDotnetAssembly ? "dotnet" : runtimeHostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runtimeHostPath)!,
        };
        if (isDotnetAssembly)
        {
            startInfo.ArgumentList.Add(runtimeHostPath);
        }
        RuntimeResourcePolicyEnvironment.RemovePolicyVariables(startInfo.Environment);
        startInfo.Environment["SUNDER_RUNTIME_CONNECTION_FILE"] = connectionInfoPath;
        startInfo.Environment["SUNDER_RUNTIME_BEARER_TOKEN"] = workerToken;
        startInfo.Environment["SUNDER_RUNTIME_SUPERVISOR_PIPE"] = supervisorLifetimePipeHandle;
        startInfo.Environment[StartupDiagnosticPipeEnvironmentVariable] = startupDiagnosticPipeHandle;
        startInfo.Environment[RuntimeLocalState.StateRootEnvironmentVariable] = runtimeStateRoot;
        startInfo.Environment[RuntimeIpcEndpoint.EnvironmentVariable] = endpoint.ToEnvironmentValue();
        startInfo.Environment[RuntimeResourcePolicyEnvironment.LaunchPayloadVariable] =
            resourcePolicyLaunchResolver.ResolveLaunchPayload();
        return startInfo;
    }

    public static async Task<(RuntimeConnectionInfo Connection, RuntimeHandshakeResponse Handshake)> WaitForReadyWorkerAsync(
        Process process,
        string workerToken,
        string connectionInfoPath,
        RuntimeIpcEndpoint endpoint,
        Stream startupDiagnosticStream,
        TimeSpan startupTimeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(startupTimeout);
        try
        {
            var diagnosticTask = ReadStartupDiagnosticAsync(startupDiagnosticStream, deadline.Token);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (diagnosticTask.IsCompletedSuccessfully
                    && diagnosticTask.Result is { } startupDiagnostic)
                {
                    throw new RuntimeWorkerStartupException(
                        startupDiagnostic.Code,
                        startupDiagnostic.Message);
                }
                if (process.HasExited)
                {
                    var exitDiagnostic = await diagnosticTask.ConfigureAwait(false);
                    if (exitDiagnostic is not null)
                    {
                        throw new RuntimeWorkerStartupException(
                            exitDiagnostic.Code,
                            exitDiagnostic.Message);
                    }
                    throw new InvalidOperationException($"Runtime worker exited with code {process.ExitCode} during startup.");
                }

                var connection = RuntimeConnectionInfoStore.Load(connectionInfoPath);
                if (connection is not null && FixedTimeTokenEquals(connection.BearerToken, workerToken))
                {
                    try
                    {
                        using var transport = new RuntimeClientTransport(
                            () => connection,
                            RuntimeIpcHttpMessageHandlerFactory.Create(endpoint));
                        var handshake = await transport.ProbeHandshakeAsync(deadline.Token).ConfigureAwait(false);
                        if (RuntimeProtocolCompatibility.GetIncompatibility(
                                handshake,
                                RuntimeProtocolFeatures.StartupDiagnosticsV1) is not { } incompatibility)
                        {
                            using var management = new RuntimeManagementClient(transport);
                            var status = await management.GetSystemStatusAsync(deadline.Token).ConfigureAwait(false);
                            if (status?.IsReady == true)
                            {
                                return (connection, handshake);
                            }
                            if (status?.State == RuntimeBootstrapState.Failed)
                            {
                                throw new RuntimeWorkerStartupException(
                                    status.StartupFailure?.Code ?? "runtime.v1.internal",
                                    status.StartupFailure?.Message
                                    ?? "Runtime startup failed without a diagnostic message.");
                            }
                            await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token).ConfigureAwait(false);
                            continue;
                        }
                        throw new InvalidOperationException(incompatibility);
                    }
                    catch (HttpRequestException)
                    {
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Runtime worker did not become ready within {startupTimeout.TotalSeconds:0} seconds.");
        }
    }

    internal static async Task<RuntimeStartupDiagnostic?> ReadStartupDiagnosticAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(MaximumStartupDiagnosticBytes);
        var chunk = new byte[512];
        while (buffer.Length <= MaximumStartupDiagnosticBytes)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0 || buffer.Length > MaximumStartupDiagnosticBytes)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<RuntimeStartupDiagnostic>(buffer.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool FixedTimeTokenEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));
}

internal sealed class RuntimeWorkerStartupException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
