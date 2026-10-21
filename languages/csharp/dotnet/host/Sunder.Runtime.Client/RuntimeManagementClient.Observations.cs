using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    public async Task<RuntimeRpcAppExecutionCaptureResponse> CaptureAppWorkerExecutionAsync(
        RuntimeRpcAppExecutionCaptureRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await PostAsync<RuntimeRpcAppExecutionCaptureRequest, RuntimeRpcAppExecutionCaptureResponse>(
            "rpc/app/runtime-observations/capture-execution", request,
            RuntimeProtocolFeatures.WorkerExecutionObservationsV1, token).ConfigureAwait(false);
        EnsureRpcErrorDetails(response.Error, "Runtime Worker execution capture error details");
        return response;
    }

    public async Task<RuntimeRpcAppExecutionObserveResponse> ObserveAppWorkerExecutionAsync(
        RuntimeRpcAppExecutionObserveRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await PostAsync<RuntimeRpcAppExecutionObserveRequest, RuntimeRpcAppExecutionObserveResponse>(
            "rpc/app/runtime-observations/observe-execution", request,
            RuntimeProtocolFeatures.WorkerExecutionObservationsV1, token).ConfigureAwait(false);
        EnsureRpcErrorDetails(response.Error, "Runtime Worker execution observation error details");
        return response;
    }
}
