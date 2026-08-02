using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class StackContributorRpcDescriptorTests
{
    [Fact]
    public void Descriptor_ParsesEmbeddedAuthoritativeContract()
    {
        var descriptor = SunderStackContributorRpc.Descriptor;

        Assert.Equal(SunderStackContributorRpc.ContractId, descriptor.ContractId);
        Assert.Equal(SunderStackContributorRpc.ContractVersion, descriptor.Version);
        Assert.Equal(
            descriptor.Sha256,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(descriptor.GetCanonicalUtf8()))
                .ToLowerInvariant());
        var service = Assert.Single(descriptor.Services);
        Assert.Equal(SunderStackContributorRpc.ServiceId, service.ServiceId);
        Assert.Equal(
            ["metadata", "list-export-items", "export", "preview-import", "import", "import-applied"],
            service.Methods.Select(static method => method.MethodId));
        Assert.All(service.Methods, static method => Assert.Equal(Sunder.Sdk.Rpc.SunderRpcMethodKind.Unary, method.Kind));
    }

    [Fact]
    public void RequiredInputSensitivity_RoundTripsThroughRpcBinding()
    {
        var schema = SunderStackContributorRpc.Descriptor.Definitions[nameof(StackRequiredInputDescriptor)];
        Assert.Contains(
            schema.GetProperty("required").EnumerateArray(),
            property => property.GetString() == "sensitivity");

        var input = new StackRequiredInputDescriptor(
            "api-key",
            "API key",
            StackValueSensitivity.Secret,
            Description: "Provider credential.");
        var json = System.Text.Json.JsonSerializer.Serialize(input, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<StackRequiredInputDescriptor>(
            json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.NotNull(roundTrip);
        Assert.Equal(StackValueSensitivity.Secret, roundTrip.Sensitivity);
        Assert.Contains("\"sensitivity\":1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ContributorAdapters_RequireTheCommonContributorContract()
    {
        Assert.Contains(typeof(IPackageStackContributor), typeof(IPackageStackExporter).GetInterfaces());
        Assert.Contains(typeof(IPackageStackContributor), typeof(IPackageStackImporter).GetInterfaces());
        Assert.Contains(typeof(IPackageStackContributor), typeof(IPackageStackImportAppliedHandler).GetInterfaces());
        Assert.Equal(
            typeof(IPackageStackContributor),
            typeof(SunderStackContributorRpc).GetMethod(nameof(SunderStackContributorRpc.CreateHandler))!
                .GetParameters()[0].ParameterType);
        Assert.All(
            typeof(SunderStackContributionRegistryExtensions).GetMethods()
                .Where(method => method.Name == nameof(SunderStackContributionRegistryExtensions.RegisterStackContributor)),
            method => Assert.Equal(typeof(IPackageStackContributor), method.GetParameters()[2].ParameterType));
    }
}
