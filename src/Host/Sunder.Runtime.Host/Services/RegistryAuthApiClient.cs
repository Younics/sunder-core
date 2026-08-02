using System.Net.Http.Json;
using Sunder.Registry.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryAuthApiClient(RegistryHttpClient registryClient)
{
    public Task<HttpResponseMessage> SendProfileRequestAsync(
        Uri origin,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "api/v1/me"));
        request.Headers.Authorization = new("Bearer", accessToken);
        return registryClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public Task<HttpResponseMessage> RevokeCurrentTokenAsync(
        Uri origin,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(origin, "api/v1/cli-auth/token"));
        request.Headers.Authorization = new("Bearer", accessToken);
        return registryClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => registryClient.EnsureSuccessAsync(response, cancellationToken);

    public async Task<RegistryCredential> ExchangeAsync(
        Uri origin,
        string code,
        string verifier,
        CancellationToken cancellationToken)
    {
        using var tokenResponse = await registryClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "api/v1/cli-auth/token"))
            {
                Content = JsonContent.Create(new RegistryCliTokenRequest(code, verifier)),
            },
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var token = await registryClient.ReadJsonAsync<RegistryCliTokenResponse>(tokenResponse, cancellationToken);
        if (token is null || !token.Success || string.IsNullOrWhiteSpace(token.Token))
        {
            var error = token?.Errors.Count > 0
                ? string.Join(" ", token.Errors)
                : await registryClient.ReadErrorAsync(tokenResponse, cancellationToken);
            throw new InvalidOperationException(error);
        }

        var credential = new RegistryCredential(token.Token, token.UserId, token.ExpiresAtUtc, null, null, null, null, false);
        using var profileResponse = await SendProfileRequestAsync(origin, credential.AccessToken, cancellationToken);
        await registryClient.EnsureSuccessAsync(profileResponse, cancellationToken);
        var user = await registryClient.ReadJsonAsync<RegistryCurrentUserResponse>(profileResponse, cancellationToken)
                   ?? throw new InvalidDataException("Registry returned an empty current-user response.");
        return ApplyProfile(credential, user);
    }

    public async Task<RegistryCredential> ReadProfileAsync(
        RegistryCredential credential,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await registryClient.EnsureSuccessAsync(response, cancellationToken);
        var user = await registryClient.ReadJsonAsync<RegistryCurrentUserResponse>(response, cancellationToken)
                   ?? throw new InvalidDataException("Registry returned an empty current-user response.");
        return ApplyProfile(credential, user);
    }

    private static RegistryCredential ApplyProfile(RegistryCredential credential, RegistryCurrentUserResponse user)
        => credential with
        {
            UserId = user.UserId,
            Username = user.Username,
            DisplayName = user.DisplayName,
            Email = user.Email,
            AvatarUrl = user.AvatarUrl,
            RequiresUsername = user.RequiresUsername,
        };
}
