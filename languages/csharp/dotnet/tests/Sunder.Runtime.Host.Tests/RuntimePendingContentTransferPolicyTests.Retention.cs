using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RuntimePendingContentTransferPolicyTests
{
    [Fact]
    public async Task Retention_PreservesImmutableUploadAcrossGenerationAndConsumesOnlyOnce()
    {
        await WithRetentionStoreAsync(async (store, _, _) =>
        {
            var upload = await UploadAsync(store, [1, 2, 3]);
            var retained = store.RetainPackageUpload(new(upload.UploadId, upload.ContentHash, upload.Length), 1);
            Assert.True(retained.Success);
            var handle = retained.Retention!.Handle;
            Assert.Null(store.AcquireUpload(upload.UploadId, RuntimeUploadKind.Package, 1, false));
            Assert.False(store.GetPackageUploadRetention(handle with { ContentHash = new string('f', 64) }, renew: true).Success);
            Assert.False(store.ReleasePackageUploadRetention(handle with { LeaseId = Guid.NewGuid().ToString("N") }).Success);
            var preview = store.AcquireUpload(upload.UploadId, RuntimeUploadKind.Package, 2, false, handle);
            Assert.NotNull(preview);
            Assert.True(File.Exists(preview.FilePath));
            Assert.True(store.GetPackageUploadRetention(handle, renew: true).Success);
            var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                store.AcquireUpload(upload.UploadId, RuntimeUploadKind.Package, 3, true, handle))));
            var consumed = Assert.Single(attempts, value => value is not null)!;
            Assert.Equal(upload.ContentHash, consumed.ContentHash);
            Assert.Equal(RuntimePackageUploadRetentionErrorCodes.Unavailable, store.GetPackageUploadRetention(handle, renew: true).ErrorCode);
            store.ReleaseUpload(consumed);
            Assert.True(store.ReleasePackageUploadRetention(handle).Success);
        });
    }

    [Theory]
    [InlineData("window")]
    [InlineData("maximum")]
    [InlineData("clock")]
    [InlineData("restart")]
    [InlineData("ordinary-expired")]
    public async Task Retention_ExpiredOrUnownedInputsCannotBeResurrected(string scenario)
    {
        await WithRetentionStoreAsync(async (store, paths, clock) =>
        {
            var upload = await UploadAsync(store, [1]);
            if (scenario == "ordinary-expired")
            {
                clock.Advance(TimeSpan.FromMinutes(31));
                Assert.False(store.RetainPackageUpload(new(upload.UploadId, upload.ContentHash, upload.Length), 1).Success);
                return;
            }
            var retained = store.RetainPackageUpload(new(upload.UploadId, upload.ContentHash, upload.Length), 1).Retention!;
            var handle = retained.Handle;
            if (scenario == "restart")
            {
                using var otherHost = new RuntimeContentTransferStore(paths, uploadClock: clock);
                Assert.False(otherHost.GetPackageUploadRetention(handle).Success);
                Assert.True(store.GetPackageUploadRetention(handle).Success);
                return;
            }
            if (scenario == "maximum")
            {
                for (var index = 0; index < 5; index++)
                {
                    clock.Advance(TimeSpan.FromMinutes(20));
                    var renewal = store.GetPackageUploadRetention(handle, renew: true);
                    Assert.True(renewal.Success);
                    Assert.Equal(retained.MaximumExpiresAtUtc, renewal.Retention!.MaximumExpiresAtUtc);
                    Assert.True(renewal.Retention.ExpiresAtUtc <= retained.MaximumExpiresAtUtc);
                }
                clock.Advance(TimeSpan.FromMinutes(20));
            }
            else if (scenario == "clock") clock.Value = clock.Value with { Ticks = clock.Value.Ticks - 1 };
            else clock.Advance(TimeSpan.FromMinutes(31));
            Assert.Equal(RuntimePackageUploadRetentionErrorCodes.Unavailable, store.GetPackageUploadRetention(handle).ErrorCode);
            Assert.False(store.GetPackageUploadRetention(handle, renew: true).Success);
            Assert.False(store.RetainPackageUpload(new(upload.UploadId, upload.ContentHash, upload.Length), 1).Success);
            Assert.Empty(Directory.EnumerateFiles(paths.TransferRootPath));
        });
    }

    [Fact]
    public async Task Retention_OneOwnerAndExistingQuotaRemainAtomic()
    {
        await WithRetentionStoreAsync(async (store, paths, _) =>
        {
            var upload = await UploadAsync(store, [1]);
            var owners = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
                store.RetainPackageUpload(new(upload.UploadId, upload.ContentHash, upload.Length), 1))));
            var owner = Assert.Single(owners, value => value.Success).Retention!;
            await Assert.ThrowsAsync<RuntimeUploadLimitException>(() => UploadAsync(store, [2]));
            Assert.True(store.ReleasePackageUploadRetention(owner.Handle).Success);
            Assert.Empty(Directory.EnumerateFiles(paths.TransferRootPath));
            var next = await UploadAsync(store, [2]);
            Assert.NotNull(store.AcquireUpload(next.UploadId, RuntimeUploadKind.Package, 1, false));
        }, Policy(1, 1));
    }

    [Fact]
    public async Task Retention_HardLeaseBoundDoesNotGrowByRepeatedRetain()
    {
        await WithRetentionStoreAsync(async (store, _, _) =>
        {
            for (var index = 0; index < RuntimePackageUploadRetentionLimits.MaximumLeases; index++)
            {
                var upload = await UploadAsync(store, [1]);
                Assert.True(store.RetainPackageUpload(new(upload.UploadId, upload.ContentHash, upload.Length), 1).Success);
            }
            var overflow = await UploadAsync(store, [1]);
            Assert.Equal(RuntimePackageUploadRetentionErrorCodes.Conflict,
                store.RetainPackageUpload(new(overflow.UploadId, overflow.ContentHash, overflow.Length), 1).ErrorCode);
        }, Policy(40, 40));
    }

    private static async Task WithRetentionStoreAsync(Func<RuntimeContentTransferStore, RuntimePackagePaths, MutablePolicyClock, Task> action,
        RuntimeTransportPolicyOptions? policy = null)
    {
        var root = CreateRoot();
        try
        {
            var paths = new RuntimePackagePaths(root);
            var clock = new MutablePolicyClock();
            using var store = new RuntimeContentTransferStore(paths, policy, new MutableTimeProvider(ActivePolicyTestContext.ObservedAt), clock);
            await action(store, paths, clock);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
