using Sunder.Cli;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli.Tests;

public sealed class CliDelegationTests
{
    [Fact]
    public async Task Auth_login_polls_runtime_without_receiving_credentials()
    {
        var polls = 0;
        var browser = new FakeBrowserLauncher();
        var runtime = new FakeRuntimeClient
        {
            AuthStart = (_, _) => Task.FromResult(new RuntimeRegistryAuthStartResponse(
                "session-1", "https://registry.test/", "https://registry.test/login?code=public", DateTimeOffset.UtcNow.AddMinutes(1))),
            AuthSession = (_, _) => Task.FromResult<RuntimeRegistryAuthSessionStatus?>(++polls == 1
                ? new("session-1", "https://registry.test/", RuntimeRegistryAuthSessionState.Pending, null, null, RuntimeRegistryErrorCode.None, null)
                : new("session-1", "https://registry.test/", RuntimeRegistryAuthSessionState.Succeeded,
                    new RuntimeRegistryUser("user-1", "User", null, "tester", null), DateTimeOffset.UnixEpoch, RuntimeRegistryErrorCode.None, null)),
        };
        var result = await CliTestHost.RunAsync(["auth", "login"], runtime, browser: browser);
        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(2, polls);
        Assert.Equal("https://registry.test/login?code=public", browser.Opened?.AbsoluteUri);
        Assert.DoesNotContain("token", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Registry_install_is_delegated_as_one_runtime_request()
    {
        RuntimeRegistryPackageRequest? captured = null;
        var runtime = new FakeRuntimeClient
        {
            InstallRegistry = (request, _) =>
            {
                captured = request;
                return Task.FromResult(Success("Installed."));
            },
        };
        var result = await CliTestHost.RunAsync(
            ["install", "demo", "--version", "1.2.3", "--allow-downgrade", "--reinstall"], runtime);
        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("demo", captured?.PackageId);
        Assert.Equal("1.2.3", captured?.Version);
        Assert.Null(captured?.Tag);
        Assert.True(captured?.AllowDowngrade);
        Assert.True(captured?.Reinstall);
    }

    [Fact]
    public async Task Update_is_delegated_to_runtime_without_registry_queries()
    {
        RuntimeRegistryUpdateRequest? captured = null;
        var runtime = new FakeRuntimeClient
        {
            UpdateRegistry = (request, _) =>
            {
                captured = request;
                return Task.FromResult(Success("Updated."));
            },
        };
        var result = await CliTestHost.RunAsync(["update", "demo", "--include-prerelease"], runtime);
        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("demo", captured?.PackageId);
        Assert.True(captured?.IncludePrerelease);
    }

    [Fact]
    public async Task Explicit_all_update_is_delegated_as_an_unscoped_runtime_request()
    {
        RuntimeRegistryUpdateRequest? captured = null;
        var runtime = new FakeRuntimeClient
        {
            UpdateRegistry = (request, _) =>
            {
                captured = request;
                return Task.FromResult(Success("Updated."));
            },
        };

        var result = await CliTestHost.RunAsync(["update", "--all"], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Null(captured?.PackageId);
    }

    [Theory]
    [InlineData(RuntimeRegistryAuthSessionState.Pending, RuntimeRegistryErrorCode.None, CliExitCodes.Timeout)]
    [InlineData(RuntimeRegistryAuthSessionState.Expired, RuntimeRegistryErrorCode.None, CliExitCodes.Timeout)]
    [InlineData(RuntimeRegistryAuthSessionState.Failed, RuntimeRegistryErrorCode.AuthenticationRequired, CliExitCodes.Authentication)]
    [InlineData(RuntimeRegistryAuthSessionState.Failed, RuntimeRegistryErrorCode.NotFound, CliExitCodes.NotFound)]
    [InlineData(RuntimeRegistryAuthSessionState.Failed, RuntimeRegistryErrorCode.RegistryUnavailable, CliExitCodes.Unavailable)]
    public async Task Auth_login_terminal_state_has_stable_exit_semantics(
        RuntimeRegistryAuthSessionState state,
        RuntimeRegistryErrorCode errorCode,
        int expectedExitCode)
    {
        var runtime = new FakeRuntimeClient
        {
            AuthStart = (_, _) => Task.FromResult(new RuntimeRegistryAuthStartResponse(
                "session-1", "https://registry.test/", "https://registry.test/login", DateTimeOffset.UtcNow.AddSeconds(-1))),
            AuthSession = (_, _) => Task.FromResult<RuntimeRegistryAuthSessionStatus?>(new(
                "session-1", "https://registry.test/", state, null, null, errorCode, "Sign-in did not complete.")),
        };

        var result = await CliTestHost.RunAsync(["auth", "login"], runtime);

        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    [Fact]
    public async Task Auth_status_propagates_typed_registry_unavailable_result()
    {
        var runtime = new FakeRuntimeClient
        {
            AuthStatus = (_, _) => Task.FromResult(new RuntimeRegistryAuthStatus(
                "https://registry.test/", false, null, null, RuntimeRegistryErrorCode.RegistryUnavailable, "Registry unavailable.")),
        };

        var result = await CliTestHost.RunAsync(["auth", "status"], runtime);

        Assert.Equal(CliExitCodes.Unavailable, result.ExitCode);
        Assert.Equal("Registry unavailable.\n", result.Error);
    }

    private static RuntimeRegistryPackageChangeResult Success(string message)
        => new(true, RuntimeRegistryErrorCode.None, message, true, false, [], [], ["demo"], []);
}
