using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli.Tests;

public sealed class RuntimeResetCommandTests
{
    [Fact]
    public async Task Reset_requires_explicit_yes()
    {
        var result = await CliTestHost.RunAsync(["runtime", "reset"]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("--yes", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_rejects_unknown_scope()
    {
        var result = await CliTestHost.RunAsync(["runtime", "reset", "--scope", "everything", "--yes"]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("startup-cache", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_cache_scope_uses_only_the_scoped_local_reset()
    {
        var calls = new List<string>();
        var runtime = new FakeRuntimeClient
        {
            ResetPrepare = _ =>
            {
                calls.Add("prepare");
                return Task.FromResult(new RuntimeResetChallengeResponse("one-time", DateTimeOffset.UtcNow.AddSeconds(30))
                {
                    SupportsBoundStateRoot = true,
                });
            },
            LocalReset = (_, _) => throw new InvalidOperationException("The full reset must not run."),
            StartupCacheReset = (challenge, _) =>
            {
                Assert.Equal("one-time", challenge.Challenge);
                calls.Add("drain");
                calls.Add("startup-cache");
                return Task.FromResult(new RuntimeV1ResetResult([
                    new("startup-cache", "reset", ["cache/package-projections/r1"]),
                ]));
            },
        };

        var result = await CliTestHost.RunAsync(
            ["runtime", "reset", "--scope", "startup-cache", "--yes"],
            runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(["prepare", "drain", "startup-cache"], calls);
        Assert.Contains("startup-cache: reset", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_uses_one_time_runtime_challenge_before_local_deletion()
    {
        var calls = new List<string>();
        var runtime = new FakeRuntimeClient
        {
            ResetPrepare = _ =>
            {
                calls.Add("prepare");
                return Task.FromResult(new RuntimeResetChallengeResponse("one-time", DateTimeOffset.UtcNow.AddSeconds(30))
                {
                    SupportsBoundStateRoot = true,
                });
            },
            LocalReset = (challenge, _) =>
            {
                Assert.Equal("one-time", challenge.Challenge);
                calls.Add("drain");
                calls.Add("delete");
                return Task.FromResult(new RuntimeV1ResetResult([
                    new("installed-catalog-and-payloads", "reset", ["catalog/installed-packages.json", "installed"]),
                    new("package-transactions-and-derived-state", "reset", ["staging", "transactions"]),
                    new("uploads-and-snapshots", "reset"),
                    new("runtime-connection", "reset"),
                ], "reset-receipts/receipt.json"));
            },
        };

        var result = await CliTestHost.RunAsync(["runtime", "reset", "--yes"], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(["prepare", "drain", "delete"], calls);
        Assert.Contains("catalog/installed-packages.json", result.Output, StringComparison.Ordinal);
        Assert.Contains("reset-receipts/receipt.json", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:6123/")]
    [InlineData("https://runtime.example.test/")]
    public async Task Custom_runtime_url_is_rejected_before_drain_or_local_deletion(string runtimeUrl)
    {
        var calls = new List<string>();
        var runtime = new FakeRuntimeClient
        {
            ResetPrepare = _ =>
            {
                calls.Add("prepare");
                throw new InvalidOperationException("A custom Runtime must not be contacted for local reset.");
            },
            LocalReset = (_, _) =>
            {
                calls.Add("drain-and-delete");
                throw new InvalidOperationException("Default local state must not be deleted for a custom Runtime.");
            },
        };

        var result = await CliTestHost.RunAsync(
            ["runtime", "reset", "--yes"],
            runtime,
            runtimeUrl: runtimeUrl);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Empty(calls);
        Assert.Contains("default local Host", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partial_reset_is_reported_and_can_be_retried()
    {
        var runtime = new FakeRuntimeClient
        {
            LocalReset = (_, _) => Task.FromResult(new RuntimeV1ResetResult([
                new("installed-catalog-and-payloads", "reset"),
                new("package-transactions-and-derived-state", "partial"),
                new("uploads-and-snapshots", "already-empty"),
                new("runtime-connection", "reset"),
            ])),
        };

        var result = await CliTestHost.RunAsync(["runtime", "reset", "--yes"], runtime);

        Assert.Equal(CliExitCodes.Failure, result.ExitCode);
        Assert.Contains("package-transactions-and-derived-state: partial", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_transport_timeout_is_not_treated_as_an_offline_reset()
    {
        var localResetCalled = false;
        var runtime = new FakeRuntimeClient
        {
            ResetPrepare = _ => throw new HttpRequestException("Runtime request timed out.", new TimeoutException()),
            LocalReset = (_, _) =>
            {
                localResetCalled = true;
                throw new InvalidOperationException("Offline reset must not run after a transport timeout.");
            },
        };

        var result = await CliTestHost.RunAsync(["runtime", "reset", "--yes"], runtime);

        Assert.Equal(CliExitCodes.Timeout, result.ExitCode);
        Assert.False(localResetCalled);
    }

    [Fact]
    public async Task Drain_failure_after_prepare_does_not_delete_local_state()
    {
        var runtime = new FakeRuntimeClient
        {
            LocalReset = (_, _) => throw new HttpRequestException("Reset drain response was lost."),
        };

        var result = await CliTestHost.RunAsync(["runtime", "reset", "--yes"], runtime);

        Assert.NotEqual(CliExitCodes.Success, result.ExitCode);
    }

    [Fact]
    public async Task Expired_challenge_does_not_fall_back_to_offline_deletion()
    {
        var localResetCalled = false;
        var runtime = new FakeRuntimeClient
        {
            ResetPrepare = _ => Task.FromResult(new RuntimeResetChallengeResponse(
                "expired",
                DateTimeOffset.UtcNow.AddSeconds(-1))),
            LocalReset = (_, _) =>
            {
                localResetCalled = true;
                throw new InvalidOperationException("Local reset must not run after challenge expiration.");
            },
        };

        var result = await CliTestHost.RunAsync(["runtime", "reset", "--yes"], runtime);

        Assert.NotEqual(CliExitCodes.Success, result.ExitCode);
        Assert.False(localResetCalled);
    }

    [Fact]
    public async Task Unreachable_prepare_fails_closed_without_local_deletion()
    {
        var localResetCalled = false;
        var runtime = new FakeRuntimeClient
        {
            ResetPrepare = _ => throw new HttpRequestException("Runtime is offline."),
            LocalReset = (_, _) =>
            {
                localResetCalled = true;
                throw new InvalidOperationException("Local reset must not run without a bound Host root.");
            },
        };

        var result = await CliTestHost.RunAsync(["runtime", "reset", "--yes"], runtime);

        Assert.NotEqual(CliExitCodes.Success, result.ExitCode);
        Assert.False(localResetCalled);
    }
}
