using System.Text.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli.Tests;

public sealed class PackageSettingsAndAuthCommandTests
{
    [Fact]
    public async Task Secret_reader_requires_redirected_stdin_and_removes_only_the_transport_newline()
    {
        var interactive = new PackageSecretValueReader(new StringReader("ignored"), () => false, _ => null);
        await Assert.ThrowsAsync<CliUsageException>(async () =>
            await interactive.ReadAsync(PackageSecretSource.StandardInput, null, CancellationToken.None));

        var redirected = new PackageSecretValueReader(new StringReader("line one\nline two\r\n"), () => true, _ => null);
        using var value = await redirected.ReadAsync(PackageSecretSource.StandardInput, null, CancellationToken.None);

        Assert.Equal("line one\nline two", value.Value);
    }

    [Fact]
    public async Task Config_set_uses_the_declared_type_and_normalizes_boolean_values()
    {
        string? captured = null;
        var runtime = RuntimeWithSchema();
        runtime.SetSetting = (_, key, value, _) =>
        {
            Assert.Equal("enabled", key);
            captured = value;
            return Task.CompletedTask;
        };

        var result = await CliTestHost.RunAsync(["package", "config", "set", "demo", "enabled", "TRUE"], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("true", captured);
    }

    [Fact]
    public async Task Config_list_projects_non_secret_values_without_secret_key_inventory()
    {
        var runtime = RuntimeWithSchema();
        runtime.SettingsValues = (_, _) => Task.FromResult<PackageSettingsValuesResponse?>(new(
            "demo",
            new Dictionary<string, string?> { ["enabled"] = "false", ["mode"] = "fast" },
            ["api-key"]));

        var result = await CliTestHost.RunAsync(["package", "config", "list", "demo", "--json"], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.DoesNotContain("api-key", result.Output, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result.Output);
        var fields = document.RootElement.GetProperty("data").GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(["enabled", "mode"], fields.Select(field => field.GetProperty("key").GetString()!).ToArray());
        Assert.Equal("false", fields[0].GetProperty("effectiveValue").GetString());
    }

    [Fact]
    public async Task Config_surface_refuses_to_read_a_declared_secret()
    {
        var readCalled = false;
        var runtime = RuntimeWithSchema();
        runtime.SettingValue = (_, _, _) =>
        {
            readCalled = true;
            throw new InvalidOperationException("Secret endpoint must not be read.");
        };

        var result = await CliTestHost.RunAsync(["package", "config", "get", "demo", "api-key"], runtime);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.False(readCalled);
        Assert.Contains("package secret", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_status_reports_presence_only()
    {
        var runtime = RuntimeWithSchema();
        runtime.SettingsValues = (_, _) => Task.FromResult<PackageSettingsValuesResponse?>(new(
            "demo",
            new Dictionary<string, string?>(),
            ["api-key"]));

        var result = await CliTestHost.RunAsync(["package", "secret", "status", "demo", "--json"], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var field = Assert.Single(document.RootElement.GetProperty("data").GetProperty("fields").EnumerateArray());
        Assert.Equal("api-key", field.GetProperty("key").GetString());
        Assert.True(field.GetProperty("configured").GetBoolean());
        Assert.False(field.TryGetProperty("value", out _));
    }

    [Fact]
    public async Task Secret_set_reads_only_the_selected_safe_source_and_never_emits_the_value()
    {
        const string value = "value-that-must-not-appear";
        string? captured = null;
        var runtime = RuntimeWithSchema();
        runtime.SetSetting = (_, key, settingValue, _) =>
        {
            Assert.Equal("api-key", key);
            captured = settingValue;
            return Task.CompletedTask;
        };
        var reader = new FakePackageSecretValueReader
        {
            Read = (source, variable, _) =>
            {
                Assert.Equal(PackageSecretSource.Environment, source);
                Assert.Equal("DEMO_API_KEY", variable);
                return ValueTask.FromResult(new PackageSecretValue(value));
            },
        };

        var result = await CliTestHost.RunAsync(
            ["package", "secret", "set", "demo", "api-key", "--from-env", "DEMO_API_KEY", "--json"],
            runtime,
            packageSecrets: reader);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(value, captured);
        Assert.DoesNotContain(value, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(value, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Package_auth_login_launches_browser_and_polls_to_connected()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        var polls = 0;
        var runtime = new FakeRuntimeClient
        {
            PackageAuthStart = (id, _) => Task.FromResult(new PackageAuthSessionStartResponse(
                id,
                "auth-1",
                PackageAuthFlowKind.Browser,
                "https://login.example.test/authorize?state=public",
                "Open browser.",
                expires)),
            PackageAuthSession = (id, sessionId, _) => Task.FromResult(++polls == 1
                ? new PackageAuthSessionStatusResponse(id, sessionId, PackageAuthSessionState.Pending, "Pending.", null, expires)
                : new PackageAuthSessionStatusResponse(id, sessionId, PackageAuthSessionState.Connected, "Connected.", null, expires)),
        };
        var browser = new FakeBrowserLauncher();

        var result = await CliTestHost.RunAsync(["package", "auth", "login", "demo"], runtime, browser: browser);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(2, polls);
        Assert.Equal("https://login.example.test/authorize?state=public", browser.Opened?.AbsoluteUri);
        Assert.Contains("Connected.", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PackageAuthSessionState.Expired, CliExitCodes.Timeout)]
    [InlineData(PackageAuthSessionState.Cancelled, CliExitCodes.Cancelled)]
    [InlineData(PackageAuthSessionState.Failed, CliExitCodes.Failure)]
    public async Task Package_auth_login_has_stable_terminal_state_semantics(
        PackageAuthSessionState state,
        int expectedExitCode)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        var runtime = new FakeRuntimeClient
        {
            PackageAuthStart = (id, _) => Task.FromResult(new PackageAuthSessionStartResponse(
                id,
                "auth-1",
                PackageAuthFlowKind.Browser,
                "https://login.example.test/",
                "Open browser.",
                expires)),
            PackageAuthSession = (id, sessionId, _) => Task.FromResult(new PackageAuthSessionStatusResponse(
                id,
                sessionId,
                state,
                "Authorization ended.",
                null,
                expires)),
        };

        var result = await CliTestHost.RunAsync(["package", "auth", "login", "demo"], runtime);

        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    [Fact]
    public async Task Package_auth_login_cancels_the_runtime_session_when_the_cli_is_cancelled()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        var cancelled = false;
        var runtime = new FakeRuntimeClient
        {
            PackageAuthStart = (id, _) => Task.FromResult(new PackageAuthSessionStartResponse(
                id,
                "auth-1",
                PackageAuthFlowKind.Browser,
                "https://login.example.test/",
                "Open browser.",
                expires)),
            PackageAuthSession = (id, sessionId, _) => Task.FromResult(new PackageAuthSessionStatusResponse(
                id,
                sessionId,
                PackageAuthSessionState.Pending,
                "Pending.",
                null,
                expires)),
            PackageAuthCancel = (_, sessionId, _) =>
            {
                Assert.Equal("auth-1", sessionId);
                cancelled = true;
                return Task.FromResult(true);
            },
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        var result = await CliTestHost.RunAsync(
            ["package", "auth", "login", "demo"],
            runtime,
            token: cancellation.Token);

        Assert.Equal(CliExitCodes.Cancelled, result.ExitCode);
        Assert.True(cancelled);
    }

    private static FakeRuntimeClient RuntimeWithSchema()
    {
        var schema = new PackageSettingsSchemaDescriptor(
            "demo",
            "Demo",
            null,
            [new PackageSettingsSectionDescriptor(
                "general",
                "General",
                null,
                [
                    new PackageSettingsFieldDescriptor("enabled", "Enabled", PackageSettingsFieldKind.Boolean, null, false, null, "true", []),
                    new PackageSettingsFieldDescriptor("mode", "Mode", PackageSettingsFieldKind.Select, null, false, null, "safe", [
                        new PackageSettingsOptionDescriptor("safe", "Safe"),
                        new PackageSettingsOptionDescriptor("fast", "Fast"),
                    ]),
                    new PackageSettingsFieldDescriptor("api-key", "API key", PackageSettingsFieldKind.Secret, null, true, null, null, []),
                ])]);
        return new FakeRuntimeClient
        {
            SettingsSchemas = _ => Task.FromResult<IReadOnlyList<PackageSettingsSchemaDescriptor>>([schema]),
        };
    }
}
