using System.Diagnostics;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.App.Tests;

public sealed class MacOsWindowDelegateProcessTests
{
    [MacOsFact]
    [Trait("Capability", "NativeUI")]
    [Trait("Capability", "Process")]
    public async Task ServicesResponderTraversalDoesNotReenterTheSelfDelegatingWindow()
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "window-delegate-probe/Sunder.App.Tests.dll");
        Assert.True(File.Exists(assembly), "The owning build did not compile the native window delegate probe.");
        var host = Environment.ProcessPath is { } executable && Path.GetFileNameWithoutExtension(executable) == "dotnet"
            ? executable : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(host) { UseShellExecute = false };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("SUNDER_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        var root = Directory.CreateTempSubdirectory("sunder-window-delegate-").FullName;
        start.Environment["SUNDER_APP_STATE_ROOT"] = root;
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Sunder.App.Tests.runtimeconfig.json"),
            assembly })
            start.ArgumentList.Add(argument);
        try
        {
            await using var process = CapturedProcess.Start(start, 64 * 1024);
            var result = await process.EnsureSuccessAsync(TimeSpan.FromSeconds(30));
            Assert.Contains("services-request-returned; callbacks-preserved; delegate-restored", result.StandardOutput, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
