using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed class RegistryAuthCommandHandler(
    ICliRuntimeAuthClient runtime,
    CliOutput output,
    IBrowserLauncher browser,
    CliOptions options)
{
    public async Task<int> ExecuteAsync(AuthLoginCommand command, CancellationToken token)
    {
        var start = await runtime.StartRegistryAuthAsync(
            new(options.RegistryApiUrl.AbsoluteUri, options.RegistryWebUrl.AbsoluteUri, "Sunder CLI"), token).ConfigureAwait(false);
        var launchUri = new Uri(start.LaunchUrl);
        output.Info(browser.TryOpen(launchUri)
            ? "Opening browser for Sunder Registry sign-in..."
            : "The browser could not be opened automatically.");
        output.Info($"Sign-in URL: {launchUri.AbsoluteUri}");

        RuntimeRegistryAuthSessionStatus? status;
        do
        {
            status = await runtime.GetRegistryAuthSessionAsync(start.SessionId, token).ConfigureAwait(false);
            if (status?.State != RuntimeRegistryAuthSessionState.Pending) break;
            if (DateTimeOffset.UtcNow >= start.ExpiresAtUtc) break;
            await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
        } while (true);

        output.Data(status);
        if (status is null)
        {
            output.Error("Registry sign-in session was not found.");
            return CliExitCodes.NotFound;
        }
        if (status.State is RuntimeRegistryAuthSessionState.Pending or RuntimeRegistryAuthSessionState.Expired)
        {
            output.Error(status.Message ?? (status.State == RuntimeRegistryAuthSessionState.Expired
                ? "Registry sign-in expired."
                : "Registry sign-in is still pending."));
            return CliExitCodes.Timeout;
        }
        if (status.State == RuntimeRegistryAuthSessionState.Failed)
        {
            output.Error(status.Message ?? "Registry sign-in failed.");
            return CliErrorMapper.FromRegistryCode(status.ErrorCode);
        }
        if (status.User is null)
        {
            output.Error("Registry sign-in succeeded without a user identity.");
            return CliExitCodes.Failure;
        }
        output.Success($"Signed in to {options.RegistryApiUrl} as {FormatUser(status.User)}.");
        if (status.CredentialExpiresAtUtc is { } expires) output.Info($"Registry sign-in expires (UTC) {expires.UtcDateTime:O}.");
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(AuthStatusCommand command, CancellationToken token)
    {
        var status = await runtime.GetRegistryAuthStatusAsync(options.RegistryApiUrl.AbsoluteUri, token).ConfigureAwait(false);
        output.Data(status);
        if (status.ErrorCode != RuntimeRegistryErrorCode.None)
        {
            output.Error(status.Message ?? "Registry sign-in status could not be read.");
            return CliErrorMapper.FromRegistryCode(status.ErrorCode);
        }
        if (!status.IsSignedIn || status.User is null)
        {
            output.Info($"Not signed in to {options.RegistryApiUrl}. Run 'sunder auth login'.");
            return CliExitCodes.Success;
        }
        output.Success($"Signed in to {options.RegistryApiUrl} as {FormatUser(status.User)}.");
        if (status.ExpiresAtUtc is { } expires) output.Info($"Registry sign-in expires (UTC) {expires.UtcDateTime:O}.");
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(AuthLogoutCommand command, CancellationToken token)
    {
        var status = await runtime.LogoutRegistryAsync(options.RegistryApiUrl.AbsoluteUri, token).ConfigureAwait(false);
        output.Data(status);
        if (status.ErrorCode != RuntimeRegistryErrorCode.None)
        {
            output.Error(status.Message ?? "Registry sign-out failed.");
            return CliErrorMapper.FromRegistryCode(status.ErrorCode);
        }
        output.Success($"Signed out from {options.RegistryApiUrl}.");
        return CliExitCodes.Success;
    }

    private static string FormatUser(RuntimeRegistryUser user)
        => string.IsNullOrWhiteSpace(user.Username) ? user.DisplayName ?? "registry user" : $"@{user.Username}";
}
