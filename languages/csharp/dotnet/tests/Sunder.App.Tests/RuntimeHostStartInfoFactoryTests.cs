using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimeHostStartInfoFactoryTests
{
    [Fact]
    public void Create_ManagedSupervisorCarriesCanonicalDeploymentIdentity()
    {
        var identity = Sunder.Host.Contracts.HostDeploymentIdentity.FromSha256(new string('a', 64));
        var policyPath = OperatingSystem.IsWindows()
            ? @"C:\Users\test\AppData\Local\Sunder\app\v1\runtime-resource-policy.json"
            : "/home/test/.local/share/Sunder/app/v1/runtime-resource-policy.json";

        var startInfo = RuntimeHostStartInfoFactory.Create(
            OperatingSystem.IsWindows() ? @"C:\Sunder\Sunder.Host.Supervisor.exe" : "/opt/sunder/Sunder.Host.Supervisor",
            new Uri("http://127.0.0.1:5275/"),
            OperatingSystem.IsWindows() ? @"C:\Sunder\host.json" : "/tmp/sunder/host.json",
            managedSupervisor: true,
            deploymentIdentity: identity,
            runtimeResourcePolicyFilePath: policyPath);

        var identityOption = startInfo.ArgumentList.IndexOf("--deployment-identity");
        Assert.True(identityOption >= 0);
        Assert.Equal(identity, startInfo.ArgumentList[identityOption + 1]);
        Assert.False(startInfo.Environment.ContainsKey("SUNDER_HOST_RUNTIME_PATH"));
        Assert.Equal(
            Path.GetFullPath(policyPath),
            startInfo.Environment[RuntimeResourcePolicyEnvironment.PolicyFilePathVariable]);
        Assert.False(startInfo.Environment.ContainsKey(RuntimeResourcePolicyEnvironment.LaunchPayloadVariable));
    }

    [Fact]
    public void ManagedSupervisorEnvironment_RetainsPublicDevelopmentTrustAndRemovesInheritedSecrets()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin",
            ["SUNDER_RUNTIME_BEARER_TOKEN"] = "inherited-secret",
            ["SUNDER_WAIT_FOR_DEBUGGER"] = "1",
            ["SUNDER_UNRELATED_SECRET"] = "secret",
            ["sunder_runtime_bearer_token"] = "mixed-case-secret",
            ["sunder_wait_for_debugger"] = "1",
            [RuntimeResourcePolicyEnvironment.PolicyFilePathVariable] = "/inherited/policy.json",
            [RuntimeResourcePolicyEnvironment.LaunchPayloadVariable] = "inherited-payload",
            ["SUNDER_HOST_RUNTIME_PATH"] = "/opt/sunder/runtime",
            ["SUNDER_HOST_STATE_ROOT"] = "/state/host",
            ["SUNDER_RUNTIME_STATE_ROOT"] = "/state/runtime",
            ["sunder_host_state_root"] = "/state/mixed-case-host",
            ["SUNDER_RUNTIME_DEVELOPMENT_RELEASE_KEY_ID"] = "development-release-key",
            ["SUNDER_RUNTIME_DEVELOPMENT_RELEASE_PUBLIC_KEY_BASE64"] = "public-key",
            ["SUNDER_RUNTIME_DEVELOPMENT_TRUST_GENERATION"] = "2",
            ["SUNDER_RUNTIME_DEVELOPMENT_ALLOW_INSECURE_LOOPBACK_REGISTRY"] = "true",
            ["SUNDER_EXECUTION_RUNTIME_DEVELOPMENT_STATE_ROOT"] = "/private/key-state",
        };

        RuntimeHostStartInfoFactory.SanitizeManagedSupervisorEnvironment(environment);

        Assert.Equal("/usr/bin", environment["PATH"]);
        Assert.Equal("/opt/sunder/runtime", environment["SUNDER_HOST_RUNTIME_PATH"]);
        Assert.Equal("/state/host", environment["SUNDER_HOST_STATE_ROOT"]);
        Assert.Equal("/state/runtime", environment["SUNDER_RUNTIME_STATE_ROOT"]);
        Assert.Equal("/state/mixed-case-host", environment["sunder_host_state_root"]);
        Assert.Equal("development-release-key", environment["SUNDER_RUNTIME_DEVELOPMENT_RELEASE_KEY_ID"]);
        Assert.Equal("public-key", environment["SUNDER_RUNTIME_DEVELOPMENT_RELEASE_PUBLIC_KEY_BASE64"]);
        Assert.Equal("2", environment["SUNDER_RUNTIME_DEVELOPMENT_TRUST_GENERATION"]);
        Assert.Equal("true", environment["SUNDER_RUNTIME_DEVELOPMENT_ALLOW_INSECURE_LOOPBACK_REGISTRY"]);
        Assert.DoesNotContain("SUNDER_RUNTIME_BEARER_TOKEN", environment.Keys);
        Assert.DoesNotContain("SUNDER_WAIT_FOR_DEBUGGER", environment.Keys);
        Assert.DoesNotContain("SUNDER_UNRELATED_SECRET", environment.Keys);
        Assert.DoesNotContain("sunder_runtime_bearer_token", environment.Keys);
        Assert.DoesNotContain("sunder_wait_for_debugger", environment.Keys);
        Assert.DoesNotContain("SUNDER_EXECUTION_RUNTIME_DEVELOPMENT_STATE_ROOT", environment.Keys);
        Assert.DoesNotContain(RuntimeResourcePolicyEnvironment.PolicyFilePathVariable, environment.Keys);
        Assert.DoesNotContain(RuntimeResourcePolicyEnvironment.LaunchPayloadVariable, environment.Keys);
    }

    [Fact]
    public void Create_StandaloneRuntimeDoesNotReceiveAppPolicyFilePath()
    {
        var startInfo = RuntimeHostStartInfoFactory.Create(
            OperatingSystem.IsWindows() ? @"C:\Sunder\Sunder.Runtime.Host.exe" : "/opt/sunder/Sunder.Runtime.Host",
            new Uri("http://127.0.0.1:5275/"),
            OperatingSystem.IsWindows() ? @"C:\Sunder\host.json" : "/tmp/sunder/host.json",
            managedSupervisor: false,
            runtimeResourcePolicyFilePath: OperatingSystem.IsWindows()
                ? @"C:\Sunder\runtime-resource-policy.json"
                : "/tmp/sunder/runtime-resource-policy.json");

        Assert.False(startInfo.Environment.ContainsKey(RuntimeResourcePolicyEnvironment.PolicyFilePathVariable));
        Assert.False(startInfo.Environment.ContainsKey(RuntimeResourcePolicyEnvironment.LaunchPayloadVariable));
    }

    [Fact]
    public void Create_StandaloneRuntimeDoesNotReceivePrivateDevelopmentStateRoot()
    {
        const string variable = "SUNDER_EXECUTION_RUNTIME_DEVELOPMENT_STATE_ROOT";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "/private/key-state");

            var startInfo = RuntimeHostStartInfoFactory.Create(
                OperatingSystem.IsWindows() ? @"C:\Sunder\Sunder.Runtime.Host.exe" : "/opt/sunder/Sunder.Runtime.Host",
                new Uri("http://127.0.0.1:5275/"),
                OperatingSystem.IsWindows() ? @"C:\Sunder\host.json" : "/tmp/sunder/host.json");

            Assert.False(startInfo.Environment.ContainsKey(variable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

}
