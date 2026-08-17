using System.Text;
using Sunder.Host.Supervisor;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Host.Supervisor.Tests;

public sealed partial class HostSupervisorFoundationTests
{
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
    public async Task RuntimeWorkerCoordinatorReloadsPolicyForEachWorkerStartInfoAndPassesOnlyLaunchPayload()
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
            using var store = new HostLifecycleStore(Path.Combine(root, "host-state"));
            await using var coordinator = new RuntimeWorkerCoordinator(
                runtimeHostPath,
                Path.Combine(root, "worker-connection.json"),
                TimeSpan.FromSeconds(1),
                store,
                isRuntimeLeaseAvailable: static () => true,
                runtimeStateRoot: Path.Combine(root, "runtime-state"),
                runtimeResourcePolicyFilePath: policyPath,
                reportResourcePolicyDiagnostic: _ => Assert.Fail("Valid policies must not emit diagnostics."));
            var endpoint = OperatingSystem.IsWindows()
                ? new RuntimeIpcEndpoint(RuntimeIpcTransportKind.NamedPipe, "sunder-runtime-policy-test")
                : new RuntimeIpcEndpoint(RuntimeIpcTransportKind.UnixSocket, Path.Combine(root, "runtime.sock"));

            var first = coordinator.CreateWorkerStartInfo("first-token", endpoint, "lifetime-1", "diagnostic-1");
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
            var second = coordinator.CreateWorkerStartInfo("second-token", endpoint, "lifetime-2", "diagnostic-2");
            var secondPayload = ParsePayload(second.Environment[
                RuntimeResourcePolicyEnvironment.LaunchPayloadVariable]!);

            Assert.Equal(RuntimeResourcePolicies.Standard, firstPayload.EffectivePolicy);
            Assert.Equal(custom, secondPayload.EffectivePolicy);
            Assert.NotEqual(firstPayload.EffectiveProfileHash, secondPayload.EffectiveProfileHash);
            Assert.False(first.Environment.ContainsKey(RuntimeResourcePolicyEnvironment.PolicyFilePathVariable));
            Assert.False(second.Environment.ContainsKey(RuntimeResourcePolicyEnvironment.PolicyFilePathVariable));
            Assert.Equal("second-token", second.Environment["SUNDER_RUNTIME_BEARER_TOKEN"]);
            Assert.Equal("lifetime-2", second.Environment["SUNDER_RUNTIME_SUPERVISOR_PIPE"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RuntimeResourcePolicyLaunchPayload ParsePayload(string json)
        => RuntimeResourcePolicyContract.ParseLaunchPayload(Encoding.UTF8.GetBytes(json));
}
