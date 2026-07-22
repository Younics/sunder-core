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
                "Sample.dll",
                new RegistryPackageCompatibility(1, "0.8.0", ["views.v1", "core.v1"], "net10.0", 1, 1),
                false,
                null,
                [new RegistryPackageDependency("sunder.package.base", ">=1.0.0")],
                new RegistryPackageArtifact("abc123", 42, "https://registry.example/api/v1/packages/sunder.package.sample/versions/1.2.3/download"),
                DateTimeOffset.Parse("2026-07-11T10:00:00Z")),
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
                    new RegistryPackageArtifact("abc123", 42, "/api/v1/packages/sunder.package.sample/versions/1.2.3/download"),
                    new RegistryPackageCompatibility(1, "0.8.0", ["core.v1"], "net10.0", 1, 1))],
                [],
                [],
                []),
            typeof(RegistryResolveInstallPlanResponse)
        },
        {
            "requirements.json",
            new RegistryResolvePackageChangesRequest(
                [new RegistryPackageChangeRequest(
                    "sunder.package.sample",
                    Version: null,
                    Tag: "preview",
                    VersionRange: ">=2.3.0",
                    Required: false)],
                [new RegistryInstalledPackageState(
                    "sunder.package.base",
                    "1.5.0",
                    [new RegistryPackageDependency("sunder.package.shared", ">=1.0.0")])],
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
                    "Selected package version does not satisfy the Stack requirement.")]),
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

    public static TheoryData<string, Type> NMinusOneCases => new()
    {
        { "requirements.json", typeof(RegistryResolvePackageChangesRequest) },
        { "requirement-conflict.json", typeof(RegistryResolveInstallPlanResponse) },
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

    [Theory]
    [MemberData(nameof(NMinusOneCases))]
    public void V1Payload_DeserializesNMinusOneJson(string fixtureName, Type payloadType)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "N-1", fixtureName);
        var payload = JsonSerializer.Deserialize(File.ReadAllText(fixturePath), payloadType, JsonOptions);

        Assert.NotNull(payload);
        if (payload is RegistryResolvePackageChangesRequest request)
        {
            var package = Assert.Single(request.Packages);
            Assert.Null(package.VersionRange);
            Assert.True(package.Required);
        }
        else
        {
            var response = Assert.IsType<RegistryResolveInstallPlanResponse>(payload);
            Assert.Equal(RegistryV1ErrorCodes.Conflict, Assert.Single(response.Conflicts).ErrorCode);
        }
    }
}
