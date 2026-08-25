using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackageArchivePickerTests
{
    [Fact]
    public async Task CopyReviewSnapshotAsync_ExactArchiveLimit_Succeeds()
    {
        Assert.Equal(512L * 1024 * 1024, PackageArchivePicker.MaxArchiveBytes);
        var source = new VirtualReadStream(PackageArchivePicker.MaxArchiveBytes);

        await PackageArchivePicker.CopyReviewSnapshotAsync(
            source,
            Stream.Null,
            PackageArchivePicker.MaxArchiveBytes,
            CancellationToken.None);

        Assert.Equal(PackageArchivePicker.MaxArchiveBytes, source.BytesRead);
    }

    [Fact]
    public async Task CreateReviewSnapshotAsync_DeclaredLengthOverArchiveLimit_RejectsBeforeCreatingSnapshot()
    {
        var rootPath = TestSupport.TestPaths.CreateTempDirectory();
        var source = new VirtualReadStream(1);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                PackageArchivePicker.CreateReviewSnapshotAsync(
                    source,
                    PackageArchivePicker.MaxArchiveBytes + 1,
                    CancellationToken.None,
                    rootPath));

            Assert.Contains(PackageArchivePicker.MaxArchiveBytes.ToString(), exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, source.ReadCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(rootPath));
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CopyReviewSnapshotAsync_StreamingOverArchiveLimit_RejectsGrowthAndUnknownLength(
        bool hasKnownInitialLength)
    {
        var source = new VirtualReadStream(PackageArchivePicker.MaxArchiveBytes + 1);
        long? sourceLength = hasKnownInitialLength
            ? PackageArchivePicker.MaxArchiveBytes
            : null;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PackageArchivePicker.CopyReviewSnapshotAsync(
                source,
                Stream.Null,
                sourceLength,
                CancellationToken.None));

        Assert.Equal(PackageArchivePicker.MaxArchiveBytes + 1, source.BytesRead);
    }

    [Fact]
    public async Task CreateReviewSnapshotAsync_CancellationDeletesPartialSnapshotDirectory()
    {
        var rootPath = TestSupport.TestPaths.CreateTempDirectory();
        using var cancellation = new CancellationTokenSource();
        var source = new CancelOnSecondReadStream(cancellation);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                PackageArchivePicker.CreateReviewSnapshotAsync(
                    source,
                    sourceLength: null,
                    cancellation.Token,
                    rootPath));

            Assert.Equal(2, source.ReadCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(rootPath));
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void CleanupStaleReviewSnapshots_RemovesDeadProcessAndLegacyDirectories()
    {
        var rootPath = TestSupport.TestPaths.CreateTempDirectory();
        var deadProcessDirectory = Path.Combine(rootPath, $"{int.MaxValue}-{Guid.NewGuid():N}");
        var legacyDirectory = Path.Combine(rootPath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(deadProcessDirectory);
        Directory.CreateDirectory(legacyDirectory);
        try
        {
            PackageArchivePicker.CleanupStaleReviewSnapshots(rootPath);

            Assert.False(Directory.Exists(deadProcessDirectory));
            Assert.False(Directory.Exists(legacyDirectory));
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void CleanupStaleReviewSnapshots_PreservesActiveProcessDirectoryAndFiles()
    {
        var rootPath = TestSupport.TestPaths.CreateTempDirectory();
        var activeProcessDirectory = Path.Combine(
            rootPath,
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        var snapshotPath = Path.Combine(activeProcessDirectory, "package.sunderpkg");
        Directory.CreateDirectory(activeProcessDirectory);
        File.WriteAllBytes(snapshotPath, [1, 2, 3]);
        try
        {
            PackageArchivePicker.CleanupStaleReviewSnapshots(rootPath);

            Assert.True(Directory.Exists(activeProcessDirectory));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(snapshotPath));
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class VirtualReadStream(long length) : Stream
    {
        private long _remaining = length;

        public long BytesRead { get; private set; }
        public int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            var read = (int)Math.Min(buffer.Length, _remaining);
            _remaining -= read;
            BytesRead += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancelOnSecondReadStream(CancellationTokenSource cancellation) : Stream
    {
        public int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (ReadCount == 1)
            {
                buffer.Span[0] = 42;
                return ValueTask.FromResult(1);
            }

            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
