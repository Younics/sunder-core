using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Stacks;

/// <summary>Composes local Stack capabilities and Host-mediated content behavior for package-local generated platform bindings.</summary>
/// <remarks>The adapter supplies domain behavior only. Contract identity, codecs, dispatch and registration come from the consuming package's verified platform import.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
[SunderSdkCapability(SunderSdkCapabilities.StacksRpcV1)]
public sealed class StackContributorServiceAdapter
{
    private readonly IPackageStackExporter? _exporter;
    private readonly IPackageStackImporter? _importer;
    private readonly IPackageStackImportAppliedHandler? _appliedHandler;
    private readonly StackContributorMetadata _metadata;

    /// <summary>Captures one contributor's identity and optional capabilities for its activation.</summary>
    public StackContributorServiceAdapter(IPackageStackContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        _exporter = contributor as IPackageStackExporter;
        _importer = contributor as IPackageStackImporter;
        _appliedHandler = contributor as IPackageStackImportAppliedHandler;
        if (_exporter is null && _importer is null && _appliedHandler is null)
            throw new ArgumentException("A Stack RPC contributor must implement at least one package-local Stack interface.", nameof(contributor));
        if (string.IsNullOrWhiteSpace(contributor.ContributorId))
            throw new ArgumentException("A Stack RPC contributor must expose a non-empty contributor id.", nameof(contributor));
        if (string.IsNullOrWhiteSpace(contributor.DisplayName))
            throw new ArgumentException("A Stack RPC contributor must expose a non-empty display name.", nameof(contributor));
        _metadata = new(contributor.ContributorId, contributor.DisplayName, _exporter is not null, _importer is not null, _appliedHandler is not null);
    }

    /// <summary>Returns the captured contributor identity and capabilities.</summary>
    [SunderRpcMethod(MethodId = "metadata", Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<StackContributorMetadata> GetMetadataAsync() => ValueTask.FromResult(_metadata);

    /// <summary>Discovers a snapshot of exportable items.</summary>
    [SunderRpcMethod(MethodId = "list-export-items", Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["stack.method.unsupported"])]
    public async ValueTask<StackExportItemList> ListExportItemsAsync(StackExportDiscoveryContext request, CancellationToken cancellationToken)
        => new(await Required(_exporter, "list-export-items").ListExportItemsAsync(request, cancellationToken).ConfigureAwait(false));

    /// <summary>Exports domain fragments, registering each payload stream as single-use Host-mediated content.</summary>
    [SunderRpcMethod(MethodId = "export", Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["stack.method.unsupported"])]
    public async ValueTask<StackRpcExportContribution> ExportAsync(StackExportRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken)
    {
        var contribution = await Required(_exporter, "export").ExportAsync(request, cancellationToken).ConfigureAwait(false);
        var fragments = new List<StackRpcFragmentExport>(contribution.Fragments.Count);
        foreach (var fragment in contribution.Fragments)
        {
            var files = new List<StackRpcPayloadFile>();
            foreach (var file in fragment.Files ?? [])
            {
                await using var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
                var content = await context.RegisterContentAsync(stream,
                    new SunderRpcContentRegistrationOptions("application/octet-stream", Path.GetFileName(file.RelativePath), file.Length,
                        null, SunderRpcContentRepeatability.SingleUse, 1), cancellationToken).ConfigureAwait(false);
                files.Add(new(file.RelativePath, content));
            }
            fragments.Add(new(fragment.FragmentId, fragment.SchemaId, fragment.SchemaVersion, fragment.DisplayName, fragment.JsonPayload,
                fragment.Description, fragment.DefaultSelected, fragment.RequiredInputs, files.Count == 0 ? null : files, fragment.SourceItemId));
        }
        return new(fragments, contribution.PackageRequirements, contribution.Warnings);
    }

    /// <summary>Previews imports with lazy content handles and Host-stamped owner/contributor identity.</summary>
    [SunderRpcMethod(MethodId = "preview-import", Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["stack.method.unsupported"])]
    public ValueTask<StackImportPreview> PreviewImportAsync(StackRpcImportPreviewRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken)
        => Required(_importer, "preview-import").PreviewImportAsync(new(
            request.Fragments.Select(fragment => ToLocal(context, fragment)).ToArray(), request.InputValues, request.IdRemaps), cancellationToken);

    /// <summary>Applies imports with lazy content handles and Host-stamped owner/contributor identity.</summary>
    [SunderRpcMethod(MethodId = "import", Idempotency = SunderRpcMethodIdempotency.None, Errors = ["stack.method.unsupported"])]
    public ValueTask<StackImportResult> ImportAsync(StackRpcImportRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken)
        => Required(_importer, "import").ImportAsync(new(
            request.Fragments.Select(fragment => ToLocal(context, fragment)).ToArray(), request.InputValues, request.IdRemaps, request.SelectedActionIds), cancellationToken);

    /// <summary>Notifies the contributor after committed effects, replacing caller-supplied owner/contributor identity.</summary>
    [SunderRpcMethod(MethodId = "import-applied", Idempotency = SunderRpcMethodIdempotency.None, Errors = ["stack.method.unsupported"])]
    public ValueTask ImportAppliedAsync(StackImportAppliedContext request, SunderRpcInvocationContext context, CancellationToken cancellationToken)
        => Required(_appliedHandler, "import-applied").OnStackImportAppliedAsync(
            new(context.Provider.PackageId, _metadata.ContributorId, request.FragmentIds, request.ImportedItems), cancellationToken);

    private StackFragmentImport ToLocal(SunderRpcInvocationContext context, StackRpcFragmentImport fragment)
        => new(fragment.FragmentId, context.Provider.PackageId, _metadata.ContributorId, fragment.SchemaId, fragment.SchemaVersion,
            fragment.DisplayName, fragment.JsonPayload, fragment.Description,
            fragment.Files?.Select(file => new StackImportPayloadHandle(file.RelativePath,
                cancellationToken => context.OpenContentAsync(file.Content, cancellationToken), file.Content.Length)).ToArray());

    private static T Required<T>(T? contribution, string methodId) where T : class
        => contribution ?? throw new SunderRpcException(new SunderRpcError(SunderRpcErrorKind.Domain, "stack.method.unsupported",
            $"This Stack contributor does not support '{methodId}'."));
}
