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
        var service = Assert.Single(descriptor.Services);
        Assert.Equal(SunderStackContributorRpc.ServiceId, service.ServiceId);
        Assert.Equal(
            ["metadata", "list-export-items", "export", "preview-import", "import", "import-applied"],
            service.Methods.Select(static method => method.MethodId));
        Assert.All(service.Methods, static method => Assert.Equal(Sunder.Sdk.Rpc.SunderRpcMethodKind.Unary, method.Kind));
    }
}
