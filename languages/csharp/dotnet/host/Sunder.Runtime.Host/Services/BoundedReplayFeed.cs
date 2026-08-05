using System.Threading.Channels;

namespace Sunder.Runtime.Host.Services;

internal sealed class BoundedReplayFeed<T>(int replayCapacity, int subscriberCapacity)
{
    private readonly object _gate = new();
    private readonly Queue<(long SequenceId, T Item)> _replay = new();
    private readonly HashSet<Channel<T>> _subscribers = [];
    private long _sequenceId;
    private bool _completed;

    public long CurrentSequenceId
    {
        get
        {
            lock (_gate)
            {
                return _sequenceId;
            }
        }
    }

    public T Publish(Func<long, T> createItem)
    {
        ArgumentNullException.ThrowIfNull(createItem);
        lock (_gate)
        {
            if (_completed)
            {
                throw new InvalidOperationException("The event feed is completed.");
            }

            var sequenceId = ++_sequenceId;
            var item = createItem(sequenceId);
            _replay.Enqueue((sequenceId, item));
            while (_replay.Count > replayCapacity)
            {
                _replay.Dequeue();
            }

            foreach (var subscriber in _subscribers.ToArray())
            {
                if (!subscriber.Writer.TryWrite(item))
                {
                    _subscribers.Remove(subscriber);
                    subscriber.Writer.TryComplete(new SlowRuntimeStreamConsumerException());
                }
            }

            return item;
        }
    }

    public ReplayFeedSnapshot<T> Snapshot(long afterSequenceId, int limit)
    {
        lock (_gate)
        {
            var historyGap = HasHistoryGap(afterSequenceId);
            var entries = (historyGap
                    ? _replay
                    : _replay.Where(entry => entry.SequenceId > afterSequenceId))
                .Take(limit)
                .ToArray();
            var sequenceId = entries.Length == 0 ? _sequenceId : entries[^1].SequenceId;
            return new ReplayFeedSnapshot<T>(
                sequenceId,
                entries.Select(static entry => entry.Item).ToArray(),
                historyGap);
        }
    }

    public ReplayFeedSubscription<T> Subscribe(long afterSequenceId, Func<long, T>? createGapSnapshot = null)
    {
        lock (_gate)
        {
            var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(subscriberCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });

            if (_completed)
            {
                channel.Writer.TryComplete();
                return new ReplayFeedSubscription<T>(this, channel, historyGap: false);
            }

            var historyGap = HasHistoryGap(afterSequenceId);
            if (historyGap && createGapSnapshot is not null)
            {
                channel.Writer.TryWrite(createGapSnapshot(_sequenceId));
            }
            else if (!historyGap)
            {
                foreach (var entry in _replay.Where(entry => entry.SequenceId > afterSequenceId))
                {
                    if (!channel.Writer.TryWrite(entry.Item))
                    {
                        channel.Writer.TryComplete(new SlowRuntimeStreamConsumerException());
                        return new ReplayFeedSubscription<T>(this, channel, historyGap);
                    }
                }
            }

            _subscribers.Add(channel);
            return new ReplayFeedSubscription<T>(this, channel, historyGap);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }

    private bool HasHistoryGap(long afterSequenceId)
        => afterSequenceId > 0
           && afterSequenceId < (_replay.Count == 0 ? _sequenceId : _replay.Peek().SequenceId) - 1;

    private void Unsubscribe(Channel<T> channel)
    {
        lock (_gate)
        {
            _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }

    internal sealed class ReplayFeedSubscription<TItem>(
        BoundedReplayFeed<TItem> owner,
        Channel<TItem> channel,
        bool historyGap) : IAsyncDisposable
    {
        public ChannelReader<TItem> Reader => channel.Reader;

        public bool HistoryGap { get; } = historyGap;

        public ValueTask DisposeAsync()
        {
            owner.Unsubscribe(channel);
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed record ReplayFeedSnapshot<T>(long SequenceId, IReadOnlyList<T> Items, bool HistoryGap);

internal sealed class SlowRuntimeStreamConsumerException : Exception
{
    public SlowRuntimeStreamConsumerException()
        : base("The Runtime stream consumer did not keep up with the bounded event buffer.")
    {
    }
}
