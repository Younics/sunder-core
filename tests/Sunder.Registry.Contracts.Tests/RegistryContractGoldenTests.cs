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
                    new RegistryPackageTarget("runtime", "win-x64", "dotnet", "lib/Sample.dll", "net10.0", "0.8.0", ["core.v1"]),
                    new RegistryPackageTarget(
                        "app",
                        "win-x64",
                        "web",
                        "web/index.html",
                        null,
                        "1.1.0",
                        ["core.v1"],
                        [new RegistryPackageWebView(
                            "sunder.package.sample.workspace",
                            "Workspace",
                            "/workspace",
                            "assets/workspace.png",
                            "rightTop",
                            true)]),
                ],
                [Projection("shared", null, "/api/v1/packages/sunder.package.sample/versions/1.2.3/projections/shared/download")],
                [new RegistryPackageContractBundle("sample.contract", "1.0.0", "contracts/sample.json", "contract-sha")],
                [new RegistryPackageContractUse("base.contract", ">=1.0.0", true, ["discover", "invoke"])],
                [new RegistryPackageProvider("sample.provider", "sample.contract", "1.0.0", "contract-sha", "runtime")],
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
                "contract-sha",
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
                    false,
                    null,
                    [],
                    [new RegistryPackageTarget("runtime", "win-x64", "dotnet", "lib/Sample.dll", "net10.0", "0.8.0", ["core.v1"])],
                    [Projection("shared", null, "/api/v1/packages/sunder.package.sample/versions/1.2.3/projections/shared/download")])],
                [],
                [],
                [],
                ["https://artifacts.example"]),
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
                    [new RegistryPackageProjectionKey("shared", null), new RegistryPackageProjectionKey("runtime", "win-x64")])],
                IncludePrerelease: true),
            typeof(RegistryResolvePackageChangesRequest)
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
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        var expected = File.ReadAllText(fixturePath).ReplaceLineEndings("\n").TrimEnd();
        var actual = JsonSerializer.Serialize(payload, payloadType, JsonOptions).ReplaceLineEndings("\n");

        Assert.Equal(expected, actual);
        Assert.NotNull(JsonSerializer.Deserialize(expected, payloadType, JsonOptions));
    }

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
}
