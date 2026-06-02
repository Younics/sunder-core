using Sunder.Registry.Shared;

namespace Sunder.App.Services;

public sealed class RegistryAuthService(ExternalBrowserService browserService)
{
    public async Task<RegistryAuthState> GetStatusAsync(
        Uri? registryUrl = null,
        CancellationToken cancellationToken = default)
    {
        registryUrl = RegistryUrlHelper.Normalize(registryUrl ?? RegistryUrlHelper.DefaultRegistryUrl);
        var store = RegistryAuthStore.Load();
        var token = store.GetToken(registryUrl);
        if (token is null)
        {
            return RegistryAuthState.SignedOut(registryUrl);
        }

        if (token.ExpiresAtUtc is not null && token.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            store.RemoveToken(registryUrl);
            store.Save();
            return RegistryAuthState.SignedOut(registryUrl, "Saved Registry token is expired.");
        }

        using var registryClient = new RegistryApiClient(registryUrl);
        var user = await registryClient.GetCurrentUserAsync(token.Token, cancellationToken);
        if (user is null)
        {
            store.RemoveToken(registryUrl);
            store.Save();
            return RegistryAuthState.SignedOut(registryUrl, "Saved Registry token is invalid.");
        }

        return RegistryAuthState.SignedIn(registryUrl, user, token.ExpiresAtUtc);
    }

    public async Task<RegistryAuthState> LoginAsync(
        Uri? registryUrl = null,
        CancellationToken cancellationToken = default)
    {
        registryUrl = RegistryUrlHelper.Normalize(registryUrl ?? RegistryUrlHelper.DefaultRegistryUrl);
        using var registryClient = new RegistryApiClient(registryUrl);
        var flow = new RegistryBrowserAuthFlow(registryUrl, registryClient, browserService);
        var result = await flow.LoginAsync(cancellationToken);

        var store = RegistryAuthStore.Load();
        store.SetToken(registryUrl, result.Token, result.UserId, result.ExpiresAtUtc);
        store.Save();

        var user = await registryClient.GetCurrentUserAsync(result.Token, cancellationToken);
        return user is null
            ? RegistryAuthState.SignedOut(registryUrl, "Registry sign-in completed, but the token could not be verified.")
            : RegistryAuthState.SignedIn(registryUrl, user, result.ExpiresAtUtc);
    }

    public RegistryAuthState Logout(Uri? registryUrl = null)
    {
        registryUrl = RegistryUrlHelper.Normalize(registryUrl ?? RegistryUrlHelper.DefaultRegistryUrl);
        var store = RegistryAuthStore.Load();
        store.RemoveToken(registryUrl);
        store.Save();
        return RegistryAuthState.SignedOut(registryUrl);
    }
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

    public static RegistryAuthState SignedIn(
        Uri registryUrl,
        RegistryCurrentUserResponse user,
        DateTimeOffset? expiresAtUtc)
        => new(registryUrl, true, user, expiresAtUtc, null);
}
