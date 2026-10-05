using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Rpc;

/// <summary>Controls the accessibility of top-level types emitted by the C# RPC binding generator.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public enum SunderRpcCSharpAccessibility
{
    /// <summary>Emits public top-level binding types. This is the compatibility default.</summary>
    Public,

    /// <summary>Emits internal top-level binding types for package-local transport implementations.</summary>
    Internal,
}

/// <summary>Emits deterministic DTO, caller-scope clients, and provider adapters from an RPC descriptor.</summary>
/// <remarks>
/// The generator is intentionally dependency-free so build tooling may host it in-process. Named formatted
/// scalars and collection aliases deliberately collapse to their ergonomic CLR value types; named string enums,
/// records, and tagged unions retain generated contract types because their names carry useful domain semantics.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public static class SunderRpcCSharpGenerator
{
    /// <summary>Generates one deterministic C# source file for a validated descriptor.</summary>
    public static string Generate(SunderRpcContractDescriptor descriptor, string namespaceName)
        => Generate(descriptor, namespaceName, SunderRpcCSharpAccessibility.Public);

    /// <summary>Generates one deterministic C# source file with explicit top-level type accessibility.</summary>
    public static string Generate(
        SunderRpcContractDescriptor descriptor,
        string namespaceName,
        SunderRpcCSharpAccessibility accessibility)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var accessibilityKeyword = accessibility switch
        {
            SunderRpcCSharpAccessibility.Public => "public",
            SunderRpcCSharpAccessibility.Internal => "internal",
            _ => throw new ArgumentOutOfRangeException(
                nameof(accessibility),
                accessibility,
                "C# RPC binding accessibility must be Public or Internal."),
        };
        if (!SunderRpcCSharpSyntax.IsNamespace(namespaceName))
        {
            throw new ArgumentException("The generated C# namespace is invalid.", nameof(namespaceName));
        }

        var model = SunderRpcCSharpGeneratorModelBuilder.Build(descriptor, accessibilityKeyword);
        return SunderRpcCSharpEmitter.Emit(model, namespaceName);
    }
}
