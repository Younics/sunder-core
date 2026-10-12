using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ExecutionRuntimeStoreTests
{
    [Fact]
    public void WindowsSealedTreeBecomesWritableWithoutChangingOwner()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        VerifyWindowsSealedTreeBecomesWritableWithoutChangingOwner();
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsSealedTreeBecomesWritableWithoutChangingOwner()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-execution-runtime-acl-tests", Guid.NewGuid().ToString("N"));
        var launcher = Path.Combine(root, "worker.exe");
        Directory.CreateDirectory(root);
        File.WriteAllText(launcher, "worker");
        try
        {
            ExecutionRuntimeRootFileSystem.Seal(root, launcher);
            var before = GetWindowsOwner(root);

            ExecutionRuntimeRootFileSystem.MakeOwnedTreeWritable(root);

            Assert.Equal(before, GetWindowsOwner(root));
            Assert.Equal(before, GetWindowsOwner(launcher));
        }
        finally
        {
            _ = ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(root);
        }
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier GetWindowsOwner(string path)
    {
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
        return Assert.IsType<SecurityIdentifier>(security.GetOwner(typeof(SecurityIdentifier)));
    }

    [Fact]
    public void ExactIdentityKeysAndReceiptPathsIncludeOfficialSourceAndChecksum()
    {
        var paths = new RuntimePackagePaths(Path.Combine(Path.GetTempPath(), "unused-runtime-state"));
        var layout = new ExecutionRuntimeStoreLayout(paths);
        var first = ExecutionRuntimeIdentity.FromContract(SyntheticIdentity("10.0.1", "linux-x64", new string('a', 128)));
        var second = first with { VendorChecksumDigest = new string('b', 128) };

        Assert.Equal(paths.ExecutionRuntimeStoreRootPath, layout.RootPath);
        Assert.Equal(paths.ExecutionRuntimeArtifactRootPath, layout.ArchiveRootPath);
        Assert.Equal(paths.ExecutionRuntimeRootsPath, layout.ContentRootPath);
        Assert.Equal(paths.ExecutionRuntimeReceiptRootPath, layout.ReceiptRootPath);
        Assert.Equal(paths.ExecutionRuntimeHealthRootPath, layout.HealthRootPath);
        Assert.Equal(paths.ExecutionRuntimeStagingRootPath, layout.StagingRootPath);
        Assert.Equal(paths.ExecutionRuntimeTransactionRootPath, layout.TransactionRootPath);
        Assert.Equal(paths.ExecutionRuntimeQuarantineRootPath, layout.QuarantineRootPath);
        Assert.Equal(paths.ExecutionRuntimeLeaseRootPath, layout.LeaseRootPath);
        Assert.Equal(Path.Combine(paths.ExecutionRuntimeLeaseRootPath, "locks"), layout.LockRootPath);
        Assert.Equal(Path.Combine(layout.LockRootPath, "identities"), layout.IdentityLockRootPath);
        Assert.Equal(Path.Combine(layout.LockRootPath, "store.lock"), layout.StoreFencePath);
        Assert.Equal(Path.Combine(layout.LockRootPath, "recovery.lock"), layout.RecoveryFencePath);
        Assert.Equal(Path.Combine(layout.LockRootPath, "gc-retentions.lock"), layout.GarbageCollectionRetentionFencePath);
        Assert.Equal(paths.ExecutionRuntimeTombstoneRootPath, layout.TombstoneRootPath);
        Assert.Equal(Path.Combine(paths.ExecutionRuntimeTombstoneRootPath, "marks"), layout.GarbageCollectionMarkRootPath);
        Assert.NotEqual(first.SingleFlightKey, second.SingleFlightKey);
        Assert.Equal(first.VersionKey, second.VersionKey);
        Assert.NotEqual(layout.GetReceiptPath(first), layout.GetReceiptPath(second));
        Assert.NotEqual(layout.GetSingleFlightLockPath(first), layout.GetSingleFlightLockPath(second));
        Assert.Equal(layout.GetVersionLockPath(first), layout.GetVersionLockPath(second));
        Assert.Matches("^[0-9a-f]{64}\\.lock$", Path.GetFileName(layout.GetSingleFlightLockPath(first)));
        Assert.EndsWith(Path.Combine("DotNet", "sunder.runtime.dotnet", "10.0.1", "linux-x64", "sunder.dotnet-worker-module.v1", "Sha512", new string('a', 128) + ".json"), layout.GetReceiptPath(first), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ensure_OfficialInstallPublishesContentAddressesAndWarmReuseDoesNotReadSource()
    {
        using var fixture = await StoreFixture.CreateAsync();

        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        File.Delete(fixture.ArchivePath);
        var reused = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);

        Assert.False(installed.AlreadyPresent);
        Assert.True(reused.AlreadyPresent);
        Assert.Equal(installed.Receipt.ContentIdentity, reused.Receipt.ContentIdentity);
        Assert.True(File.Exists(fixture.Layout.GetArchivePath(installed.Receipt.ArchiveSha256)));
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
        Assert.True(File.Exists(fixture.Layout.GetReceiptPath(installed.Receipt.Identity)));
        Assert.True(File.Exists(fixture.Layout.GetHealthPath(installed.Receipt.ContentIdentity)));
        Assert.Equal(installed.Receipt.ArchiveSha256, installed.Receipt.InstalledArtifact.ArchiveSha256);
        Assert.Equal(installed.Receipt.ContentIdentity, installed.Receipt.InstalledArtifact.ContentIdentity);
        Assert.Equal(fixture.Plan.Artifact.Distribution.DownloadUrl, installed.Receipt.Provenance.FinalDownloadUrl);
        Assert.Equal(fixture.Plan.Artifact.Distribution, installed.Receipt.Provenance.Distribution);
        Assert.Equal(fixture.Plan.Artifact.Support, installed.Receipt.Support);
    }

    [Fact]
    public async Task Ensure_OfficialArchivePlanMismatchPublishesNothing()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var mismatch = fixture.Plan with { ExpectedArchiveSha256 = new string('f', 64) };

        await Assert.ThrowsAsync<ExecutionRuntimePlanMismatchException>(() => fixture.Store.EnsureAsync(mismatch, fixture.ArchivePath));

        Assert.Null(await fixture.Store.GetReceiptAsync(fixture.Plan.Identity, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(fixture.Layout.ReceiptRootPath, "*.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(fixture.Layout.ArchiveRootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Ensure_RejectsSameVersionChangedVendorChecksumAndKeepsOriginalReceipt()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var first = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var changed = await fixture.CreateArtifactAsync(version: first.Identity.VendorVersion, payloadByte: 0x44);

        var failure = await Assert.ThrowsAsync<RuntimeExecutionRuntimeException>(() =>
            fixture.Store.EnsureAsync(new ExecutionRuntimeAcquisitionPlan(changed.Artifact), changed.ArchivePath));

        Assert.Equal(ExecutionRuntimeErrorCodes.ArtifactChanged, failure.Code);
        Assert.NotNull(await fixture.Store.GetReceiptAsync(first.Identity, CancellationToken.None));
        Assert.Null(await fixture.Store.GetReceiptAsync(ExecutionRuntimeIdentity.FromContract(changed.Artifact.Identity), CancellationToken.None));
    }

    [Fact]
    public async Task Ensure_ForwardOfficialVersionUsesActualVendorVersionWithoutRevisionAlias()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var first = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var forward = await fixture.CreateArtifactAsync(version: "10.0.2", payloadByte: 0x45);

        var second = (await fixture.Store.EnsureAsync(new ExecutionRuntimeAcquisitionPlan(forward.Artifact), forward.ArchivePath)).Receipt;
        await using var snapshot = await new LocalExecutionRuntimeBindingResolver(fixture.Store, TimeProvider.System).CaptureAsync();
        var binding = Assert.Single(await snapshot.ResolveInstalledAsync(CreateRuntimePackageManifest(PackageTargetSelection.GetCurrentRuntimeIdentifier(), ">=10.0.0 <11.0.0"), CancellationToken.None));

        Assert.Equal("10.0.1", first.Identity.VendorVersion);
        Assert.Equal("10.0.2", second.Identity.VendorVersion);
        Assert.Equal(second.Identity.ToContract(), binding.Runtime);
    }

    [Fact]
    public async Task Ensure_PublishedRootRejectsOrdinaryWritesAndPreservesLauncherMode()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var root = fixture.Layout.GetContentRootPath(receipt.ContentIdentity);

        AssertRuntimeRootSealed(root, receipt);
    }

    [Fact]
    public async Task Verify_WritablePermissionDriftFailsWarmAndDeepVerification()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var launcher = GetLauncherPath(fixture.Layout.GetContentRootPath(receipt.ContentIdentity), receipt.LauncherPath);
        File.SetUnixFileMode(launcher, File.GetUnixFileMode(launcher) | UnixFileMode.UserWrite);

        var warm = await fixture.Store.VerifyAsync(receipt.Identity, deep: false);
        var deep = await fixture.Store.VerifyAsync(receipt.Identity, deep: true);

        Assert.False(warm.Healthy);
        Assert.False(deep.Healthy);
    }

    [Fact]
    public async Task Ensure_CorruptWarmRootIsQuarantinedAndRebuiltFromExactArchive()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var file = Path.Combine(fixture.Layout.GetContentRootPath(receipt.ContentIdentity), "shared", "Microsoft.NETCore.App", receipt.Identity.VendorVersion, "System.Private.CoreLib.dll");
        ExecutionRuntimeRootFileSystem.MakeOwnedTreeWritable(fixture.Layout.GetContentRootPath(receipt.ContentIdentity));
        await File.WriteAllTextAsync(file, "tampered");

        var repaired = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);

        Assert.False(repaired.AlreadyPresent);
        Assert.True((await fixture.Store.VerifyAsync(receipt.Identity, deep: true)).Healthy);
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(fixture.Layout.QuarantineRootPath));
    }

    [Fact]
    public async Task Repair_CorruptRootReusesImmutableCachedArchive()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        File.Delete(fixture.ArchivePath);
        var root = fixture.Layout.GetContentRootPath(receipt.ContentIdentity);
        ExecutionRuntimeRootFileSystem.MakeOwnedTreeWritable(root);
        File.Delete(Path.Combine(root, "shared", "Microsoft.NETCore.App", receipt.Identity.VendorVersion, "System.Private.CoreLib.dll"));

        var repaired = await fixture.Store.RepairAsync(receipt.Identity);

        Assert.NotNull(repaired);
        Assert.True((await fixture.Store.VerifyAsync(receipt.Identity, deep: true)).Healthy);
    }

    [Fact]
    public async Task Ensure_CancellationAfterValidationLeavesNoPublicationOrJournal()
    {
        using var injector = new BlockingFaultInjector(ExecutionRuntimeStoreFaultPoint.Validated);
        using var fixture = await StoreFixture.CreateAsync(faultInjector: injector);
        using var cancel = new CancellationTokenSource();
        var task = fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath, cancel.Token);
        Assert.True(injector.WaitUntilBlocked(TimeSpan.FromSeconds(10)));
        await cancel.CancelAsync();
        injector.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.Null(await fixture.Store.GetReceiptAsync(fixture.Plan.Identity, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(fixture.Layout.TransactionRootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Ensure_IndependentStoreInstancesSerializeExactIdentityBeforeValidation()
    {
        using var injector = new BlockingFaultInjector(ExecutionRuntimeStoreFaultPoint.Validated);
        using var fixture = await StoreFixture.CreateAsync(faultInjector: injector);
        var secondStore = fixture.CreateStore();
        var first = fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        Assert.True(injector.WaitUntilBlocked(TimeSpan.FromSeconds(10)));

        var second = secondStore.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        await Task.Delay(100);
        Assert.False(second.IsCompleted);
        injector.Release();

        await first;
        Assert.True((await second).AlreadyPresent);
    }

    [Fact]
    public async Task Ensure_ConcurrentExactRequestsShareOneAcquisition()
    {
        using var injector = new BlockingFaultInjector(ExecutionRuntimeStoreFaultPoint.Validated);
        using var fixture = await StoreFixture.CreateAsync(faultInjector: injector);
        var first = fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        Assert.True(injector.WaitUntilBlocked(TimeSpan.FromSeconds(10)));
        var second = fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var third = fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        injector.Release();

        var results = await Task.WhenAll(first, second, third);

        Assert.All(results, result => Assert.False(result.AlreadyPresent));
        Assert.Single(results.Select(result => result.Receipt.ContentIdentity).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Ensure_CancellationWhileWaitingForIdentityLockPublishesNothingForCancelledWaiter()
    {
        using var injector = new BlockingFaultInjector(ExecutionRuntimeStoreFaultPoint.Validated);
        using var fixture = await StoreFixture.CreateAsync(faultInjector: injector);
        var leader = fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        Assert.True(injector.WaitUntilBlocked(TimeSpan.FromSeconds(10)));
        using var cancel = new CancellationTokenSource();
        var waiter = fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath, cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        injector.Release();
        var receipt = (await leader).Receipt;

        Assert.NotNull(await fixture.Store.GetReceiptAsync(receipt.Identity, CancellationToken.None));
    }

    [Fact]
    public async Task GetReceipt_ValidatesDurableStateAndCarriesOfficialLauncherArguments()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;

        var receipt = await fixture.Store.GetReceiptAsync(installed.Identity, CancellationToken.None);

        Assert.NotNull(receipt);
        Assert.Equal(installed.Identity, receipt.Identity);
        Assert.Equal(OfficialRuntimeMaterializer.LauncherPath(fixture.Plan.Artifact.Identity), receipt.LauncherPath);
        Assert.Equal(["exec", OfficialRuntimeSourcePolicy.EntryPointToken], receipt.LauncherArguments);
    }

    [Fact]
    public async Task GetReceipt_NullContentPathIsRejectedAsInvalidData()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var path = fixture.Layout.GetReceiptPath(receipt.Identity);
        var json = JsonSerializer.SerializeToNode(receipt, ExecutionRuntimeStore.JsonOptions)!;
        json["content"]![0]!["path"] = null;
        await File.WriteAllTextAsync(path, json.ToJsonString(ExecutionRuntimeStore.JsonOptions));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.GetReceiptAsync(receipt.Identity, CancellationToken.None));
    }

    [Fact]
    public async Task Verify_WarmReceiptMismatchFailsClosedWithoutHashingPayload()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        await File.WriteAllTextAsync(fixture.Layout.GetHealthPath(receipt.ContentIdentity), "{\"schemaVersion\":3,\"contentIdentity\":\"" + receipt.ContentIdentity + "\",\"status\":\"healthy\",\"deepVerified\":false,\"checkedAtUtc\":\"2026-01-01T00:00:00Z\",\"receiptSha256\":\"" + new string('f', 64) + "\",\"files\":[]}");

        var verification = await fixture.Store.VerifyAsync(receipt.Identity, deep: false);

        Assert.False(verification.Healthy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verify_MissingOrMalformedWarmMarkerRequiresExplicitDeepRepair(bool malformed)
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var healthPath = fixture.Layout.GetHealthPath(receipt.ContentIdentity);
        if (malformed)
        {
            await File.WriteAllTextAsync(healthPath, "not json");
        }
        else
        {
            File.Delete(healthPath);
        }

        var warm = await fixture.Store.VerifyAsync(receipt.Identity, deep: false);
        var deep = await fixture.Store.VerifyAsync(receipt.Identity, deep: true);

        Assert.False(warm.Healthy);
        Assert.True(deep.Healthy);
    }

    [Fact]
    public async Task Verify_DeepVerificationRejectsReceiptContentNotBoundToInventory()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var path = fixture.Layout.GetReceiptPath(receipt.Identity);
        var json = JsonSerializer.SerializeToNode(receipt, ExecutionRuntimeStore.JsonOptions)!;
        json["content"]![0]!["sha256"] = new string('0', 64);
        await File.WriteAllTextAsync(path, json.ToJsonString(ExecutionRuntimeStore.JsonOptions));

        var verification = await fixture.Store.VerifyAsync(receipt.Identity, deep: true);

        Assert.False(verification.Healthy);
    }

    [Fact]
    public async Task LocalBinding_DistinguishesMissingRuntimeAndSelectsHighestHealthyVersion()
    {
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        using var fixture = await StoreFixture.CreateAsync();
        await using (var empty = await new LocalExecutionRuntimeBindingResolver(fixture.Store, TimeProvider.System).CaptureAsync())
        {
            var missing = await Assert.ThrowsAsync<InvalidDataException>(() => empty.ResolveInstalledAsync(CreateRuntimePackageManifest(rid, ">=10.0.0 <11.0.0"), CancellationToken.None));
            Assert.StartsWith(ExecutionRuntimeErrorCodes.Missing, missing.Message, StringComparison.Ordinal);
        }

        var first = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var secondArtifact = await fixture.CreateArtifactAsync(version: "10.0.2", payloadByte: 0x46);
        var second = (await fixture.Store.EnsureAsync(new ExecutionRuntimeAcquisitionPlan(secondArtifact.Artifact), secondArtifact.ArchivePath)).Receipt;

        await using var snapshot = await new LocalExecutionRuntimeBindingResolver(fixture.Store, TimeProvider.System).CaptureAsync();
        var binding = Assert.Single(await snapshot.ResolveInstalledAsync(CreateRuntimePackageManifest(rid, ">=10.0.0 <11.0.0"), CancellationToken.None));

        Assert.Equal(first.Identity.RuntimeId, binding.Runtime.RuntimeId);
        Assert.Equal(second.Identity.ToContract(), binding.Runtime);
    }

    [Fact]
    public async Task LocalBinding_IgnoresNonCurrentRidTargetsAndReportsUnsupportedLineAsMissing()
    {
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        var alternateRid = AlternateRid(rid);
        using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        await using var snapshot = await new LocalExecutionRuntimeBindingResolver(fixture.Store, TimeProvider.System).CaptureAsync();

        var bindings = await snapshot.ResolveInstalledAsync(CreateRuntimePackageManifest(alternateRid, ">=10.0.0 <11.0.0", rid), CancellationToken.None);
        var unsupported = await Assert.ThrowsAsync<InvalidDataException>(() => snapshot.ResolveInstalledAsync(CreateRuntimePackageManifest(rid, ">=11.0.0 <12.0.0"), CancellationToken.None));

        Assert.Single(bindings);
        Assert.StartsWith(ExecutionRuntimeErrorCodes.Missing, unsupported.Message, StringComparison.Ordinal);
        Assert.Contains("sunder runtime ensure sunder.runtime.dotnet '>=11.0.0 <12.0.0'", unsupported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GarbageCollection_AuthoritativeBindingRetainsRootReceiptAndArchive()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        fixture.GarbageCollectionAuthority.SetBindings(CreateInstalledBinding(receipt));

        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        var result = await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);

        Assert.Equal(0, result.RootsRemoved);
        Assert.Equal(0, result.ArchivesRemoved);
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(receipt.ContentIdentity)));
        Assert.True(File.Exists(fixture.Layout.GetReceiptPath(receipt.Identity)));
        Assert.True(File.Exists(fixture.Layout.GetArchivePath(receipt.ArchiveSha256)));
    }

    [Fact]
    public async Task GarbageCollection_AuthoritativeBindingWithoutExactReceiptFailsClosed()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        File.Delete(fixture.Layout.GetReceiptPath(receipt.Identity));
        fixture.GarbageCollectionAuthority.SetBindings(CreateInstalledBinding(receipt));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.CollectGarbageAsync(TimeSpan.Zero));
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(receipt.ContentIdentity)));
    }

    [Fact]
    public async Task GarbageCollection_AuthoritativeMalformedReceiptFailsClosedWithoutMutation()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        await File.WriteAllTextAsync(fixture.Layout.GetReceiptPath(receipt.Identity), "not json");
        fixture.GarbageCollectionAuthority.SetBindings(CreateInstalledBinding(receipt));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.CollectGarbageAsync(TimeSpan.Zero));
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(receipt.ContentIdentity)));
    }

    [Fact]
    public async Task GarbageCollection_TamperedAuthoritativeContentIdentityFailsBeforeSweep()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var binding = CreateInstalledBinding(receipt) with
        {
            InstalledArtifact = receipt.InstalledArtifact with { ContentIdentity = new string('0', 64) },
        };
        fixture.GarbageCollectionAuthority.SetBindings(binding);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.CollectGarbageAsync(TimeSpan.Zero));
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(receipt.ContentIdentity)));
    }

    [Fact]
    public async Task GarbageCollection_UnreferencedRuntimeMarksThenCollectsRootsAndArchivesOnly()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;

        var marked = await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        var removed = await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);

        Assert.Equal(0, marked.RootsRemoved);
        Assert.Equal(0, marked.ArchivesRemoved);
        Assert.Equal(1, removed.RootsRemoved);
        Assert.Equal(1, removed.ArchivesRemoved);
        Assert.False(Directory.Exists(fixture.Layout.GetContentRootPath(receipt.ContentIdentity)));
        Assert.False(File.Exists(fixture.Layout.GetReceiptPath(receipt.Identity)));
        Assert.False(File.Exists(fixture.Layout.GetArchivePath(receipt.ArchiveSha256)));
    }

    [Fact]
    public async Task GarbageCollection_TransientRetentionBridgesAuthorityHandoff()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        await using var retention = await fixture.Store.RetainAsync(new ExecutionRuntimeRetention(Identity: receipt.Identity, ContentIdentity: receipt.ContentIdentity, ArchiveSha256: receipt.ArchiveSha256, ArchiveSize: receipt.ArchiveSize));

        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(receipt.ContentIdentity)));

        await retention.DisposeAsync();
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        var removed = await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        Assert.Equal(1, removed.RootsRemoved);
    }

    [Fact]
    public async Task GarbageCollection_RetentionFromIndependentStoreDefersSweep()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var collector = fixture.CreateStore();
        await using var retention = await fixture.Store.RetainAsync(new ExecutionRuntimeRetention(Identity: receipt.Identity));

        await collector.CollectGarbageAsync(TimeSpan.Zero);
        await collector.CollectGarbageAsync(TimeSpan.Zero);

        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(receipt.ContentIdentity)));
    }

    [Fact]
    public async Task GarbageCollection_AuthorityFailureMutatesNothing()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        fixture.GarbageCollectionAuthority.ThrowOnExecute = true;

        await Assert.ThrowsAsync<IOException>(() => fixture.Store.CollectGarbageAsync(TimeSpan.Zero));

        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(receipt.ContentIdentity)));
        Assert.True(File.Exists(fixture.Layout.GetArchivePath(receipt.ArchiveSha256)));
    }

    [Theory]
    [InlineData("roots")]
    [InlineData("archives")]
    [InlineData("receipts")]
    public async Task GarbageCollection_RejectsLinkedStoreSubtreeWithoutTouchingTarget(string subtree)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await StoreFixture.CreateAsync();
        var target = Path.Combine(fixture.RootPath, "outside-target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "sentinel.txt"), "safe");
        var link = subtree switch
        {
            "roots" => fixture.Layout.ContentRootPath,
            "archives" => fixture.Layout.ArchiveRootPath,
            _ => fixture.Layout.ReceiptRootPath,
        };
        Directory.Delete(link, recursive: true);
        Directory.CreateSymbolicLink(link, target);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.CollectGarbageAsync(TimeSpan.Zero));
        Assert.True(File.Exists(Path.Combine(target, "sentinel.txt")));
    }

    [Fact]
    public async Task ProcessLease_ReservesDurablyAndReleasesBeforeContainedLaunch()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        await using var lease = await ReserveAsync(fixture, receipt, "package.a");
        var leases = Directory.EnumerateFiles(fixture.Layout.LeaseRootPath, "*.json").ToArray();

        Assert.Single(leases);
        var durable = await ReadLeaseAsync(leases[0]);
        Assert.Equal(receipt.ContentIdentity, durable.ContentIdentity);
        Assert.Equal("package.a", durable.PackageId);

        await lease.DisposeAsync();
        Assert.Empty(Directory.EnumerateFiles(fixture.Layout.LeaseRootPath, "*.json"));
    }

    [Fact]
    public async Task GarbageCollection_ActiveLeasePreventsZeroGraceSweep()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        await using var lease = await ReserveAsync(fixture, receipt, "package.a");

        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);

        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(receipt.ContentIdentity)));
    }

    [Fact]
    public async Task GarbageCollection_SharedRootRemainsUntilEveryLeaseIsReleased()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var first = await ReserveAsync(fixture, receipt, "package.a");
        var second = await ReserveAsync(fixture, receipt, "package.b");

        await first.DisposeAsync();
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(receipt.ContentIdentity)));

        await second.DisposeAsync();
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        var removed = await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        Assert.Equal(1, removed.RootsRemoved);
    }

    [Fact]
    public async Task Initialize_RemovesNeverStartedLeaseReservation()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var lease = await ReserveAsync(fixture, receipt, "package.a");
        var leasePath = Assert.Single(Directory.EnumerateFiles(fixture.Layout.LeaseRootPath, "*.json"));
        GC.KeepAlive(lease);

        await fixture.CreateStore().RecoverProcessLeasesAsync(CancellationToken.None);

        Assert.False(File.Exists(leasePath));
    }

    [Fact]
    public async Task Initialize_EmptyContainmentAfterPidReuseNeverSignalsProcess()
    {
        using var controller = new FakeContainmentController { ProbeResult = ExecutionRuntimeContainmentProbe.Empty };
        using var fixture = await StoreFixture.CreateAsync(containmentController: controller);
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        await using var lease = await ReserveAsync(fixture, receipt, "package.a");
        var leasePath = Assert.Single(Directory.EnumerateFiles(fixture.Layout.LeaseRootPath, "*.json"));
        var current = await ReadLeaseAsync(leasePath);
        await File.WriteAllTextAsync(leasePath, JsonSerializer.Serialize(CreateAttachedContainment(current, fixture.Layout.GetContentRootPath(receipt.ContentIdentity), GetLauncherPath(fixture.Layout.GetContentRootPath(receipt.ContentIdentity), receipt.LauncherPath), 99), ExecutionRuntimeStore.JsonOptions));

        await fixture.CreateStore(containmentController: controller).RecoverProcessLeasesAsync(CancellationToken.None);

        Assert.Equal(0, controller.TerminateCalls);
        Assert.False(File.Exists(leasePath));
    }

    [Fact]
    public async Task Initialize_TerminatesAndReapsVerifiedOrphanProcessTree()
    {
        using var controller = new FakeContainmentController { ProbeResult = ExecutionRuntimeContainmentProbe.Active };
        using var fixture = await StoreFixture.CreateAsync(containmentController: controller);
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        await using var lease = await ReserveAsync(fixture, receipt, "package.a");
        var leasePath = Assert.Single(Directory.EnumerateFiles(fixture.Layout.LeaseRootPath, "*.json"));
        var current = await ReadLeaseAsync(leasePath);
        await File.WriteAllTextAsync(leasePath, JsonSerializer.Serialize(CreateAttachedContainment(current, fixture.Layout.GetContentRootPath(receipt.ContentIdentity), GetLauncherPath(fixture.Layout.GetContentRootPath(receipt.ContentIdentity), receipt.LauncherPath), 100), ExecutionRuntimeStore.JsonOptions));

        await fixture.CreateStore(containmentController: controller).RecoverProcessLeasesAsync(CancellationToken.None);

        Assert.Equal(1, controller.TerminateCalls);
        Assert.False(File.Exists(leasePath));
    }

    [Fact]
    public async Task Initialize_RecoversOrSafelyCollectsEveryPublicationBoundary()
    {
        foreach (var point in new[]
        {
            ExecutionRuntimeStoreFaultPoint.JournalPrepared,
            ExecutionRuntimeStoreFaultPoint.ArchivePublished,
            ExecutionRuntimeStoreFaultPoint.RootSealed,
            ExecutionRuntimeStoreFaultPoint.RootMoved,
            ExecutionRuntimeStoreFaultPoint.RootPublished,
            ExecutionRuntimeStoreFaultPoint.ReceiptPublished,
            ExecutionRuntimeStoreFaultPoint.HealthPublished,
        })
        {
            using var fixture = await StoreFixture.CreateAsync(faultInjector: new ThrowingFaultInjector(point));
            await Assert.ThrowsAsync<ExecutionRuntimeStoreSimulatedCrashException>(() => fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath));

            var recovered = fixture.CreateStore();
            await recovered.InitializeAsync();
            var result = await recovered.EnsureAsync(fixture.Plan, fixture.ArchivePath);

            Assert.NotNull(await recovered.GetReceiptAsync(result.Receipt.Identity, CancellationToken.None));
            Assert.True((await recovered.VerifyAsync(result.Receipt.Identity, deep: true)).Healthy);
        }
    }

    [Fact]
    public async Task Initialize_QuarantinesUnownedAndAmbiguousPartialState()
    {
        using var fixture = await StoreFixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(fixture.Layout.StagingRootPath, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(fixture.Layout.TransactionRootPath, Guid.NewGuid().ToString("N")));

        await fixture.CreateStore().InitializeAsync();

        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(fixture.Layout.QuarantineRootPath));
    }

    [Fact]
    public async Task GarbageCollection_PartialTombstoneMoveIsRestoredBeforeReturning()
    {
        using var fixture = await StoreFixture.CreateAsync(faultInjector: new ThrowingFaultInjector(ExecutionRuntimeStoreFaultPoint.TombstoneRootMoved));
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        await Assert.ThrowsAsync<ExecutionRuntimeStoreSimulatedCrashException>(() => fixture.Store.CollectGarbageAsync(TimeSpan.Zero));

        await fixture.CreateStore().InitializeAsync();
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(receipt.ContentIdentity)));
        var recoveredStore = fixture.CreateStore();
        await recoveredStore.CollectGarbageAsync(TimeSpan.Zero); // Recovery starts a new unreferenced grace observation.
        var result = await recoveredStore.CollectGarbageAsync(TimeSpan.Zero);
        Assert.False(Directory.Exists(fixture.Layout.GetContentRootPath(receipt.ContentIdentity)));
        Assert.Equal(1, result.RootsRemoved);
    }

    private static SunderPackageManifest CreateRuntimePackageManifest(string rid, string versionRange, string? alternateRid = null)
    {
        SunderPackageTargetManifest CreateTarget(string targetRid) => new()
        {
            Role = SunderPackageFormat.RuntimeHostRole,
            Rid = targetRid,
            Kind = SunderPackageFormat.WorkerTargetKind,
            EntryPoint = "worker.dll",
            TargetFramework = "net10.0",
            SdkVersion = "1.1.0",
            RequiredHostCapabilities = ["core.v1", "execution-runtime.v1", "rpc.v1", "sdk-baseline-1-1.v1", "worker-protocol.v1"],
            ExecutionRuntime = new SunderPackageExecutionRuntimeManifest
            {
                Id = SunderPackageFormat.DotNetExecutionRuntimeId,
                VersionRange = versionRange,
                ModuleAbi = SunderPackageFormat.DotNetWorkerModuleAbi,
            },
        };

        return new SunderPackageManifest
        {
            ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion,
            ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
            Id = "local.binding.test",
            Name = "Local Binding Test",
            Version = "1.0.0",
            Targets = alternateRid is null ? [CreateTarget(rid)] : [CreateTarget(rid), CreateTarget(alternateRid)],
            DependsOn = [],
            ContractBundles = [],
            UsesContracts = [],
            Provides = [],
        };
    }

    private static InstalledPackageExecutionRuntimeBindingRecord CreateInstalledBinding(ExecutionRuntimeReceiptV1 receipt)
        => new(
            SunderPackageFormat.RuntimeHostRole,
            receipt.Identity.Rid,
            new RuntimeExecutionRuntimeRequirement(receipt.Identity.RuntimeId, ">=10.0.0 <11.0.0", receipt.Identity.ModuleAbi),
            receipt.Identity.ToContract(),
            receipt.InstalledArtifact,
            DateTimeOffset.Parse("2026-08-28T12:00:00Z"));

    private static RuntimeExecutionRuntimeIdentity SyntheticIdentity(string version, string rid, string digest)
        => new(
            ConfiguredOfficialSourceId.DotNet,
            SunderPackageFormat.DotNetExecutionRuntimeId,
            version,
            rid,
            SunderPackageFormat.DotNetWorkerModuleAbi,
            RuntimeExecutionRuntimeChecksumAlgorithm.Sha512,
            digest);

    private static string AlternateRid(string current)
    {
        var prefix = current[..(current.IndexOf('-', StringComparison.Ordinal) + 1)];
        return SunderPackageFormat.SupportedRuntimeIdentifiers.First(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal) && !string.Equals(candidate, current, StringComparison.Ordinal));
    }

    private static void AssertRuntimeRootSealed(string rootPath, ExecutionRuntimeReceiptV1 receipt)
    {
        var launcherPath = GetLauncherPath(rootPath, receipt.LauncherPath);
        ExecutionRuntimeRootFileSystem.VerifySealed(rootPath, launcherPath);
        if (OperatingSystem.IsWindows())
        {
            Assert.All(Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories), path => Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0));
            return;
        }

        foreach (var content in receipt.Content)
        {
            var path = Path.Combine([rootPath, .. content.Path.Split('/')]);
            Assert.Equal((UnixFileMode)content.UnixMode, File.GetUnixFileMode(path));
        }
    }

    private static async Task<IExecutionRuntimeProcessLease> ReserveAsync(StoreFixture fixture, ExecutionRuntimeReceiptV1 receipt, string packageId)
    {
        var runtimeRoot = fixture.Layout.GetContentRootPath(receipt.ContentIdentity);
        return await fixture.Store.ReserveProcessLeaseAsync(
            new ExecutionRuntimeLeaseOwner(packageId, Guid.NewGuid().ToString("N"), Guid.NewGuid()),
            receipt.ContentIdentity,
            runtimeRoot,
            GetLauncherPath(runtimeRoot, receipt.LauncherPath),
            receipt.Identity,
            CancellationToken.None);
    }

    private static string GetLauncherPath(string runtimeRoot, string launcherPath)
        => Path.Combine([runtimeRoot, .. launcherPath.Split('/')]);

    private static async Task<ExecutionRuntimeProcessLeaseV1> ReadLeaseAsync(string path)
        => JsonSerializer.Deserialize<ExecutionRuntimeProcessLeaseV1>(
               await File.ReadAllTextAsync(path),
               ExecutionRuntimeStore.JsonOptions)
           ?? throw new InvalidDataException($"Lease '{path}' is empty.");

    private static ExecutionRuntimeProcessLeaseV1 CreateAttachedContainment(
        ExecutionRuntimeProcessLeaseV1 reservation,
        string runtimeRoot,
        string launcherPath,
        long birthIdentity)
    {
        var startRequestedAtUtc = reservation.ReservedAtUtc.AddMilliseconds(100);
        var containmentAttachedAtUtc = reservation.ReservedAtUtc.AddMilliseconds(200);
        return reservation with
        {
            ProcessStartRequestedAtUtc = startRequestedAtUtc,
            ContainmentGeneration = Guid.NewGuid(),
            ContainmentKind = OperatingSystem.IsWindows()
                ? SystemExecutionRuntimeProcessContainmentController.WindowsJobKind
                : SystemExecutionRuntimeProcessContainmentController.UnixProcessGroupKind,
            ContainmentIdentity = OperatingSystem.IsWindows()
                ? $@"Local\Sunder.ExecutionRuntime.{reservation.LeaseId}"
                : Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ContainmentProcessId = Environment.ProcessId,
            ContainmentProcessBirthIdentity = ExecutionRuntimeProcessBirthIdentity.CreateSyntheticForCurrentPlatform(birthIdentity),
            ContainmentProcessStartedAtUtc = startRequestedAtUtc,
            ContainmentExecutablePath = Path.GetFullPath(typeof(object).Assembly.Location),
            ContainmentAttachedAtUtc = containmentAttachedAtUtc,
            ProcessId = Environment.ProcessId,
            ProcessBirthIdentity = ExecutionRuntimeProcessBirthIdentity.CreateSyntheticForCurrentPlatform(birthIdentity + 1),
            ProcessStartedAtUtc = containmentAttachedAtUtc,
            ExecutablePath = launcherPath,
            RuntimeRootPath = runtimeRoot,
            AttachedAtUtc = reservation.ReservedAtUtc.AddMilliseconds(300),
        };
    }

    private sealed class StoreFixture : IDisposable
    {
        private StoreFixture(string rootPath, RuntimeArtifact artifact, ExecutionRuntimeStoreLayout layout, TestExecutionRuntimeGarbageCollectionAuthority authority, ExecutionRuntimeStore store)
        {
            RootPath = rootPath;
            ArchivePath = artifact.ArchivePath;
            Layout = layout;
            GarbageCollectionAuthority = authority;
            Store = store;
            Plan = new ExecutionRuntimeAcquisitionPlan(artifact.Artifact);
        }

        public string RootPath { get; }
        public string ArchivePath { get; }
        public ExecutionRuntimeStoreLayout Layout { get; }
        public ExecutionRuntimeAcquisitionPlan Plan { get; }
        public TestExecutionRuntimeGarbageCollectionAuthority GarbageCollectionAuthority { get; }
        public ExecutionRuntimeStore Store { get; }

        public static async Task<StoreFixture> CreateAsync(
            IExecutionRuntimeStoreFaultInjector? faultInjector = null,
            TimeProvider? timeProvider = null,
            Guid? hostInstanceId = null,
            IExecutionRuntimeProcessContainmentController? containmentController = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "SunderExecutionRuntimeStoreTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            var authority = new TestExecutionRuntimeGarbageCollectionAuthority();
            var store = CreateStore(layout, authority, faultInjector, timeProvider, hostInstanceId, containmentController);
            await store.InitializeAsync();
            var artifact = await ExecutionRuntimeStoreTests.CreateArtifactAsync(root, PackageTargetSelection.GetCurrentRuntimeIdentifier(), "10.0.1", 0x42);
            return new StoreFixture(root, artifact, layout, authority, store);
        }

        public Task<RuntimeArtifact> CreateArtifactAsync(string version = "10.0.1", byte payloadByte = 0x42)
            => ExecutionRuntimeStoreTests.CreateArtifactAsync(RootPath, PackageTargetSelection.GetCurrentRuntimeIdentifier(), version, payloadByte);

        public ExecutionRuntimeStore CreateStore(
            TimeProvider? timeProvider = null,
            Guid? hostInstanceId = null,
            IExecutionRuntimeProcessContainmentController? containmentController = null,
            IExecutionRuntimeStoreFaultInjector? faultInjector = null,
            IExecutionRuntimeVerificationIoObserver? verificationIoObserver = null)
            => CreateStore(Layout, GarbageCollectionAuthority, faultInjector, timeProvider, hostInstanceId, containmentController, verificationIoObserver);

        private static ExecutionRuntimeStore CreateStore(
            ExecutionRuntimeStoreLayout layout,
            TestExecutionRuntimeGarbageCollectionAuthority authority,
            IExecutionRuntimeStoreFaultInjector? faultInjector,
            TimeProvider? timeProvider,
            Guid? hostInstanceId,
            IExecutionRuntimeProcessContainmentController? containmentController,
            IExecutionRuntimeVerificationIoObserver? verificationIoObserver = null)
            => new(
                layout,
                authority,
                timeProvider,
                faultInjector,
                hostInstanceId,
                processController: null,
                containmentController,
                verificationIoObserver,
                materializer: new OfficialRuntimeMaterializer(new NoopOfficialRuntimePlatformProbe()));

        public void Dispose()
            => _ = ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(RootPath);
    }

    private sealed record RuntimeArtifact(string ArchivePath, RuntimeExecutionRuntimeVendorArtifact Artifact);

    private static async Task<RuntimeArtifact> CreateArtifactAsync(string rootPath, string rid, string version, byte payloadByte)
    {
        var archiveRoot = Path.Combine(rootPath, "archives", version + "-" + payloadByte.ToString("x2"));
        Directory.CreateDirectory(archiveRoot);
        var archivePath = Path.Combine(archiveRoot, OfficialRuntimeSourcePolicy.ArchiveName(ConfiguredOfficialSourceId.DotNet, version, rid));
        var launcherName = rid.StartsWith("win-", StringComparison.Ordinal) ? "dotnet.exe" : "dotnet";
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [launcherName] = [0x7f, 0x45, 0x4c, 0x46, payloadByte],
            [$"shared/Microsoft.NETCore.App/{version}/System.Private.CoreLib.dll"] = Enumerable.Repeat(payloadByte, 64).ToArray(),
            ["LICENSE.txt"] = Encoding.UTF8.GetBytes("Synthetic official .NET runtime fixture."),
        };
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            foreach (var (name, bytes) in files.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                var entry = zip.CreateEntry(name.Replace('/', Path.DirectorySeparatorChar), CompressionLevel.NoCompression);
                await using var output = entry.Open();
                await output.WriteAsync(bytes);
            }
        }
        else
        {
            await using var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var gzip = new GZipStream(archive, CompressionLevel.NoCompression);
            await using var tar = new TarWriter(gzip, leaveOpen: false);
            foreach (var (name, bytes) in files.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                using var stream = new MemoryStream(bytes);
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = stream });
            }
        }

        var vendorHash = HashFile(archivePath, SHA512.Create());
        var archiveName = OfficialRuntimeSourcePolicy.ArchiveName(ConfiguredOfficialSourceId.DotNet, version, rid);
        var artifact = new RuntimeExecutionRuntimeVendorArtifact(
            new RuntimeExecutionRuntimeIdentity(
                ConfiguredOfficialSourceId.DotNet,
                SunderPackageFormat.DotNetExecutionRuntimeId,
                version,
                rid,
                SunderPackageFormat.DotNetWorkerModuleAbi,
                RuntimeExecutionRuntimeChecksumAlgorithm.Sha512,
                vendorHash),
            new RuntimeExecutionRuntimeDistribution(
                $"https://builds.dotnet.microsoft.com/dotnet/Runtime/{version}/{archiveName}",
                OfficialRuntimeSourcePolicy.MetadataUri(ConfiguredOfficialSourceId.DotNet).AbsoluteUri,
                Hash(Encoding.UTF8.GetBytes("metadata\n" + version + rid + payloadByte)),
                null,
                null,
                rid.StartsWith("win-", StringComparison.Ordinal) ? RuntimeExecutionRuntimeArchiveFormat.Zip : RuntimeExecutionRuntimeArchiveFormat.TarGzip,
                OfficialRuntimeSourcePolicy.Profile(ConfiguredOfficialSourceId.DotNet),
                new FileInfo(archivePath).Length),
            new RuntimeExecutionRuntimeSupportStatus(
                RuntimeExecutionRuntimeSupportState.Supported,
                "10.0 LTS",
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                null,
                null));
        return new RuntimeArtifact(archivePath, artifact);
    }

    private static string Hash(byte[] bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string HashFile(string path, HashAlgorithm algorithm)
    {
        using (algorithm)
        using (var stream = File.OpenRead(path))
        {
            return Convert.ToHexStringLower(algorithm.ComputeHash(stream));
        }
    }

    private sealed class NoopOfficialRuntimePlatformProbe : IOfficialRuntimePlatformProbe
    {
        public Task VerifyAsync(RuntimeExecutionRuntimeVendorArtifact artifact, string rootPath, string launcherRelativePath, CancellationToken token)
        {
            _ = artifact;
            token.ThrowIfCancellationRequested();
            var launcher = Path.Combine([rootPath, .. launcherRelativePath.Split('/')]);
            if (!File.Exists(launcher))
            {
                throw new FileNotFoundException("Official runtime launcher was not materialized.", launcher);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingFaultInjector(ExecutionRuntimeStoreFaultPoint point) : IExecutionRuntimeStoreFaultInjector
    {
        private int _hit;

        public void Hit(ExecutionRuntimeStoreFaultPoint current)
        {
            if (current == point && Interlocked.Exchange(ref _hit, 1) == 0)
            {
                throw new ExecutionRuntimeStoreSimulatedCrashException(point);
            }
        }
    }

    private sealed class BlockingFaultInjector(ExecutionRuntimeStoreFaultPoint point) : IExecutionRuntimeStoreFaultInjector, IDisposable
    {
        private readonly ManualResetEventSlim _blocked = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private int _hit;

        public void Hit(ExecutionRuntimeStoreFaultPoint current)
        {
            if (current != point || Interlocked.Exchange(ref _hit, 1) != 0)
            {
                return;
            }
            _blocked.Set();
            _release.Wait();
        }

        public bool WaitUntilBlocked(TimeSpan timeout) => _blocked.Wait(timeout);

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _blocked.Dispose();
            _release.Dispose();
        }
    }

    private sealed class FakeContainmentController : IExecutionRuntimeProcessContainmentController, IDisposable
    {
        public ExecutionRuntimeContainmentProbe ProbeResult { get; set; } = ExecutionRuntimeContainmentProbe.Empty;
        public int TerminateCalls { get; private set; }

        public Task<IExecutionRuntimePendingContainedProcess> StartAsync(
            string leaseId,
            Guid generation,
            ProcessStartInfo payloadStartInfo,
            string payloadReportPath,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("This fake only exercises durable containment recovery.");

        public ExecutionRuntimeContainmentProbe Probe(ExecutionRuntimeProcessLeaseV1 lease) => ProbeResult;

        public Task<bool> TerminateAndDrainAsync(ExecutionRuntimeProcessLeaseV1 lease, CancellationToken cancellationToken)
        {
            TerminateCalls++;
            ProbeResult = ExecutionRuntimeContainmentProbe.Empty;
            return Task.FromResult(true);
        }

        public void Dispose()
        {
        }
    }
}
