using System.IO.Pipelines;

namespace Sunder.Runtime.Host.Services;

// Windows Process redirection returns synchronous anonymous-pipe FileStreams.
// ReadAsync on those handles parks a shared ThreadPool worker for the entire idle
// read, so two idle pipes per package can starve Runtime HTTP/IPC dispatch. Keep
// those native waits on owned readers instead, with one bounded chunk of read-ahead.
// The guardian still owns/closes the source; containment exit must settle the real
// read before this owner reports its drain complete.
internal sealed class ExecutionRuntimeProcessOutput
{
    internal const int BufferSize = 4096;
    private readonly Pipe _pipe = new(new PipeOptions(
        readerScheduler: PipeScheduler.ThreadPool,
        writerScheduler: PipeScheduler.Inline,
        pauseWriterThreshold: BufferSize,
        resumeWriterThreshold: BufferSize / 2,
        minimumSegmentSize: BufferSize,
        useSynchronizationContext: false));
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stopping;

    internal ExecutionRuntimeProcessOutput(Stream source, string name)
    {
        Stream = _pipe.Reader.AsStream();
        new Thread(() => Pump(source)) { IsBackground = true, Name = name }.Start();
    }

    internal Stream Stream { get; }
    internal Task Drained => _drained.Task;

    internal void Stop()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0) Stream.Dispose();
    }

    private void Pump(Stream source)
    {
        Exception? failure = null;
        try
        {
            while (Volatile.Read(ref _stopping) == 0)
            {
                var buffer = _pipe.Writer.GetMemory(BufferSize)[..BufferSize];
                var read = source.Read(buffer.Span);
                if (read == 0) break;
                _pipe.Writer.Advance(read);
                // Backpressure blocks only this owned thread, never the shared
                // pool. Do not prefetch again while the single chunk is full.
                var flush = _pipe.Writer.FlushAsync();
                var result = flush.IsCompletedSuccessfully ? flush.Result : flush.AsTask().GetAwaiter().GetResult();
                if (result.IsCompleted || result.IsCanceled) break;
            }
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            try { _pipe.Writer.Complete(failure); }
            catch (Exception exception) { _drained.TrySetException(exception); }
            finally { _drained.TrySetResult(); }
        }
    }
}
