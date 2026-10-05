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

    [Fact]
    public void Parse_PackageConcurrencyDefaultsToTwoAndAcceptsIndependentBounds()
    {
        var defaults = RuntimeHostStartupOptions.Parse([]);
        var configured = RuntimeHostStartupOptions.Parse([
            "--package-preparation-concurrency", "8",
            "--package-worker-startup-concurrency", "1",
        ]);

        Assert.Equal(2, defaults.PackagePreparationConcurrency);
        Assert.Equal(2, defaults.PackageWorkerStartupConcurrency);
        Assert.Equal(8, configured.PackagePreparationConcurrency);
        Assert.Equal(1, configured.PackageWorkerStartupConcurrency);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("not-a-number")]
    public void Parse_RejectsPackageConcurrencyOutsideOneThroughEight(string value)
    {
        Assert.Throws<ArgumentException>(() => RuntimeHostStartupOptions.Parse([
            "--package-preparation-concurrency", value,
        ]));
    }
}
