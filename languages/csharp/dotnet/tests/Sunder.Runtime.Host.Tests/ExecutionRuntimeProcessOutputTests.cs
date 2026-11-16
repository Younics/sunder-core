using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ExecutionRuntimeProcessOutputTests
{
    [Fact]
    public async Task IdleReadUsesAnOwnedThreadAndCancellationCannotReleaseItsNativeBuffer()
    {
        using var source = new BlockingOutput();
        var output = new ExecutionRuntimeProcessOutput(source, "sunder-output-test");
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(source.SharedThreadPoolRead);
            var canceledBuffer = new byte[] { 0xaa, 0xaa, 0xaa };
            using var cancellation = new CancellationTokenSource();
            var canceled = output.Stream.ReadAsync(canceledBuffer, cancellation.Token).AsTask();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            Assert.False(output.Drained.IsCompleted);

            var actual = new byte[3];
            var resumed = output.Stream.ReadExactlyAsync(actual).AsTask();
            source.Release.Set();
            await resumed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, actual);
            Assert.Equal(new byte[] { 0xaa, 0xaa, 0xaa }, canceledBuffer);
            Assert.Equal(0, await output.Stream.ReadAsync(new byte[1]));
            await output.Drained.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            output.Stop();
            source.Release.Set();
            await output.Drained.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StoppingABackpressuredReaderDrainsWithoutUnboundedReadAhead()
    {
        using var source = new RepeatingOutput();
        var output = new ExecutionRuntimeProcessOutput(source, "sunder-output-backpressure-test");
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(source.SharedThreadPoolRead);
            Assert.Equal(ExecutionRuntimeProcessOutput.BufferSize, source.BytesRead);
        }
        finally
        {
            output.Stop();
            await output.Drained.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(ExecutionRuntimeProcessOutput.BufferSize, source.BytesRead);
    }

    [Fact]
    public async Task StoppingTheConsumerCannotAcknowledgeAnUnsettledNativeRead()
    {
        using var source = new BlockingOutput();
        var output = new ExecutionRuntimeProcessOutput(source, "sunder-output-retirement-test");
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            output.Stop();
            Assert.False(output.Drained.IsCompleted);
        }
        finally
        {
            output.Stop();
            source.Release.Set();
            await output.Drained.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SourceFailureRemainsAReadFailureAndTheActualPumpStillDrains()
    {
        using var source = new FailedOutput();
        var output = new ExecutionRuntimeProcessOutput(source, "sunder-output-failure-test");
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => output.Stream.ReadAsync(new byte[1]).AsTask());
            Assert.Same(source.Failure, error);
            await output.Drained.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { output.Stop(); }
    }

    private abstract class Output : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingOutput : Output
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        internal bool SharedThreadPoolRead;
        private bool _read;
        public override int Read(Span<byte> buffer)
        {
            if (_read) return 0;
            _read = true;
            SharedThreadPoolRead = Thread.CurrentThread.IsThreadPoolThread;
            Entered.SetResult();
            Release.Wait();
            new byte[] { 0x10, 0x20, 0x30 }.CopyTo(buffer);
            return 3;
        }

        protected override void Dispose(bool disposing) { if (disposing) Release.Dispose(); base.Dispose(disposing); }
    }

    private sealed class RepeatingOutput : Output
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool SharedThreadPoolRead;
        private int _bytesRead;
        internal int BytesRead => Volatile.Read(ref _bytesRead);
        public override int Read(Span<byte> buffer)
        {
            SharedThreadPoolRead = Thread.CurrentThread.IsThreadPoolThread;
            buffer.Fill(0x5a);
            Interlocked.Add(ref _bytesRead, buffer.Length);
            Entered.TrySetResult();
            return buffer.Length;
        }
    }

    private sealed class FailedOutput : Output
    {
        internal readonly IOException Failure = new("owned output read failed");
        public override int Read(Span<byte> buffer) => throw Failure;
    }
}
