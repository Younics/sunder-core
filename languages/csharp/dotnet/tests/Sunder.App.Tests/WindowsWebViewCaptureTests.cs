using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Avalonia;
using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.App.Tests;

public sealed class WindowsWebViewCaptureTests
{
    [Theory]
    [InlineData("write")]
    [InlineData("span")]
    [InlineData("byte")]
    [InlineData("size")]
    [InlineData("seek")]
    [InlineData("position")]
    [InlineData("capacity")]
    public void NativeStreamCannotGrowPastItsBudgetThroughAnySupportedMutation(string operation)
    {
        using var stream = new WindowsWebViewCapture.CaptureStream();
        stream.SetLength(WindowsWebViewCapture.CaptureStream.MaximumBytes);
        stream.Position = stream.Length;
        Assert.Throws<InvalidDataException>(() =>
        {
            switch (operation)
            {
                case "write": ((IStream)stream).Write([1], 1, 0); break;
                case "span": stream.Write(new ReadOnlySpan<byte>([1])); break;
                case "byte": stream.WriteByte(1); break;
                case "size": ((IStream)stream).SetSize(stream.Length + 1); break;
                case "seek": ((IStream)stream).Seek(1, 2, 0); break;
                case "position": stream.Position++; break;
                case "capacity": stream.Capacity++; break;
            }
        });
        Assert.True(stream.LimitExceeded);
        Assert.Equal(WindowsWebViewCapture.CaptureStream.MaximumBytes, stream.Length);
        Assert.Equal(WindowsWebViewCapture.CaptureStream.MaximumBytes, stream.Capacity);
    }

    [Fact]
    public async Task ChunkedAndAsyncWritesRespectBothTheLengthAndAllocationBudget()
    {
        using var stream = new WindowsWebViewCapture.CaptureStream();
        var chunk = new byte[3 * WindowsWebViewCapture.CaptureStream.MaximumBytes / 4];
        await stream.WriteAsync(chunk.AsMemory());
        stream.WriteByte(1);
        Assert.Equal(chunk.Length + 1, stream.Length);
        Assert.InRange(stream.Capacity, (int)stream.Length, WindowsWebViewCapture.CaptureStream.MaximumBytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => stream.WriteAsync(chunk, 0, chunk.Length));
        Assert.True(stream.LimitExceeded);
        Assert.Equal(chunk.Length + 1, stream.Length);
    }

    [Fact]
    public void NativeStreamSupportsBoundedWriteSeekAndStatWithoutCloningSharedState()
    {
        using var stream = new WindowsWebViewCapture.CaptureStream();
        var native = (IStream)stream;
        native.Write([1, 2, 3], 3, 0);
        native.Seek(1, 0, 0);
        native.Write([4], 1, 0);
        native.Commit(0);
        native.Stat(out var stat, 1);
        Assert.Equal(2, stat.type);
        Assert.Equal(3, stat.cbSize);
        Assert.Equal(new byte[] { 1, 4, 3 }, stream.ToArray());
        Assert.Throws<NotSupportedException>(() => native.Clone(out _));
        Assert.Throws<NotSupportedException>(() => native.CopyTo(native, 3, 0, 0));
    }

