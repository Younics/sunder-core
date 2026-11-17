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
}
