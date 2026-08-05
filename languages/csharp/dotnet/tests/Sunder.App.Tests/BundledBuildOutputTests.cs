using Sunder.App.Tests.TestSupport;
using Xunit;

namespace Sunder.App.Tests;

public sealed class BundledBuildOutputTests
{
    [Fact]
    public void AppBuildContainsSupervisorRuntimeAndCliExecutables()
    {
        var appOutput = TestPaths.GetSunderAppOutputDirectory();
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

        Assert.True(File.Exists(Path.Combine(
            appOutput,
            "RuntimeHost",
            "Sunder.Host.Supervisor" + extension)));
        Assert.True(File.Exists(Path.Combine(
            appOutput,
            "RuntimeHost",
            "RuntimeHost",
            "Sunder.Runtime.Host" + extension)));
        Assert.True(File.Exists(Path.Combine(
            appOutput,
            "Cli",
            "sunder" + extension)));
    }
}
