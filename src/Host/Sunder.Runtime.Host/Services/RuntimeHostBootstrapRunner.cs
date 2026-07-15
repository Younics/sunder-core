using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal static class RuntimeHostBootstrapRunner
{
    public static async Task StartAsync(
        WebApplication app,
        RuntimeSessionOwner sessions,
        Func<CancellationToken, Task<PackageLifecycleOperationResult>> bootstrapAsync,
        Action? onStarted = null,
        CancellationToken cancellationToken = default)
    {
        await app.StartAsync(cancellationToken);
        onStarted?.Invoke();
        using var bootstrapCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            app.Lifetime.ApplicationStopping);
        try
        {
            var result = await bootstrapAsync(bootstrapCancellation.Token);
            if (result.Success)
            {
                sessions.MarkReady(result.Warnings, result.Errors);
                return;
            }

            var details = result.Errors.Count > 0
                ? string.Join(Environment.NewLine, result.Errors)
                : result.Message ?? "The Runtime rejected the startup packages.";
            sessions.MarkFailed(details, result.Warnings, result.Errors);
            app.Logger.LogError("Runtime package bootstrap failed: {Details}", details);
        }
        catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            sessions.MarkFailed(exception.Message, errors: [exception.Message]);
            app.Logger.LogError(exception, "Runtime package bootstrap failed");
        }
    }
}
