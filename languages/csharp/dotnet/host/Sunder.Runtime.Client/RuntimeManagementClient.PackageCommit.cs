using System.Runtime.ExceptionServices;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    private async Task<PackageOperationResult> CommitPackageStageAsync(
        string stageId,
        PackageOperationResult stagedResult,
        CancellationToken token)
    {
        try
        {
            using var response = await _httpClient.PostAsync(
                CreateUri($"packages/store/stage/{Uri.EscapeDataString(stageId)}/commit"), null, token).ConfigureAwait(false);
            return await _responses.ReadRequiredJsonAsync<PackageOperationResult>(
                response, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            try
            {
                using var reconciliationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var status = await GetPackageStageStatusAsync(stageId, reconciliationDeadline.Token).ConfigureAwait(false);
                if (status.Kind == RuntimePackageStageKind.PackageStore
                    && status.State == RuntimePackageStageState.Committed)
                {
                    return stagedResult with
                    {
                        Success = true,
                        Message = status.Message ?? stagedResult.Message,
                        RuntimeSessionApplied = status.RuntimeSessionApplied,
                        RequiresAppRestart = false,
                        Errors = [],
                        CommittedStamp = status.CommittedStamp,
                        StoreCommitted = true,
                        RuntimeSessionReconciliationPending = status.ReconciliationPending,
                    };
                }
            }
            catch
            {
                // Preserve the original ambiguous transport/cancellation failure.
            }

            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
    }
}
