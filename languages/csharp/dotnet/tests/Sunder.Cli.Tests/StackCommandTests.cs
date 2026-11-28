using Sunder.Registry.Contracts;

namespace Sunder.Cli.Tests;

public sealed class StackCommandTests
{
    [Fact]
    public async Task Stack_search_preserves_registry_relevance_order()
    {
        var registry = new FakeRegistryClient
        {
            SearchStacks = (_, _, _, _) => Task.FromResult<IReadOnlyList<RegistryStackSummary>>([
                Summary("zeta", "Best match"),
                Summary("alpha", "Second match"),
            ]),
        };

        var result = await CliTestHost.RunAsync(["stack", "search", "match"], registry: registry);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.True(result.Output.IndexOf("zeta", StringComparison.Ordinal) < result.Output.IndexOf("alpha", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stack_download_output_is_an_exact_file_and_force_is_forwarded()
    {
        string? capturedPath = null;
        var capturedForce = false;
        var registry = new FakeRegistryClient
        {
            GetStack = (_, _) => Task.FromResult<RegistryStackDetails?>(Details("demo")),
            Download = (_, _, path, force, _) =>
            {
                capturedPath = path;
                capturedForce = force;
                return Task.CompletedTask;
            },
        };
        var output = Path.Combine(Path.GetTempPath(), $"sunder-stack-output-{Guid.NewGuid():N}");

        var result = await CliTestHost.RunAsync(
            ["stack", "download", "demo", "--output", output, "--force"], registry: registry);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(Path.GetFullPath(output), capturedPath);
        Assert.True(capturedForce);
        Assert.Equal("Downloading Stack 'demo'...\n", result.Error);
    }

    [Fact]
    public async Task Stack_download_rejects_a_directory_as_output()
    {
        var registry = new FakeRegistryClient
        {
            GetStack = (_, _) => Task.FromResult<RegistryStackDetails?>(Details("demo")),
        };

        var result = await CliTestHost.RunAsync(
            ["stack", "download", "demo", "--output", Path.GetTempPath()], registry: registry);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("exact destination file", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("demo")]
    [InlineData("another-stack")]
    public async Task Stack_open_preserves_the_requested_hub_and_identity(string returnedId)
    {
        var registry = new FakeRegistryClient
        {
            GetStack = (_, _) => Task.FromResult<RegistryStackDetails?>(Details(returnedId)),
        };
        var browser = new FakeBrowserLauncher();

        var result = await CliTestHost.RunAsync(["stack", "open", "demo", "--registry-api-url", "https://team.example/hub/"],
            registry: registry, browser: browser);

        if (returnedId != "demo")
        {
            Assert.NotEqual(CliExitCodes.Success, result.ExitCode);
            Assert.Null(browser.Opened);
            return;
        }
        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("sunder://stacks/demo?registry=https%3A%2F%2Fteam.example%2Fhub%2F", browser.Opened?.AbsoluteUri);
        Assert.Contains("Opened Stack 'demo'", result.Output, StringComparison.Ordinal);
    }

    private static RegistryStackSummary Summary(string id, string summary)
        => new(id, id, summary, 0, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static RegistryStackDetails Details(string id)
        => new(
            id,
            id,
            null,
            [],
            [],
            [],
            new RegistryStackArtifact("", 0, "/artifact"),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
}
