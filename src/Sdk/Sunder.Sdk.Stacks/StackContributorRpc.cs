using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Stacks;

/// <summary>Defines the authoritative Stack contributor RPC contract and local adapter factories.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
[SunderSdkCapability(SunderSdkCapabilities.StacksRpcV1)]
public static class SunderStackContributorRpc
{
    /// <summary>The Stack contributor contract id.</summary>
    public const string ContractId = "sunder.stack.contributor";

    /// <summary>The Stack contributor contract version.</summary>
    public const string ContractVersion = "1.0.0";

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

    /// <summary>Creates a typed client for one exact Stack contributor endpoint.</summary>
    public static StackContributorRpcClient CreateClient(
        ISunderRpcCallScope scope,
        SunderRpcEndpointReference endpoint)
        => new(scope, endpoint);

    private static SunderRpcContractDescriptor LoadDescriptor()
    {
        var assembly = typeof(SunderStackContributorRpc).Assembly;
        using var stream = assembly.GetManifestResourceStream(DescriptorResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded Stack RPC descriptor '{DescriptorResourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return SunderRpcContractDescriptor.Parse(memory.ToArray());
    }
}

/// <summary>Registers a package-local Stack contributor as a manifest-declared RPC provider.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
[SunderSdkCapability(SunderSdkCapabilities.StacksRpcV1)]
public static class SunderStackContributionRegistryExtensions
{
    /// <summary>Registers a typed Stack provider using invocation-bound Host content authority.</summary>
    public static void RegisterStackContributor(
        this ISunderRuntimeContributionRegistry registry,
        string providerId,
        IPackageStackContributor contributor,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(contributor);
        ArgumentNullException.ThrowIfNull(services);
        registry.RegisterRpcProvider(
            providerId,
            SunderStackContributorRpc.CreateHandler(contributor));
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

/// <summary>Invokes one exact Stack contributor endpoint through the Sunder RPC broker.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
[SunderSdkCapability(SunderSdkCapabilities.StacksRpcV1)]
public sealed class StackContributorRpcClient
{
    private readonly ISunderRpcCallScope _scope;
    private readonly SunderRpcEndpointReference _endpoint;

    /// <summary>Creates a client for one exact endpoint reference.</summary>
    public StackContributorRpcClient(
        ISunderRpcCallScope scope,
        SunderRpcEndpointReference endpoint)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    /// <summary>Gets stable contributor metadata.</summary>
    public ValueTask<StackContributorMetadata> GetMetadataAsync(
        CancellationToken cancellationToken = default)
        => InvokeAsync<StackRpcEmpty, StackContributorMetadata>(
            "metadata",
            StackRpcEmpty.Instance,
            cancellationToken);

    /// <summary>Lists an immutable snapshot of exportable items.</summary>
    public async ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
        StackExportDiscoveryContext context,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync<StackExportDiscoveryContext, StackExportItemList>(
            "list-export-items",
            context,
            cancellationToken).ConfigureAwait(false)).Items;

    /// <summary>Exports selected Stack items.</summary>
    public ValueTask<StackRpcExportContribution> ExportAsync(
        StackExportRequest request,
        CancellationToken cancellationToken = default)
        => InvokeAsync<StackExportRequest, StackRpcExportContribution>(
            "export",
            request,
            cancellationToken);

    /// <summary>Previews selected Stack fragments without mutation.</summary>
    public ValueTask<StackImportPreview> PreviewImportAsync(
        StackRpcImportPreviewRequest request,
        CancellationToken cancellationToken = default)
        => InvokeAsync<StackRpcImportPreviewRequest, StackImportPreview>(
            "preview-import",
            request,
            cancellationToken);

    /// <summary>Applies selected actions from a previewed Stack plan.</summary>
    public ValueTask<StackImportResult> ImportAsync(
        StackRpcImportRequest request,
        CancellationToken cancellationToken = default)
        => InvokeAsync<StackRpcImportRequest, StackImportResult>(
            "import",
            request,
            cancellationToken);

    /// <summary>Notifies the exact contributor activation after committed import effects.</summary>
    public async ValueTask ImportAppliedAsync(
        StackImportAppliedContext context,
        CancellationToken cancellationToken = default)
        => _ = await InvokeAsync<StackImportAppliedContext, StackRpcEmpty>(
            "import-applied",
            context,
            cancellationToken).ConfigureAwait(false);

    private ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        string methodId,
        TRequest request,
        CancellationToken cancellationToken)
        where TRequest : notnull
        where TResponse : notnull
        => _scope.InvokeAsync<TRequest, TResponse>(
            _endpoint,
            SunderStackContributorRpc.ServiceId,
            methodId,
            request,
            cancellationToken: cancellationToken);
}

internal sealed class StackContributorRpcHandler : ISunderRpcServiceHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly IPackageStackExporter? _exporter;
    private readonly IPackageStackImporter? _importer;
    private readonly IPackageStackImportAppliedHandler? _appliedHandler;
    private readonly StackContributorMetadata _metadata;

