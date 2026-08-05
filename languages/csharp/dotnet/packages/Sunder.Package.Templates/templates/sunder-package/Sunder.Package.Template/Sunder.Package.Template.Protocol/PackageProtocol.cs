using System.Reflection;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Template.Protocol;

public static class PackageProtocol
{
    private const string DescriptorResourceName =
        "Sunder.Package.Template.Protocol.Contracts.sample.rpc.json";
    private static readonly Lazy<SunderRpcContractDescriptor> ContractDescriptor =
        new(LoadDescriptor, LazyThreadSafetyMode.ExecutionAndPublication);

    public static SunderRpcContractDescriptor Sample => ContractDescriptor.Value;

    private static SunderRpcContractDescriptor LoadDescriptor()
    {
        var assembly = typeof(PackageProtocol).Assembly;
        using var stream = assembly.GetManifestResourceStream(DescriptorResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded RPC descriptor '{DescriptorResourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return SunderRpcContractDescriptor.Parse(memory.ToArray());
    }
}
