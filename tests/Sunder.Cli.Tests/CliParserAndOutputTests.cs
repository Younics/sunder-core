using System.Net;
using System.Text.Json;
using Sunder.Cli;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;

namespace Sunder.Cli.Tests;

public sealed class CliParserAndOutputTests
{
    [Fact]
    public async Task Invalid_command_returns_usage_exit_code()
    {
        var result = await CliTestHost.RunAsync(["wat"]);
        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal("Unknown command 'wat'.\n", result.Error);
    }

    [Fact]
    public async Task Removed_combined_registry_url_alias_is_rejected()
    {
        var result = await CliTestHost.RunAsync(["system", "status", "--registry-url", "https://old.test/"]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.DoesNotContain("--registry-url", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_take_is_clamped_to_registry_page_bound()
    {
        var observedTake = 0;
        var registry = new FakeRegistryClient
        {
            Search = (_, _, take, _) =>
            {
                observedTake = take;
                return Task.FromResult<IReadOnlyList<RegistryPackageSummary>>([]);
            },
        };

        var result = await CliTestHost.RunAsync(["search", "--take", "100000"], registry: registry);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(100, observedTake);
    }

    [Fact]
    public void Global_urls_reject_non_http_schemes()
    {
        var error = Assert.Throws<ArgumentException>(() => CliCommandParser.Parse([
            "system", "status",
            "--registry-api-url", "file:///tmp/registry",
            "--registry-web-url", "https://registry.test/",
            "--runtime-url", "http://runtime.test/",
        ]));

        Assert.Contains("only HTTP and HTTPS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conflicting_install_options_are_parse_errors()
    {
        var result = await CliTestHost.RunAsync(["install", "demo", "--version", "1.0.0", "--tag", "latest"]);
        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("either '--version' or '--tag'", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("install", "Package.Owner", "--version", "1.0.0")]
    [InlineData("install", "package.owner", "--version", "1.0")]
    public async Task Package_commands_use_canonical_sdk_identity_validation(params string[] arguments)
    {
        var result = await CliTestHost.RunAsync(arguments);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
    }

    [Fact]
    public async Task Typed_http_errors_map_to_stable_exit_codes()
    {
        var runtime = new FakeRuntimeClient
        {
            SystemStatus = _ => throw new RuntimeClientException(HttpStatusCode.Unauthorized, "Sign-in required"),
        };
        var result = await CliTestHost.RunAsync(["system", "status"], runtime);
        Assert.Equal(CliExitCodes.Authentication, result.ExitCode);
        Assert.Equal("Sign-in required\n", result.Error);
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
        var result = await CliTestHost.RunAsync(["system", "status"], runtime, token: cancellation.Token);
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
        var result = await CliTestHost.RunAsync(["system", "status", "--timeout", "0.01s"], runtime);
        Assert.Equal(CliExitCodes.Timeout, result.ExitCode);
        Assert.Equal("Operation timed out. Use --timeout <duration> to increase the request timeout.\n", result.Error);
    }

    [Fact]
    public async Task Text_search_snapshot_is_sorted_and_stable()
    {
        var registry = new FakeRegistryClient
        {
            Search = (_, _, _, _) => Task.FromResult<IReadOnlyList<RegistryPackageSummary>>([
                Package("zeta", "2.0.0", "Last"),
                Package("alpha", "1.0.0", "First"),
            ]),
        };
        var result = await CliTestHost.RunAsync(["search"], registry: registry);
        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("Package  Latest  Summary\nalpha    1.0.0   First\nzeta     2.0.0   Last\n", result.Output);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public async Task Json_search_snapshot_is_one_document()
    {
        var registry = new FakeRegistryClient
        {
            Search = (_, _, _, _) => Task.FromResult<IReadOnlyList<RegistryPackageSummary>>([Package("alpha", "1.0.0", "First")]),
        };
        var result = await CliTestHost.RunAsync(["search", "--json"], registry: registry);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(0, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("alpha", document.RootElement.GetProperty("data")[0].GetProperty("packageId").GetString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(1, result.Output.Count(character => character == '\n'));
    }

    [Fact]
    public async Task Server_messages_are_redacted_and_html_is_not_rendered()
    {
        var runtime = new FakeRuntimeClient
        {
            InstallRegistry = (_, _) => Task.FromResult(new Sunder.Runtime.Contracts.RuntimeRegistryPackageChangeResult(
                false,
                Sunder.Runtime.Contracts.RuntimeRegistryErrorCode.InternalError,
                "failed",
                false,
                false,
                [],
                ["access_token=super-secret", "<html><body>proxy failure</body></html>"],
                [],
                [])),
        };
        var result = await CliTestHost.RunAsync(["install", "demo"], runtime);
        Assert.DoesNotContain("super-secret", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy failure", result.Error, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.Error, StringComparison.Ordinal);
        Assert.Contains("untrusted HTML", result.Error, StringComparison.Ordinal);
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
    public void Help_contains_every_supported_command_usage()
    {
        string[] expected =
        [
            "sunder system status", "sunder auth login", "sunder auth status", "sunder auth logout",
            "sunder search", "sunder info", "sunder list", "sunder install", "sunder update",
            "sunder publish", "sunder yank", "sunder unyank", "sunder deprecate", "sunder undeprecate",
            "sunder dist-tag list", "sunder dist-tag set", "sunder dist-tag delete", "sunder package validate",
            "sunder validate", "sunder stack search", "sunder stack info", "sunder stack download",
            "sunder stack publish", "sunder stack update", "sunder stack delete", "sunder stack use",
            "sunder stack inspect", "sunder stack validate",
        ];
        foreach (var usage in expected) Assert.Contains(usage, CliUsage.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_help_contains_complete_usage()
    {
        var result = await CliTestHost.RunAsync(["--help", "--json"]);
        using var document = JsonDocument.Parse(result.Output);
        var usage = document.RootElement.GetProperty("data").GetProperty("usage").GetString();
        Assert.Contains("sunder stack validate <stack.sunderstack>", usage, StringComparison.Ordinal);
        Assert.Contains("--help, -h", usage, StringComparison.Ordinal);
    }

    private static RegistryPackageSummary Package(string id, string version, string summary)
        => new(id, id, summary, version, null, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
}
