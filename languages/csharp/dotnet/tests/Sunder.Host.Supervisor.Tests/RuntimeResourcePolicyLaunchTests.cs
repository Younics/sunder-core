using System.Text;
using Sunder.Host.Supervisor;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Host.Supervisor.Tests;

public sealed partial class HostSupervisorFoundationTests
{
    [Fact]
    public void RuntimeWorkerLaunch_CreatesCompleteManagedDllStartInfo()
    {
        var root = CreateTempDirectory();
        var runtimeHostPath = Path.Combine(root, "Sunder.Runtime.Host.DLL");
        var connectionInfoPath = Path.Combine(root, "worker-connection.json");
        var runtimeStateRoot = Path.Combine(root, "runtime-state");
        File.WriteAllBytes(runtimeHostPath, []);
        try
        {
            var resolver = new RuntimeResourcePolicyLaunchResolver(policyFilePath: null);
            var endpoint = CreateTestEndpoint(root, "managed");

            var startInfo = RuntimeWorkerLaunchProbe.CreateWorkerStartInfo(
                runtimeHostPath,
                connectionInfoPath,
                runtimeStateRoot,
                resolver,
                "managed-token",
                endpoint,
                "managed-lifetime",
                "managed-diagnostic");

            Assert.Equal("dotnet", startInfo.FileName);
            Assert.Equal([runtimeHostPath], startInfo.ArgumentList);
            Assert.Equal(root, startInfo.WorkingDirectory);
            Assert.False(startInfo.UseShellExecute);
            Assert.True(startInfo.CreateNoWindow);
            AssertPrivateLaunchEnvironment(
                startInfo,
                connectionInfoPath,
                "managed-token",
                "managed-lifetime",
                "managed-diagnostic",
                runtimeStateRoot,
                endpoint);
            Assert.DoesNotContain(
                startInfo.ArgumentList,
                argument => argument.Contains("managed-token", StringComparison.Ordinal));
            Assert.DoesNotContain(
                startInfo.ArgumentList,
                argument => argument.Contains("managed-lifetime", StringComparison.Ordinal));
            Assert.DoesNotContain(
                startInfo.ArgumentList,
                argument => argument.Contains("managed-diagnostic", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RuntimeWorkerLaunch_CreatesNativeExecutableWithoutDotnetArgument()
    {
        var root = CreateTempDirectory();
        var runtimeHostPath = Path.Combine(root, "Sunder.Runtime.Host");
        var connectionInfoPath = Path.Combine(root, "worker-connection.json");
        var runtimeStateRoot = Path.Combine(root, "runtime-state");
        File.WriteAllBytes(runtimeHostPath, []);
        try
        {
            var resolver = new RuntimeResourcePolicyLaunchResolver(policyFilePath: null);
            var endpoint = CreateTestEndpoint(root, "native");

            var startInfo = RuntimeWorkerLaunchProbe.CreateWorkerStartInfo(
                runtimeHostPath,
                connectionInfoPath,
                runtimeStateRoot,
                resolver,
                "native-token",
                endpoint,
                "native-lifetime",
                "native-diagnostic");

            Assert.Equal(runtimeHostPath, startInfo.FileName);
            Assert.Empty(startInfo.ArgumentList);
            Assert.Equal(root, startInfo.WorkingDirectory);
            Assert.False(startInfo.UseShellExecute);
            Assert.True(startInfo.CreateNoWindow);
            AssertPrivateLaunchEnvironment(
                startInfo,
                connectionInfoPath,
                "native-token",
                "native-lifetime",
                "native-diagnostic",
                runtimeStateRoot,
                endpoint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RuntimeWorkerLaunch_MissingRuntimePathFailsBeforePolicyResolution()
    {
        var root = CreateTempDirectory();
        var diagnostics = new List<string>();
        var policyPath = Path.Combine(root, "invalid-policy.json");
        File.WriteAllText(policyPath, "not-json");
        try
        {
            var resolver = new RuntimeResourcePolicyLaunchResolver(policyPath, diagnostics.Add);

            var exception = Assert.Throws<FileNotFoundException>(() =>
                RuntimeWorkerLaunchProbe.CreateWorkerStartInfo(
                    runtimeHostPath: null,
                    Path.Combine(root, "worker-connection.json"),
                    Path.Combine(root, "runtime-state"),
                    resolver,
                    "missing-token",
                    CreateTestEndpoint(root, "missing"),
                    "missing-lifetime",
                    "missing-diagnostic"));

            Assert.Equal(
                "Sunder.Runtime.Host is not installed in this Host distribution.",
                exception.Message);
            Assert.Empty(diagnostics);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PolicyEnvironmentRemovalClearsInheritedPathAndPayloadCaseInsensitively()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [RuntimeResourcePolicyEnvironment.PolicyFilePathVariable] = "/app/policy.json",
            [RuntimeResourcePolicyEnvironment.LaunchPayloadVariable.ToLowerInvariant()] = "stale",
            ["PATH"] = "/usr/bin",
        };

        RuntimeResourcePolicyEnvironment.RemovePolicyVariables(environment);

        Assert.Equal("/usr/bin", environment["PATH"]);
        Assert.DoesNotContain(environment.Keys, key => key.StartsWith(
            "SUNDER_RUNTIME_RESOURCE_POLICY",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LaunchResolverReloadsEveryCallAndInvalidPolicyFallsBackToStandardWithDiagnostic()
    {
        var root = CreateTempDirectory();
        var policyPath = Path.Combine(root, "runtime-resource-policy.json");
        var diagnostics = new List<string>();
        try
        {
            var resolver = new RuntimeResourcePolicyLaunchResolver(policyPath, diagnostics.Add);

            var missingPayload = ParsePayload(resolver.ResolveLaunchPayload());
            Assert.Equal(RuntimeResourcePolicies.Standard, missingPayload.EffectivePolicy);
            Assert.Empty(diagnostics);

            File.WriteAllBytes(
                policyPath,
                RuntimeResourcePolicyContract.SerializeSettings(new RuntimeResourcePolicySelection(
                    RuntimeResourcePolicyPreset.Large,
                    RuntimeResourcePolicies.Standard)));
            var largePayload = ParsePayload(resolver.ResolveLaunchPayload());
            Assert.Equal(RuntimeResourcePolicies.Large, largePayload.EffectivePolicy);

            var invalid = Encoding.UTF8.GetString(RuntimeResourcePolicyContract.SerializeSettings(
                RuntimeResourcePolicies.DefaultSelection,
                writeIndented: false)).Replace(
                "\"valueDepth\":64",
                "\"valueDepth\":65",
                StringComparison.Ordinal);
            File.WriteAllText(policyPath, invalid);
            var fallbackPayload = ParsePayload(resolver.ResolveLaunchPayload());

            Assert.Equal(RuntimeResourcePolicies.Standard, fallbackPayload.EffectivePolicy);
            Assert.Equal(
                RuntimeResourcePolicyContract.GetEffectiveProfileHash(RuntimeResourcePolicies.Standard),
                fallbackPayload.EffectiveProfileHash);
            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("runtime.resource-policy.invalid", diagnostic, StringComparison.Ordinal);
            Assert.Contains("using Standard", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RuntimeWorkerLaunchProbeReloadsPolicyForEachWorkerStartInfoAndPassesOnlyLaunchPayload()
    {
        var root = CreateTempDirectory();
        var runtimeHostPath = Path.Combine(root, "Sunder.Runtime.Host.dll");
        var policyPath = Path.Combine(root, "app", "v1", "runtime-resource-policy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(policyPath)!);
        File.WriteAllBytes(runtimeHostPath, []);
        File.WriteAllBytes(
            policyPath,
            RuntimeResourcePolicyContract.SerializeSettings(new RuntimeResourcePolicySelection(
                RuntimeResourcePolicyPreset.Standard,
                RuntimeResourcePolicies.Standard)));
        try
        {
            var connectionInfoPath = Path.Combine(root, "worker-connection.json");
            var runtimeStateRoot = Path.Combine(root, "runtime-state");
            var resolver = new RuntimeResourcePolicyLaunchResolver(
                policyPath,
                _ => Assert.Fail("Valid policies must not emit diagnostics."));
            var endpoint = OperatingSystem.IsWindows()
                ? new RuntimeIpcEndpoint(RuntimeIpcTransportKind.NamedPipe, "sunder-runtime-policy-test")
                : new RuntimeIpcEndpoint(RuntimeIpcTransportKind.UnixSocket, Path.Combine(root, "runtime.sock"));

            var first = RuntimeWorkerLaunchProbe.CreateWorkerStartInfo(
                runtimeHostPath,
                connectionInfoPath,
                runtimeStateRoot,
                resolver,
                "first-token",
                endpoint,
                "lifetime-1",
                "diagnostic-1");
            var firstPayload = ParsePayload(first.Environment[
                RuntimeResourcePolicyEnvironment.LaunchPayloadVariable]!);

            var custom = new RuntimeResourcePolicy(
                20 * 1024 * 1024,
                32,
                3_000_000,
                32_000,
                3 * 1024 * 1024,
                200 * 1024 * 1024);
            File.WriteAllBytes(
                policyPath,
                RuntimeResourcePolicyContract.SerializeSettings(new RuntimeResourcePolicySelection(
                    RuntimeResourcePolicyPreset.Custom,
                    custom)));
            var second = RuntimeWorkerLaunchProbe.CreateWorkerStartInfo(
                runtimeHostPath,
                connectionInfoPath,
                runtimeStateRoot,
                resolver,
                "second-token",
                endpoint,
                "lifetime-2",
                "diagnostic-2");
            var secondPayload = ParsePayload(second.Environment[
                RuntimeResourcePolicyEnvironment.LaunchPayloadVariable]!);

            Assert.Equal(RuntimeResourcePolicies.Standard, firstPayload.EffectivePolicy);
            Assert.Equal(custom, secondPayload.EffectivePolicy);
            Assert.NotEqual(firstPayload.EffectiveProfileHash, secondPayload.EffectiveProfileHash);
            Assert.NotSame(first, second);
            Assert.False(first.Environment.ContainsKey(RuntimeResourcePolicyEnvironment.PolicyFilePathVariable));
            Assert.False(second.Environment.ContainsKey(RuntimeResourcePolicyEnvironment.PolicyFilePathVariable));
            Assert.Equal(connectionInfoPath, second.Environment["SUNDER_RUNTIME_CONNECTION_FILE"]);
            Assert.Equal("second-token", second.Environment["SUNDER_RUNTIME_BEARER_TOKEN"]);
            Assert.Equal("lifetime-2", second.Environment["SUNDER_RUNTIME_SUPERVISOR_PIPE"]);
            Assert.Equal("diagnostic-2", second.Environment["SUNDER_RUNTIME_STARTUP_DIAGNOSTIC_PIPE"]);
            Assert.Equal(runtimeStateRoot, second.Environment[RuntimeLocalState.StateRootEnvironmentVariable]);
            Assert.Equal(endpoint.ToEnvironmentValue(), second.Environment[RuntimeIpcEndpoint.EnvironmentVariable]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RuntimeResourcePolicyLaunchPayload ParsePayload(string json)
        => RuntimeResourcePolicyContract.ParseLaunchPayload(Encoding.UTF8.GetBytes(json));

    private static RuntimeIpcEndpoint CreateTestEndpoint(string root, string suffix)
        => OperatingSystem.IsWindows()
            ? new RuntimeIpcEndpoint(RuntimeIpcTransportKind.NamedPipe, $"sunder-runtime-launch-{suffix}")
            : new RuntimeIpcEndpoint(RuntimeIpcTransportKind.UnixSocket, Path.Combine(root, $"runtime-{suffix}.sock"));

    private static void AssertPrivateLaunchEnvironment(
        System.Diagnostics.ProcessStartInfo startInfo,
        string connectionInfoPath,
        string workerToken,
        string supervisorLifetimePipeHandle,
        string startupDiagnosticPipeHandle,
        string runtimeStateRoot,
        RuntimeIpcEndpoint endpoint)
    {
        Assert.Equal(connectionInfoPath, startInfo.Environment["SUNDER_RUNTIME_CONNECTION_FILE"]);
        Assert.Equal(workerToken, startInfo.Environment["SUNDER_RUNTIME_BEARER_TOKEN"]);
        Assert.Equal(supervisorLifetimePipeHandle, startInfo.Environment["SUNDER_RUNTIME_SUPERVISOR_PIPE"]);
        Assert.Equal(startupDiagnosticPipeHandle, startInfo.Environment["SUNDER_RUNTIME_STARTUP_DIAGNOSTIC_PIPE"]);
        Assert.Equal(runtimeStateRoot, startInfo.Environment[RuntimeLocalState.StateRootEnvironmentVariable]);
        Assert.Equal(endpoint.ToEnvironmentValue(), startInfo.Environment[RuntimeIpcEndpoint.EnvironmentVariable]);
        Assert.NotNull(startInfo.Environment[RuntimeResourcePolicyEnvironment.LaunchPayloadVariable]);
        Assert.False(startInfo.Environment.ContainsKey(RuntimeResourcePolicyEnvironment.PolicyFilePathVariable));
    }
}
