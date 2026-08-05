using Sunder.Runtime.Host;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeHostStartupOptionsTests
{
    [Fact]
    public void Parse_WhenLegacyDevPackageArgumentsAreProvided_DoesNotTreatThemAsRuntimeInputs()
    {
        var options = RuntimeHostStartupOptions.Parse([
            "--urls",
            "http://127.0.0.1:5275",
            "--dev-package",
            Path.Combine(".", "dev-package"),
            "--wait-for-debugger",
        ]);

        Assert.True(options.WaitForDebugger);
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
