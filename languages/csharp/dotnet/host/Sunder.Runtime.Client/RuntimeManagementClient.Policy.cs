using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    public Task<RuntimeAppPolicySessionDescriptor> OpenAppPolicySessionAsync(RuntimeAppPolicySessionOpenRequest request, CancellationToken token = default)
        => PostAsync<RuntimeAppPolicySessionOpenRequest, RuntimeAppPolicySessionDescriptor>("app-policy-sessions", request, token);
    public Task<RuntimeAppPolicySessionStatus> GetAppPolicySessionStatusAsync(string sessionId, CancellationToken token = default)
        => GetRequiredAsync<RuntimeAppPolicySessionStatus>($"app-policy-sessions/{Uri.EscapeDataString(sessionId)}", token);
    public Task<RuntimeAppPolicySessionStatus> HeartbeatAppPolicySessionAsync(RuntimeAppPolicySessionRequest request, CancellationToken token = default)
        => PostAsync<RuntimeAppPolicySessionRequest, RuntimeAppPolicySessionStatus>($"app-policy-sessions/{Uri.EscapeDataString(request.SessionId)}/heartbeat", request, token);
    public Task<RuntimeAppPackageLoadPermitResult> GetAppPackageLoadPermitAsync(RuntimeAppPackageLoadPermitRequest request, CancellationToken token = default)
        => PostAsync<RuntimeAppPackageLoadPermitRequest, RuntimeAppPackageLoadPermitResult>($"app-policy-sessions/{Uri.EscapeDataString(request.Session.SessionId)}/load-permit", request, token);
    public Task<RuntimeAppPolicySessionStatus> ShutdownAppPolicySessionAsync(RuntimeAppPolicySessionRequest request, CancellationToken token = default)
        => PostAsync<RuntimeAppPolicySessionRequest, RuntimeAppPolicySessionStatus>($"app-policy-sessions/{Uri.EscapeDataString(request.SessionId)}/shutdown", request, token);

    public RuntimeManagementClient(Uri runtimeUrl, RuntimeClientPolicyOptions policy)
        : this(() => RuntimeConnectionInfoStore.LoadFor(runtimeUrl), policy: policy)
    {
    }
}
