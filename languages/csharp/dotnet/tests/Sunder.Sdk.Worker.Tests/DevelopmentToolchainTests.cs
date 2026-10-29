using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Worker.Internal;

namespace Sunder.Sdk.Worker.Tests;

public sealed class DevelopmentToolchainTests
{
    [Fact]
    public void HostVersionsAreOptionalForOldWorkersAndExactWhenProvided()
    {
        var environment = EnvironmentValues();
        Assert.Null(WorkerEnvironment.Capture(environment.GetValueOrDefault).DevelopmentToolchain);
        var expected = new SunderDevelopmentToolchain("10.0.403", "24.22.1", "11.19.1");
        environment["SUNDER_DEVELOPMENT_TOOLCHAIN"] = JsonSerializer.Serialize(expected);
        Assert.Equal(expected, WorkerEnvironment.Capture(environment.GetValueOrDefault).DevelopmentToolchain);
    }

    [Theory]
    [InlineData("10.0.403-preview.1", "24.22.1", "11.19.1")]
    [InlineData("10.0.403", "lts/*", "11.19.1")]
    [InlineData("10.0.403", "24.22.1", "latest")]
    [InlineData("10.0.403+unqualified", "24.22.1", "11.19.1")]
    public void FloatingOrPrereleaseToolchainsAreRejected(string dotnet, string node, string npm)
    {
        var environment = EnvironmentValues();
        environment["SUNDER_DEVELOPMENT_TOOLCHAIN"] = JsonSerializer.Serialize(new SunderDevelopmentToolchain(dotnet, node, npm));
        Assert.Throws<InvalidOperationException>(() => WorkerEnvironment.Capture(environment.GetValueOrDefault));
    }

    private static Dictionary<string, string?> EnvironmentValues() => new()
    {
        ["SUNDER_WORKER_PROTOCOL"] = WorkerProtocol.Name,
        ["SUNDER_PACKAGE_ID"] = "test.package",
        ["SUNDER_PACKAGE_VERSION"] = "1.0.0",
        ["SUNDER_ACTIVATION_ID"] = Guid.NewGuid().ToString("N"),
        ["SUNDER_SESSION_ID"] = "test-session",
        ["SUNDER_PACKAGE_CONTENT_ROOT"] = AppContext.BaseDirectory,
        ["SUNDER_WORKER_TEMP_ROOT"] = Path.GetTempPath(),
    };
}
