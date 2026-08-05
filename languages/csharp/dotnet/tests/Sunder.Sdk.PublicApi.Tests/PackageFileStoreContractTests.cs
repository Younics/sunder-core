using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Storage;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class PackageFileStoreContractTests
{
    [Fact]
    public async Task DefaultStreamWrite_LeavesTheInputOpen()
    {
        IPackageFileStore store = new MemoryPackageFileStore();
        var source = new TrackingMemoryStream([1, 2, 3]);

        await store.WriteAsync("data.bin", source);

        Assert.False(source.WasDisposed);
        Assert.Equal(3, source.Position);
        Assert.Equal([1, 2, 3], await store.ReadAsync("data.bin"));
        source.Dispose();
    }

    [Fact]
    public async Task DefaultStreamWrite_RejectsOversizedContentBeforeCallingTheAtomicWriter()
    {
        var implementation = new MemoryPackageFileStore();
        IPackageFileStore store = implementation;
        using var source = new MemoryStream(new byte[PackageStorageValidation.MaximumFileBytes + 1], writable: false);

        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("data.bin", source));

        Assert.Equal(0, implementation.WriteCount);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task DefaultStreamWrite_PreCancelledOperationDoesNotCallTheAtomicWriterOrDisposeInput()
    {
        var implementation = new MemoryPackageFileStore();
        IPackageFileStore store = implementation;
        var source = new TrackingMemoryStream([1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.WriteAsync("data.bin", source, cancellation.Token));

        Assert.Equal(0, implementation.WriteCount);
        Assert.False(source.WasDisposed);
        source.Dispose();
    }

    [Fact]
    public async Task DefaultStreamMembers_ValidatePathsAndReturnedFileLimits()
    {
        var implementation = new MemoryPackageFileStore();
        implementation.SetRaw("oversized.bin", new byte[PackageStorageValidation.MaximumFileBytes + 1]);
        IPackageFileStore store = implementation;

        await Assert.ThrowsAsync<ArgumentException>(async () => await store.OpenReadAsync("folder\\file.bin"));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.OpenReadAsync("oversized.bin"));
    }

    private sealed class MemoryPackageFileStore : IPackageFileStore
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        internal int WriteCount { get; private set; }

        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
            => Task.FromResult(_files.TryGetValue(relativePath, out var value) ? value.ToArray() : null);

        public Task WriteAsync(
            string relativePath,
            ReadOnlyMemory<byte> contents,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;
            _files[relativePath] = contents.ToArray();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            _files.Remove(relativePath);
            return Task.CompletedTask;
        }

        internal void SetRaw(string relativePath, byte[] contents) => _files[relativePath] = contents;
    }

    private sealed class TrackingMemoryStream(byte[] contents) : MemoryStream(contents, writable: false)
    {
        internal bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
