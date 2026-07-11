using Sunder.Runtime.Host;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeHostStartupOptionsTests
{
    [Fact]
    public void Parse_WhenDevPackagesProvided_NormalizesAndDeduplicatesFolders()
    {
        var devPackageFolder = Path.Combine(".", "dev-package");
        var fullDevPackageFolder = Path.GetFullPath(devPackageFolder);

        var options = RuntimeHostStartupOptions.Parse([
            "--urls",
            "http://127.0.0.1:5275",
            "--dev-package",
            devPackageFolder,
            "--dev-package=" + fullDevPackageFolder,
            "--wait-for-debugger",
        ]);

        Assert.True(options.WaitForDebugger);
        var folder = Assert.Single(options.DevPackageFolders);
        Assert.Equal(fullDevPackageFolder, folder);
    }

    [Fact]
    public void Parse_WhenDevelopmentNonLoopbackOverrideProvided_EnablesOverride()
    {
        var options = RuntimeHostStartupOptions.Parse([
            "--development-allow-non-loopback-runtime-listen",
        ]);

        Assert.True(options.DevelopmentAllowNonLoopbackRuntimeListen);
    }
}
