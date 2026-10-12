using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli.Tests;

public sealed class ExecutionRuntimeCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlockedPackageGuidanceShowsRuntimeFirstWithoutPrintingSourceSecrets(bool registry)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var output = new CliOutput(stdout, stderr, json: false);
        var hash = new string('a', 64);
        var prerequisites = new RuntimePackageRuntimePrerequisitePlan(true, false, "execution-runtime.missing", "Install runtime first.", hash, "osx-arm64",
            [new(new("example.package", "1.0.0", hash, "runtime", "osx-arm64"), hash,
                new("sunder.runtime.dotnet", ">=10.0.0 <11.0.0", "sunder.dotnet-worker-module.v1"), false, "execution-runtime.missing", null,
                new(false, "unavailable", "Host=private;Password=do-not-print", null, null, true))], []);
        if (registry) CliRenderers.RegistryPackageChange(output,
            new(false, RuntimeRegistryErrorCode.RuntimePrerequisitesRequired, "Runtime is required.", false, false, [], [], [], []) { RuntimePrerequisites = prerequisites });
        else CliRenderers.PackageOperation(output, new(false, "Runtime is required.", false, false, [], []) { RuntimePrerequisites = prerequisites });
        Assert.Contains("runtime first", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sunder runtime install sunder.runtime.dotnet", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-print", stdout.ToString() + stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_settings_uses_host_revision_and_false_is_not_a_missing_option()
    {
        RuntimeExecutionRuntimeSettingsUpdateRequest? captured = null;
        var runtime = new FakeRuntimeClient
        {
            RuntimeSettings = _ => Task.FromResult(new RuntimeExecutionRuntimeSettings(19)),
            SaveRuntimeSettings = (request, _) => { captured = request; return Task.FromResult(new RuntimeExecutionRuntimeSettings(20, false)); },
        };
        var result = await CliTestHost.RunAsync(["runtime", "settings", "--automatic-updates", "false"], runtime);
        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(new RuntimeExecutionRuntimeSettingsUpdateRequest(19, false), captured);
    }

    [Fact]
    public async Task Runtime_install_preview_does_not_acquire_without_yes_and_confirms_exact_fingerprint()
    {
        var fingerprint = new string('a', 64);
        var applied = 0;
        var runtime = new FakeRuntimeClient
        {
            RuntimeInstallPreview = (request, _) => Task.FromResult(new RuntimeExecutionRuntimeInstallPlan(true, null, "Preview", new("plan", fingerprint, DateTimeOffset.UtcNow.AddMinutes(1), "osx-arm64"), null, request.RetainUntilUninstall)),
            RuntimeInstallApply = (request, _) =>
            {
                Assert.Equal(new RuntimeExecutionRuntimePlanConfirmation("plan", fingerprint), request);
                applied++;
                return Task.FromResult(Success(RuntimeExecutionRuntimeOperationActions.Install));
            },
        };
        string[] arguments = ["runtime", "install", "sunder.runtime.dotnet", ">=10.0.0 <11.0.0", "sunder.dotnet-worker-module.v1"];
        Assert.Equal(CliExitCodes.Success, (await CliTestHost.RunAsync(arguments, runtime)).ExitCode);
        Assert.Equal(0, applied);
        Assert.Equal(CliExitCodes.Success, (await CliTestHost.RunAsync([.. arguments, "--yes"], runtime)).ExitCode);
        Assert.Equal(1, applied);
    }

    [Fact]
    public async Task Ensure_delegates_requirement_to_runtime()
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
            "runtime", "info", "dotnet", "sunder.runtime.dotnet", "8.0.1", "linux-x64", "sunder.dotnet-worker-module.v1", "sha512", new string('b', 128),
        ], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("sunder.runtime.dotnet", captured?.RuntimeId);
        Assert.Equal(ConfiguredOfficialSourceId.DotNet, captured?.SourceId);
        Assert.Equal(new string('b', 128), captured?.VendorChecksumDigest);
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
            "runtime", "repair", "dotnet", "sunder.runtime.dotnet", "8.0.1", "linux-x64", "sunder.dotnet-worker-module.v1", "sha512", new string('b', 128), "--yes",
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
            "runtime", "verify", "dotnet", "sunder.runtime.dotnet", "8.0.1", "linux-x64", "sunder.dotnet-worker-module.v1", "sha512", new string('b', 128),
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
                ConfiguredOfficialSourceId.DotNet,
                "sunder.runtime.dotnet",
                "8.0.1",
                "linux-x64",
                "sunder.dotnet-worker-module.v1",
                RuntimeExecutionRuntimeChecksumAlgorithm.Sha512,
                new string('b', 128)),
            RuntimeExecutionRuntimeStatuses.Ready,
            1024,
            1,
            InstalledArtifact(),
            new RuntimeExecutionRuntimeHealth(RuntimeExecutionRuntimeStatuses.Ready, now, false, null),
            new RuntimeExecutionRuntimeSupportStatus(RuntimeExecutionRuntimeSupportState.Supported, "8.0", now, null, null),
            now);
    }

    private static RuntimeExecutionRuntimeInstalledArtifact InstalledArtifact()
        => new(
            new string('c', 64),
            1024,
            new string('d', 64),
            new RuntimeExecutionRuntimeProvenance(
                new RuntimeExecutionRuntimeDistribution(
                    "https://download.example/dotnet.tar.gz",
                    "https://metadata.example/dotnet.json",
                    new string('e', 64),
                    null,
                    null,
                    RuntimeExecutionRuntimeArchiveFormat.TarGzip,
                    "dotnet-runtime-v1",
                    1024),
                "https://download.example/dotnet.tar.gz",
                DateTimeOffset.UnixEpoch));
}