    public StackContributorRpcHandler(IPackageStackContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        _exporter = contributor as IPackageStackExporter;
        _importer = contributor as IPackageStackImporter;
        _appliedHandler = contributor as IPackageStackImportAppliedHandler;
        if (_exporter is null && _importer is null && _appliedHandler is null)
        {
            throw new ArgumentException(
                "A Stack RPC contributor must implement at least one package-local Stack interface.",
                nameof(contributor));
        }

        if (string.IsNullOrWhiteSpace(contributor.ContributorId))
        {
            throw new ArgumentException(
                "A Stack RPC contributor must expose a non-empty contributor id.",
                nameof(contributor));
        }
        if (string.IsNullOrWhiteSpace(contributor.DisplayName))
        {
            throw new ArgumentException(
                "A Stack RPC contributor must expose a non-empty display name.",
                nameof(contributor));
        }

        _metadata = new StackContributorMetadata(
            contributor.ContributorId,
            contributor.DisplayName,
            _exporter is not null,
            _importer is not null,
            _appliedHandler is not null);
    }

    public async ValueTask<JsonElement> InvokeUnaryAsync(
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(serviceId, SunderStackContributorRpc.ServiceId, StringComparison.Ordinal))
        {
            throw NotFound("stack.service.not-found", "The Stack contributor service was not found.");
        }

        return methodId switch
        {
            "metadata" => Serialize(_metadata),
            "list-export-items" => Serialize(new StackExportItemList(
                await Required(_exporter, methodId).ListExportItemsAsync(
                    Deserialize<StackExportDiscoveryContext>(request),
                    cancellationToken).ConfigureAwait(false))),
            "export" => Serialize(await ExportAsync(
                context,
                Deserialize<StackExportRequest>(request),
                cancellationToken).ConfigureAwait(false)),
            "preview-import" => Serialize(await Required(_importer, methodId).PreviewImportAsync(
                ToLocal(context, Deserialize<StackRpcImportPreviewRequest>(request)),
                cancellationToken).ConfigureAwait(false)),
            "import" => Serialize(await Required(_importer, methodId).ImportAsync(
                ToLocal(context, Deserialize<StackRpcImportRequest>(request)),
                cancellationToken).ConfigureAwait(false)),
            "import-applied" => await ImportAppliedAsync(
                context,
                Deserialize<StackImportAppliedContext>(request),
                cancellationToken).ConfigureAwait(false),
            _ => throw NotFound("stack.method.not-found", "The Stack contributor method was not found."),
        };
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

