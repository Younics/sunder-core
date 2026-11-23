using System.Diagnostics;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.App.Integration.Tests;

[Collection("App/Runtime integration")]
public sealed class WindowsWebViewCaptureProcessTests
{
    [WindowsFact]
    [Trait("Capability", "NativeUI")]
    [Trait("Capability", "Process")]
    public Task WebView2CapturesOnlyNativePackagePixelsAndDrainsCallbacksBeforeRetirement()
        => TemporaryDirectoryScope.RunAsync("sunder-browser", RunAsync);

    [WindowsFact]
    [Trait("Capability", "NativeUI")]
    [Trait("Capability", "Process")]
    public Task WebView2FinalRetirementJoinsResetWaitingForControllerAcquisition()
        => TemporaryDirectoryScope.RunAsync("sunder-browser", async temporary =>
        {
            var result = await RunProbeAsync(temporary, "webview2-pending-reset");
            Assert.Contains("pending-reset; acquisition-settled; native-exit; profile-removed", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("stage=neutral-navigation-started", result.StandardError, StringComparison.Ordinal);
        });

    private static async Task RunAsync(TemporaryDirectoryScope temporary)
    {
        var result = await RunProbeAsync(temporary);
        Assert.Contains("native-capture; cancellation-drained; reparent-preserved; native-exit; profile-removed", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("hosted-package; fixed-document-query; composed-capture; exact-revision", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("lazy; isolated-profiles; retained-controller; sibling-preserved", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("in-flight-controller-closed; callbacks-drained; sibling-preserved", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("capture-owner-closed; callbacks-drained; sibling-preserved", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("pending-initialization-settled; original-sta; native-exit; profile-removed", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("pending-revoked; presentation-detached; native-cleanup-owned", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("pending-reopen; no-extra-native-view; fresh-authority; native-capture; sibling-preserved", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("closedOwner=True", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("native-renderer-failure; unavailable-fallback; fresh-reload; sibling-preserved", result.StandardOutput, StringComparison.Ordinal);
    }

    private static async Task<CapturedProcessResult> RunProbeAsync(TemporaryDirectoryScope temporary, string? scenario = null)
    {
        var probe = Path.Combine(AppContext.BaseDirectory, "webview-capture-probe", "Sunder.App.Tests.dll");
        Assert.True(File.Exists(probe), "The owning build did not compile the native WebView2 capture probe.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(host) { UseShellExecute = false };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Sunder.App.Integration.Tests.runtimeconfig.json"),
                     "--depsfile", Path.Combine(AppContext.BaseDirectory, "Sunder.App.Integration.Tests.deps.json"), probe, temporary.RootPath })
            start.ArgumentList.Add(argument);
        if (scenario is not null) start.ArgumentList.Add(scenario);
        await using var process = CapturedProcess.Start(start, 128 * 1024);
        return await WindowsWebViewBrowserProcess.EnsureSuccessAndExitAsync(process, TimeSpan.FromSeconds(150));
    }
}
