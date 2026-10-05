using System.Text.Json;
using Sunder.Registry.Contracts;
using Xunit;

namespace Sunder.Registry.Contracts.Tests;

public sealed class RegistryContractGoldenTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static TheoryData<string, object, Type> Cases => new()
    {
        {
            "package.json",
            new RegistryPackageVersionDetails(
                "sunder.package.sample",
                "Sample",
                "A sample package.",
                "1.2.3",
                "assets/icon.png",
                false,
                null,
                [new RegistryPackageDependency("sunder.package.base", ">=1.0.0")],
                new RegistryPackageCanonicalArtifact("canonical-sha", 420, "https://registry.example/api/v1/packages/sunder.package.sample/versions/1.2.3/canonical"),
                [
                    new RegistryPackageTarget(
                        "runtime",
                        "win-x64",
                        "worker",
                        "bin/sample-worker.dll",
                        "net10.0",
                        "1.1.0",
                        ["core.v1", "execution-runtime.v1", "rpc.v1", "sdk-baseline-1-1.v1", "worker-protocol.v1"],
                        ExecutionRuntime: Requirement()),
                    new RegistryPackageTarget(
                        "app",
                        "win-x64",
                        "web",
                        "web/index.html",
                        null,
                        "1.1.0",
                        ["core.v1", "sdk-baseline-1-1.v1", "views.v1"],
                        [new RegistryPackageWebView(
                            "sunder.package.sample.workspace",
                            "Workspace",
                            "/workspace",
                            "assets/workspace.png",
                            "rightTop",
                            true)]),
                ],
                [Projection("shared", null, "/api/v1/packages/sunder.package.sample/versions/1.2.3/projections/shared/download")],
                [new RegistryPackageContractBundle(
                    "sample.contract",
                    "1.0.0",
                    "contracts/sample.json",
                    "descriptor-sha",
                    "abi-sha",
                    "public")],
                [new RegistryPackageContractUse(
                    "runtime",
                    "base.contract",
                    "1.4.2",
                    "base-abi-sha",
                    true,
                    ["discover", "invoke"])],
                [new RegistryPackageProvider(
                    "sample.provider",
                    "runtime",
                    "sample.contract",
                    "1.0.0",
                    "descriptor-sha",
                    "abi-sha",
                    "public")],
                1,
                1,
                DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
                ["https://artifacts.example"]),
            typeof(RegistryPackageVersionDetails)
        },
        {
            "stack.json",
            new RegistryStackSummary(
                "sample-stack",
                "Sample Stack",
                "A sample Stack.",
                2,
                1,
                DateTimeOffset.Parse("2026-07-10T10:00:00Z"),
                DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
                new RegistryStackStats(12, 3, true, 4, [new RegistryStackDownloadPoint(new DateOnly(2026, 7, 11), 2)], 9)),
            typeof(RegistryStackSummary)
        },
        {
            "auth.json",
            new RegistryCliTokenResponse(true, "sunder_v1_token", "user_123", DateTimeOffset.Parse("2026-07-12T10:00:00Z"), []),
            typeof(RegistryCliTokenResponse)
        },
        {
            "contract.json",
            new RegistryContractDescriptorMetadata(
                "sample.contract",
                "1.2.3",
                "descriptor-sha",
                "abi-sha",
                "public",
                420,
                "https://registry.example/api/v1/contracts/sample.contract/versions/1.2.3/descriptor",
                "sunder.package.sample",
                "2.0.0",
                DateTimeOffset.Parse("2026-07-11T10:00:00Z")),
            typeof(RegistryContractDescriptorMetadata)
        },
        {
            "plan.json",
            new RegistryResolveInstallPlanResponse(
                true,
                [new RegistryPackageInstallPlanItem(
                    "sunder.package.sample",
                    null,
                    "1.2.3",
                    "package-content-identity-123",
                    RegistryPackageActionKinds.PackageInstall,
                    null,
                    [],
                    [new RegistryPackageTarget(
                        "runtime",
                        "win-x64",
                        "worker",
                        "bin/sample-worker.dll",
                        "net10.0",
                        "1.1.0",
                        ["core.v1", "execution-runtime.v1", "rpc.v1", "sdk-baseline-1-1.v1", "worker-protocol.v1"],
                        ExecutionRuntime: Requirement())],
                    [
                        Projection("shared", null, "/api/v1/packages/sunder.package.sample/versions/1.2.3/projections/shared/download"),
                        Projection("runtime", "win-x64", "/api/v1/packages/sunder.package.sample/versions/1.2.3/projections/runtime/win-x64/download"),
                    ],
                    [Binding(RuntimeIdentity())])],
                [],
                [],
                [],
                ["https://artifacts.example"],
                [RuntimeArtifact()],
                [],
                "plan-fingerprint-001",
                42,
                DateTimeOffset.Parse("2026-07-11T10:05:00Z")),
            typeof(RegistryResolveInstallPlanResponse)
        },
        {
            "requirements.json",
            new RegistryResolvePackageChangesRequest(
                [new RegistryPackageChangeRequest(
                    "sunder.package.sample",
                    Version: null,
                    [new RegistryPackageTargetRequest("runtime", "win-x64")],
                    Tag: "preview",
                    VersionRange: ">=2.3.0",
                    Required: false)],
                [new RegistryInstalledPackageState(
                    "sunder.package.base",
                    "1.5.0",
                    [new RegistryPackageDependency("sunder.package.shared", ">=1.0.0")],
                    [new RegistryPackageProjectionKey("shared", null), new RegistryPackageProjectionKey("runtime", "win-x64")],
                    "package-content-base-150",
                    [Binding(RuntimeIdentity("10.0.0", 1))])],
                Compatibility(),
                RuntimeTrust(),
                "previous-fingerprint-001",
                IncludePrerelease: true),
            typeof(RegistryResolvePackageChangesRequest)
        },
        {
            "runtime-browse.json",
            new RegistryExecutionRuntimeSearchResult(
                [new RegistryExecutionRuntimeSummary(
                    "sunder.runtime.dotnet",
                    ".NET Runtime",
                    "sunder.dotnet-worker-module.v1",
                    "10.0.1",
                    [RuntimeReleaseSummary()])],
                1,
                0,
                20,
                42),
            typeof(RegistryExecutionRuntimeSearchResult)
        },
        {
            "runtime-details.json",
            new RegistryExecutionRuntimeReleaseDetails(
                RuntimeReleaseSummary(),
                [new RegistryExecutionRuntimeArtifactDetails(
                    RuntimeArtifact(),
                    "runtime-content-identity-001",
                    DateTimeOffset.Parse("2026-07-10T12:00:00Z"))]),
            typeof(RegistryExecutionRuntimeReleaseDetails)
        },
        {
            "runtime-publication-request.json",
            new RegistryStageExecutionRuntimeReleaseRequest(
                "sunder.runtime.dotnet",
                "10.0.1",
                2,
                "sunder.dotnet-worker-module.v1",
                "sunder.worker.v1",
                2,
                "sunder-runtime-release-2026-01",
                PublicationAttestations(),
                ["linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64"]),
            typeof(RegistryStageExecutionRuntimeReleaseRequest)
        },
        {
            "runtime-publication-lookup-request.json",
            new RegistryLookupExecutionRuntimePublicationRequest(
                new RegistryStageExecutionRuntimeReleaseRequest(
                    "sunder.runtime.dotnet",
                    "10.0.1",
                    2,
                    "sunder.dotnet-worker-module.v1",
                    "sunder.worker.v1",
                    2,
                    "sunder-runtime-release-2026-01",
                    PublicationAttestations(),
                    ["linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64"]),
                [PublicationArtifactExpectation("linux-x64")]),
            typeof(RegistryLookupExecutionRuntimePublicationRequest)
        },
        {
            "runtime-publication.json",
            new RegistryStagedExecutionRuntimePublicationResponse(
                "stage-runtime-001",
                RuntimeReleaseSummary(ExecutionRuntimeLifecycleStates.Staged),
                "sunder-runtime-release-2026-01",
                PublicationAttestations(),
                ["linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64"],
                ["linux-x64", "win-x64"],
                ["linux-arm64", "osx-arm64", "osx-x64", "win-arm64"],
                false,
                [PublicationArtifact("linux-x64"), PublicationArtifact("win-x64")]),
            typeof(RegistryStagedExecutionRuntimePublicationResponse)
        },
        {
            "runtime-rebind.json",
            RebindPlan(),
            typeof(RegistryResolveInstallPlanResponse)
        },
        {
            "runtime-ensure-request.json",
            new RegistryResolveExecutionRuntimeRequest(
                Requirement(),
                "win-x64",
                RuntimeTrust(),
                RuntimeIdentity("10.0.0", 1),
                "previous-ensure-fingerprint-001"),
            typeof(RegistryResolveExecutionRuntimeRequest)
        },
        {
            "runtime-ensure.json",
            new RegistryResolveExecutionRuntimeResponse(
                true,
                RuntimeArtifact(),
                "ensure-fingerprint-001",
                42,
                DateTimeOffset.Parse("2026-07-11T10:05:00Z"),
                [],
                []),
            typeof(RegistryResolveExecutionRuntimeResponse)
        },
        {
            "runtime-conflicts.json",
            ConflictPlan(),
            typeof(RegistryResolveInstallPlanResponse)
        },
        {
            "runtime-stale-plan.json",
            FailedRuntimeResolution(
                ExecutionRuntimeErrorCodes.PlanStale,
                "The expected resolution fingerprint is stale."),
            typeof(RegistryResolveExecutionRuntimeResponse)
        },
        {
            "runtime-old-host-trust.json",
            FailedRuntimeResolution(
                ExecutionRuntimeErrorCodes.HostTrustUpdateRequired,
                "Host runtime trust generation 2 is required.",
                requiredHostTrustGeneration: 2),
            typeof(RegistryResolveExecutionRuntimeResponse)
        },
        {
            "requirement-conflict.json",
            new RegistryResolveInstallPlanResponse(
                false,
                [],
                [],
                [],
                [new RegistryPackageInstallPlanConflict(
                    "sunder.package.sample",
                    "1.0.0",
                    ">=2.3.0",
                    null,
                    RegistryV1ErrorCodes.PackageRequirementUnsatisfied,
                    "Selected package version does not satisfy the Stack requirement.")],
                []),
            typeof(RegistryResolveInstallPlanResponse)
        },
        {
            "stack-requirement.json",
            new RegistryStackPackageRequirement(
                "sunder.package.sample",
                "preview",
                "2.4.0",
                "2.3.0",
                false,
                "Sample",
                "/api/v1/packages/sunder.package.sample/versions/2.4.0/icon"),
            typeof(RegistryStackPackageRequirement)
        },
        {
            "pagination.json",
            new RegistryPackageSearchResult(
                [new RegistryPackageSummary(
                    "sunder.package.sample",
                    "Sample",
                    "A sample package.",
                    "1.2.3",
                    null,
                    false,
                    DateTimeOffset.Parse("2026-07-10T10:00:00Z"),
                    DateTimeOffset.Parse("2026-07-11T10:00:00Z"))],
                12,
                5,
                5),
            typeof(RegistryPackageSearchResult)
        },
        {
            "error.json",
            new RegistryProblemDetails(
                "https://registry.sunder.dev/problems/registry.v1.request.invalid",
                "Invalid request",
                400,
                "The request could not be accepted.",
                "/api/v1/packages",
                RegistryV1ErrorCodes.InvalidRequest,
                "00-trace-01",
                "correlation-123",
                new Dictionary<string, string[]> { ["packageId"] = ["Package id is required."] }),
            typeof(RegistryProblemDetails)
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void V1Payload_MatchesGoldenJson(string fixtureName, object payload, Type payloadType)
    {
        var actual = JsonSerializer.Serialize(payload, payloadType, JsonOptions).ReplaceLineEndings("\n");
        var updateRoot = Environment.GetEnvironmentVariable("SUNDER_UPDATE_REGISTRY_CONTRACT_GOLDENS");
        var updateFixtures = Environment.GetEnvironmentVariable("SUNDER_UPDATE_REGISTRY_CONTRACT_FIXTURES")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!string.IsNullOrWhiteSpace(updateRoot) && (updateFixtures is null || updateFixtures.Contains(fixtureName, StringComparer.Ordinal)))
        {
            Directory.CreateDirectory(updateRoot);
            File.WriteAllText(Path.Combine(updateRoot, fixtureName), actual + Environment.NewLine);
            return;
        }

        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        var expected = File.ReadAllText(fixturePath).ReplaceLineEndings("\n").TrimEnd();

        Assert.Equal(expected, actual);
        Assert.NotNull(JsonSerializer.Deserialize(expected, payloadType, JsonOptions));
    }

    [Fact]
    public void PackageRpcMetadata_UsesManifestV1Properties()
    {
        using var document = ReadFixture("package.json");
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("manifestFormatVersion").GetInt32());
        Assert.Equal(">=1.0.0", root.GetProperty("dependsOn")[0].GetProperty("versionRange").GetString());
        AssertPropertyNames(
            root.GetProperty("contractBundles")[0],
            "contractId",
            "version",
            "descriptorPath",
            "descriptorSha256",
            "abiSha256",
            "exposure");
        AssertPropertyNames(
            root.GetProperty("usesContracts")[0],
            "role",
            "contractId",
            "contractVersion",
            "abiSha256",
            "required",
            "actions");
        Assert.Equal("1.4.2", root.GetProperty("usesContracts")[0].GetProperty("contractVersion").GetString());
        AssertPropertyNames(
            root.GetProperty("providers")[0],
            "providerId",
            "role",
            "contractId",
            "contractVersion",
            "descriptorSha256",
            "abiSha256",
            "audience");
    }

    [Fact]
    public void ContractMetadata_UsesDescriptorV1HashesAndExposure()
    {
        using var document = ReadFixture("contract.json");

        AssertPropertyNames(
            document.RootElement,
            "contractId",
            "version",
            "descriptorSha256",
            "abiSha256",
            "exposure",
            "size",
            "descriptorDownloadUrl",
            "firstPublisherPackageId",
            "firstPublisherPackageVersion",
            "publishedAtUtc",
            "etag");
        Assert.Equal("\"descriptor-sha\"", document.RootElement.GetProperty("etag").GetString());
    }

    [Theory]
    [InlineData(typeof(RegistryResolveUpdatesRequest))]
    [InlineData(typeof(RegistryResolveInstallPlanRequest))]
    [InlineData(typeof(RegistryResolvePackageChangesRequest))]
    public void ResolutionRequests_RequireOneCompatibilityProfile(Type requestType)
    {
        var parameter = Assert.Single(
            Assert.Single(requestType.GetConstructors()).GetParameters(),
            candidate => candidate.Name == "Compatibility");

        Assert.Equal(typeof(RegistryClientCompatibilityProfile), parameter.ParameterType);
        Assert.False(parameter.HasDefaultValue);
    }

    [Fact]
    public void CompatibilityProfile_IsImmutableAndPublishesBounds()
    {
        var capabilities = new List<string> { "rpc.v1", "sdk-baseline-1-1.v1", "worker-protocol.v1" };
        var profile = new RegistryClientCompatibilityProfile(
            [1],
            ["worker"],
            "worker-protocol.v1",
            capabilities,
            "sdk-baseline-1-1.v1",
            "win-x64");
        capabilities.Clear();

        Assert.Equal(["rpc.v1", "sdk-baseline-1-1.v1", "worker-protocol.v1"], profile.WorkerCapabilities);
        Assert.Equal(8, RegistryClientCompatibilityProfile.MaximumManifestVersions);
        Assert.Equal(8, RegistryClientCompatibilityProfile.MaximumRuntimeTargetKinds);
        Assert.Equal(64, RegistryClientCompatibilityProfile.MaximumWorkerCapabilities);
        Assert.Equal(128, RegistryClientCompatibilityProfile.MaximumIdentifierLength);
    }

    [Fact]
    public void CompatibilityQueryParameters_MatchTheJsonProfile()
    {
        Assert.Equal("manifestVersions", RegistryClientCompatibilityProfile.ManifestVersionsQueryParameter);
        Assert.Equal("runtimeTargetKinds", RegistryClientCompatibilityProfile.RuntimeTargetKindsQueryParameter);
        Assert.Equal("workerProtocol", RegistryClientCompatibilityProfile.WorkerProtocolQueryParameter);
        Assert.Equal("workerCapabilities", RegistryClientCompatibilityProfile.WorkerCapabilitiesQueryParameter);
        Assert.Equal("sdkBaseline", RegistryClientCompatibilityProfile.SdkBaselineQueryParameter);
        Assert.Equal("rid", RegistryClientCompatibilityProfile.RidQueryParameter);
    }

    [Fact]
    public void ResolutionRequests_RejectMissingCompatibilityDuringDeserialization()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryResolveUpdatesRequest>(
            """
            {"installedPackages":[],"desiredTargets":[]}
            """,
            JsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryResolveInstallPlanRequest>(
            """
            {"packageId":"test.package","version":null,"tag":"latest","desiredTargets":[],"installedPackages":[]}
            """,
            JsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryResolvePackageChangesRequest>(
            """
            {"packages":[],"installedPackages":[]}
            """,
            JsonOptions));
    }

    [Fact]
    public void ResolutionRequests_RejectMissingRuntimeTrustDuringDeserialization()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryResolvePackageChangesRequest>(
            """
            {"packages":[],"installedPackages":[],"compatibility":{"manifestVersions":[1],"runtimeTargetKinds":["worker"],"workerProtocol":"worker-protocol.v1","workerCapabilities":[],"sdkBaseline":"sdk-baseline-1-1.v1","rid":"win-x64"}}
            """,
            JsonOptions));
    }

    [Fact]
    public void ExecutionRuntimeErrorCodes_AreExactAndOwnedOnce()
    {
        string[] expected =
        [
            "execution-runtime.requirement-invalid",
            "execution-runtime.unsupported",
            "execution-runtime.unresolved",
            "execution-runtime.missing",
            "execution-runtime.offline",
            "execution-runtime.policy-required",
            "execution-runtime.download-failed",
            "execution-runtime.artifact-invalid",
            "execution-runtime.integrity-failed",
            "execution-runtime.install-failed",
            "execution-runtime.binding-invalid",
            "execution-runtime.launch-invalid",
            "execution-runtime.unhealthy",
            "execution-runtime.deprecated",
            "execution-runtime.retired",
            "execution-runtime.revoked",
            "execution-runtime.busy",
            "execution-runtime.plan-stale",
            "execution-runtime.host-trust-update-required",
            "execution-runtime.current-runtime-trust-denied",
        ];
        var actual = typeof(ExecutionRuntimeErrorCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(static field => Assert.IsType<string>(field.GetRawConstantValue()))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
        var conflictCodes = typeof(ExecutionRuntimeResolutionConflictCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(static field => Assert.IsType<string>(field.GetRawConstantValue()));
        Assert.Equal(expected.Concat(conflictCodes).Order(StringComparer.Ordinal), ExecutionRuntimeErrorCatalog.All.Select(static error => error.Code).Order(StringComparer.Ordinal));
        Assert.All(ExecutionRuntimeErrorCatalog.All, static error =>
        {
            Assert.NotEmpty(error.Owner);
            Assert.InRange(error.HttpStatus, 400, 599);
            Assert.NotEmpty(error.SafeMessage);
            Assert.DoesNotContain(error.DiagnosticFields, static field =>
                field.Contains("path", StringComparison.OrdinalIgnoreCase)
                || field.Contains("token", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void ResolutionConflictCodes_AreExact()
    {
        Assert.Equal("execution-runtime.no-compatible-version", ExecutionRuntimeResolutionConflictCodes.NoCompatibleVersion);
        Assert.Equal("execution-runtime.rid-unavailable", ExecutionRuntimeResolutionConflictCodes.RidUnavailable);
        Assert.Equal("execution-runtime.module-abi-mismatch", ExecutionRuntimeResolutionConflictCodes.ModuleAbiMismatch);
    }

    [Fact]
    public void PackageActionKinds_AreExactAndContainNoBooleanCompatibilityFlag()
    {
        Assert.Equal("package-install", RegistryPackageActionKinds.PackageInstall);
        Assert.Equal("package-update", RegistryPackageActionKinds.PackageUpdate);
        Assert.Equal("package-remove", RegistryPackageActionKinds.PackageRemove);
        Assert.Equal("keep", RegistryPackageActionKinds.Keep);
        Assert.Equal("runtime-only-rebind", RegistryPackageActionKinds.RuntimeOnlyRebind);
        Assert.Null(typeof(RegistryPackageInstallPlanItem).GetProperty("IsUpdate"));
    }

    [Fact]
    public void InstallPlan_DeduplicatesRuntimeArtifactAndBindingsReferenceIdentityOnly()
    {
        var testCase = Cases.Single(testCase => (string)testCase[0] == "plan.json");
        var plan = Assert.IsType<RegistryResolveInstallPlanResponse>(testCase[1]);
        var artifact = Assert.Single(plan.RuntimeArtifacts);
        var binding = Assert.Single(Assert.Single(plan.Items).RuntimeBindings);

        Assert.Equal(artifact.Identity, binding.Runtime);
        Assert.DoesNotContain(
            typeof(RegistryPackageTargetRuntimeBinding).GetProperties(),
            static property => property.Name.Contains("Download", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExecutionRuntimeContractNullability_IsIntentional()
    {
        var context = new System.Reflection.NullabilityInfoContext();
        Assert.Equal(System.Reflection.NullabilityState.NotNull, context.Create(typeof(RegistryResolvedExecutionRuntimeArtifact).GetProperty(nameof(RegistryResolvedExecutionRuntimeArtifact.DownloadUrl))!).ReadState);
        Assert.Equal(System.Reflection.NullabilityState.NotNull, context.Create(typeof(RegistryResolveInstallPlanResponse).GetProperty(nameof(RegistryResolveInstallPlanResponse.RuntimeArtifacts))!).ReadState);
        Assert.Equal(System.Reflection.NullabilityState.Nullable, context.Create(typeof(RegistryExecutionRuntimeLifecycle).GetProperty(nameof(RegistryExecutionRuntimeLifecycle.Message))!).ReadState);
        Assert.Equal(System.Reflection.NullabilityState.Nullable, context.Create(typeof(RegistryResolveExecutionRuntimeResponse).GetProperty(nameof(RegistryResolveExecutionRuntimeResponse.RuntimeArtifact))!).ReadState);
    }

    private static JsonDocument ReadFixture(string fixtureName)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName)));

    private static void AssertPropertyNames(JsonElement element, params string[] expected)
        => Assert.Equal(expected, element.EnumerateObject().Select(property => property.Name));

    private static RegistryPackageProjectionArtifact Projection(string kind, string? rid, string downloadUrl)
        => new(
            kind,
            rid,
            "projection-sha",
            42,
            downloadUrl,
            "canonical-sha",
            "manifest-sha",
            "projection-content-id",
            1);

    private static RegistryClientCompatibilityProfile Compatibility()
        => new(
            [1],
            ["worker"],
            "worker-protocol.v1",
            ["core.v1", "execution-runtime.v1", "rpc.v1", "sdk-baseline-1-1.v1", "worker-protocol.v1"],
            "sdk-baseline-1-1.v1",
            "win-x64");

    private static RegistryHostRuntimeTrustProfile RuntimeTrust()
        => new("sunder.runtime-trust.v1", 2);

    private static RegistryExecutionRuntimeRequirement Requirement()
        => new("sunder.runtime.dotnet", ">=10.0.0 <11.0.0", "sunder.dotnet-worker-module.v1");

    private static RegistryExecutionRuntimeIdentity RuntimeIdentity(string version = "10.0.1", int packagingRevision = 2)
        => new("sunder.runtime.dotnet", version, packagingRevision, "win-x64", "sunder.dotnet-worker-module.v1");

    private static RegistryExecutionRuntimeLifecycle Lifecycle(string state = ExecutionRuntimeLifecycleStates.Supported)
        => new(
            state,
            DateTimeOffset.Parse("2026-07-09T12:00:00Z"),
            state == ExecutionRuntimeLifecycleStates.Staged ? null : DateTimeOffset.Parse("2026-07-10T12:00:00Z"),
            null,
            null,
            null,
            null,
            null);

    private static RegistryResolvedExecutionRuntimeArtifact RuntimeArtifact()
        => new(
            RuntimeIdentity(),
            "runtime-manifest-sha-001",
            "runtime-artifact-sha-001",
            104857600,
            "sunder-runtime-release-2026-01",
            2,
            "native-attestation-sha-001",
            "sunder-native-ci-2026-01",
            Lifecycle(),
            42,
            "https://registry.example/api/v1/execution-runtimes/sunder.runtime.dotnet/releases/10.0.1/revisions/2/artifacts/win-x64/download");

    private static RegistryPackageTargetRuntimeBinding Binding(RegistryExecutionRuntimeIdentity identity)
        => new("runtime", "win-x64", Requirement(), identity);

    private static RegistryExecutionRuntimeReleaseSummary RuntimeReleaseSummary(string state = ExecutionRuntimeLifecycleStates.Supported)
        => new(
            "sunder.runtime.dotnet",
            "10.0.1",
            2,
            "sunder.dotnet-worker-module.v1",
            "sunder.worker.v1",
            2,
            Lifecycle(state),
            ["linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64"],
            DateTimeOffset.Parse("2026-07-10T12:00:00Z"));

    private static RegistryExecutionRuntimePublicationArtifactExpectation PublicationArtifactExpectation(string rid)
        => new(
            rid,
            $"runtime-publication-{rid}",
            "runtime-artifact-sha-001",
            104857600,
            "runtime-manifest-sha-001",
            "runtime-content-identity-001",
            "sunder-runtime-release-2026-01",
            "native-attestation-sha-001",
            "sunder-native-ci-2026-01");

    private static RegistryExecutionRuntimeNativeAttestationExpectation[] PublicationAttestations()
        => [
            new("linux-arm64", "sunder-native-ci-2026-01"),
            new("linux-x64", "sunder-native-ci-2026-01"),
            new("osx-arm64", "sunder-native-ci-2026-01"),
            new("osx-x64", "sunder-native-ci-2026-01"),
            new("win-arm64", "sunder-native-ci-2026-01"),
            new("win-x64", "sunder-native-ci-2026-01"),
        ];

    private static RegistryExecutionRuntimePublicationArtifact PublicationArtifact(string rid)
    {
        var expected = PublicationArtifactExpectation(rid);
        return new RegistryExecutionRuntimePublicationArtifact(
            expected.Rid,
            expected.ArtifactSha256,
            expected.ArtifactSize,
            expected.ManifestSha256,
            expected.ContentIdentity,
            expected.SigningKeyId,
            expected.NativeAttestationSha256,
            expected.NativeAttestationKeyId);
    }

    private static RegistryResolveInstallPlanResponse RebindPlan()
    {
        var previous = Binding(RuntimeIdentity("10.0.0", 1));
        var next = Binding(RuntimeIdentity());
        return new RegistryResolveInstallPlanResponse(
            true,
            [new RegistryPackageInstallPlanItem(
                "sunder.package.sample",
                "1.2.3",
                "1.2.3",
                "package-content-identity-123",
                RegistryPackageActionKinds.RuntimeOnlyRebind,
                null,
                [],
                [],
                [],
                [next],
                new RegistryRuntimeOnlyRebindDetails(
                    "1.2.3",
                    "package-content-identity-123",
                    [previous],
                    [next]))],
            [],
            [],
            [],
            ["https://artifacts.example"],
            [RuntimeArtifact()],
            [],
            "rebind-fingerprint-001",
            42,
            DateTimeOffset.Parse("2026-07-11T10:05:00Z"));
    }

    private static RegistryResolveInstallPlanResponse ConflictPlan()
    {
        var conflicts = new[]
        {
            ExecutionRuntimeErrorCodes.Unsupported,
            ExecutionRuntimeResolutionConflictCodes.NoCompatibleVersion,
            ExecutionRuntimeResolutionConflictCodes.RidUnavailable,
            ExecutionRuntimeResolutionConflictCodes.ModuleAbiMismatch,
            ExecutionRuntimeErrorCodes.Deprecated,
            ExecutionRuntimeErrorCodes.Retired,
            ExecutionRuntimeErrorCodes.Revoked,
            ExecutionRuntimeErrorCodes.PolicyRequired,
            ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied,
            ExecutionRuntimeErrorCodes.PlanStale,
            ExecutionRuntimeErrorCodes.HostTrustUpdateRequired,
        }.Select(code => new RegistryExecutionRuntimeResolutionConflict(
            code,
            $"Conflict: {code}",
            Requirement(),
            "win-x64",
            "sunder.package.sample",
            "runtime",
            RuntimeIdentity("10.0.0", 1),
            code == ExecutionRuntimeErrorCodes.HostTrustUpdateRequired ? 2 : null)).ToArray();
        return new RegistryResolveInstallPlanResponse(
            false,
            [],
            [],
            [],
            [],
            [],
            [],
            conflicts,
            "conflict-fingerprint-001",
            42,
            DateTimeOffset.Parse("2026-07-11T10:05:00Z"));
    }

    private static RegistryResolveExecutionRuntimeResponse FailedRuntimeResolution(
        string code,
        string message,
        long? requiredHostTrustGeneration = null)
        => new(
            false,
            null,
            "replacement-fingerprint-001",
            43,
            DateTimeOffset.Parse("2026-07-11T10:05:00Z"),
            [],
            [new RegistryExecutionRuntimeResolutionConflict(
                code,
                message,
                Requirement(),
                "win-x64",
                CurrentRuntime: RuntimeIdentity("10.0.0", 1),
                RequiredHostTrustGeneration: requiredHostTrustGeneration)]);
}
