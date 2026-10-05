using System.Text.Json;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli.Tests;

public sealed class PackagePresentationCommandTests
{
    [Theory]
    [InlineData(false, 50L * 1024L * 1024L)]
    [InlineData(true, 0L)]
    public async Task Registry_install_dry_run_separates_package_full_runtime_and_local_incremental_bytes(
        bool runtimeAlreadyInstalled,
        long expectedIncrementalRuntimeBytes)
    {
        var plan = Plan();
        var runtimeArtifact = Assert.Single(plan.RuntimeArtifacts);
        var applyCalls = 0;
        var runtime = new FakeRuntimeClient
        {
            ResolveRegistry = (_, _) => Task.FromResult(plan),
            ExecutionRuntimes = _ => Task.FromResult<IReadOnlyList<InstalledExecutionRuntimeSummary>>(
                runtimeAlreadyInstalled ? [InstalledRuntime(runtimeArtifact)] : []),
            InstallRegistry = (_, _) =>
            {
                applyCalls++;
                throw new InvalidOperationException("Dry-run preflight must not apply the install.");
            },
        };

        var result = await CliTestHost.RunAsync([
            "package", "install", "demo", "--version", "2.0.0", "--dry-run", "--json",
        ], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(0, applyCalls);
        using var document = JsonDocument.Parse(result.Output);
        var data = document.RootElement.GetProperty("data");
        var preflight = data.GetProperty("preflight");
        Assert.Equal("dry-run", data.GetProperty("mode").GetString());
        Assert.False(data.GetProperty("applied").GetBoolean());
        Assert.Equal(12L * 1024L * 1024L, preflight.GetProperty("packageDownloadBytes").GetInt64());
        Assert.Equal(50L * 1024L * 1024L, preflight.GetProperty("fullRuntimeDownloadBytes").GetInt64());
        Assert.Equal(expectedIncrementalRuntimeBytes, preflight.GetProperty("incrementalRuntimeDownloadBytes").GetInt64());
        Assert.Equal(
            12L * 1024L * 1024L + expectedIncrementalRuntimeBytes,
            preflight.GetProperty("incrementalDownloadBytes").GetInt64());
        var runtimeDownload = Assert.Single(preflight.GetProperty("runtimeDownloads").EnumerateArray());
        Assert.Equal(runtimeAlreadyInstalled, runtimeDownload.GetProperty("alreadyInstalledAndHealthy").GetBoolean());
        Assert.Equal("supported", runtimeDownload.GetProperty("lifecycle").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Registry_install_conflicts_are_structured_and_block_apply()
    {
        var requirement = Requirement();
        var plan = new RuntimeRegistryResolveInstallPlanResponse(
            false,
            [],
            [],
            ["Package graph could not be resolved."],
            [new RuntimeRegistryPackageInstallPlanConflict(
                "demo",
                "1.0.0",
                ">=2.0.0",
                "dependent",
                "registry.v1.resource.conflict",
                "Installed demo conflicts with dependent.")],
            [],
            RuntimeConflicts: [new RuntimeRegistryExecutionRuntimeResolutionConflict(
                ExecutionRuntimeResolutionConflictCodes.RidUnavailable,
                "No runtime artifact is available for win-x64.",
                requirement,
                "win-x64",
                "demo",
                "runtime")]);
        var applyCalls = 0;
        var runtime = new FakeRuntimeClient
        {
            ResolveRegistry = (_, _) => Task.FromResult(plan),
            InstallRegistry = (_, _) =>
            {
                applyCalls++;
                throw new InvalidOperationException("A conflicted preflight must not be applied.");
            },
        };

        var result = await CliTestHost.RunAsync([
            "package", "install", "demo", "--yes", "--json",
        ], runtime);

        Assert.Equal(CliExitCodes.Conflict, result.ExitCode);
        Assert.Equal(0, applyCalls);
        using var document = JsonDocument.Parse(result.Output);
        var preflight = document.RootElement.GetProperty("data").GetProperty("preflight");
        Assert.Equal(
            "registry.v1.resource.conflict",
            Assert.Single(preflight.GetProperty("conflicts").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal(
            ExecutionRuntimeResolutionConflictCodes.RidUnavailable,
            Assert.Single(preflight.GetProperty("runtimeConflicts").EnumerateArray()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Registry_install_yes_renders_preflight_before_one_apply_and_keeps_it_in_json()
    {
        var plan = Plan();
        var events = new List<string>();
        var runtime = new FakeRuntimeClient
        {
            ResolveRegistry = (_, _) =>
            {
                events.Add("preflight");
                return Task.FromResult(plan);
            },
            InstallRegistry = (_, _) =>
            {
                events.Add("apply");
                return Task.FromResult(Success("Installed."));
            },
        };

        var result = await CliTestHost.RunAsync([
            "package", "install", "demo", "--yes", "--json",
        ], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(["preflight", "apply"], events);
        using var document = JsonDocument.Parse(result.Output);
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("applied").GetBoolean());
        Assert.Equal(12L * 1024L * 1024L, data.GetProperty("preflight").GetProperty("packageDownloadBytes").GetInt64());
        Assert.True(data.GetProperty("result").GetProperty("success").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registry_install_stale_preview_has_clear_human_and_stable_json_errors(bool json)
    {
        const string message =
            "The Registry package plan changed after it was previewed. Review the current plan and retry; no package changes were applied.";
        var plan = Plan();
        var runtime = new FakeRuntimeClient
        {
            ResolveRegistry = (_, _) => Task.FromResult(plan),
            InstallRegistry = (request, _) =>
            {
                Assert.Equal(plan.ResolutionFingerprint, request.ExpectedResolutionFingerprint);
                return Task.FromResult(new RuntimeRegistryPackageChangeResult(
                    false,
                    RuntimeRegistryErrorCode.StalePlan,
                    message,
                    false,
                    false,
                    [],
                    [message],
                    [],
                    []));
            },
        };
        var arguments = new List<string> { "package", "install", "demo", "--yes" };
        if (json)
        {
            arguments.Add("--json");
        }

        var result = await CliTestHost.RunAsync(arguments.ToArray(), runtime);

        Assert.Equal(CliExitCodes.Conflict, result.ExitCode);
        if (!json)
        {
            Assert.Contains(message, result.Error, StringComparison.Ordinal);
            return;
        }
        using var document = JsonDocument.Parse(result.Output);
        var applied = document.RootElement.GetProperty("data").GetProperty("result");
        Assert.Equal("stale_plan", applied.GetProperty("errorCode").GetString());
        Assert.Equal(message, applied.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Registry_update_uses_exact_runtime_preflight_before_apply()
    {
        var plan = Plan(currentVersion: "1.0.0", actionKind: RegistryPackageActionKinds.PackageUpdate);
        var requests = new List<RuntimeRegistryUpdateRequest>();
        var planCalls = 0;
        var runtime = new FakeRuntimeClient
        {
            Installed = _ => Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>([ManagedPackage()]),
            UpdateRegistry = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(request.DryRun
                    ? new RuntimeRegistryPackageChangeResult(
                        true,
                        RuntimeRegistryErrorCode.None,
                        "Previewed.",
                        false,
                        false,
                        [],
                        [],
                        [],
                        plan.Items)
                    {
                        PlanExpectations =
                        [
                            new RuntimeRegistryPackagePlanExpectation(
                                "https://registry.test/",
                                false,
                                plan.ResolutionFingerprint),
                        ],
                        RuntimeArtifacts = plan.RuntimeArtifacts,
                    }
                    : Success("Updated."));
            },
            ResolveRegistry = (_, _) =>
            {
                planCalls++;
                return Task.FromResult(plan);
            },
        };

        var result = await CliTestHost.RunAsync([
            "package", "update", "demo", "--yes", "--json",
        ], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Collection(
            requests,
            request => Assert.True(request.DryRun),
            request =>
            {
                Assert.False(request.DryRun);
                var expected = Assert.Single(request.ExpectedPlans);
                Assert.Equal("https://registry.test/", expected.RegistryOrigin);
                Assert.False(expected.IncludePrerelease);
                Assert.Equal(plan.ResolutionFingerprint, expected.ExpectedResolutionFingerprint);
            });
        Assert.Equal(0, planCalls);
        using var document = JsonDocument.Parse(result.Output);
        var preflight = document.RootElement.GetProperty("data").GetProperty("preflight");
        Assert.Equal(12L * 1024L * 1024L, preflight.GetProperty("packageDownloadBytes").GetInt64());
        Assert.Equal(50L * 1024L * 1024L, preflight.GetProperty("incrementalRuntimeDownloadBytes").GetInt64());
        Assert.Equal("package-update", Assert.Single(preflight.GetProperty("plan").EnumerateArray()).GetProperty("actionKind").GetString());
    }

    [Fact]
    public async Task Registry_source_adoption_displays_and_applies_the_same_source_fenced_preview()
    {
        const string origin = "https://other.example/";
        var consent = new PackageCapabilityConsent(
            "demo",
            "2.0.0",
            new string('c', 64),
            ["docker-environment.v1"]);
        var plan = Plan(currentVersion: "1.0.0", actionKind: RegistryPackageActionKinds.PackageUpdate) with
        {
            RequiredCapabilityConsents = [consent],
        };
        var requests = new List<RuntimeRegistrySourceAdoptionRequest>();
        var runtime = new FakeRuntimeClient
        {
            ResolveRegistry = (_, _) => throw new InvalidOperationException(
                "Source adoption must display its dedicated Runtime preview."),
            AdoptRegistrySource = (request, _) =>
            {
                requests.Add(request);
                if (request.DryRun)
                {
                    return Task.FromResult(new RuntimeRegistryPackageChangeResult(
                        true,
                        RuntimeRegistryErrorCode.None,
                        "Previewed.",
                        false,
                        false,
                        plan.Warnings,
                        plan.Errors,
                        [],
                        plan.Items,
                        plan.RequiredCapabilityConsents)
                    {
                        PlanExpectations =
                        [
                            new RuntimeRegistryPackagePlanExpectation(
                                origin,
                                true,
                                plan.ResolutionFingerprint),
                        ],
                        RuntimeArtifacts = plan.RuntimeArtifacts,
                    });
                }

                Assert.Equal(plan.ResolutionFingerprint, request.ExpectedResolutionFingerprint);
                Assert.Equal([consent], request.CapabilityConsents);
                return Task.FromResult(Success("Adopted."));
            },
        };

        var result = await CliTestHost.RunAsync([
            "package", "source", "adopt", "demo",
            "--registry-origin", origin,
            "--tag", "beta",
            "--include-prerelease",
            "--allow-downgrade",
            "--yes",
            "--approve-sensitive-capabilities",
            "--json",
        ], runtime);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Collection(
            requests,
            preview =>
            {
                Assert.True(preview.DryRun);
                Assert.Null(preview.ExpectedResolutionFingerprint);
            },
            apply =>
            {
                Assert.True(apply.Confirm);
                Assert.Equal(origin, apply.RegistryOrigin);
                Assert.Equal("beta", apply.Tag);
                Assert.True(apply.IncludePrerelease);
                Assert.True(apply.AllowDowngrade);
                Assert.Equal(plan.ResolutionFingerprint, apply.ExpectedResolutionFingerprint);
            });
        using var document = JsonDocument.Parse(result.Output);
        var preflight = document.RootElement.GetProperty("data").GetProperty("preflight");
        Assert.Equal(12L * 1024L * 1024L, preflight.GetProperty("packageDownloadBytes").GetInt64());
        Assert.Equal(50L * 1024L * 1024L, preflight.GetProperty("incrementalRuntimeDownloadBytes").GetInt64());
        Assert.Equal(
            consent.ManifestSha256,
            Assert.Single(preflight.GetProperty("requiredCapabilityConsents").EnumerateArray())
                .GetProperty("manifestSha256")
                .GetString());
        var planExpectation = Assert.Single(preflight.GetProperty("planExpectations").EnumerateArray());
        Assert.Equal(origin, planExpectation.GetProperty("registryOrigin").GetString());
        Assert.True(planExpectation.GetProperty("includePrerelease").GetBoolean());
        Assert.Equal(
            plan.ResolutionFingerprint,
            planExpectation.GetProperty("expectedResolutionFingerprint").GetString());
    }

    [Fact]
    public async Task Registry_source_adoption_preserves_bytes_conflicts_consents_and_source_from_its_preview()
    {
        const string origin = "https://other.example/";
        var plan = Plan(currentVersion: "1.0.0", actionKind: RegistryPackageActionKinds.PackageUpdate);
        var consent = new PackageCapabilityConsent(
            "demo",
            "2.0.0",
            new string('c', 64),
            ["docker-environment.v1"]);
        var packageConflict = new RuntimeRegistryPackageInstallPlanConflict(
            "demo",
            "1.0.0",
            ">=2.0.0",
            "dependent",
            Message: "Installed demo conflicts with dependent.");
        var runtimeConflict = new RuntimeRegistryExecutionRuntimeResolutionConflict(
            ExecutionRuntimeResolutionConflictCodes.RidUnavailable,
            "No runtime artifact is available for win-x64.",
            Requirement(),
            "win-x64",
            "demo",
            "runtime");
        var requests = new List<RuntimeRegistrySourceAdoptionRequest>();
        var runtime = new FakeRuntimeClient
        {
            AdoptRegistrySource = (request, _) =>
            {
                requests.Add(request);
                if (!request.DryRun)
                {
                    throw new InvalidOperationException("A conflicted source-adoption preview must not be applied.");
                }
                return Task.FromResult(new RuntimeRegistryPackageChangeResult(
                    false,
                    RuntimeRegistryErrorCode.Conflict,
                    packageConflict.Message,
                    false,
                    false,
                    [],
                    [packageConflict.Message, runtimeConflict.Message],
                    [],
                    plan.Items,
                    [consent])
                {
                    Conflicts = [packageConflict],
                    PlanExpectations =
                    [
                        new RuntimeRegistryPackagePlanExpectation(
                            origin,
                            true,
                            plan.ResolutionFingerprint),
                    ],
                    RuntimeArtifacts = plan.RuntimeArtifacts,
                    RuntimeConflicts = [runtimeConflict],
                });
            },
        };

        var result = await CliTestHost.RunAsync([
            "package", "source", "adopt", "demo",
            "--registry-origin", origin,
            "--tag", "beta",
            "--include-prerelease",
            "--yes",
            "--approve-sensitive-capabilities",
            "--json",
        ], runtime);

        Assert.Equal(CliExitCodes.Conflict, result.ExitCode);
        Assert.Single(requests);
        using var document = JsonDocument.Parse(result.Output);
        var preflight = document.RootElement.GetProperty("data").GetProperty("preflight");
        Assert.Equal(12L * 1024L * 1024L, preflight.GetProperty("packageDownloadBytes").GetInt64());
        Assert.Equal(50L * 1024L * 1024L, preflight.GetProperty("incrementalRuntimeDownloadBytes").GetInt64());
        Assert.Equal(
            packageConflict.ErrorCode,
            Assert.Single(preflight.GetProperty("conflicts").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal(
            runtimeConflict.Code,
            Assert.Single(preflight.GetProperty("runtimeConflicts").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal(
            consent.ManifestSha256,
            Assert.Single(preflight.GetProperty("requiredCapabilityConsents").EnumerateArray())
                .GetProperty("manifestSha256")
                .GetString());
        var planExpectation = Assert.Single(preflight.GetProperty("planExpectations").EnumerateArray());
        Assert.Equal(origin, planExpectation.GetProperty("registryOrigin").GetString());
        Assert.True(planExpectation.GetProperty("includePrerelease").GetBoolean());
        Assert.Equal(
            plan.ResolutionFingerprint,
            planExpectation.GetProperty("expectedResolutionFingerprint").GetString());
    }

    [Fact]
    public async Task Registry_source_adoption_rejects_non_lowercase_preview_fingerprint_without_apply()
    {
        var requests = new List<RuntimeRegistrySourceAdoptionRequest>();
        var runtime = new FakeRuntimeClient
        {
            AdoptRegistrySource = (request, _) =>
            {
                requests.Add(request);
                if (!request.DryRun)
                {
                    throw new InvalidOperationException("An invalid preview fingerprint must not be applied.");
                }
                return Task.FromResult(new RuntimeRegistryPackageChangeResult(
                    true,
                    RuntimeRegistryErrorCode.None,
                    "Previewed.",
                    false,
                    false,
                    [],
                    [],
                    [],
                    [])
                {
                    PlanExpectations =
                    [
                        new RuntimeRegistryPackagePlanExpectation(
                            "https://other.example/",
                            false,
                            new string('A', 64)),
                    ],
                });
            },
        };

        var result = await CliTestHost.RunAsync([
            "package", "source", "adopt", "demo",
            "--registry-origin", "https://other.example/",
            "--tag", "latest",
            "--yes",
        ], runtime);

        Assert.Equal(CliExitCodes.Conflict, result.ExitCode);
        Assert.Single(requests);
        Assert.Contains("fingerprint", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static RuntimeRegistryResolveInstallPlanResponse Plan(
        string? currentVersion = null,
        string actionKind = RegistryPackageActionKinds.PackageInstall)
    {
        var artifact = RuntimeArtifact();
        return new RuntimeRegistryResolveInstallPlanResponse(
            true,
            [new RuntimeRegistryPackageInstallPlanItem(
                "demo",
                currentVersion,
                "2.0.0",
                new string('f', 64),
                actionKind,
                null,
                [],
                [],
                [new RuntimeRegistryPackageProjectionArtifact(
                    "runtime",
                    "win-x64",
                    new string('e', 64),
                    12L * 1024L * 1024L,
                    "/package",
                    new string('d', 64),
                    new string('c', 64),
                    new string('b', 64),
                    1)])],
            [],
            [],
            [],
            [],
            RuntimeArtifacts: [artifact],
            ResolutionFingerprint: new string('a', 64));
    }

    private static RuntimeRegistryResolvedExecutionRuntimeArtifact RuntimeArtifact()
        => new(
            new RuntimeRegistryExecutionRuntimeIdentity(
                "sunder.runtime.dotnet",
                "10.0.1",
                2,
                "win-x64",
                "sunder.dotnet-worker-module.v1"),
            new string('1', 64),
            new string('2', 64),
            50L * 1024L * 1024L,
            "release-key",
            1,
            new string('3', 64),
            "native-key",
            new RuntimeRegistryExecutionRuntimeLifecycle(
                ExecutionRuntimeLifecycleStates.Supported,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                null,
                null,
                null,
                null,
                null),
            1,
            "/runtime");

    private static RuntimeRegistryExecutionRuntimeRequirement Requirement()
        => new(
            "sunder.runtime.dotnet",
            ">=10.0.0 <11.0.0",
            "sunder.dotnet-worker-module.v1");

    private static InstalledExecutionRuntimeSummary InstalledRuntime(
        RuntimeRegistryResolvedExecutionRuntimeArtifact artifact)
        => new(
            new RuntimeExecutionRuntimeIdentity(
                artifact.Identity.RuntimeId,
                artifact.Identity.Version,
                artifact.Identity.PackagingRevision,
                artifact.Identity.Rid,
                artifact.Identity.ModuleAbi,
                artifact.ManifestSha256,
                artifact.ArtifactSha256),
            RuntimeExecutionRuntimeStatuses.Ready,
            artifact.ArtifactSize,
            1,
            RuntimeExecutionRuntimeAcquisitionSources.Registry,
            artifact.SigningKeyId,
            1,
            new RuntimeExecutionRuntimeHealth(
                RuntimeExecutionRuntimeStatuses.Ready,
                DateTimeOffset.UnixEpoch,
                false,
                null),
            new RuntimeExecutionRuntimeLifecycle(
                ExecutionRuntimeLifecycleStates.Supported,
                DateTimeOffset.UnixEpoch,
                null,
                null,
                null,
                null,
                null,
                1,
                DateTimeOffset.UnixEpoch),
            DateTimeOffset.UnixEpoch);

    private static InstalledPackageDescriptor ManagedPackage()
        => new(
            "demo",
            "Demo",
            "1.0.0",
            PackageHostRoles.Runtime,
            null,
            null,
            true,
            [],
            DateTimeOffset.UnixEpoch,
            null,
            Provenance: new InstalledPackageProvenance(
                InstalledPackageSourceKind.Registry,
                InstalledPackageVersionPolicy.FollowTag,
                "https://registry.test/",
                RequestedTag: "latest"));

    private static RuntimeRegistryPackageChangeResult Success(string message)
        => new(
            true,
            RuntimeRegistryErrorCode.None,
            message,
            true,
            false,
            [],
            [],
            ["demo"],
            []);
}
