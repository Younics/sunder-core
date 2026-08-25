using System.Diagnostics;
using System.Text.Json;
using Sunder.Host.Supervisor;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Host.Supervisor.Tests;

public sealed class RuntimeWorkerLaunchProbeTests
{
    [Fact]
    public async Task WaitForReadyWorkerAsync_DecodesTypedStartupDiagnostic()
    {
        using var process = Process.GetCurrentProcess();
        using var diagnosticStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(
            new RuntimeStartupDiagnostic("runtime.test.failure", "Runtime failed before listening.")));

        var exception = await Assert.ThrowsAsync<RuntimeWorkerStartupException>(() =>
            RuntimeWorkerLaunchProbe.WaitForReadyWorkerAsync(
                process,
                "expected-token",
                CreateMissingConnectionPath(),
                CreateUnboundEndpoint(),
                diagnosticStream,
                TimeSpan.FromSeconds(1),
                CancellationToken.None));

        Assert.Equal("runtime.test.failure", exception.Code);
        Assert.Equal("Runtime failed before listening.", exception.Message);
        Assert.False(process.HasExited);
        Assert.True(diagnosticStream.CanRead);
    }

    [Fact]
    public async Task ReadStartupDiagnosticAsync_RejectsMalformedAndOversizedPayloads()
    {
        using var malformed = new MemoryStream("not-json"u8.ToArray());
        using var oversized = new MemoryStream(new byte[4097]);

        Assert.Null(await RuntimeWorkerLaunchProbe.ReadStartupDiagnosticAsync(
            malformed,
            CancellationToken.None));
        Assert.Null(await RuntimeWorkerLaunchProbe.ReadStartupDiagnosticAsync(
            oversized,
            CancellationToken.None));
    }

    [Fact]
    public async Task WaitForReadyWorkerAsync_TimesOutWithoutMatchingPublication()
    {
        using var process = Process.GetCurrentProcess();
        using var diagnosticStream = new MemoryStream();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            RuntimeWorkerLaunchProbe.WaitForReadyWorkerAsync(
                process,
                "expected-token",
                CreateMissingConnectionPath(),
                CreateUnboundEndpoint(),
                diagnosticStream,
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None));
    }

    [Fact]
    public async Task WaitForReadyWorkerAsync_PreservesCallerCancellation()
    {
        using var process = Process.GetCurrentProcess();
        using var diagnosticStream = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RuntimeWorkerLaunchProbe.WaitForReadyWorkerAsync(
                process,
                "expected-token",
                CreateMissingConnectionPath(),
                CreateUnboundEndpoint(),
                diagnosticStream,
                TimeSpan.FromSeconds(1),
                cancellation.Token));
    }

    private static string CreateMissingConnectionPath()
        => Path.Combine(
            Path.GetTempPath(),
            "sunder-runtime-launch-probe-tests",
            Guid.NewGuid().ToString("N"),
            "connection.json");

    private static RuntimeIpcEndpoint CreateUnboundEndpoint()
        => OperatingSystem.IsWindows()
            ? new RuntimeIpcEndpoint(
                RuntimeIpcTransportKind.NamedPipe,
                $"sunder-runtime-launch-probe-{Guid.NewGuid():N}")
            : new RuntimeIpcEndpoint(
                RuntimeIpcTransportKind.UnixSocket,
                Path.Combine(Path.GetTempPath(), $"sunder-runtime-launch-probe-{Guid.NewGuid():N}.sock"));
}
