using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed class RegistryAuthService(
    IRuntimeApiClientFactory runtimeApiClientFactory,
    ExternalBrowserService browserService)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(350);

    public RegistryAuthState GetCachedStatus(Uri? registryUrl = null)
        => RegistryAuthState.SignedOut(RegistryUrlHelper.Normalize(registryUrl ?? RegistryUrlHelper.DefaultRegistryUrl));

    public async Task<RegistryAuthState> GetStatusAsync(Uri? registryUrl = null, CancellationToken cancellationToken = default)
    {
        registryUrl = RegistryUrlHelper.Normalize(registryUrl ?? RegistryUrlHelper.DefaultRegistryUrl);
        using var runtime = runtimeApiClientFactory.CreateClient();
        return ToState(await runtime.GetRegistryAuthStatusAsync(registryUrl.AbsoluteUri, cancellationToken));
    }

    public async Task<RegistryAuthState> LoginAsync(Uri? registryUrl = null, CancellationToken cancellationToken = default)
    {
        registryUrl = RegistryUrlHelper.Normalize(registryUrl ?? RegistryUrlHelper.DefaultRegistryUrl);
        using var runtime = runtimeApiClientFactory.CreateClient();
        var start = await runtime.StartRegistryAuthAsync(
            new RuntimeRegistryAuthStartRequest(registryUrl.AbsoluteUri, DisplayName: "Sunder App"),
            cancellationToken);
        browserService.Open(new Uri(start.LaunchUrl));

        while (DateTimeOffset.UtcNow < start.ExpiresAtUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await runtime.GetRegistryAuthSessionAsync(start.SessionId, cancellationToken);
            if (status?.State == RuntimeRegistryAuthSessionState.Succeeded)
            {
                return RegistryAuthState.SignedIn(registryUrl, ToUser(status.User!), status.CredentialExpiresAtUtc);
            }
            if (status?.State is RuntimeRegistryAuthSessionState.Failed or RuntimeRegistryAuthSessionState.Expired)
            {
                return RegistryAuthState.SignedOut(registryUrl, status.Message ?? "Registry sign-in failed.");
            }
            await Task.Delay(PollInterval, cancellationToken);
        }

        return RegistryAuthState.SignedOut(registryUrl, "Registry sign-in timed out.");
    }

    public async Task<RegistryAuthState> LogoutAsync(Uri? registryUrl = null, CancellationToken cancellationToken = default)
    {
        registryUrl = RegistryUrlHelper.Normalize(registryUrl ?? RegistryUrlHelper.DefaultRegistryUrl);
        using var runtime = runtimeApiClientFactory.CreateClient();
        return ToState(await runtime.LogoutRegistryAsync(registryUrl.AbsoluteUri, cancellationToken));
    }

    private static RegistryAuthState ToState(RuntimeRegistryAuthStatus status)
    {
        var origin = new Uri(status.RegistryOrigin);
        return status.IsSignedIn && status.User is not null
            ? RegistryAuthState.SignedIn(origin, ToUser(status.User), status.ExpiresAtUtc)
            : RegistryAuthState.SignedOut(origin, status.Message);
    }

    private static RegistryCurrentUserResponse ToUser(RuntimeRegistryUser user)
        => new(user.UserId, user.DisplayName, user.Email, user.Username, user.AvatarUrl, user.RequiresUsername);
}

public sealed record RegistryAuthState(
    Uri RegistryUrl,
    bool IsSignedIn,
    RegistryCurrentUserResponse? User,
    DateTimeOffset? ExpiresAtUtc,
    string? Message)
{
    public static RegistryAuthState SignedOut(Uri registryUrl, string? message = null)
        => new(registryUrl, false, null, null, message);

    public static RegistryAuthState SignedIn(Uri registryUrl, RegistryCurrentUserResponse user, DateTimeOffset? expiresAtUtc)
        => new(registryUrl, true, user, expiresAtUtc, null);
}
