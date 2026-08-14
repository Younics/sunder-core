using System.Reflection;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Quickstart.Protocol;

public static class QuickstartProtocol
{
    private const string DescriptorResourceName =
        "Sunder.Package.Quickstart.Protocol.Contracts.greeting.rpc.json";
    private static readonly Lazy<SunderRpcContractDescriptor> GreetingDescriptor =
        new(LoadDescriptor, LazyThreadSafetyMode.ExecutionAndPublication);

    public static SunderRpcContractDescriptor Greeting => GreetingDescriptor.Value;

    private static SunderRpcContractDescriptor LoadDescriptor()
    {
        var assembly = typeof(QuickstartProtocol).Assembly;
        using var stream = assembly.GetManifestResourceStream(DescriptorResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded RPC descriptor '{DescriptorResourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return SunderRpcContractDescriptor.Parse(memory.ToArray());
    }
}
