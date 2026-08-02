using Sunder.Host.Contracts;
using Sunder.Host.Supervisor;
using Xunit;

namespace Sunder.Host.Supervisor.Tests;

public sealed partial class HostSupervisorFoundationTests
{
    [Fact]
    public void HostStartupOptions_PreservesPerUserDefaults()
    {
        var root = CreateTempDirectory();
        try
        {
            var options = HostStartupOptions.Parse([], static _ => null, root);

            Assert.Null(options.RuntimeHostPath);
            Assert.Equal(TimeSpan.FromSeconds(30), options.WorkerStartupTimeout);
            Assert.Null(options.HostStateRoot);
            Assert.Null(options.RuntimeStateRoot);
            Assert.Null(options.DeploymentIdentity);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HostStartupOptions_ValidatesDeploymentIdentity()
    {
        var root = CreateTempDirectory();
        try
        {
            var identity = HostDeploymentIdentity.FromSha256(new string('a', 64));

            var options = HostStartupOptions.Parse(
                ["--deployment-identity", identity],
                static _ => null,
                root);

            Assert.Equal(identity, options.DeploymentIdentity);
            Assert.Throws<ArgumentException>(() => HostStartupOptions.Parse(
                ["--deployment-identity", "not-canonical"],
                static _ => null,
                root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HostDeploymentIdentityVerifier_BindsIdentityToDirectoryContent()
    {
        var root = CreateTempDirectory();
        try
        {
            var file = Path.Combine(root, "host.dat");
            File.WriteAllText(file, "one");
            var identity = HostDeploymentIdentityVerifier.Compute(root);

            HostDeploymentIdentityVerifier.Validate(root, identity);
            File.WriteAllText(file, "two");

            Assert.Throws<InvalidDataException>(() =>
                HostDeploymentIdentityVerifier.Validate(root, identity));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HostStartupOptions_NormalizesExplicitDebugPaths()
    {
        var root = CreateTempDirectory();
        var runtimeHost = Path.Combine(root, "Sunder.Runtime.Host.dll");
        File.WriteAllBytes(runtimeHost, []);
        try
        {
            var options = HostStartupOptions.Parse(
                [
                    "--runtime-host-path", runtimeHost,
                    "--host-state-root", Path.Combine(root, "host"),
                    "--runtime-state-root", Path.Combine(root, "runtime"),
                    "--worker-startup-timeout-seconds", "12.5",
                ],
                static _ => null,
                root);

            Assert.Equal(runtimeHost, options.RuntimeHostPath);
            Assert.Equal(Path.Combine(root, "host"), options.HostStateRoot);
            Assert.Equal(Path.Combine(root, "runtime"), options.RuntimeStateRoot);
            Assert.Equal(TimeSpan.FromSeconds(12.5), options.WorkerStartupTimeout);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HostBearerTokenValidator_RequiresMatchingBearerCredential()
    {
        var validator = new HostBearerTokenValidator("expected-token");

        Assert.True(validator.IsValid("Bearer expected-token"));
        Assert.False(validator.IsValid(null));
        Assert.False(validator.IsValid("Basic expected-token"));
        Assert.False(validator.IsValid("Bearer wrong-token"));
    }

    [Fact]
    public void HostRuntimeResetChallenge_IsOneTimeAndRejectsWrongConfirmation()
    {
        var challenges = new HostRuntimeResetChallengeService();
        var first = challenges.Create();

        Assert.False(challenges.TryConsume("wrong"));
        Assert.False(challenges.TryConsume(first.Challenge));

        var second = challenges.Create();
        Assert.True(challenges.TryConsume(second.Challenge));
        Assert.False(challenges.TryConsume(second.Challenge));
    }

    [Fact]
    public void ListenUrlValidator_AcceptsLoopbackAndRejectsRemoteHttp()
    {
        Assert.Equal(
            "http://127.0.0.1:5275",
            HostListenUrlValidator.ParseAndValidateLocal("http://127.0.0.1:5275"));
        Assert.Throws<InvalidOperationException>(() =>
            HostListenUrlValidator.ParseAndValidateLocal("http://192.0.2.1:5275"));
        Assert.Throws<InvalidOperationException>(() =>
            HostListenUrlValidator.ParseAndValidateLocal("http://127.0.0.1:5275;http://127.0.0.1:5276"));
    }

    [Fact]
    public void IdentityStore_PersistsStableHostIdAndFailsClosedOnCorruption()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new HostIdentityStore(root);
            var first = store.LoadOrCreateHostId();

            Assert.NotEqual(Guid.Empty, first);
            Assert.Equal(first, new HostIdentityStore(root).LoadOrCreateHostId());

            File.WriteAllText(Path.Combine(root, "identity.json"), "{}");
            Assert.Throws<InvalidDataException>(() => new HostIdentityStore(root).LoadOrCreateHostId());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
