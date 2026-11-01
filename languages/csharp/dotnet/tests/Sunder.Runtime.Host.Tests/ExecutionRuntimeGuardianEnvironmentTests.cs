using System.Runtime.InteropServices;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ExecutionRuntimeGuardianEnvironmentTests
{
    [Fact]
    public void Configure_UsesTheLoadedHostRuntimeInsteadOfAmbientRoots()
    {
        var environment = CreateEnvironment();
        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        var expectedRoot = new DirectoryInfo(Path.TrimEndingDirectorySeparator(runtimeDirectory))
            .Parent!.Parent!.Parent!.FullName;

        ExecutionRuntimeGuardianEnvironment.Configure(environment, runtimeDirectory);

        Assert.Equal(expectedRoot, environment["DOTNET_ROOT"]);
        Assert.Equal(["DOTNET_ROOT"], environment.Keys.Where(IsRuntimeRoot));
        Assert.Equal("temporary-path", environment["TMPDIR"]);
        Assert.Equal("package.identity", environment["SUNDER_PACKAGE_ID"]);

        ExecutionRuntimeGuardianEnvironment.Clear(environment);

        Assert.DoesNotContain(environment.Keys, IsRuntimeRoot);
        Assert.Equal(2, environment.Count);
    }

    [Theory]
    [InlineData("app")]
    [InlineData("shared/Microsoft.AspNetCore.App/10.0.14")]
    public void Configure_AppLocalOrUnrecognizedLayout_DoesNotInventAnInstallationRoot(string relativePath)
    {
        var environment = CreateEnvironment();
        var runtimeDirectory = Path.GetFullPath(Path.Combine("guardian-deployment", relativePath));

        ExecutionRuntimeGuardianEnvironment.Configure(environment, runtimeDirectory);

        Assert.DoesNotContain(environment.Keys, IsRuntimeRoot);
        Assert.Equal(2, environment.Count);
    }

    [Fact]
    public void StartupFailure_ExposesOnlyHostAuthoredGuardianDiagnostics()
    {
        var failure = new ExecutionRuntimeGuardianLaunchException(150);
        var diagnostic = PackageSessionLifecycleService.DescribeWorkerStartupFailure(failure, "fallback");

        Assert.Equal(failure.Message, diagnostic);
        Assert.Contains("exit code 150", diagnostic, StringComparison.Ordinal);
        Assert.Contains(".NET runtime", diagnostic, StringComparison.Ordinal);
        Assert.Equal("fallback", PackageSessionLifecycleService.DescribeWorkerStartupFailure(
            new IOException("untrusted stderr with secret values"), "fallback"));
    }

    private static Dictionary<string, string?> CreateEnvironment()
        => new(StringComparer.Ordinal)
        {
            ["DOTNET_ROOT"] = "wrong-runtime",
            ["DOTNET_ROOT_ARM64"] = "wrong-arm64-runtime",
            ["DOTNET_ROOT_X64"] = "wrong-x64-runtime",
            ["DOTNET_ROOT_X86"] = "wrong-x86-runtime",
            ["DOTNET_ROOT(x86)"] = "wrong-legacy-runtime",
            ["Dotnet_Root_Arm64"] = "wrong-mixed-case-runtime",
            ["TMPDIR"] = "temporary-path",
            ["SUNDER_PACKAGE_ID"] = "package.identity",
        };

    private static bool IsRuntimeRoot(string name)
        => name.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase);
}
