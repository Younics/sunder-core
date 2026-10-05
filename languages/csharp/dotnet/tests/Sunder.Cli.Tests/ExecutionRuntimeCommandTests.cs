using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli.Tests;

public sealed class ExecutionRuntimeCommandTests
{
    [Fact]
    public async Task Ensure_delegates_requirement_and_configured_registry_origin_to_runtime()
    {
        RuntimeExecutionRuntimeEnsureRequest? captured = null;
        var runtime = new FakeRuntimeClient
        {
            EnsureExecutionRuntime = (request, _) =>
            {
                captured = request;
                return Task.FromResult(Success(RuntimeExecutionRuntimeOperationActions.Ensure));
            },
        };

        var result = await CliTestHost.RunAsync([
            "runtime", "ensure", "sunder.runtime.dotnet", ">=8.0.0 <9.0.0", "sunder.dotnet-worker-module.v1",
        ], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("https://registry.test/", captured?.RegistryOrigin);
        Assert.Equal("sunder.runtime.dotnet", captured?.Requirement.RuntimeId);
        Assert.Equal(">=8.0.0 <9.0.0", captured?.Requirement.VersionRange);
        Assert.Equal("sunder.dotnet-worker-module.v1", captured?.Requirement.ModuleAbi);
    }

    [Fact]
    public async Task Info_delegates_exact_identity_and_lists_dependent_packages()
    {
        RuntimeExecutionRuntimeIdentity? captured = null;
        var summary = Summary();
        var runtime = new FakeRuntimeClient
        {
            ExecutionRuntime = (identity, _) =>
            {
                captured = identity;
                return Task.FromResult(summary);
            },
            ExecutionRuntimeReferences = (_, _) => Task.FromResult<IReadOnlyList<RuntimeExecutionRuntimePackageReference>>([
                new("demo.package", "2.0.0", "content", "worker", "linux-x64"),
            ]),
        };

        var result = await CliTestHost.RunAsync([
            "runtime", "info", "sunder.runtime.dotnet", "8.0.1", "2", "linux-x64", "sunder.dotnet-worker-module.v1",
        ], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("sunder.runtime.dotnet", captured?.RuntimeId);
        Assert.Equal(2, captured?.PackagingRevision);
        Assert.Contains("demo.package 2.0.0", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repair_delegates_exact_identity_after_explicit_confirmation()
    {
        RuntimeExecutionRuntimeIdentity? captured = null;
        var runtime = new FakeRuntimeClient
        {
            RepairExecutionRuntime = (identity, _) =>
            {
                captured = identity;
                return Task.FromResult(Success(RuntimeExecutionRuntimeOperationActions.Repair) with
                {
                    Runtime = Summary(),
                });
            },
        };

        var result = await CliTestHost.RunAsync([
            "runtime", "repair", "sunder.runtime.dotnet", "8.0.1", "2", "linux-x64", "sunder.dotnet-worker-module.v1", "--yes",
        ], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("linux-x64", captured?.Rid);
        Assert.Contains("Completed.", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Typed_runtime_failure_has_stable_not_found_exit_semantics()
    {
        var runtime = new FakeRuntimeClient
        {
            VerifyExecutionRuntime = (_, _) => Task.FromResult(new RuntimeExecutionRuntimeOperationResult(
                false,
                "execution-runtime.missing",
                "The exact execution runtime is not installed.",
                RuntimeExecutionRuntimeOperationActions.Verify,
                Runtime: null,
                AlreadyPresent: false)),
        };

        var result = await CliTestHost.RunAsync([
            "runtime", "verify", "sunder.runtime.dotnet", "8.0.1", "2", "linux-x64", "sunder.dotnet-worker-module.v1",
        ], runtime);

        Assert.Equal(CliExitCodes.NotFound, result.ExitCode);
        Assert.Contains("not installed", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolution_conflict_has_stable_conflict_exit_semantics()
    {
        var runtime = new FakeRuntimeClient
        {
            EnsureExecutionRuntime = (_, _) => Task.FromResult(new RuntimeExecutionRuntimeOperationResult(
                false,
                ExecutionRuntimeResolutionConflictCodes.NoCompatibleVersion,
                "No compatible version is available.",
                RuntimeExecutionRuntimeOperationActions.Ensure,
                Runtime: null,
                AlreadyPresent: false)),
        };

        var result = await CliTestHost.RunAsync([
            "runtime", "ensure", "sunder.runtime.dotnet", ">=8.0.0 <9.0.0", "sunder.dotnet-worker-module.v1",
        ], runtime);

        Assert.Equal(CliExitCodes.Conflict, result.ExitCode);
    }

    private static RuntimeExecutionRuntimeOperationResult Success(string action)
        => new(true, null, "Completed.", action, Runtime: null, AlreadyPresent: false);

    private static InstalledExecutionRuntimeSummary Summary()
    {
        var now = DateTimeOffset.UtcNow;
        return new InstalledExecutionRuntimeSummary(
            new RuntimeExecutionRuntimeIdentity(
                "sunder.runtime.dotnet",
                "8.0.1",
                2,
                "linux-x64",
                "sunder.dotnet-worker-module.v1",
                new string('a', 64),
                new string('b', 64)),
            RuntimeExecutionRuntimeStatuses.Ready,
            1024,
            1,
            RuntimeExecutionRuntimeAcquisitionSources.Registry,
            "release-key",
            1,
            new RuntimeExecutionRuntimeHealth(RuntimeExecutionRuntimeStatuses.Ready, now, false, null),
            new RuntimeExecutionRuntimeLifecycle(
                "supported",
                now,
                null,
                null,
                null,
                null,
                null,
                1,
                now),
            now);
    }
}
