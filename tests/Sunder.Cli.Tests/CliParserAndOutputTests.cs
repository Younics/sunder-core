using System.Net;
using System.Text.Json;
using Sunder.Cli;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli.Tests;

public sealed class CliParserAndOutputTests
{
    [Fact]
    public void Command_tree_has_only_the_canonical_roots()
    {
        var roots = new CliCommandParser().Root.Subcommands.Select(command => command.Name).ToArray();

        Assert.Equal(["runtime", "package", "stack", "registry", "dev", "config", "version"], roots);
    }

    [Theory]
    [InlineData("system", "status")]
    [InlineData("auth", "status")]
    [InlineData("search")]
    [InlineData("info", "demo")]
    [InlineData("list")]
    [InlineData("install", "demo")]
    [InlineData("update", "--all")]
    [InlineData("publish", "--file", "demo.sunderpkg")]
    [InlineData("validate", "demo.sunderpkg")]
    [InlineData("stacks", "search")]
    [InlineData("--version")]
    public async Task Legacy_roots_are_rejected(params string[] arguments)
    {
        var result = await CliTestHost.RunAsync(arguments);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
    }

    [Fact]
    public async Task Unknown_command_returns_usage_exit_code()
    {
        var result = await CliTestHost.RunAsync(["wat"]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("wat", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_option_is_not_consumed_as_an_optional_argument()
    {
        var result = await CliTestHost.RunAsync(["package", "search", "--bogus"]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Equal("Unrecognized option '--bogus'. Use '--' before argument values that begin with '-'.\n", result.Error);
    }

    [Fact]
    public async Task Double_dash_allows_an_option_shaped_argument()
    {
        string? query = null;
        var registry = new FakeRegistryClient
        {
            Search = (value, _, _, _) =>
            {
                query = value;
                return Task.FromResult<IReadOnlyList<RegistryPackageSummary>>([]);
            },
        };

        var result = await CliTestHost.RunAsync(["package", "search", "--", "--bogus"], registry: registry);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("--bogus", query);
    }

    [Fact]
    public async Task Repeated_options_are_rejected_instead_of_using_last_value()
    {
        var result = await CliTestHost.RunAsync([
            "package", "search", "--take", "10", "--take", "20",
        ]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("single argument", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--skip", "-1")]
    [InlineData("--take", "0")]
    [InlineData("--take", "101")]
    [InlineData("--take", "not-a-number")]
    public async Task Pagination_is_validated_without_silent_clamping(string option, string value)
    {
        var result = await CliTestHost.RunAsync(["package", "search", option, value]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1s")]
    [InlineData("5000000m")]
    [InlineData("forever")]
    public async Task Timeout_values_are_validated_before_execution(string timeout)
    {
        var result = await CliTestHost.RunAsync(["runtime", "status", "--timeout", timeout]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
    }

    [Fact]
    public async Task Conflicting_install_options_are_parse_errors()
    {
        var result = await CliTestHost.RunAsync([
            "package", "install", "demo", "--version", "1.0.0", "--tag", "latest",
        ]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("cannot be used together", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("package", "install", "Package.Owner", "--version", "1.0.0")]
    [InlineData("package", "install", "package.owner", "--version", "1.0")]
    [InlineData("registry", "package", "yank", "Package.Owner", "1.0.0")]
    public async Task Package_commands_use_canonical_sdk_identity_validation(params string[] arguments)
    {
        var result = await CliTestHost.RunAsync(arguments);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
    }

    [Fact]
    public async Task Bare_update_is_rejected_because_scope_must_be_explicit()
    {
        var result = await CliTestHost.RunAsync(["package", "update"]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("exactly one package id or '--all'", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scoped_help_bypasses_required_arguments_configuration_and_clients()
    {
        var result = await CliTestHost.RunAsync(
            ["package", "install", "--help", "--runtime-url", "not-a-url"],
            runtimeFactory: _ => throw new InvalidOperationException("Runtime factory must not run."),
            registryFactory: _ => throw new InvalidOperationException("Registry factory must not run."));

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("sunder package install", result.Output, StringComparison.Ordinal);
        Assert.Contains("--allow-downgrade", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("registry stack", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bare_group_prints_its_scoped_help()
    {
        var result = await CliTestHost.RunAsync(["registry", "package"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("sunder registry package", result.Output, StringComparison.Ordinal);
        Assert.Contains("publish", result.Output, StringComparison.Ordinal);
        Assert.Contains("yank", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime reset", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ordinary_user_help_exposes_safe_sources_and_lifecycle_without_a_secret_value_argument()
    {
        var secret = await CliTestHost.RunAsync(["package", "secret", "set", "--help"]);
        var runtime = await CliTestHost.RunAsync(["runtime"]);
        var stack = await CliTestHost.RunAsync(["stack"]);

        Assert.Equal(CliExitCodes.Success, secret.ExitCode);
        Assert.Contains("--from-env", secret.Output, StringComparison.Ordinal);
        Assert.Contains("--stdin", secret.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("<value>", secret.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start", runtime.Output, StringComparison.Ordinal);
        Assert.Contains("stop", runtime.Output, StringComparison.Ordinal);
        Assert.Contains("restart", runtime.Output, StringComparison.Ordinal);
        Assert.Contains("import", stack.Output, StringComparison.Ordinal);
        Assert.Contains("export", stack.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_does_not_create_clients_or_load_endpoint_configuration()
    {
        var result = await CliTestHost.RunAsync(
            ["version", "--json", "--runtime-url", "not-a-url"],
            runtimeFactory: _ => throw new InvalidOperationException("Runtime factory must not run."),
            registryFactory: _ => throw new InvalidOperationException("Registry factory must not run."));

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("version", document.RootElement.GetProperty("command").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("data").GetProperty("version").GetString()));
    }

    [Fact]
    public async Task Config_show_resolves_effective_settings_without_creating_clients()
    {
        var result = await CliTestHost.RunAsync(
            ["config", "show", "--json"],
            runtimeFactory: _ => throw new InvalidOperationException("Runtime client must not be created."),
            registryFactory: _ => throw new InvalidOperationException("Registry client must not be created."));

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(
            "https://registry.test/",
            document.RootElement.GetProperty("data").GetProperty("registry").GetProperty("apiUrl").GetString());
        Assert.Equal(
            "http://127.0.0.1:5275/",
            document.RootElement.GetProperty("data").GetProperty("runtime").GetProperty("url").GetString());
    }

    [Fact]
    public async Task Local_validation_does_not_create_clients_or_require_endpoint_configuration()
    {
        CliConfigurationRequirements? captured = null;
        var result = await CliTestHost.RunAsync(
            ["dev", "package", "validate", "missing.sunderpkg"],
            runtimeFactory: _ => throw new InvalidOperationException("Runtime factory must not run."),
            registryFactory: _ => throw new InvalidOperationException("Registry factory must not run."),
            optionsFactory: (overrides, requirements) =>
            {
                captured = requirements;
                return CliOptions.Load(overrides, requirements, new CliAppSettings("bad", "bad", "bad"), _ => "bad");
            });

        Assert.Equal(CliConfigurationRequirements.None, captured);
        Assert.Equal(CliExitCodes.NotFound, result.ExitCode);
    }

    [Fact]
    public void Registry_api_override_is_the_web_fallback_at_the_same_precedence()
    {
        var options = CliOptions.Load(
            new CliOptionOverrides("https://override.test/api", null, null, null),
            CliConfigurationRequirements.RegistryApi | CliConfigurationRequirements.RegistryWeb,
            new CliAppSettings("https://settings-api.test/", "https://settings-web.test/", null),
            _ => null);

        Assert.Equal("https://override.test/api/", options.RegistryApiUrl?.AbsoluteUri);
        Assert.Equal("https://override.test/api/", options.RegistryWebUrl?.AbsoluteUri);
    }

    [Fact]
    public void Registry_only_configuration_does_not_read_or_validate_runtime_overrides()
    {
        var options = CliOptions.Load(
            new CliOptionOverrides(null, null, null, null),
            CliConfigurationRequirements.RegistryApi,
            new CliAppSettings("https://registry.test/", null, "not-a-url"),
            name => name == "SUNDER_RUNTIME_URL"
                ? throw new InvalidOperationException("Runtime environment must not be read.")
                : null);

        Assert.Equal("https://registry.test/", options.RegistryApiUrl?.AbsoluteUri);
        Assert.Null(options.RuntimeUrl);
    }

    [Fact]
    public async Task Configuration_failures_have_a_structured_usage_error()
    {
        var result = await CliTestHost.RunAsync(
            ["runtime", "status", "--json"],
            optionsFactory: (_, _) => throw new CliConfigurationException("Runtime URL is invalid."),
            runtimeFactory: _ => throw new InvalidOperationException("Runtime client must not be created."));

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var error = Assert.Single(document.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("cli.configuration.invalid", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Invalid_url_errors_do_not_echo_url_credentials()
    {
        var result = await CliTestHost.RunAsync([
            "config", "show", "--registry-api-url", "https://user:super-secret@registry.test/",
        ]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.DoesNotContain("super-secret", result.Error, StringComparison.Ordinal);
        Assert.Contains("user information", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_configuration_is_reported_as_a_configuration_exception()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sunder-cli-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "appsettings.json"), "{ not-json }");

            var error = Assert.Throws<CliConfigurationException>(() => CliAppSettings.Load(directory, environmentName: ""));

            Assert.Contains("not valid JSON", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Development_publication_requires_a_loopback_registry()
    {
        var result = await CliTestHost.RunAsync([
            "dev", "registry", "package", "publish-local", "--file", "demo.sunderpkg",
            "--registry-api-url", "https://registry.test/",
        ]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("loopback Registry API URL", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_search_snapshot_preserves_registry_relevance_order()
    {
        var registry = new FakeRegistryClient
        {
            Search = (_, _, _, _) => Task.FromResult<IReadOnlyList<RegistryPackageSummary>>([
                Package("zeta", "2.0.0", "Last"),
                Package("alpha", "1.0.0", "First"),
            ]),
        };

        var result = await CliTestHost.RunAsync(["package", "search"], registry: registry);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("Package  Latest  Summary\nzeta     2.0.0   Last\nalpha    1.0.0   First\n", result.Output);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task Json_result_uses_a_versioned_cli_owned_envelope()
    {
        var registry = new FakeRegistryClient
        {
            Search = (_, _, _, _) => Task.FromResult<IReadOnlyList<RegistryPackageSummary>>([
                Package("alpha", "1.0.0", "First"),
            ]),
        };

        var result = await CliTestHost.RunAsync(["package", "search", "--json"], registry: registry);

        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("package search", root.GetProperty("command").GetString());
        Assert.Equal(CliExitCodes.Success, root.GetProperty("exitCode").GetInt32());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("alpha", root.GetProperty("data")[0].GetProperty("packageId").GetString());
        Assert.Empty(root.GetProperty("errors").EnumerateArray());
        Assert.Empty(result.Error);
        Assert.Equal(1, result.Output.Count(character => character == '\n'));
    }

    [Fact]
    public async Task Progress_is_suppressed_in_json_mode()
    {
        var runtime = new FakeRuntimeClient
        {
            InstallRegistry = (_, _) => Task.FromResult(new RuntimeRegistryPackageChangeResult(
                true, RuntimeRegistryErrorCode.None, "Installed.", true, false, [], [], ["demo"], [])),
        };

        var result = await CliTestHost.RunAsync(["package", "install", "demo", "--json"], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Equal(1, result.Output.Count(character => character == '\n'));
    }

    [Fact]
    public async Task Runtime_problem_details_keep_structured_code_and_correlation_id()
    {
        var runtime = new FakeRuntimeClient
        {
            SystemStatus = _ => throw new RuntimeClientException(
                HttpStatusCode.Unauthorized,
                "Sign-in required",
                detail: null,
                errorCode: "runtime.auth.required",
                correlationId: "correlation-42",
                innerException: null),
        };

        var result = await CliTestHost.RunAsync(["runtime", "status", "--json"], runtime);

        Assert.Equal(CliExitCodes.Authentication, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var error = Assert.Single(document.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("runtime.auth.required", error.GetProperty("code").GetString());
        Assert.Equal("correlation-42", error.GetProperty("correlationId").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CliExitCodes.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, CliExitCodes.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, CliExitCodes.NotFound)]
    [InlineData(HttpStatusCode.Conflict, CliExitCodes.Conflict)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, CliExitCodes.Failure)]
    [InlineData(HttpStatusCode.TooManyRequests, CliExitCodes.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, CliExitCodes.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CliExitCodes.Unavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout, CliExitCodes.Timeout)]
    public void Http_errors_map_to_stable_exit_categories(HttpStatusCode status, int expected)
        => Assert.Equal(expected, CliErrorMapper.FromException(new CliHttpException(status, "failed")));

    [Fact]
    public void Host_http_conflicts_keep_the_stable_conflict_exit_category()
        => Assert.Equal(
            CliExitCodes.Conflict,
            CliErrorMapper.FromException(new HttpRequestException("conflict", null, HttpStatusCode.Conflict)));

    [Theory]
    [InlineData(RuntimeRegistryErrorCode.AuthenticationRequired, CliExitCodes.Authentication)]
    [InlineData(RuntimeRegistryErrorCode.Forbidden, CliExitCodes.Forbidden)]
    [InlineData(RuntimeRegistryErrorCode.NotFound, CliExitCodes.NotFound)]
    [InlineData(RuntimeRegistryErrorCode.Conflict, CliExitCodes.Conflict)]
    [InlineData(RuntimeRegistryErrorCode.RegistryUnavailable, CliExitCodes.Unavailable)]
    public void Typed_registry_errors_map_to_stable_exit_categories(RuntimeRegistryErrorCode code, int expected)
        => Assert.Equal(expected, CliErrorMapper.FromRegistryCode(code));

    [Fact]
    public void Typed_publish_conflict_uses_the_stable_conflict_exit_code()
    {
        var output = new CliOutput(TextWriter.Null, TextWriter.Null, json: false);
        var response = new RegistryPublishPackageResponse(false, "demo", "1.0.0", "Already exists.", [], ["Already exists."])
        {
            ErrorCode = RegistryV1ErrorCodes.PackageVersionExists,
        };

        Assert.Equal(CliExitCodes.Conflict, CliRenderers.PackagePublish(output, response));
    }

    [Theory]
    [InlineData(RegistryV1ErrorCodes.Unauthorized, CliExitCodes.Authentication)]
    [InlineData(RegistryV1ErrorCodes.Forbidden, CliExitCodes.Forbidden)]
    [InlineData(RegistryV1ErrorCodes.RateLimited, CliExitCodes.Unavailable)]
    [InlineData(RegistryV1ErrorCodes.RequestTooLarge, CliExitCodes.Failure)]
    public void Typed_publish_failures_with_missing_collections_keep_stable_exit_categories(
        string errorCode,
        int expectedExitCode)
    {
        var output = new CliOutput(TextWriter.Null, TextWriter.Null, json: false);
        var response = new RegistryPublishPackageResponse(
            false,
            null,
            null,
            "Publication failed.",
            null!,
            null!)
        {
            ErrorCode = errorCode,
        };

        Assert.Equal(expectedExitCode, CliRenderers.PackagePublish(output, response));
    }

    [Fact]
    public async Task Cancellation_returns_shell_standard_exit_code()
    {
        var runtime = new FakeRuntimeClient
        {
            SystemStatus = async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            },
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        var result = await CliTestHost.RunAsync(["runtime", "status"], runtime, token: cancellation.Token);

        Assert.Equal(CliExitCodes.Cancelled, result.ExitCode);
        Assert.Equal("Operation cancelled.\n", result.Error);
    }

    [Fact]
    public async Task Timeout_returns_stable_exit_code()
    {
        var runtime = new FakeRuntimeClient
        {
            SystemStatus = async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            },
        };

        var result = await CliTestHost.RunAsync(["runtime", "status", "--timeout", "0.01s"], runtime);

        Assert.Equal(CliExitCodes.Timeout, result.ExitCode);
        Assert.Equal("Operation timed out. Use --timeout <duration> to increase the request timeout.\n", result.Error);
    }

    [Fact]
    public async Task Transport_cancellation_maps_to_timeout_without_owning_the_cli_token()
    {
        var runtime = new FakeRuntimeClient
        {
            SystemStatus = _ => throw new TaskCanceledException("Transport timed out."),
        };

        var result = await CliTestHost.RunAsync(["runtime", "status"], runtime);

        Assert.Equal(CliExitCodes.Timeout, result.ExitCode);
    }

    [Fact]
    public async Task Server_messages_are_redacted_and_html_is_not_rendered()
    {
        var runtime = new FakeRuntimeClient
        {
            InstallRegistry = (_, _) => Task.FromResult(new RuntimeRegistryPackageChangeResult(
                false,
                RuntimeRegistryErrorCode.InternalError,
                "failed",
                false,
                false,
                [],
                ["access_token=super-secret", "<html><body>proxy failure</body></html>"],
                [],
                [])),
        };

        var result = await CliTestHost.RunAsync(["package", "install", "demo"], runtime);

        Assert.DoesNotContain("super-secret", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy failure", result.Error, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.Error, StringComparison.Ordinal);
        Assert.Contains("untrusted HTML", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_result_messages_are_emitted_once()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var output = new CliOutput(stdout, stderr, json: false);

        output.Error("same result");
        output.Error("same result");
        output.Success("same success");
        output.Success("same success");

        Assert.Equal("same result\n", stderr.ToString().Replace("\r\n", "\n"));
        Assert.Equal("same success\n", stdout.ToString().Replace("\r\n", "\n"));
    }

    [Fact]
    public void Json_secret_properties_are_always_redacted()
    {
        var writer = new StringWriter();
        var output = new CliOutput(writer, TextWriter.Null, json: true);

        output.Data(new { accessToken = "super-secret", value = "safe" });
        output.Complete(CliExitCodes.Success);

        Assert.DoesNotContain("super-secret", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_help_uses_the_same_versioned_envelope()
    {
        var result = await CliTestHost.RunAsync(["stack", "download", "--help", "--json"]);

        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("stack download", document.RootElement.GetProperty("command").GetString());
        Assert.Contains("sunder stack download", document.RootElement.GetProperty("data").GetProperty("help").GetString(), StringComparison.Ordinal);
    }

    private static RegistryPackageSummary Package(string id, string version, string summary)
        => new(id, id, summary, version, null, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
}
