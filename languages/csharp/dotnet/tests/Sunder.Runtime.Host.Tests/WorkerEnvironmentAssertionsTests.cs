using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class WorkerEnvironmentAssertionsTests
{
    [Theory]
    [InlineData(true, Architecture.Arm64, Architecture.Arm64, "PROCESSOR_ARCHITECTURE", "ARM64", true)]
    [InlineData(true, Architecture.Arm64, Architecture.X64, "PROCESSOR_ARCHITECTURE", "AMD64", true)]
    [InlineData(true, Architecture.Arm64, Architecture.Arm64, "PROCESSOR_ARCHITECTURE", "AMD64", false)]
    [InlineData(true, Architecture.Arm64, Architecture.X64, "PROCESSOR_ARCHITECTURE", "ARM64", false)]
    [InlineData(true, Architecture.Arm64, Architecture.Arm64, null, null, true)]
    [InlineData(true, Architecture.X64, Architecture.X64, "PROCESSOR_ARCHITECTURE", "AMD64", false)]
    [InlineData(false, Architecture.Arm64, Architecture.Arm64, "PROCESSOR_ARCHITECTURE", "ARM64", false)]
    [InlineData(false, Architecture.Arm64, Architecture.Arm64, null, null, true)]
    [InlineData(true, Architecture.Arm64, Architecture.Arm64, "PROCESSOR_ARCHITEW6432", null, false)]
    [InlineData(true, Architecture.Arm64, Architecture.Arm64, "PATH", null, false)]
    [InlineData(true, Architecture.Arm64, Architecture.Arm64, "NODE_OPTIONS", null, false)]
    [InlineData(true, Architecture.Arm64, Architecture.Arm64, "DOTNET_STARTUP_HOOKS", null, false)]
    [InlineData(true, Architecture.Arm64, Architecture.Arm64, "SUNDER_RUNTIME_BEARER_TOKEN", null, false)]
    public void ChildMetadataExceptionIsArchitectureSpecificAndNeverAnAmbientAllowlist(
        bool windows, Architecture osArchitecture, Architecture workerArchitecture,
        string? extraName, string? value, bool accepted)
    {
        string[] launch = ["SUNDER_WORKER_PROTOCOL"];
        var report = JsonSerializer.SerializeToElement(new
        {
            environmentNames = extraName is null ? launch : [.. launch, extraName],
            processorArchitecture = value,
        });
        var error = Record.Exception(() => WorkerEnvironmentAssertions.AssertObservedChild(
            report, launch, windows, osArchitecture, workerArchitecture));
        if (accepted) Assert.Null(error);
        else Assert.NotNull(error);
    }
}
