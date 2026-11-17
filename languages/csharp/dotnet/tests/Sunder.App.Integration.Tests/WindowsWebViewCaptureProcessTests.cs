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

    private static async Task RunAsync(TemporaryDirectoryScope temporary)
    {
        var probe = Path.Combine(AppContext.BaseDirectory, "webview-capture-probe", "Sunder.App.Tests.dll");
        Assert.True(File.Exists(probe), "The owning build did not compile the native WebView2 capture probe.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(host) { UseShellExecute = false };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Sunder.App.Integration.Tests.runtimeconfig.json"), probe, temporary.RootPath })
            start.ArgumentList.Add(argument);
        await using var process = CapturedProcess.Start(start, 128 * 1024);
        var result = await WindowsWebViewBrowserProcess.EnsureSuccessAndExitAsync(process, TimeSpan.FromSeconds(60));
        Assert.Contains("native-capture; cancellation-drained; reparent-preserved; native-exit; profile-removed", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("hosted-package; fixed-document-query; composed-capture; exact-revision", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("lazy; isolated-profiles; retained-controller; sibling-preserved", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("pending-initialization-settled; original-sta; native-exit; profile-removed", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("closedOwner=True", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("native-renderer-failure; unavailable-fallback; fresh-reload; sibling-preserved", result.StandardOutput, StringComparison.Ordinal);
    }
}
