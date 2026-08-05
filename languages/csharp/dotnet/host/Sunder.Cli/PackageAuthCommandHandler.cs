using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed class PackageAuthCommandHandler(
    ICliRuntimePackageAuthClient runtime,
    CliOutput output,
    IBrowserLauncher browser)
{
    public async Task<int> ExecuteAsync(PackageAuthStatusCommand command, CancellationToken token)
    {
        var status = await runtime.GetPackageAuthStatusAsync(command.PackageId, token).ConfigureAwait(false);
        WriteStatus(status);
        if (status.Status == PackageAuthStatusKind.Failed)
        {
            output.Error(status.Message, "runtime.package.auth.failed");
            return CliExitCodes.Failure;
        }
        output.Info(status.Message);
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(PackageAuthLoginCommand command, CancellationToken token)
    {
        var start = await runtime.StartPackageAuthAsync(command.PackageId, token).ConfigureAwait(false);
        try
        {
            if (start.Flow != PackageAuthFlowKind.Browser)
                throw new InvalidDataException("Runtime returned an unsupported package authentication flow.");
            var launchUri = ValidateLaunchUri(start.LaunchUrl);
            output.Info(browser.TryOpen(launchUri)
                ? $"Opening browser to authorize package '{command.PackageId}'..."
                : "The browser could not be opened automatically.");
            output.Info($"Authorization URL: {launchUri.AbsoluteUri}");

            PackageAuthSessionStatusResponse status;
            while (true)
            {
                status = await runtime.GetPackageAuthSessionStatusAsync(
                    command.PackageId,
                    start.AuthSessionId,
                    token).ConfigureAwait(false);
                if (status.State != PackageAuthSessionState.Pending) break;
                var remaining = start.ExpiresAtUtc - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), token)
                    .ConfigureAwait(false);
            }

            output.Data(new
            {
                packageId = status.PackageId,
                state = status.State.ToString().ToLowerInvariant(),
                message = status.Message,
                expiresAtUtc = status.ExpiresAtUtc,
            });
            switch (status.State)
            {
                case PackageAuthSessionState.Connected:
                    output.Success(status.Message);
                    return CliExitCodes.Success;
                case PackageAuthSessionState.Expired:
                    output.Error(status.Message, "runtime.package.auth.expired");
                    return CliExitCodes.Timeout;
                case PackageAuthSessionState.Pending:
                    await TryCancelAsync(command.PackageId, start.AuthSessionId).ConfigureAwait(false);
                    output.Error("Package authorization expired before it completed.", "runtime.package.auth.expired");
                    return CliExitCodes.Timeout;
                case PackageAuthSessionState.Cancelled:
                    output.Error(status.Message, "runtime.package.auth.cancelled");
                    return CliExitCodes.Cancelled;
                default:
                    output.Error(status.Message, "runtime.package.auth.failed");
                    return CliExitCodes.Failure;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await TryCancelAsync(command.PackageId, start.AuthSessionId).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await TryCancelAsync(command.PackageId, start.AuthSessionId).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<int> ExecuteAsync(PackageAuthLogoutCommand command, CancellationToken token)
    {
        var status = await runtime.DisconnectPackageAuthAsync(command.PackageId, token).ConfigureAwait(false);
        WriteStatus(status);
        if (status.Status == PackageAuthStatusKind.Failed)
        {
            output.Error(status.Message, "runtime.package.auth.disconnect_failed");
            return CliExitCodes.Failure;
        }
        output.Success(status.Message);
        return CliExitCodes.Success;
    }

    private void WriteStatus(PackageAuthStatusResponse status)
    {
        output.Data(new
        {
            packageId = status.PackageId,
            status = status.Status.ToString().ToLowerInvariant(),
            message = status.Message,
            canLogin = status.CanAuthorize,
            canLogout = status.CanDisconnect,
        });
        output.Line($"Package: {status.PackageId}");
        output.Line($"Authentication: {status.Status.ToString().ToLowerInvariant()}");
    }

    private static Uri ValidateLaunchUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Scheme != Uri.UriSchemeHttps && (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback))
        {
            throw new InvalidDataException(
                "Runtime returned an unsafe package authorization URL; HTTPS or loopback HTTP is required.");
        }
        return uri;
    }

    private async Task TryCancelAsync(string packageId, string authSessionId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await runtime.CancelPackageAuthSessionAsync(packageId, authSessionId, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Runtime sessions are expiry-bounded; cleanup must not replace the command outcome.
        }
    }
}
