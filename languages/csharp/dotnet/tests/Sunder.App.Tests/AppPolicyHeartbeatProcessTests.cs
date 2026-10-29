using System.Diagnostics;
using System.Text.Json;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPolicyHeartbeatProcessTests
{
    [MacOsTheory]
    [InlineData("quit")]
    [InlineData("quit-timeout")]
    [Trait("Capability", "NativeUI")]
    [Trait("Capability", "Process")]
    public async Task NativeQuitKeepsSupervisionThroughDelayedOrTimedOutCleanupAndExitsTheDesktop(string mode)
    {
        var result = await RunProbeAsync(mode);
        using var report = JsonDocument.Parse(result.StandardOutput.Trim());
        Assert.True(report.RootElement.GetProperty("nativeDesktopExited").GetBoolean());
        Assert.Equal(1, report.RootElement.GetProperty("shutdowns").GetInt32());
        Assert.Equal(mode == "quit", report.RootElement.GetProperty("cleanupCompleted").GetBoolean());
    }

    [Fact]
    [Trait("Capability", "Process")]
    public async Task OneProofExchangeDoesNotRestartItsDeadlineAcrossHttpRequests()
    {
        var result = await RunProbeAsync("deadline");
        using var report = JsonDocument.Parse(result.StandardOutput.Trim());
        Assert.Equal("deadline", report.RootElement.GetProperty("mode").GetString());
        Assert.True(report.RootElement.GetProperty("timedOut").GetBoolean());
        Assert.Equal(0, report.RootElement.GetProperty("availablePoolWorkers").GetInt32());
        Assert.InRange(report.RootElement.GetProperty("elapsedMilliseconds").GetDouble(), 1500, 2600);
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("unavailable")]
    [Trait("Capability", "Process")]
    public async Task RealPolicyTransportSurvivesPoolStarvationAndStillFailsClosedOnActualLoss(string mode)
    {
        var result = await RunProbeAsync(mode);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
        using var report = JsonDocument.Parse(result.StandardOutput.Trim());
        Assert.Equal(mode, report.RootElement.GetProperty("mode").GetString());
        Assert.Equal(1, report.RootElement.GetProperty("failures").GetArrayLength());
        Assert.True(report.RootElement.GetProperty("heartbeatsAfter").GetInt32() >= report.RootElement.GetProperty("heartbeatsBefore").GetInt32() + 5);
    }

    private static async Task<Sunder.Core.TestSupport.CapturedProcessResult> RunProbeAsync(string mode)
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "policy-heartbeat-probe/Sunder.App.Tests.dll");
        Assert.True(File.Exists(assembly), "The owning build did not compile the policy heartbeat process probe.");
        var host = Environment.ProcessPath is { } processPath && Path.GetFileNameWithoutExtension(processPath) == "dotnet"
            ? processPath : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(host) { UseShellExecute = false };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("SUNDER_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        var root = Directory.CreateTempSubdirectory("sunder-policy-probe-").FullName;
        start.Environment["SUNDER_APP_STATE_ROOT"] = Path.Combine(root, "app");
        start.Environment["SUNDER_RUNTIME_STATE_ROOT"] = Path.Combine(root, "runtime");
        start.Environment["SUNDER_HOST_STATE_ROOT"] = Path.Combine(root, "host");
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Sunder.App.Tests.runtimeconfig.json"),
            assembly, mode }) start.ArgumentList.Add(argument);
        try
        {
            await using var process = CapturedProcess.Start(start, 128 * 1024);
            var result = await process.EnsureSuccessAsync(TimeSpan.FromSeconds(35));
            Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput), process.Diagnostics);
            return result;
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
