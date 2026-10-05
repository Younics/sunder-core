using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Stacks;

/// <summary>Defines the authoritative Stack contributor RPC contract and local adapter factories.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
[SunderSdkCapability(SunderSdkCapabilities.StacksRpcV1)]
public static class SunderStackContributorRpc
{
    /// <summary>The Stack contributor descriptor version.</summary>
    public const int DescriptorVersion = SunderRpcProtocol.DescriptorVersion;

    /// <summary>The Stack contributor contract id.</summary>
    public const string ContractId = "sunder.stack.contributor";

    /// <summary>The Stack contributor contract version.</summary>
    public const string ContractVersion = "2.0.0";

    /// <summary>The canonical Stack contributor descriptor SHA-256.</summary>
    public const string DescriptorSha256 = "38c8f49840f951aec29398d3641ff2306c906aee12ea80c2277db734bce49898";

    /// <summary>The domain-separated Stack contributor ABI SHA-256.</summary>
    public const string AbiSha256 = "6ca6d6385c720290548592de890f3e3ceb86f3d999167f4e80c6afb15f8a02d8";

    /// <summary>The Stack contributor service id.</summary>
    public const string ServiceId = "stack-contributor";

    private const string DescriptorResourceName =
        "Sunder.Sdk.Stacks.Contracts.sunder.stack.contributor.rpc.json";
    private static readonly Lazy<SunderRpcContractDescriptor> ContractDescriptor =
        new(LoadDescriptor, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Gets the validated embedded contract descriptor.</summary>
    public static SunderRpcContractDescriptor Descriptor => ContractDescriptor.Value;

    /// <summary>Creates a JSON RPC handler over convenient package-local Stack interfaces.</summary>
    public static ISunderRpcServiceHandler CreateHandler(IPackageStackContributor contributor)
        => new StackContributorRpcHandler(contributor);

    private static SunderRpcContractDescriptor LoadDescriptor()
    {
        var assembly = typeof(SunderStackContributorRpc).Assembly;
        using var stream = assembly.GetManifestResourceStream(DescriptorResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded Stack RPC descriptor '{DescriptorResourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var descriptor = SunderRpcContractDescriptor.Parse(memory.ToArray());
        if (descriptor.DescriptorVersion != DescriptorVersion
            || !string.Equals(descriptor.ContractId, ContractId, StringComparison.Ordinal)
            || !string.Equals(descriptor.Version, ContractVersion, StringComparison.Ordinal)
            || !string.Equals(descriptor.Sha256, DescriptorSha256, StringComparison.Ordinal)
            || !string.Equals(descriptor.AbiSha256, AbiSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Embedded Stack RPC descriptor '{DescriptorResourceName}' does not match its contract identity.");
        }
        return descriptor;
    }
}

/// <summary>Describes the operations implemented by one package-scoped Stack contributor.</summary>
public sealed record StackContributorMetadata(
    string ContributorId,
    string DisplayName,
    bool SupportsExport,
    bool SupportsImport,
    bool SupportsImportApplied);

/// <summary>Wraps a deterministic export discovery result.</summary>
public sealed record StackExportItemList(IReadOnlyList<StackExportItemDescriptor> Items);

/// <summary>Associates a safe fragment-relative path with Host-mediated content.</summary>
public sealed record StackRpcPayloadFile(
    string RelativePath,
    SunderRpcContentReference Content);

/// <summary>Defines one Stack export fragment on the RPC boundary.</summary>
public sealed record StackRpcFragmentExport(
    string FragmentId,
    string SchemaId,
    int SchemaVersion,
    string DisplayName,
    string JsonPayload,
    string? Description = null,
    bool DefaultSelected = true,
    IReadOnlyList<StackRequiredInputDescriptor>? RequiredInputs = null,
    IReadOnlyList<StackRpcPayloadFile>? Files = null,
    string? SourceItemId = null);

/// <summary>Contains one contributor's complete Stack export result.</summary>
public sealed record StackRpcExportContribution(
    IReadOnlyList<StackRpcFragmentExport> Fragments,
    IReadOnlyList<StackPackageRequirement> PackageRequirements,
    IReadOnlyList<string> Warnings);

/// <summary>Defines one validated Stack import fragment on the RPC boundary.</summary>
public sealed record StackRpcFragmentImport(
    string FragmentId,
    string OwnerPackageId,
    string ContributorId,
    string SchemaId,
    int SchemaVersion,
    string DisplayName,
    string JsonPayload,
    string? Description = null,
    IReadOnlyList<StackRpcPayloadFile>? Files = null);

/// <summary>Requests a side-effect-free Stack import preview over content references.</summary>
public sealed record StackRpcImportPreviewRequest(
    IReadOnlyList<StackRpcFragmentImport> Fragments,
    IReadOnlyDictionary<string, string> InputValues,
    IReadOnlyDictionary<string, string> IdRemaps);

/// <summary>Requests application of a previewed Stack plan over content references.</summary>
public sealed record StackRpcImportRequest(
    IReadOnlyList<StackRpcFragmentImport> Fragments,
    IReadOnlyDictionary<string, string> InputValues,
    IReadOnlyDictionary<string, string> IdRemaps,
    IReadOnlyList<string> SelectedActionIds);

internal sealed class StackContributorRpcHandler : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
{
    private readonly ISunderRpcServiceHandler _handler;

    public StackContributorRpcHandler(IPackageStackContributor contributor)
        => _handler = new StackContributorRpcServiceAdapter(contributor).CreateSunderRpcServiceHandler();

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors
        => ((ISunderRpcDeclaredErrorMetadata)_handler).DeclaredErrors;

    public async ValueTask<JsonElement> InvokeUnaryAsync(
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken = default)
    {
        var service = SunderStackContributorRpc.Descriptor.FindService(serviceId);
        if (service is null)
        {
            throw NotFound("stack.service.not-found", "The Stack contributor service was not found.");
        }

        if (service.FindMethod(methodId) is null)
        {
            throw NotFound("stack.method.not-found", "The Stack contributor method was not found.");
        }
        return await _handler.InvokeUnaryAsync(context, serviceId, methodId, request, cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken = default)
        => throw NotFound(
            "stack.stream.unsupported",
            "The Stack contributor contract defines unary methods only.");

    private static Exception NotFound(string code, string message)
        => new InvalidOperationException($"{code}: {message}");
}

internal sealed class StackRpcEmpty
{
    public static StackRpcEmpty Instance { get; } = new();

    public StackRpcEmpty()
    {
    }
}
