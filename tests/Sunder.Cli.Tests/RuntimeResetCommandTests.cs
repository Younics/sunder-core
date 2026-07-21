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
    public async Task Reset_uses_one_time_runtime_challenge_before_local_deletion()
    {
        var calls = new List<string>();
        var runtime = new FakeRuntimeClient
        {
            ResetPrepare = _ =>
            {
                calls.Add("prepare");
                return Task.FromResult(new RuntimeResetChallengeResponse("one-time", DateTimeOffset.UtcNow.AddSeconds(30)));
            },
            ResetDrain = (challenge, _) =>
            {
                Assert.Equal("one-time", challenge);
                calls.Add("drain");
                return Task.FromResult(new RuntimeResetDrainResponse([]));
            },
            LocalReset = _ =>
            {
                calls.Add("delete");
                return Task.FromResult(new RuntimeV1ResetResult([
                    new("package-catalog-and-payloads", "reset"),
                    new("package-state-files-secrets-and-logs", "reset"),
                    new("uploads-and-snapshots", "reset"),
                    new("registry-credentials", "reset"),
                    new("runtime-connection", "reset"),
                    new("runtime-v1-root", "reset"),
                ]));
            },
        };

        var result = await CliTestHost.RunAsync(["runtime", "reset", "--yes"], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(["prepare", "drain", "delete"], calls);
        Assert.DoesNotContain("/", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partial_reset_is_reported_and_can_be_retried()
    {
        var runtime = new FakeRuntimeClient
        {
            LocalReset = _ => Task.FromResult(new RuntimeV1ResetResult([
                new("package-catalog-and-payloads", "reset"),
                new("package-state-files-secrets-and-logs", "partial"),
                new("uploads-and-snapshots", "already-empty"),
                new("registry-credentials", "reset"),
                new("runtime-connection", "reset"),
                new("runtime-v1-root", "partial"),
            ])),
        };

        var result = await CliTestHost.RunAsync(["runtime", "reset", "--yes"], runtime);

        Assert.Equal(CliExitCodes.Failure, result.ExitCode);
        Assert.Contains("package-state-files-secrets-and-logs: partial", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_transport_timeout_is_not_treated_as_an_offline_reset()
    {
        var localResetCalled = false;
        var runtime = new FakeRuntimeClient
        {
            ResetPrepare = _ => throw new HttpRequestException("Runtime request timed out.", new TimeoutException()),
            LocalReset = _ =>
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
        var localResetCalled = false;
        var runtime = new FakeRuntimeClient
        {
            ResetDrain = (_, _) => throw new HttpRequestException("Reset drain response was lost."),
            LocalReset = _ =>
            {
                localResetCalled = true;
                throw new InvalidOperationException("Local reset must not run after ambiguous drain failure.");
            },
        };

        var result = await CliTestHost.RunAsync(["runtime", "reset", "--yes"], runtime);

        Assert.NotEqual(CliExitCodes.Success, result.ExitCode);
        Assert.False(localResetCalled);
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
            LocalReset = _ =>
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
    public async Task Unreachable_prepare_uses_offline_lease_reset()
    {
        var localResetCalled = false;
        var runtime = new FakeRuntimeClient
        {
            ResetPrepare = _ => throw new HttpRequestException("Runtime is offline."),
            LocalReset = _ =>
            {
                localResetCalled = true;
                return Task.FromResult(new RuntimeV1ResetResult([
                    new("runtime-v1-root", "already-empty"),
                ]));
            },
        };

        var result = await CliTestHost.RunAsync(["runtime", "reset", "--yes"], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.True(localResetCalled);
    }
}
