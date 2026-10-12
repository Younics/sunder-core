using System.Text.Json;
using System.Text.Json.Nodes;
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
                "sunder.package.sample", "Sample", "A sample package.", "1.2.3", "assets/icon.png", false, null,
                [new RegistryPackageDependency("sunder.package.base", ">=1.0.0")],
                new RegistryPackageCanonicalArtifact("canonical-sha", 420, "https://registry.example/api/v1/packages/sunder.package.sample/versions/1.2.3/canonical"),
                [
                    Target(),
                    new RegistryPackageTarget("app", "win-x64", "web", "web/index.html", null, "1.1.0",
                        ["core.v1", "sdk-baseline-1-1.v1", "views.v1"],
                        [new RegistryPackageWebView("sunder.package.sample.workspace", "Workspace", "/workspace", "assets/workspace.png", "rightTop", true)]),
                ],
                [Projection("shared", null, "/api/v1/packages/sunder.package.sample/versions/1.2.3/projections/shared/download")],
                [new RegistryPackageContractBundle("sample.contract", "1.0.0", "contracts/sample.json", "descriptor-sha", "abi-sha", "public")],
                [new RegistryPackageContractUse("runtime", "base.contract", "1.4.2", "base-abi-sha", true, ["discover", "invoke"])],
                [new RegistryPackageProvider("sample.provider", "runtime", "sample.contract", "1.0.0", "descriptor-sha", "abi-sha", "public")],
                1, 1, DateTimeOffset.Parse("2026-07-11T10:00:00Z"), ["https://artifacts.example"]),
            typeof(RegistryPackageVersionDetails)
        },
        {
            "stack.json",
            new RegistryStackSummary("sample-stack", "Sample Stack", "A sample Stack.", 2, 1,
                DateTimeOffset.Parse("2026-07-10T10:00:00Z"), DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
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
            new RegistryContractDescriptorMetadata("sample.contract", "1.2.3", "descriptor-sha", "abi-sha", "public", 420,
                "https://registry.example/api/v1/contracts/sample.contract/versions/1.2.3/descriptor",
                "sunder.package.sample", "2.0.0", DateTimeOffset.Parse("2026-07-11T10:00:00Z")),
            typeof(RegistryContractDescriptorMetadata)
        },
        { "plan.json", Plan(), typeof(RegistryResolveInstallPlanResponse) },
        {
            "requirements.json",
            new RegistryResolvePackageChangesRequest(
                [new RegistryPackageChangeRequest("sunder.package.sample", null, [new("runtime", "win-x64")],
                    Tag: "preview", VersionRange: ">=2.3.0", Required: false)],
                [new RegistryInstalledPackageState("sunder.package.base", "1.5.0",
                    [new RegistryPackageDependency("sunder.package.shared", ">=1.0.0")],
                    [new RegistryPackageProjectionKey("shared", null), new RegistryPackageProjectionKey("runtime", "win-x64")],
                    "package-content-base-150")],
                Compatibility(), "previous-fingerprint-001", IncludePrerelease: true),
            typeof(RegistryResolvePackageChangesRequest)
        },
        {
            "requirement-conflict.json",
            new RegistryResolveInstallPlanResponse(false, [], [], [],
                [new RegistryPackageInstallPlanConflict("sunder.package.sample", "1.0.0", ">=2.3.0", null,
                    RegistryV1ErrorCodes.PackageRequirementUnsatisfied,
                    "Selected package version does not satisfy the Stack requirement.")], []),
            typeof(RegistryResolveInstallPlanResponse)
        },
        {
            "stack-requirement.json",
            new RegistryStackPackageRequirement("sunder.package.sample", "preview", "2.4.0", "2.3.0", false, "Sample",
                "/api/v1/packages/sunder.package.sample/versions/2.4.0/icon"),
            typeof(RegistryStackPackageRequirement)
        },
        {
            "pagination.json",
            new RegistryPackageSearchResult([new RegistryPackageSummary("sunder.package.sample", "Sample", "A sample package.", "1.2.3", null, false,
                DateTimeOffset.Parse("2026-07-10T10:00:00Z"), DateTimeOffset.Parse("2026-07-11T10:00:00Z"))], 12, 5, 5),
            typeof(RegistryPackageSearchResult)
        },
        {
            "error.json",
            new RegistryProblemDetails("https://registry.sunder.dev/problems/registry.v1.request.invalid", "Invalid request", 400,
                "The request could not be accepted.", "/api/v1/packages", RegistryV1ErrorCodes.InvalidRequest, "00-trace-01", "correlation-123",
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
        Assert.Equal(actual, JsonSerializer.Serialize(JsonSerializer.Deserialize(expected, payloadType, JsonOptions), payloadType, JsonOptions));
    }

    [Fact]
    public void PackageRpcMetadata_UsesManifestV1Properties()
    {
        using var document = ReadFixture("package.json");
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("manifestFormatVersion").GetInt32());
        Assert.Equal(">=1.0.0", root.GetProperty("dependsOn")[0].GetProperty("versionRange").GetString());
        AssertPropertyNames(root.GetProperty("contractBundles")[0], "contractId", "version", "descriptorPath", "descriptorSha256", "abiSha256", "exposure");
        AssertPropertyNames(root.GetProperty("usesContracts")[0], "role", "contractId", "contractVersion", "abiSha256", "required", "actions");
        Assert.Equal("1.4.2", root.GetProperty("usesContracts")[0].GetProperty("contractVersion").GetString());
        AssertPropertyNames(root.GetProperty("providers")[0], "providerId", "role", "contractId", "contractVersion", "descriptorSha256", "abiSha256", "audience");
    }

    [Fact]
    public void ContractMetadata_UsesDescriptorV1HashesAndExposure()
    {
        using var document = ReadFixture("contract.json");
        AssertPropertyNames(document.RootElement, "contractId", "version", "descriptorSha256", "abiSha256", "exposure", "size",
            "descriptorDownloadUrl", "firstPublisherPackageId", "firstPublisherPackageVersion", "publishedAtUtc", "etag");
        Assert.Equal("\"descriptor-sha\"", document.RootElement.GetProperty("etag").GetString());
    }

    [Theory]
    [InlineData(typeof(RegistryResolveUpdatesRequest))]
    [InlineData(typeof(RegistryResolveInstallPlanRequest))]
    [InlineData(typeof(RegistryResolvePackageChangesRequest))]
    public void ResolutionRequests_RequireOneCompatibilityProfile(Type requestType)
    {
        var parameter = Assert.Single(Assert.Single(requestType.GetConstructors()).GetParameters(), candidate => candidate.Name == "Compatibility");
        Assert.Equal(typeof(RegistryClientCompatibilityProfile), parameter.ParameterType);
        Assert.False(parameter.HasDefaultValue);
    }

    [Fact]
    public void CompatibilityProfile_IsImmutableAndPublishesBounds()
    {
        var capabilities = new List<string> { "rpc.v1", "sdk-baseline-1-1.v1", "worker-protocol.v1" };
        var profile = new RegistryClientCompatibilityProfile([1], ["worker"], "worker-protocol.v1", capabilities, "sdk-baseline-1-1.v1", "win-x64");
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
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryResolveUpdatesRequest>("{\"installedPackages\":[],\"desiredTargets\":[]}", JsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryResolveInstallPlanRequest>(
            "{\"packageId\":\"test.package\",\"version\":null,\"tag\":\"latest\",\"desiredTargets\":[],\"installedPackages\":[]}", JsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryResolvePackageChangesRequest>("{\"packages\":[],\"installedPackages\":[]}", JsonOptions));
    }

    public static TheoryData<object, string> RemovedAuthorityMembers => new()
    {
        { new RegistryResolvePackageChangesRequest([], [], Compatibility()), "runtimeTrust" },
        { new RegistryResolveInstallPlanRequest("example.package", null, "latest", [], [], Compatibility()), "runtimeTrust" },
        { new RegistryResolveUpdatesRequest([], [], Compatibility()), "runtimeTrust" },
        { new RegistryInstalledPackage("example.package", "1.0.0", [], "content"), "runtimeBindings" },
        { new RegistryInstalledPackageState("example.package", "1.0.0", [], [], "content"), "runtimeBindings" },
        { Plan(), "runtimeArtifacts" },
        { Plan(), "runtimeConflicts" },
        { Plan(), "runtimePolicyRevision" },
        { new RegistryResolveUpdatesResponse([], [], []), "runtimeArtifacts" },
        { new RegistryResolveUpdatesResponse([], [], []), "runtimeConflicts" },
        { new RegistryResolveUpdatesResponse([], [], []), "runtimePolicyRevision" },
        { Plan().Items[0], "runtimeBindings" },
        { Plan().Items[0], "runtimeOnlyRebind" },
        { Requirement(), "downloadUrl" },
        { Requirement(), "sourceId" },
    };

    [Theory]
    [MemberData(nameof(RemovedAuthorityMembers))]
    public void PackagePlanning_AcceptsRequirementsOnlyAndRejectsRemovedAuthority(object payload, string field)
    {
        var type = payload.GetType();
        var value = JsonSerializer.SerializeToNode(payload, type, JsonOptions)!.AsObject();
        Assert.NotNull(JsonSerializer.Deserialize(value.ToJsonString(), type, JsonOptions));
        value[field] = null;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(value.ToJsonString(), type, JsonOptions));
    }

    [Fact]
    public void PackageActionKinds_AreExactAndContainNoBooleanCompatibilityFlag()
    {
        var fields = typeof(RegistryPackageActionKinds).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.Equal(new[] { "keep", "package-install", "package-remove", "package-update" },
            fields.Select(field => (string)field.GetRawConstantValue()!).Order(StringComparer.Ordinal));
        Assert.Null(typeof(RegistryPackageInstallPlanItem).GetProperty("IsUpdate"));
    }

    [Fact]
    public void InstallPlan_PreservesExactPackageTargetRequirementsWithoutSelectingRuntimeBytes()
    {
        var plan = Plan();
        var target = Assert.Single(Assert.Single(plan.Items).Targets);
        Assert.Equal(("runtime", "win-x64", Requirement()), (target.Role, target.Rid, target.ExecutionRuntime));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(plan, JsonOptions));
        AssertPropertyNames(json.RootElement.GetProperty("items")[0].GetProperty("targets")[0].GetProperty("executionRuntime"),
            "runtimeId", "versionRange", "moduleAbi");
        Assert.All(Assert.Single(plan.Items).Artifacts, artifact => Assert.Equal("manifest-sha", artifact.ManifestSha256));
    }

    [Fact]
    public void PlanCollectionsAreBoundedDetachedAndReadOnlyOnConstructionAndReplacement()
    {
        var targets = new List<RegistryPackageTarget> { Target() };
        var item = Plan().Items[0] with { Targets = targets };
        targets.Clear();
        Assert.Single(item.Targets);
        Assert.Throws<NotSupportedException>(() => ((IList<RegistryPackageTarget>)item.Targets).Clear());
        var items = new List<RegistryPackageInstallPlanItem> { item };
        var plan = new RegistryResolveInstallPlanResponse(true, items, [], [], [], []);
        items.Clear();
        Assert.Single(plan.Items);
        Assert.Throws<ArgumentException>(() => item with { Targets = [null!] });
        Assert.Throws<ArgumentException>(() => plan with
        { Items = Enumerable.Repeat(item, RegistryPackagePlanLimits.MaximumItems + 1).ToArray() });
        Assert.Throws<ArgumentException>(() => new RegistryResolvePackageChangesRequest(
            Enumerable.Repeat(new RegistryPackageChangeRequest("example.package", null, []), RegistryPackagePlanLimits.MaximumItems + 1).ToArray(), [], Compatibility()));
    }

    private static JsonDocument ReadFixture(string fixtureName)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName)));

    private static void AssertPropertyNames(JsonElement element, params string[] expected)
        => Assert.Equal(expected, element.EnumerateObject().Select(property => property.Name));

    private static RegistryPackageProjectionArtifact Projection(string kind, string? rid, string downloadUrl)
        => new(kind, rid, "projection-sha", 42, downloadUrl, "canonical-sha", "manifest-sha", "projection-content-id", 1);

    private static RegistryClientCompatibilityProfile Compatibility()
        => new([1], ["worker"], "worker-protocol.v1",
            ["core.v1", "execution-runtime.v1", "rpc.v1", "sdk-baseline-1-1.v1", "worker-protocol.v1"], "sdk-baseline-1-1.v1", "win-x64");

    private static RegistryExecutionRuntimeRequirement Requirement()
        => new("sunder.runtime.dotnet", ">=10.0.0 <11.0.0", "sunder.dotnet-worker-module.v1");

    private static RegistryPackageTarget Target()
        => new("runtime", "win-x64", "worker", "bin/sample-worker.dll", "net10.0", "1.1.0",
            ["core.v1", "execution-runtime.v1", "rpc.v1", "sdk-baseline-1-1.v1", "worker-protocol.v1"], ExecutionRuntime: Requirement());

    private static RegistryResolveInstallPlanResponse Plan()
        => new(true,
            [new RegistryPackageInstallPlanItem("sunder.package.sample", null, "1.2.3", "package-content-identity-123",
                RegistryPackageActionKinds.PackageInstall, null, [], [Target()],
                [Projection("shared", null, "/api/v1/packages/sunder.package.sample/versions/1.2.3/projections/shared/download"),
                 Projection("runtime", "win-x64", "/api/v1/packages/sunder.package.sample/versions/1.2.3/projections/runtime/win-x64/download")])],
            [], [], [], ["https://artifacts.example"], "plan-fingerprint-001", DateTimeOffset.Parse("2026-07-11T10:05:00Z"));
}
