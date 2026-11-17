using System.Diagnostics;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.App.Integration.Tests;

[Collection("App/Runtime integration")]
public sealed class NativeWebViewCaptureProcessTests
{
    [LinuxFact]
    [Trait("Capability", "NativeUI")]
    [Trait("Capability", "Process")]
    public Task WebKitGtkCapturesOnlyNativePackagePixelsAndDrainsCallbacksBeforeRetirement()
        => RunAsync("gtk");

    [MacOsFact]
    [Trait("Capability", "NativeUI")]
    [Trait("Capability", "Process")]
    public Task WkWebViewCapturesOnlyNativePackagePixelsAndDrainsCallbacksBeforeRetirement()
        => RunAsync("wkwebview");

    private static Task RunAsync(string backend)
        => TemporaryDirectoryScope.RunAsync("sunder-native-capture", async temporary =>
        {
            var probe = Path.Combine(AppContext.BaseDirectory, "webview-capture-probe", "Sunder.App.Tests.dll");
            Assert.True(File.Exists(probe), "The owning build did not compile the native browser capture probe.");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { UseShellExecute = false };
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Sunder.App.Integration.Tests.runtimeconfig.json"),
                         "--depsfile", Path.Combine(AppContext.BaseDirectory, "Sunder.App.Integration.Tests.deps.json"), probe, temporary.RootPath, backend })
                start.ArgumentList.Add(argument);
            await using var process = CapturedProcess.Start(start, 128 * 1024);
            var result = await process.EnsureSuccessAsync(TimeSpan.FromSeconds(60));
            Assert.Contains("native-pixels; package-only; selected-crop; cancellation-drained; detach-drained", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("hosted-package; fixed-document-query; composed-capture; exact-revision", result.StandardOutput, StringComparison.Ordinal);
        });
}
