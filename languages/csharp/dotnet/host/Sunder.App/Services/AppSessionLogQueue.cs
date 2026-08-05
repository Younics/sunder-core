using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Sunder.App.Services;

internal sealed class AppSessionLogQueue
{
    private readonly Channel<AppSessionLogEntry> _entries;
    private long _droppedEntries;

    public AppSessionLogQueue(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _entries = Channel.CreateBounded<AppSessionLogEntry>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    internal long DroppedEntries => Interlocked.Read(ref _droppedEntries);

    public bool TryWrite(AppSessionLogEntry entry)
    {
        if (_entries.Writer.TryWrite(entry))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedEntries);
        return false;
    }

    public async Task FlushAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var droppedEntries = Interlocked.Exchange(ref _droppedEntries, 0);
            if (droppedEntries > 0)
            {
                await _entries.Writer
                    .WriteAsync(AppSessionLogEntry.Dropped(droppedEntries), deadline.Token)
                    .ConfigureAwait(false);
            }

            await _entries.Writer
                .WriteAsync(AppSessionLogEntry.Flush(completion), deadline.Token)
                .ConfigureAwait(false);
            await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"App session log flush exceeded {timeout}.", exception);
        }
    }

    public async IAsyncEnumerable<AppSessionLogEntry> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var entry in _entries.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return entry;
        }
    }
}
