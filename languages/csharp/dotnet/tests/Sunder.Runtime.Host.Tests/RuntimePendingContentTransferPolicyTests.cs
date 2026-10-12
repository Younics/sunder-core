using Sunder.Package.Format;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RuntimePendingContentTransferPolicyTests
{
    [Fact]
    public void Construction_DoesNotDeleteTransfersOwnedByTheRunningRuntime()
    {
        WithStore(
            new RuntimeTransportPolicyOptions(),
            (store, paths) =>
            {
                Assert.Equal(2, Directory.EnumerateFileSystemEntries(paths.TransferRootPath).Count());
            },
            beforeConstruction: paths =>
            {
                Directory.CreateDirectory(Path.Combine(paths.TransferRootPath, "stale-directory"));
                File.WriteAllBytes(Path.Combine(paths.TransferRootPath, "stale.partial"), [1]);
                File.WriteAllBytes(
                    Path.Combine(paths.TransferRootPath, "stale-directory", "content.bin"),
                    [2]);
            });
    }

    [Fact]
    public void StartupCleanup_AfterRootLeaseAcquisition_RemovesCrashLeftTransfers()
    {
        var root = CreateRoot();
        try
        {
            RuntimeLocalState.EnsureInitialized(root);
            var paths = new RuntimePackagePaths(root);
            Directory.CreateDirectory(Path.Combine(paths.TransferRootPath, "stale-directory"));
            File.WriteAllBytes(Path.Combine(paths.TransferRootPath, "stale.partial"), [1]);
            File.WriteAllBytes(
                Path.Combine(paths.TransferRootPath, "stale-directory", "content.bin"),
                [2]);

            using var lease = RuntimeRootLease.Acquire(paths);
            RuntimeContentTransferStore.CleanupCrashLeftTransfers(paths);

            Assert.Empty(Directory.EnumerateFileSystemEntries(paths.TransferRootPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RuntimeStartup_OrdersCrashCleanupAfterLeaseAndBeforeServingRequests()
    {
        var source = File.ReadAllText(Path.Combine(
            LocateRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "host",
            "Sunder.Runtime.Host",
            "Program.cs"));
        var leaseIndex = source.IndexOf("RuntimeRootLease.Acquire", StringComparison.Ordinal);
        var cleanupIndex = source.IndexOf("CleanupCrashLeftTransfers", StringComparison.Ordinal);
        var startIndex = source.IndexOf("RuntimeHostBootstrapRunner.StartAsync", StringComparison.Ordinal);

        Assert.True(leaseIndex >= 0 && leaseIndex < cleanupIndex);
        Assert.True(cleanupIndex < startIndex);
    }

    [Fact]
    public void Defaults_AreProductOwnedAndDoNotScaleWithRuntimeResourceProfiles()
    {
        var standard = RuntimeTransportPolicyOptions.FromResourcePolicy(RuntimeResourcePolicies.Standard);
        var large = RuntimeTransportPolicyOptions.FromResourcePolicy(RuntimeResourcePolicies.Large);

        Assert.Equal(32, standard.MaxPendingContentTransfers);
        Assert.Equal(2 * SunderPackageFormat.MaxArchiveBytes, standard.MaxPendingContentTransferBytes);
        Assert.Equal(standard.MaxPendingContentTransfers, large.MaxPendingContentTransfers);
        Assert.Equal(standard.MaxPendingContentTransferBytes, large.MaxPendingContentTransferBytes);
    }

    [Theory]
    [InlineData(4, 100)]
    [InlineData(64, 4)]
    public async Task ConcurrentAdmissions_EnforceCountAndReservedBytesAtomically(
        int maxCount,
        long maxBytes)
    {
        await WithStoreAsync(
            Policy(maxCount, maxBytes),
            async (store, paths) =>
            {
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var attempts = Enumerable.Range(0, 32).Select(async value =>
                {
                    await start.Task;
                    try
                    {
                        return await UploadAsync(store, [(byte)value]);
                    }
                    catch (RuntimeUploadLimitException)
                    {
                        return null;
                    }
                }).ToArray();

                start.SetResult();
                var accepted = (await Task.WhenAll(attempts)).Where(static upload => upload is not null).ToArray();

                Assert.Equal(4, accepted.Length);
                Assert.Equal(4, Directory.EnumerateFiles(paths.TransferRootPath).Count());
                foreach (var upload in accepted) store.DiscardUpload(upload!.UploadId);
                Assert.Empty(Directory.EnumerateFileSystemEntries(paths.TransferRootPath));
            });
    }

    [Fact]
    public async Task PendingCount_IsReleasedWhenUploadIsDiscarded()
    {
        await WithStoreAsync(
            Policy(maxCount: 1, maxBytes: 10),
            async (store, _) =>
            {
                var first = await UploadAsync(store, [1]);

                await Assert.ThrowsAsync<RuntimeUploadLimitException>(() => UploadAsync(store, [2]));

                store.DiscardUpload(first.UploadId);
                var replacement = await UploadAsync(store, [2]);
                store.DiscardUpload(replacement.UploadId);
            });
    }

    [Fact]
    public async Task AggregateBytes_AcceptsExactLimitAndReleasesConsumedUpload()
    {
        await WithStoreAsync(
            Policy(maxCount: 4, maxBytes: 3),
            async (store, paths) =>
            {
                var first = await UploadAsync(store, [1, 2]);
                var exact = await UploadAsync(store, [3]);

                await Assert.ThrowsAsync<RuntimeUploadLimitException>(() => UploadAsync(store, [4]));

                var lease = store.AcquireUpload(
                    first.UploadId,
                    RuntimeUploadKind.Package,
                    generation: 1,
                    consume: true);
                Assert.NotNull(lease);
                var replacement = await UploadAsync(store, [4, 5]);

                store.ReleaseUpload(lease!);
                store.DiscardUpload(exact.UploadId);
                store.DiscardUpload(replacement.UploadId);
                Assert.Empty(Directory.EnumerateFileSystemEntries(paths.TransferRootPath));
            });
    }

    [Fact]
    public async Task DeclaredReservation_ShrinksToObservedLengthAfterUploadCompletes()
    {
        await WithStoreAsync(
            Policy(maxCount: 2, maxBytes: 3),
            async (store, _) =>
            {
                var first = await UploadAsync(store, [1], declaredLength: 3);

                var second = await UploadAsync(store, [2, 3]);

                store.DiscardUpload(first.UploadId);
                store.DiscardUpload(second.UploadId);
            });
    }

    [Fact]
    public async Task UnknownLengthGrowthFailure_RollsBackBytesAndPartialFile()
    {
        await WithStoreAsync(
            Policy(maxCount: 1, maxBytes: 3),
            async (store, paths) =>
            {
                await Assert.ThrowsAsync<RuntimeUploadLimitException>(() =>
                    UploadAsync(store, [1, 2, 3, 4], declaredLength: null));

                var exact = await UploadAsync(store, [1, 2, 3], declaredLength: null);
                store.DiscardUpload(exact.UploadId);
                Assert.Empty(Directory.EnumerateFileSystemEntries(paths.TransferRootPath));
            });
    }

    [Fact]
    public async Task CancelledInFlightUpload_ReleasesDeclaredReservationAndPartialFile()
    {
        await WithStoreAsync(
            Policy(maxCount: 1, maxBytes: 3),
            async (store, paths) =>
            {
                await using var source = new CancellationBlockingReadStream();
                using var cancellation = new CancellationTokenSource();
                var pending = store.CreateUploadAsync(
                    RuntimeUploadKind.Package,
                    source,
                    contentLength: 3,
                    expectedHash: null,
                    "package.sunderpkg",
                    "application/vnd.sunder.package",
                    generation: 1,
                    cancellation.Token);
                await source.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));

                await Assert.ThrowsAsync<RuntimeUploadLimitException>(() => UploadAsync(store, [1]));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

                Assert.Empty(Directory.EnumerateFileSystemEntries(paths.TransferRootPath));
                var replacement = await UploadAsync(store, [1, 2, 3]);
                store.DiscardUpload(replacement.UploadId);
            });
    }

    [Fact]
    public async Task SweepExpired_ReleasesUploadAndDownloadReservations()
    {
        var root = CreateRoot();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var policy = Policy(maxCount: 2, maxBytes: 2) with
        {
            ContentTransferLifetime = TimeSpan.FromMinutes(1),
        };
        try
        {
            var paths = new RuntimePackagePaths(root);
            using var store = new RuntimeContentTransferStore(paths, policy, clock);
            var upload = await UploadAsync(store, [1]);
            var downloadPath = Path.Combine(paths.TransferRootPath, "pending.download");
            await File.WriteAllBytesAsync(downloadPath, [2]);
            store.RegisterDownload(
                downloadPath,
                new string('a', 64),
                length: 1,
                "download.bin",
                "application/octet-stream",
                generation: 1);
            await Assert.ThrowsAsync<RuntimeUploadLimitException>(() => UploadAsync(store, [3]));

            clock.Advance(policy.ContentTransferLifetime + TimeSpan.FromTicks(1));
            store.SweepExpired(clock.GetUtcNow());

            Assert.Null(store.AcquireUpload(
                upload.UploadId,
                RuntimeUploadKind.Package,
                generation: 1,
                consume: true));
            Assert.Empty(Directory.EnumerateFileSystemEntries(paths.TransferRootPath));
            var firstReplacement = await UploadAsync(store, [3]);
            var secondReplacement = await UploadAsync(store, [4]);
            store.DiscardUpload(firstReplacement.UploadId);
            store.DiscardUpload(secondReplacement.UploadId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadsAndUploads_SharePendingAggregateQuota()
    {
        await WithStoreAsync(
            Policy(maxCount: 2, maxBytes: 3),
            async (store, paths) =>
            {
                var downloadPath = Path.Combine(paths.RootPath, "download.bin");
                await File.WriteAllBytesAsync(downloadPath, [1, 2]);
                var download = store.RegisterDownload(
                    downloadPath,
                    new string('a', 64),
                    length: 2,
                    "download.bin",
                    "application/octet-stream",
                    generation: 1);

                await Assert.ThrowsAsync<RuntimeUploadLimitException>(() => UploadAsync(store, [3, 4]));

                var lease = store.AcquireDownload(download.DownloadId, generation: 1);
                Assert.NotNull(lease);
                store.ReleaseUpload(lease!);
                var upload = await UploadAsync(store, [3, 4]);
                store.DiscardUpload(upload.UploadId);
            });
    }

    private static RuntimeTransportPolicyOptions Policy(int maxCount, long maxBytes)
        => new()
        {
            MaxPendingContentTransfers = maxCount,
            MaxPendingContentTransferBytes = maxBytes,
        };

    private static async Task<Sunder.Runtime.Contracts.ContentUploadDescriptor> UploadAsync(
        RuntimeContentTransferStore store,
        byte[] bytes,
        long? declaredLength = -1)
    {
        await using var stream = new MemoryStream(bytes);
        return await store.CreateUploadAsync(
            RuntimeUploadKind.Package,
            stream,
            declaredLength == -1 ? bytes.Length : declaredLength,
            expectedHash: null,
            "package.sunderpkg",
            "application/vnd.sunder.package",
            generation: 1,
            CancellationToken.None);
    }

    private static async Task WithStoreAsync(
        RuntimeTransportPolicyOptions policy,
        Func<RuntimeContentTransferStore, RuntimePackagePaths, Task> action)
    {
        var root = CreateRoot();
        try
        {
            var paths = new RuntimePackagePaths(root);
            using var store = new RuntimeContentTransferStore(paths, policy);
            await action(store, paths);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void WithStore(
        RuntimeTransportPolicyOptions policy,
        Action<RuntimeContentTransferStore, RuntimePackagePaths> action,
        Action<RuntimePackagePaths>? beforeConstruction = null)
    {
        var root = CreateRoot();
        try
        {
            var paths = new RuntimePackagePaths(root);
            beforeConstruction?.Invoke(paths);
            using var store = new RuntimeContentTransferStore(paths, policy);
            action(store, paths);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sunder-pending-transfer-policy-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the Core repository root.");
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;
    }

    private sealed class CancellationBlockingReadStream : Stream
    {
        private readonly TaskCompletionSource _readStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _readStarted.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
