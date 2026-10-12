using System.Net.Http.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    public Task<RuntimeExecutionRuntimeSettings> GetExecutionRuntimeSettingsAsync(CancellationToken token = default)
        => GetRequiredAsync<RuntimeExecutionRuntimeSettings>(
            ExecutionRuntimeApiPath(RuntimeApiRoutes.ExecutionRuntimeSettings), ExecutionRuntimeFeature, token);

    public async Task<RuntimeExecutionRuntimeSettings> UpdateExecutionRuntimeSettingsAsync(
        RuntimeExecutionRuntimeSettingsUpdateRequest request,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(HttpMethod.Put, CreateUri(ExecutionRuntimeApiPath(RuntimeApiRoutes.ExecutionRuntimeSettings)))
        {
            Content = JsonContent.Create(request),
        };
        message.Options.Set(RuntimeAuthenticatedHttpMessageHandler.RequiredFeaturesKey, new[] { ExecutionRuntimeFeature });
        using var response = await _transport.SendAsync(message, cancellationToken: token).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<RuntimeExecutionRuntimeSettings>(response, token).ConfigureAwait(false);
    }

    public Task<RuntimeExecutionRuntimeUpdateStatus> GetExecutionRuntimeUpdateStatusAsync(CancellationToken token = default)
        => GetRequiredAsync<RuntimeExecutionRuntimeUpdateStatus>(
            ExecutionRuntimeApiPath(RuntimeApiRoutes.ExecutionRuntimeUpdateStatus), ExecutionRuntimeFeature, token);

    public Task<RuntimeExecutionRuntimeCatalogPage> GetExecutionRuntimeCatalogAsync(
        RuntimeExecutionRuntimeCatalogRequest request,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = request.SourceId switch
        {
            null => string.Empty,
            ConfiguredOfficialSourceId.DotNet => "&sourceId=dotnet",
            ConfiguredOfficialSourceId.Node => "&sourceId=node",
            _ => throw new ArgumentOutOfRangeException(nameof(request), "Unknown official runtime source."),
        };
        var query = FormattableString.Invariant($"?skip={request.Skip}&take={request.Take}") + source;
        return GetRequiredAsync<RuntimeExecutionRuntimeCatalogPage>(
            ExecutionRuntimeApiPath(RuntimeApiRoutes.ExecutionRuntimeCatalog) + query, ExecutionRuntimeFeature, token);
    }

    public Task<RuntimeExecutionRuntimeInstallPlan> PreviewExecutionRuntimeInstallAsync(
        RuntimeExecutionRuntimeInstallPreviewRequest request,
        CancellationToken token = default)
        => PostAsync<RuntimeExecutionRuntimeInstallPreviewRequest, RuntimeExecutionRuntimeInstallPlan>(
            ExecutionRuntimeApiPath(RuntimeApiRoutes.ExecutionRuntimeInstallPreview), request, ExecutionRuntimeFeature, token);

    public Task<RuntimeExecutionRuntimeOperationResult> ApplyExecutionRuntimeInstallAsync(
        RuntimeExecutionRuntimePlanConfirmation request,
        CancellationToken token = default)
        => PostAsync<RuntimeExecutionRuntimePlanConfirmation, RuntimeExecutionRuntimeOperationResult>(
            ExecutionRuntimeApiPath(RuntimeApiRoutes.ExecutionRuntimeInstallApply), request, ExecutionRuntimeFeature, token);

    public Task<RuntimePackageRuntimePrerequisitePlan> PreviewPackageRuntimePrerequisitesAsync(
        RuntimePackageRuntimePrerequisiteRequest request,
        CancellationToken token = default)
        => PostAsync<RuntimePackageRuntimePrerequisiteRequest, RuntimePackageRuntimePrerequisitePlan>(
            ExecutionRuntimeApiPath(RuntimeApiRoutes.PackageRuntimePrerequisites), request,
            request.Adoption is not null || request.Local?.Mutations.Any(mutation => mutation.UploadRetention is not null) == true
                ? RuntimeProtocolFeatures.PackageInstallResumptionV1 : ExecutionRuntimeFeature, token);

    public Task<RuntimeExecutionRuntimeUpdatePlan> PreviewExecutionRuntimeUpdateAsync(
        RuntimeExecutionRuntimeUpdatePreviewRequest request,
        CancellationToken token = default)
        => PostAsync<RuntimeExecutionRuntimeUpdatePreviewRequest, RuntimeExecutionRuntimeUpdatePlan>(
            ExecutionRuntimeApiPath(RuntimeApiRoutes.ExecutionRuntimeUpdatePreview), request, ExecutionRuntimeFeature, token);

    public Task<RuntimeExecutionRuntimeUpdateResult> PrepareExecutionRuntimeUpdateAsync(
        RuntimeExecutionRuntimePlanConfirmation request,
        CancellationToken token = default)
        => PostAsync<RuntimeExecutionRuntimePlanConfirmation, RuntimeExecutionRuntimeUpdateResult>(
            ExecutionRuntimeApiPath(RuntimeApiRoutes.ExecutionRuntimeUpdatePrepare), request, ExecutionRuntimeFeature, token);

    public Task<RuntimeExecutionRuntimeUpdateResult> ApplyExecutionRuntimeUpdateAsync(
        RuntimeExecutionRuntimeUpdateApplyRequest request,
        CancellationToken token = default)
        => PostAsync<RuntimeExecutionRuntimeUpdateApplyRequest, RuntimeExecutionRuntimeUpdateResult>(
            ExecutionRuntimeApiPath(RuntimeApiRoutes.ExecutionRuntimeUpdateApply), request, ExecutionRuntimeFeature, token);

    public Task<RuntimeExecutionRuntimeUninstallPlan> PreviewExecutionRuntimeUninstallAsync(
        RuntimeExecutionRuntimeUninstallPreviewRequest request,
        CancellationToken token = default)
        => PostAsync<RuntimeExecutionRuntimeUninstallPreviewRequest, RuntimeExecutionRuntimeUninstallPlan>(
            ExecutionRuntimeApiPath(RuntimeApiRoutes.ExecutionRuntimeUninstallPreview), request, ExecutionRuntimeFeature, token);

    public Task<RuntimeExecutionRuntimeUninstallResult> ApplyExecutionRuntimeUninstallAsync(
        RuntimeExecutionRuntimePlanConfirmation request,
        CancellationToken token = default)
        => PostAsync<RuntimeExecutionRuntimePlanConfirmation, RuntimeExecutionRuntimeUninstallResult>(
            ExecutionRuntimeApiPath(RuntimeApiRoutes.ExecutionRuntimeUninstallApply), request, ExecutionRuntimeFeature, token);

    // RuntimeClientTransport already prepends api/v1. Keep the shared route constants authoritative.
    private static string ExecutionRuntimeApiPath(string route) => route[(RuntimeApiRoutes.Root.Length + 1)..];
}