    [Theory]
    [InlineData(-1, 0, 1, 1, 10)]
    [InlineData(0, 0, 0, 1, 10)]
    [InlineData(0, 0, 1, 11, 10)]
    [InlineData(double.NaN, 0, 1, 1, 10)]
    [InlineData(0, 0, double.PositiveInfinity, 1, 10)]
    [InlineData(0, 0, 1, 1, 0)]
    [InlineData(0, 0, 1, 1, 2049)]
    public void InvalidGeometryIsRejectedBeforeNativeSubmission(double x, double y, double width, double height, int edge)
        => Assert.Throws<ArgumentException>(() => AppNativeWebViewCapture.ValidateRegion(new Rect(x, y, width, height), new Size(10, 10), edge));

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task RelinquishedCaptureCallbackSettlesWithoutManufacturingAnImage()
    {
        using var callback = new WindowsWebViewCapture.Completion();
        var id = new Guid("697e05e9-3d8f-45fa-96f4-8ffe1ededaf5");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(callback.Pointer, in id, out var native));
        callback.MarkSubmitted();
        Assert.False(callback.Result.IsCompleted);
        Assert.Equal(1, Marshal.Release(native));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callback.Result);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task NativeCallbackRemainsRootedUntilTheLastComReferenceReleasesIt()
    {
        var callback = new WindowsWebViewCapture.Completion();
        var task = callback.Result;
        var id = new Guid("697e05e9-3d8f-45fa-96f4-8ffe1ededaf5");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(callback.Pointer, in id, out var retained));
        callback.Dispose(); // Simulate the native owner outliving managed ownership.
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.False(task.IsCompleted);
            var invoke = Marshal.GetDelegateForFunctionPointer<WindowsWebViewCapture.Completion.Invoke>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(retained), 3 * IntPtr.Size));
            Assert.Equal(0, invoke(retained, unchecked((int)0x80004005)));
            Assert.Equal(unchecked((int)0x80004005), await task);
        }
        finally { Assert.Equal(0, Marshal.Release(retained)); }
        callback.Dispose();
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task CaptureDoesNotKeepItsNativeViewAliveWhileWaitingForThatViewsRelinquishment()
    {
        using var native = new CaptureNativeView();
        var capture = WindowsWebViewCapture.CaptureCoreAsync(native.Pointer, 0,
            new Rect(0, 0, 8, 8), new Size(8, 8), 8, new PixelSize(8, 8));
        Assert.False(capture.IsCompleted, capture.Exception?.ToString() ?? capture.Status.ToString());
        native.Dispose(); // The native owner ends; its final Release relinquishes the callback.
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(native.Released);
        }
        finally
        {
            // Preserve the failed assertion but drain the fake native operation
            // too, including on the original self-retaining implementation.
            native.RelinquishCompletion();
            try { await capture; } catch (OperationCanceledException) { }
        }
    }

    private sealed class CaptureNativeView : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Query(nint self, ref Guid id, out nint result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint Reference(nint self);
        private static readonly Query QueryHandler = QueryInterface;
        private static readonly Reference AddHandler = AddReference, ReleaseHandler = ReleaseReference;
        private static readonly WindowsWebViewCapture.CapturePreview CaptureHandler = Submit;
        private static readonly nint Vtable = CreateVtable();
        private readonly GCHandle _root;
        private int _references = 1, _owned = 1;
        private nint _callback;
        internal nint Pointer { get; }
        internal bool Released { get; private set; }
        internal CaptureNativeView()
        {
            Pointer = Marshal.AllocHGlobal(2 * IntPtr.Size);
            _root = GCHandle.Alloc(this);
            Marshal.WriteIntPtr(Pointer, Vtable);
            Marshal.WriteIntPtr(Pointer, IntPtr.Size, GCHandle.ToIntPtr(_root));
        }
        public void Dispose() { if (Interlocked.Exchange(ref _owned, 0) != 0) Marshal.Release(Pointer); }
        internal void RelinquishCompletion() { var callback = Interlocked.Exchange(ref _callback, 0); if (callback != 0) Marshal.Release(callback); }
        private static CaptureNativeView Context(nint self) => (CaptureNativeView)GCHandle.FromIntPtr(Marshal.ReadIntPtr(self, IntPtr.Size)).Target!;
        private static int QueryInterface(nint self, ref Guid id, out nint result)
        {
            result = 0;
            if (id != new Guid("76eceacb-0462-4d94-ac83-423a6793775e") && id != new Guid("00000000-0000-0000-c000-000000000046"))
                return unchecked((int)0x80004002);
            AddReference(self); result = self; return 0;
        }
        private static uint AddReference(nint self) => checked((uint)Interlocked.Increment(ref Context(self)._references));
        private static uint ReleaseReference(nint self)
        {
            var context = Context(self);
            var count = Interlocked.Decrement(ref context._references);
            if (count == 0)
            {
                context.RelinquishCompletion(); context.Released = true;
                Marshal.FreeHGlobal(self); context._root.Free();
            }
            return checked((uint)count);
        }
        private static int Submit(nint self, int format, nint stream, nint callback)
        {
            Marshal.AddRef(callback); Context(self)._callback = callback; return 0;
        }
        private static nint CreateVtable()
        {
            var table = Marshal.AllocHGlobal(31 * IntPtr.Size);
            for (var slot = 0; slot < 31; slot++) Marshal.WriteIntPtr(table, slot * IntPtr.Size, 0);
            Marshal.WriteIntPtr(table, Marshal.GetFunctionPointerForDelegate(QueryHandler));
            Marshal.WriteIntPtr(table, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(AddHandler));
            Marshal.WriteIntPtr(table, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(ReleaseHandler));
            Marshal.WriteIntPtr(table, 30 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(CaptureHandler));
            return table;
        }
    }
}
