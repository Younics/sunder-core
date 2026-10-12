using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Compatibility;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeRegistryContractCompatibilityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static TheoryData<Type, Type> ProjectionPairs => new()
    {
        { typeof(RegistryPackageChangeRequest), typeof(RuntimeRegistryPackageChangeRequest) },
        { typeof(RegistryPackageTargetRequest), typeof(RuntimeRegistryPackageTargetRequest) },
        { typeof(RegistryPackageDependency), typeof(RuntimeRegistryPackageDependency) },
        { typeof(RegistryPackageProjectionArtifact), typeof(RuntimeRegistryPackageProjectionArtifact) },
        { typeof(RegistryPackageTarget), typeof(RuntimeRegistryPackageTarget) },
        { typeof(RegistryPackageWebView), typeof(RuntimeRegistryPackageWebView) },
        { typeof(RegistryExecutionRuntimeRequirement), typeof(RuntimeRegistryExecutionRuntimeRequirement) },
        { typeof(RegistryPackageInstallPlanItem), typeof(RuntimeRegistryPackageInstallPlanItem) },
        { typeof(RegistryPackageInstallPlanConflict), typeof(RuntimeRegistryPackageInstallPlanConflict) },
        { typeof(RegistryResolveInstallPlanResponse), typeof(RuntimeRegistryResolveInstallPlanResponse) },
    };

    [Theory]
    [MemberData(nameof(ProjectionPairs))]
    public void RuntimeProjectionTypes_StayStructurallyAligned(Type registryType, Type runtimeType)
    {
        var registryParameters = Assert.Single(registryType.GetConstructors()).GetParameters();
        var runtimeParameters = Assert.Single(runtimeType.GetConstructors()).GetParameters();
        var alignedRuntimeParameters = runtimeParameters
            .Where(static parameter => parameter.Name != "RequiredCapabilityConsents")
            .ToArray();

        Assert.Equal(registryParameters.Select(static parameter => parameter.Name), alignedRuntimeParameters.Select(static parameter => parameter.Name));
        Assert.Equal(registryParameters.Select(static parameter => parameter.HasDefaultValue), alignedRuntimeParameters.Select(static parameter => parameter.HasDefaultValue));
        Assert.Equal(registryParameters.Select(parameter => NormalizeShape(parameter.ParameterType)), alignedRuntimeParameters.Select(parameter => NormalizeShape(parameter.ParameterType)));
        if (runtimeType == typeof(RuntimeRegistryResolveInstallPlanResponse))
        {
            var consentParameter = Assert.Single(runtimeParameters, static parameter => parameter.Name == "RequiredCapabilityConsents");
            Assert.Equal("RequiredCapabilityConsents", consentParameter.Name);
            Assert.True(consentParameter.HasDefaultValue);
            Assert.Equal(
                "System.Collections.Generic.IReadOnlyList<PackageCapabilityConsent>",
                NormalizeShape(consentParameter.ParameterType));
        }
        else
        {
            Assert.Equal(registryParameters.Length, runtimeParameters.Length);
        }
    }

    [Fact]
    public void RuntimeProjection_PreservesCurrentRegistryJsonShape()
    {
        var request = new RuntimeRegistryPackageChangeRequest(
            "test.package",
            null,
            [new RuntimeRegistryPackageTargetRequest("runtime", "win-x64")],
            "preview",
            ">=1.1.0 <1.2.0",
            false);
        var registryRequest = Assert.Single(RuntimeRegistryContractMapper.ToRegistry([request]));
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(request, JsonOptions), JsonSerializer.SerializeToNode(registryRequest, JsonOptions)));

        var registryResponse = new RegistryResolveInstallPlanResponse(
            false,
            [new RegistryPackageInstallPlanItem(
                "test.package",
                "1.0.0",
                "1.1.0",
                new string('e', 64),
                RegistryPackageActionKinds.PackageUpdate,
                null,
                [new RegistryPackageDependency("test.base", ">=1.1.0 <1.2.0")],
                [new RegistryPackageTarget(
                    "runtime",
                    "win-x64",
                    "worker",
                    "lib/Test.Worker.dll",
                    "net10.0",
                    "1.1.0",
                    ["core.v1", "execution-runtime.v1", "rpc.v1", "sdk-baseline-1-1.v1", "worker-protocol.v1"],
                    [],
                    new RegistryExecutionRuntimeRequirement(
                        SunderPackageFormat.DotNetExecutionRuntimeId,
                        SunderPackageFormat.DotNetExecutionRuntimeVersionRange,
                        SunderPackageFormat.DotNetWorkerModuleAbi))],
                [new RegistryPackageProjectionArtifact(
                    "runtime",
                    "win-x64",
                    new string('a', 64),
                    42,
                    "/download",
                    new string('b', 64),
                    new string('c', 64),
                    new string('d', 64),
                    1)])],
            ["warning"],
            ["error"],
            [new RegistryPackageInstallPlanConflict(
                "test.package",
                "1.0.0",
                ">=1.1.0 <1.2.0",
                "test.root",
                RegistryV1ErrorCodes.PackageRequirementUnsatisfied,
                "Version conflict.")],
            ["https://artifacts.example"],
            "test-resolution",
            DateTimeOffset.Parse("2026-10-07T00:00:00Z"));

        var runtimeResponse = RuntimeRegistryContractMapper.ToRuntime(registryResponse);
        var runtimeJson = JsonSerializer.SerializeToNode(runtimeResponse, JsonOptions)!.AsObject();

        Assert.Empty(runtimeResponse.RequiredCapabilityConsents);
        Assert.True(runtimeJson.Remove("requiredCapabilityConsents"));
        Assert.True(JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(registryResponse, JsonOptions),
            runtimeJson));
    }

    [Fact]
    public void RuntimeProjection_PreservesExactCompatibilityConflictDiagnostics()
    {
        var registry = new RegistryResolveInstallPlanResponse(false, [], [], [], [], [])
        {
            CompatibilityConflicts = [new("example.authority", PackagePolicyErrorCodes.ContractAuthorityReleaseConflict,
                [new("example.authority", ">=1.0.0 <2.0.0", false, "required",
                    [new("example.rpc", "1.0.0", new('a', 64), new('b', 64))], "example.consumer")])],
        };
        var mapped = RuntimeRegistryContractMapper.ToRuntime(registry);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(registry.CompatibilityConflicts, JsonOptions),
            JsonSerializer.SerializeToNode(mapped.CompatibilityConflicts, JsonOptions)));
    }

    [Fact]
    public void RegistryPlanTargets_RejectUnknownRuntimeRequirementFields()
    {
        var json = """
            {
              "role":"runtime",
              "rid":"win-x64",
              "kind":"worker",
              "entryPoint":"lib/Test.Worker.dll",
              "targetFramework":"net10.0",
              "sdkVersion":"1.1.0",
              "requiredHostCapabilities":["core.v1"],
              "views":[],
              "executionRuntime":{
                "runtimeId":"sunder.runtime.dotnet",
                "versionRange":">=10.0.0 <11.0.0",
                "moduleAbi":"sunder.dotnet-worker-module.v1",
                "selectedArtifact":"forbidden"
              }
            }
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryPackageTarget>(json, JsonOptions));
    }

    [Fact]
    public void RegistryCompatibilityProfile_TracksCurrentManifestWorkerAndSdkSupport()
    {
        var profile = RuntimeRegistryContractMapper.CompatibilityProfile;

        Assert.Equal([SunderPackageFormat.CurrentManifestVersion], profile.ManifestVersions);
        Assert.Equal([SunderPackageFormat.WorkerTargetKind], profile.RuntimeTargetKinds);
        Assert.Equal(SunderWorkerProtocol.V1Capability, profile.WorkerProtocol);
        Assert.Equal(
            SunderSdkCompatibilityProfile.WorkerV1CapabilityClassification
                .Where(static capability => capability.Value)
                .Select(static capability => capability.Key)
                .Order(StringComparer.Ordinal),
            profile.WorkerCapabilities);
        Assert.Contains(profile.WorkerProtocol, profile.WorkerCapabilities);
        Assert.Equal(SunderSdkCapabilities.Baseline11V1, profile.SdkBaseline);
        Assert.Contains(profile.SdkBaseline, profile.WorkerCapabilities);
        Assert.Equal(PackageTargetSelection.GetCurrentRuntimeIdentifier(), profile.Rid);
    }

    private static string NormalizeShape(Type type)
    {
        if (type.IsGenericType)
        {
            var name = type.GetGenericTypeDefinition().FullName!.Split('`')[0];
            return $"{name}<{string.Join(',', type.GetGenericArguments().Select(NormalizeShape))}>";
        }

        return type.Namespace is "Sunder.Registry.Contracts" or "Sunder.Runtime.Contracts"
            ? type.Name.Replace("RuntimeRegistry", string.Empty, StringComparison.Ordinal)
                .Replace("Registry", string.Empty, StringComparison.Ordinal)
            : type.FullName ?? type.Name;
    }
}
