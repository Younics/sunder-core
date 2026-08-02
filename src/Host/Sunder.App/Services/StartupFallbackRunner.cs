namespace Sunder.App.Services;

internal sealed record StartupFallbackOutcome(
    Exception? StartupFailure,
    Exception? CoreShellFailure
);

internal static class StartupFallbackRunner
{
    internal static async Task<StartupFallbackOutcome> RunAsync(
        Func<bool, CancellationToken, Task> startAsync,
        Func<Exception, CancellationToken, Task> notifyStartupFailureAsync,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(startAsync);
        ArgumentNullException.ThrowIfNull(notifyStartupFailureAsync);

        var startupFailure = await TryStartAsync(
            startAsync,
            openCoreShell: false,
            cancellationToken
        );
        if (startupFailure is null)
        {
            return new StartupFallbackOutcome(null, null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var coreShellFailure = await TryStartAsync(
            startAsync,
            openCoreShell: true,
            cancellationToken
        );
        if (coreShellFailure is not null)
        {
            return new StartupFallbackOutcome(startupFailure, coreShellFailure);
        }

        await notifyStartupFailureAsync(startupFailure, cancellationToken);
        return new StartupFallbackOutcome(startupFailure, null);
    }

    private static async Task<Exception?> TryStartAsync(
        Func<bool, CancellationToken, Task> startAsync,
        bool openCoreShell,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await startAsync(openCoreShell, cancellationToken);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
