namespace Sunder.Runtime.Host.Services;

internal sealed class ExecutionRuntimeStartupMaintenance
{
    private readonly ExecutionRuntimeStore _store;
    private readonly ILogger<ExecutionRuntimeStartupMaintenance> _logger;

    public ExecutionRuntimeStartupMaintenance(
        ExecutionRuntimeStore store,
        ILogger<ExecutionRuntimeStartupMaintenance> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var rootsRemoved = 0;
        var archivesRemoved = 0;
        try
        {
            var collection = await _store.CollectGarbageAsync(cancellationToken)
                .ConfigureAwait(false);
            rootsRemoved += collection.RootsRemoved;
            archivesRemoved += collection.ArchivesRemoved;
            _logger.LogInformation(
                "Execution-runtime startup maintenance removed {RootCount} roots and {ArchiveCount} archives",
                rootsRemoved,
                archivesRemoved);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Execution-runtime startup maintenance failed after confirmed removal of {RootCount} roots and {ArchiveCount} archives; the failing collection pass may also have completed partial durable work",
                rootsRemoved,
                archivesRemoved);
        }
    }
}
