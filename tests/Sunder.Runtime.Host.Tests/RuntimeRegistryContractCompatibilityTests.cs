using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeRegistryContractCompatibilityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static TheoryData<Type, Type> ProjectionPairs => new()
    {
        { typeof(RegistryPackageChangeRequest), typeof(RuntimeRegistryPackageChangeRequest) },
        { typeof(RegistryPackageDependency), typeof(RuntimeRegistryPackageDependency) },
        { typeof(RegistryPackageArtifact), typeof(RuntimeRegistryPackageArtifact) },
        { typeof(RegistryPackageCompatibility), typeof(RuntimeRegistryPackageCompatibility) },
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

        Assert.Equal(registryParameters.Select(static parameter => parameter.Name), runtimeParameters.Select(static parameter => parameter.Name));
        Assert.Equal(registryParameters.Select(static parameter => parameter.HasDefaultValue), runtimeParameters.Select(static parameter => parameter.HasDefaultValue));
        Assert.Equal(registryParameters.Select(parameter => NormalizeShape(parameter.ParameterType)), runtimeParameters.Select(parameter => NormalizeShape(parameter.ParameterType)));
    }

    [Fact]
    public void RuntimeProjection_PreservesCurrentRegistryJsonShape()
    {
        var request = new RuntimeRegistryPackageChangeRequest("test.package", null, "preview", ">=1.1.0 <1.2.0", false);
        var registryRequest = Assert.Single(RuntimeRegistryContractMapper.ToRegistry([request]));
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(request, JsonOptions), JsonSerializer.SerializeToNode(registryRequest, JsonOptions)));

        var registryResponse = new RegistryResolveInstallPlanResponse(
            false,
            [new RegistryPackageInstallPlanItem(
                "test.package",
                "1.0.0",
                "1.1.0",
                true,
                null,
                [new RegistryPackageDependency("test.base", ">=1.1.0 <1.2.0")],
                new RegistryPackageArtifact(new string('a', 64), 42, "/download"),
                new RegistryPackageCompatibility(1, "1.1.0", ["core.v1"], "net10.0", 1, 1))],
            ["warning"],
            ["error"],
            [new RegistryPackageInstallPlanConflict(
                "test.package",
                "1.0.0",
                ">=1.1.0 <1.2.0",
                "test.root",
                RegistryV1ErrorCodes.PackageRequirementUnsatisfied,
                "Version conflict.")]);

        var runtimeResponse = RuntimeRegistryContractMapper.ToRuntime(registryResponse);

        Assert.True(JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(registryResponse, JsonOptions),
            JsonSerializer.SerializeToNode(runtimeResponse, JsonOptions)));
    }

    [Fact]
    public void RuntimeRegistryProjection_DeserializesNMinusOneJsonWithCurrentDefaults()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "RuntimeRegistryProjection.n-1.json");
        var payload = JsonSerializer.Deserialize<RuntimeProjectionPayload>(File.ReadAllText(fixturePath), JsonOptions);

        Assert.NotNull(payload);
        Assert.Null(payload.Request.VersionRange);
        Assert.True(payload.Request.Required);
        Assert.Equal("registry.v1.resource.conflict", Assert.Single(payload.Response.Conflicts).ErrorCode);
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

    private sealed record RuntimeProjectionPayload(
        RuntimeRegistryPackageChangeRequest Request,
        RuntimeRegistryResolveInstallPlanResponse Response);
}
