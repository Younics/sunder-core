using System.Diagnostics;
using Sunder.Core.TestSupport;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    private static async Task ActivatePackedAppAsync(string entryPath, string root)
    {
        // Real Avalonia activation may retain package types in shared caches. The
        // probe owns that lifetime; process exit releases its mapped DLLs on Windows.
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, WorkingDirectory = root };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Sunder.Package.Build.Tests.runtimeconfig.json"),
                     Path.Combine(AppContext.BaseDirectory, "packed-app-probe/PackedAppActivationProbe.dll"), AppContext.BaseDirectory, entryPath, root })
            start.ArgumentList.Add(argument);
        await using var process = CapturedProcess.Start(start, 128 * 1024);
        await process.EnsureSuccessAsync(TimeSpan.FromSeconds(30));
    }
}
