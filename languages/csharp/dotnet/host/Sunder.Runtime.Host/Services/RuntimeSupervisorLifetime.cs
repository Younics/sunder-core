using System.IO.Pipes;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeSupervisorLifetime : IAsyncDisposable
{
    private readonly AnonymousPipeClientStream _pipe;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _monitor;

    private RuntimeSupervisorLifetime(
        string handle,
        IHostApplicationLifetime applicationLifetime,
        ILogger logger)
    {
        _pipe = new AnonymousPipeClientStream(PipeDirection.In, handle);
        _monitor = MonitorAsync(applicationLifetime, logger);
    }

    public static RuntimeSupervisorLifetime? Start(
        string? handle,
        IHostApplicationLifetime applicationLifetime,
        ILogger logger)
        => string.IsNullOrWhiteSpace(handle)
            ? null
            : new RuntimeSupervisorLifetime(handle, applicationLifetime, logger);

    private async Task MonitorAsync(
        IHostApplicationLifetime applicationLifetime,
        ILogger logger)
    {
        try
        {
            var buffer = new byte[1];
            var read = await _pipe.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false);
            if (read == 0 && !_stopping.IsCancellationRequested)
            {
                logger.LogWarning("Runtime Supervisor lifetime channel closed; stopping the Runtime worker");
                applicationLifetime.StopApplication();
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Runtime Supervisor lifetime channel failed; stopping the Runtime worker");
            applicationLifetime.StopApplication();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        await _pipe.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _monitor.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        _stopping.Dispose();
    }
}
