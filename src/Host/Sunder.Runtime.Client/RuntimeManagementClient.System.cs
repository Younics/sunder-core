using System.Net.Http.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    public Task<SystemStatusResponse> GetSystemStatusAsync(CancellationToken token = default)
        => GetRequiredAsync<SystemStatusResponse>("system", token);

    public Task<RuntimeResetChallengeResponse> PrepareResetAsync(CancellationToken token = default)
        => PostResetControlAsync<object, RuntimeResetChallengeResponse>("system/reset/prepare", new { }, token);

    public Task<RuntimeResetDrainResponse> DrainForResetAsync(string challenge, CancellationToken token = default)
        => PostResetControlAsync<RuntimeResetConfirmRequest, RuntimeResetDrainResponse>(
            "system/reset/drain", new RuntimeResetConfirmRequest(challenge), token);

    private async Task<TResponse> PostResetControlAsync<TRequest, TResponse>(
        string path,
        TRequest body,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CreateUri(path))
        {
            Content = JsonContent.Create(body),
        };
        request.Options.Set(RuntimeAuthenticatedHttpMessageHandler.SkipProtocolNegotiationKey, true);
        using var response = await _transport.SendAsync(request, cancellationToken: token).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<TResponse>(response, token).ConfigureAwait(false);
    }
}
