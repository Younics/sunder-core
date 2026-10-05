using Sunder.Sdk.Rpc;
using Wire = Sunder.Sdk.Stacks.Wire;

namespace Sunder.Sdk.Stacks;

[SunderRpcService(SunderRpcExposure.Public,
    ContractId = SunderStackContributorRpc.ContractId, ServiceId = SunderStackContributorRpc.ServiceId)]
[SunderRpcDomainError("stack.method.unsupported",
    Description = "The contributor does not implement the requested optional Stack operation.")]
internal sealed partial class StackContributorRpcServiceAdapter
{
    private readonly StackContributorServiceAdapter _adapter;

    internal partial ISunderRpcServiceHandler CreateSunderRpcServiceHandler();

    public StackContributorRpcServiceAdapter(IPackageStackContributor contributor)
        => _adapter = new(contributor);

    [SunderRpcMethod(MethodId = "metadata", Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public async ValueTask<Wire.StackContributorMetadata> GetMetadataAsync(StackRpcEmpty request)
    {
        var value = await _adapter.GetMetadataAsync().ConfigureAwait(false);
        return new() { ContributorId = value.ContributorId, DisplayName = value.DisplayName,
            SupportsExport = value.SupportsExport, SupportsImport = value.SupportsImport, SupportsImportApplied = value.SupportsImportApplied };
    }

    [SunderRpcMethod(MethodId = "list-export-items", Idempotency = SunderRpcMethodIdempotency.Idempotent,
        Errors = ["stack.method.unsupported"])]
    public async ValueTask<Wire.StackExportItemList> ListExportItemsAsync(
        Wire.StackExportDiscoveryContext request,
        CancellationToken cancellationToken)
        => new()
        {
            Items = new()
            {
                Value = (await _adapter.ListExportItemsAsync(
                    request.ToPublic(), cancellationToken).ConfigureAwait(false)).Items
                    .Select(Wire.StackExportItemDescriptor.FromPublic).ToArray(),
            },
        };

    [SunderRpcMethod(MethodId = "export", Idempotency = SunderRpcMethodIdempotency.Idempotent,
        Errors = ["stack.method.unsupported"])]
    public async ValueTask<Wire.StackRpcExportContribution> ExportAsync(
        Wire.StackExportRequest request,
        SunderRpcInvocationContext context,
        CancellationToken cancellationToken)
    {
        var contribution = await _adapter.ExportAsync(request.ToPublic(), context, cancellationToken).ConfigureAwait(false);

        return new()
        {
            Fragments = new() { Value = contribution.Fragments.Select(Wire.StackRpcFragmentExport.FromPublic).ToArray() },
            PackageRequirements = new()
            {
                Value = contribution.PackageRequirements.Select(Wire.StackPackageRequirement.FromPublic).ToArray(),
            },
            Warnings = Wire.StringList.FromPublic(contribution.Warnings),
        };
    }

    [SunderRpcMethod(MethodId = "preview-import", Idempotency = SunderRpcMethodIdempotency.Idempotent,
        Errors = ["stack.method.unsupported"])]
    public async ValueTask<Wire.StackImportPreview> PreviewImportAsync(
        Wire.StackRpcImportPreviewRequest request,
        SunderRpcInvocationContext context,
        CancellationToken cancellationToken)
        => Wire.StackImportPreview.FromPublic(await _adapter.PreviewImportAsync(
            new StackRpcImportPreviewRequest(
                request.Fragments.Value.Select(fragment => fragment.ToPublic()).ToArray(),
                request.InputValues.ToPublic(),
                request.IdRemaps.ToPublic()),
            context, cancellationToken).ConfigureAwait(false));

    [SunderRpcMethod(MethodId = "import", Idempotency = SunderRpcMethodIdempotency.None,
        Errors = ["stack.method.unsupported"])]
    public async ValueTask<Wire.StackImportResult> ImportAsync(
        Wire.StackRpcImportRequest request,
        SunderRpcInvocationContext context,
        CancellationToken cancellationToken)
        => Wire.StackImportResult.FromPublic(await _adapter.ImportAsync(
            new StackRpcImportRequest(
                request.Fragments.Value.Select(fragment => fragment.ToPublic()).ToArray(),
                request.InputValues.ToPublic(),
                request.IdRemaps.ToPublic(),
                request.SelectedActionIds.ToPublic()),
            context, cancellationToken).ConfigureAwait(false));

    [SunderRpcMethod(MethodId = "import-applied", Idempotency = SunderRpcMethodIdempotency.None,
        Errors = ["stack.method.unsupported"])]
    public async ValueTask<StackRpcEmpty> ImportAppliedAsync(
        Wire.StackImportAppliedContext request,
        SunderRpcInvocationContext context,
        CancellationToken cancellationToken)
    {
        await _adapter.ImportAppliedAsync(
            new StackImportAppliedContext(
                request.OwnerPackageId,
                request.ContributorId,
                request.FragmentIds.ToPublic(),
                request.ImportedItems.Value.Select(item => item.ToPublic()).ToArray()),
            context, cancellationToken).ConfigureAwait(false);
        return StackRpcEmpty.Instance;
    }
}
