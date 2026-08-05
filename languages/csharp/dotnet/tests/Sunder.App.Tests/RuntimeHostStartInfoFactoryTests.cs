using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimeHostStartInfoFactoryTests
{
    [Fact]
    public void Create_ManagedSupervisorCarriesCanonicalDeploymentIdentity()
    {
        var identity = Sunder.Host.Contracts.HostDeploymentIdentity.FromSha256(new string('a', 64));

        var startInfo = RuntimeHostStartInfoFactory.Create(
            OperatingSystem.IsWindows() ? @"C:\Sunder\Sunder.Host.Supervisor.exe" : "/opt/sunder/Sunder.Host.Supervisor",
            new Uri("http://127.0.0.1:5275/"),
            OperatingSystem.IsWindows() ? @"C:\Sunder\host.json" : "/tmp/sunder/host.json",
            managedSupervisor: true,
            deploymentIdentity: identity);

        var identityOption = startInfo.ArgumentList.IndexOf("--deployment-identity");
        Assert.True(identityOption >= 0);
        Assert.Equal(identity, startInfo.ArgumentList[identityOption + 1]);
        Assert.False(startInfo.Environment.ContainsKey("SUNDER_HOST_RUNTIME_PATH"));
    }

    [Fact]
    public void ManagedSupervisorEnvironment_RemovesInheritedSunderSecretsAndRetainsStateOverrides()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin",
            ["SUNDER_RUNTIME_BEARER_TOKEN"] = "inherited-secret",
            ["SUNDER_WAIT_FOR_DEBUGGER"] = "1",
            ["SUNDER_UNRELATED_SECRET"] = "secret",
            ["sunder_runtime_bearer_token"] = "mixed-case-secret",
            ["sunder_wait_for_debugger"] = "1",
            ["SUNDER_HOST_RUNTIME_PATH"] = "/opt/sunder/runtime",
            ["SUNDER_HOST_STATE_ROOT"] = "/state/host",
            ["SUNDER_RUNTIME_STATE_ROOT"] = "/state/runtime",
            ["sunder_host_state_root"] = "/state/mixed-case-host",
        };

        RuntimeHostStartInfoFactory.SanitizeManagedSupervisorEnvironment(environment);

        Assert.Equal("/usr/bin", environment["PATH"]);
        Assert.Equal("/opt/sunder/runtime", environment["SUNDER_HOST_RUNTIME_PATH"]);
        Assert.Equal("/state/host", environment["SUNDER_HOST_STATE_ROOT"]);
        Assert.Equal("/state/runtime", environment["SUNDER_RUNTIME_STATE_ROOT"]);
        Assert.Equal("/state/mixed-case-host", environment["sunder_host_state_root"]);
        Assert.DoesNotContain("SUNDER_RUNTIME_BEARER_TOKEN", environment.Keys);
        Assert.DoesNotContain("SUNDER_WAIT_FOR_DEBUGGER", environment.Keys);
        Assert.DoesNotContain("SUNDER_UNRELATED_SECRET", environment.Keys);
        Assert.DoesNotContain("sunder_runtime_bearer_token", environment.Keys);
        Assert.DoesNotContain("sunder_wait_for_debugger", environment.Keys);
    }

}