    private async ValueTask<StackRpcExportContribution> ExportAsync(
        SunderRpcInvocationContext context,
        StackExportRequest request,
        CancellationToken cancellationToken)
    {
        var contribution = await Required(_exporter, "export")
            .ExportAsync(request, cancellationToken).ConfigureAwait(false);
        var fragments = new List<StackRpcFragmentExport>(contribution.Fragments.Count);
        foreach (var fragment in contribution.Fragments)
        {
            var files = new List<StackRpcPayloadFile>();
            foreach (var file in fragment.Files ?? [])
            {
                await using var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
                var content = await context.RegisterContentAsync(
                    stream,
                    new SunderRpcContentRegistrationOptions(
                        "application/octet-stream",
                        Path.GetFileName(file.RelativePath),
                        file.Length,
                        null,
                        SunderRpcContentRepeatability.SingleUse,
                        1),
                    cancellationToken).ConfigureAwait(false);
                files.Add(new StackRpcPayloadFile(file.RelativePath, content));
            }

            fragments.Add(new StackRpcFragmentExport(
                fragment.FragmentId,
                fragment.SchemaId,
                fragment.SchemaVersion,
                fragment.DisplayName,
                fragment.JsonPayload,
                fragment.Description,
                fragment.DefaultSelected,
                fragment.RequiredInputs,
                files.Count == 0 ? null : files,
                fragment.SourceItemId));
        }

        return new StackRpcExportContribution(
            fragments,
            contribution.PackageRequirements,
            contribution.Warnings);
    }

    private async ValueTask<JsonElement> ImportAppliedAsync(
        SunderRpcInvocationContext invocation,
        StackImportAppliedContext context,
        CancellationToken cancellationToken)
    {
        context = context with
        {
            OwnerPackageId = invocation.Provider.PackageId,
            ContributorId = _metadata.ContributorId,
        };
        await Required(_appliedHandler, "import-applied")
            .OnStackImportAppliedAsync(context, cancellationToken).ConfigureAwait(false);
        return Serialize(StackRpcEmpty.Instance);
    }

    private StackImportPreviewRequest ToLocal(
        SunderRpcInvocationContext context,
        StackRpcImportPreviewRequest request)
        => new(
            request.Fragments.Select(fragment => ToLocal(context, fragment)).ToArray(),
            request.InputValues,
            request.IdRemaps);

    private StackImportRequest ToLocal(
        SunderRpcInvocationContext context,
        StackRpcImportRequest request)
        => new(
            request.Fragments.Select(fragment => ToLocal(context, fragment)).ToArray(),
            request.InputValues,
            request.IdRemaps,
            request.SelectedActionIds);

    private StackFragmentImport ToLocal(
        SunderRpcInvocationContext context,
        StackRpcFragmentImport fragment)
        => new(
            fragment.FragmentId,
            context.Provider.PackageId,
            _metadata.ContributorId,
            fragment.SchemaId,
            fragment.SchemaVersion,
            fragment.DisplayName,
            fragment.JsonPayload,
            fragment.Description,
            fragment.Files?.Select(file => new StackImportPayloadHandle(
                file.RelativePath,
                cancellationToken => context.OpenContentAsync(
                    file.Content,
                    cancellationToken),
                file.Content.Length)).ToArray());

    private static T Required<T>(T? contribution, string methodId)
        where T : class
        => contribution ?? throw new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.Domain,
            "stack.method.unsupported",
            $"This Stack contributor does not support '{methodId}'."));

    private static T Deserialize<T>(JsonElement value)
        where T : notnull
        => value.Deserialize<T>(JsonOptions)
           ?? throw new InvalidOperationException(
               $"The validated Stack RPC request could not be deserialized as {typeof(T).Name}.");

    private static JsonElement Serialize<T>(T value)
        where T : notnull
        => JsonSerializer.SerializeToElement(value, JsonOptions);

    private static Exception NotFound(string code, string message)
        => new InvalidOperationException($"{code}: {message}");
}

internal sealed class StackRpcEmpty
{
    public static StackRpcEmpty Instance { get; } = new();

    private StackRpcEmpty()
    {
    }
}
