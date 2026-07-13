using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    public Task<SystemStatusResponse> GetSystemStatusAsync(CancellationToken token = default)
        => GetRequiredAsync<SystemStatusResponse>("system", token);

    public Task<RuntimeResetChallengeResponse> PrepareResetAsync(CancellationToken token = default)
        => PostAsync<object, RuntimeResetChallengeResponse>("system/reset/prepare", new { }, token);

    public Task<RuntimeResetDrainResponse> DrainForResetAsync(string challenge, CancellationToken token = default)
        => PostAsync<RuntimeResetConfirmRequest, RuntimeResetDrainResponse>(
            "system/reset/drain", new RuntimeResetConfirmRequest(challenge), token);
}
