using System.Diagnostics;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class OfficialRuntimePlatformProbeTests
{
    [Theory]
    [InlineData("Windows")]
    [InlineData("MacOS")]
    [InlineData("Linux")]
    public void ProbeEnvironment_UsesWorkerBaselineAndRejectsInjectionWithAnyWindowsCasing(string platformName)
    {
        var platform = Enum.Parse<WorkerProcessPlatform>(platformName);
        using var temp = TemporaryDirectoryScope.Create("official-probe-environment");
        var root = Path.Combine(temp.RootPath, "runtime");
        using var process = new Process();
        var environment = process.StartInfo.Environment;
        environment.Clear();
        environment[platform == WorkerProcessPlatform.Windows ? "systemroot" : "SystemRoot"] = @"C:\Windows";
        environment["__CF_USER_TEXT_ENCODING"] = "0x1F5:0x0:0x0";
        string[] injected = ["NODE_OPTIONS", "node_options", "Node_Options", "NODE_PATH", "Node_Path",
            "OPENSSL_CONF", "openssl_conf", "OpenSSL_Modules", "DOTNET_STARTUP_HOOKS", "dotnet_startup_hooks",
            "DOTNET_ROOT_ARM64", "CORECLR_ENABLE_PROFILING", "CoreClr_Profiler_Path", "COR_ENABLE_PROFILING",
            "COMPlus_ReadyToRun", "LD_PRELOAD", "DYLD_INSERT_LIBRARIES", "DOCKER_HOST", "SUNDER_RUNTIME_BEARER_TOKEN"];
        foreach (var name in injected) environment[name] = "untrusted-preload";
        environment["PATH"] = "ambient-runtime-path";
        environment["DOTNET_ROOT"] = "ambient-dotnet";
        OfficialRuntimePlatformProbe.ConfigureEnvironment(environment, root, platform);

        Assert.All(injected, name => Assert.DoesNotContain(environment.Keys, key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(Path.GetFullPath(root), environment["PATH"]);
        Assert.Equal(Path.GetFullPath(root), environment["DOTNET_ROOT"]);
        Assert.Equal("0", environment["DOTNET_MULTILEVEL_LOOKUP"]);
        Assert.Equal("0", environment["DOTNET_EnableDiagnostics"]);
        Assert.Equal(temp.RootPath, environment["TMP"]);
        Assert.Equal(temp.RootPath, environment["TEMP"]);
        Assert.Equal(temp.RootPath, environment["TMPDIR"]);
        if (platform == WorkerProcessPlatform.Windows)
        {
            Assert.Equal(@"C:\Windows", environment["SystemRoot"]);
            Assert.Equal(@"C:\Windows", environment["WINDIR"]);
        }
        else Assert.DoesNotContain("SystemRoot", environment.Keys);
        Assert.Equal(platform == WorkerProcessPlatform.MacOS, environment.ContainsKey("__CF_USER_TEXT_ENCODING"));
    }
}
