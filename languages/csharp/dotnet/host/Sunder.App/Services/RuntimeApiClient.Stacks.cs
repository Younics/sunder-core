using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed partial class RuntimeApiClient
{
    public Task<RuntimeStackExportDiscoveryResponse> ListStackExportItemsAsync(CancellationToken cancellationToken = default)
        => _management.ListStackExportItemsAsync(cancellationToken);

    public Task<RuntimeStackExportResponse> ExportStackAsync(RuntimeStackExportRequest request, CancellationToken cancellationToken = default)
        => _management.ExportStackAsync(request, cancellationToken);

    public Task<RuntimeStackImportPreviewResponse> PreviewStackImportAsync(RuntimeStackImportPreviewRequest request, CancellationToken cancellationToken = default)
        => _management.PreviewStackImportAsync(request, cancellationToken);

    public Task<RuntimeStackImportResponse> ImportStackAsync(RuntimeStackImportRequest request, CancellationToken cancellationToken = default)
        => _management.ImportStackAsync(request, cancellationToken);
}
