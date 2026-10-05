using System.Security.Cryptography;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ExecutionRuntimeStoreTests
{
    private const string ReleaseKeyId = "sunder.release.store-test.v1";
    private const string AttestationKeyId = "sunder.ci.store-test.v1";
    private static readonly byte[] ReleaseKey =
        Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();

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
        var root = Path.Combine(
            Path.GetTempPath(),
            "sunder-execution-runtime-acl-tests",
            Guid.NewGuid().ToString("N"));
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
    public void ExactIdentityKeysAndReceiptPathsIncludeModuleAbi()
    {
        var paths = new RuntimePackagePaths(Path.Combine(Path.GetTempPath(), "unused-runtime-state"));
        var layout = new ExecutionRuntimeStoreLayout(paths);
        var first = new ExecutionRuntimeIdentity("dotnet", "10.0.1", 7, "linux-x64", "net10.0");
        var second = first with { ModuleAbi = "net10.0-preview" };

        Assert.Equal(paths.ExecutionRuntimeStoreRootPath, layout.RootPath);
        Assert.Equal(paths.ExecutionRuntimeArtifactRootPath, layout.ArchiveRootPath);
        Assert.Equal(paths.ExecutionRuntimeManifestRootPath, layout.ManifestRootPath);
        Assert.Equal(paths.ExecutionRuntimeRootsPath, layout.ContentRootPath);
        Assert.Equal(paths.ExecutionRuntimeReceiptRootPath, layout.ReceiptRootPath);
        Assert.Equal(paths.ExecutionRuntimeHealthRootPath, layout.HealthRootPath);
        Assert.Equal(paths.ExecutionRuntimePolicyRootPath, layout.PolicyRootPath);
        Assert.Equal(paths.ExecutionRuntimeLifecyclePolicyFilePath, layout.LifecyclePolicyPath);
        Assert.Equal(paths.ExecutionRuntimeStagingRootPath, layout.StagingRootPath);
        Assert.Equal(paths.ExecutionRuntimeTransactionRootPath, layout.TransactionRootPath);
        Assert.Equal(paths.ExecutionRuntimeQuarantineRootPath, layout.QuarantineRootPath);
        Assert.Equal(paths.ExecutionRuntimeLeaseRootPath, layout.LeaseRootPath);
        Assert.Equal(Path.Combine(paths.ExecutionRuntimeLeaseRootPath, "locks"), layout.LockRootPath);
        Assert.Equal(Path.Combine(layout.LockRootPath, "identities"), layout.IdentityLockRootPath);
        Assert.Equal(Path.Combine(layout.LockRootPath, "store.lock"), layout.StoreFencePath);
        Assert.Equal(
            Path.Combine(layout.LockRootPath, "gc-retentions.lock"),
            layout.GarbageCollectionRetentionFencePath);
        Assert.Equal(
            Path.Combine(layout.LockRootPath, "lease-recovery-blocked"),
            layout.LeaseRecoveryFailurePath);
        Assert.Equal(paths.ExecutionRuntimeTombstoneRootPath, layout.TombstoneRootPath);
        Assert.Equal(
            Path.Combine(paths.ExecutionRuntimeTombstoneRootPath, "marks"),
            layout.GarbageCollectionMarkRootPath);
        Assert.NotEqual(first.SingleFlightKey, second.SingleFlightKey);
        Assert.NotEqual(layout.GetReceiptPath(first), layout.GetReceiptPath(second));
        Assert.NotEqual(layout.GetSingleFlightLockPath(first), layout.GetSingleFlightLockPath(second));
        Assert.Matches(
            "^[0-9a-f]{64}\\.lock$",
            Path.GetFileName(layout.GetSingleFlightLockPath(first)));
        Assert.Equal(
            Path.Combine("linux-x64", "net10.0.json"),
            Path.GetRelativePath(
                Path.Combine(layout.ReceiptRootPath, "dotnet", "10.0.1", "7"),
                layout.GetReceiptPath(first)));
    }

    [Fact]
    public async Task LocalBinding_DistinguishesMissingRuntimeFromMissingPolicy()
    {
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        using var fixture = await StoreFixture.CreateAsync(runtimeIdentifier: rid);
        var now = DateTimeOffset.Parse("2026-08-29T12:00:00Z");
        var cache = new ExecutionRuntimeLifecyclePolicyCache(fixture.Layout);
        var resolver = new LocalExecutionRuntimeBindingResolver(
            fixture.Store,
            cache,
            new MutableTimeProvider(now));
        var manifest = CreateRuntimePackageManifest(rid, ">=10.0.0 <11.0.0");

        await using (var empty = await resolver.CaptureAsync())
        {
            var missing = await Assert.ThrowsAsync<InvalidDataException>(() =>
                empty.ResolveInstalledAsync(manifest, CancellationToken.None));
            Assert.StartsWith(ExecutionRuntimeErrorCodes.Missing, missing.Message, StringComparison.Ordinal);
        }

        await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        await using var withoutPolicy = await resolver.CaptureAsync();
        var policyRequired = await Assert.ThrowsAsync<InvalidDataException>(() =>
            withoutPolicy.ResolveInstalledAsync(manifest, CancellationToken.None));
        Assert.StartsWith(ExecutionRuntimeErrorCodes.PolicyRequired, policyRequired.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalBinding_ReportsUnsupportedInstalledRuntimeLine()
    {
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        using var fixture = await StoreFixture.CreateAsync(runtimeIdentifier: rid);
        await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var resolver = new LocalExecutionRuntimeBindingResolver(
            fixture.Store,
            new ExecutionRuntimeLifecyclePolicyCache(fixture.Layout),
            TimeProvider.System);

        await using var snapshot = await resolver.CaptureAsync();
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            snapshot.ResolveInstalledAsync(
                CreateRuntimePackageManifest(rid, ">=11.0.0 <12.0.0"),
                CancellationToken.None));

        Assert.StartsWith(ExecutionRuntimeErrorCodes.Unsupported, failure.Message, StringComparison.Ordinal);
        Assert.Contains($"sunder runtime ensure {SunderExecutionRuntimeFormat.DotNetRuntimeId} '>=11.0.0 <12.0.0'", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExecutionRuntimeLifecycleStates.Deprecated, ExecutionRuntimeErrorCodes.Deprecated)]
    [InlineData(ExecutionRuntimeLifecycleStates.Retired, ExecutionRuntimeErrorCodes.Retired)]
    [InlineData(ExecutionRuntimeLifecycleStates.Revoked, ExecutionRuntimeErrorCodes.Revoked)]
    public async Task LocalBinding_ReportsLifecycleBlock(
        string lifecycleState,
        string expectedErrorCode)
    {
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        using var fixture = await StoreFixture.CreateAsync(runtimeIdentifier: rid);
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var now = DateTimeOffset.Parse("2026-08-29T12:00:00Z");
        var cache = new ExecutionRuntimeLifecyclePolicyCache(fixture.Layout);
        await cache.ObserveAsync([CreatePolicyObservation(
            receipt,
            now,
            revision: 7,
            lifecycleState: lifecycleState,
            installBlockedAtUtc: now.AddMinutes(-1),
            activationBlockedAtUtc: now.AddMinutes(-1))]);
        var resolver = new LocalExecutionRuntimeBindingResolver(
            fixture.Store,
            cache,
            new MutableTimeProvider(now));

        await using var snapshot = await resolver.CaptureAsync();
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            snapshot.ResolveInstalledAsync(
                CreateRuntimePackageManifest(rid, ">=10.0.0 <11.0.0"),
                CancellationToken.None));

        Assert.StartsWith(expectedErrorCode, failure.Message, StringComparison.Ordinal);
        Assert.Contains("sunder runtime ensure", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalBinding_ReportsCorruptRuntimeWithExactRepairCommand()
    {
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        using var fixture = await StoreFixture.CreateAsync(runtimeIdentifier: rid);
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var now = DateTimeOffset.Parse("2026-08-29T12:00:00Z");
        var cache = new ExecutionRuntimeLifecyclePolicyCache(fixture.Layout);
        await cache.ObserveAsync([CreatePolicyObservation(receipt, now, revision: 7)]);
        File.Delete(fixture.Layout.GetHealthPath(receipt.ContentIdentity));
        var resolver = new LocalExecutionRuntimeBindingResolver(
            fixture.Store,
            cache,
            new MutableTimeProvider(now));

        await using var snapshot = await resolver.CaptureAsync();
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            snapshot.ResolveInstalledAsync(
                CreateRuntimePackageManifest(rid, ">=10.0.0 <11.0.0"),
                CancellationToken.None));

        Assert.StartsWith(ExecutionRuntimeErrorCodes.IntegrityFailed, failure.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"sunder runtime repair {receipt.Identity.RuntimeId} {receipt.Identity.Version} {receipt.Identity.PackagingRevision} {receipt.Identity.Rid} {receipt.Identity.ModuleAbi} --yes",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalBinding_SelectsHighestAuthorizedVersionThenPackagingRevision()
    {
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        using var fixture = await StoreFixture.CreateAsync(runtimeIdentifier: rid);
        var first = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var secondArtifact = await CreateArtifactAsync(
            fixture.RootPath,
            payloadByte: 0x43,
            runtimeVersion: "10.0.2",
            packagingRevision: 1,
            runtimeIdentifier: rid);
        var thirdArtifact = await CreateArtifactAsync(
            fixture.RootPath,
            payloadByte: 0x44,
            runtimeVersion: "10.0.2",
            packagingRevision: 2,
            runtimeIdentifier: rid);
        var second = (await fixture.Store.EnsureAsync(
            CreatePlan(secondArtifact),
            secondArtifact.ArchivePath)).Receipt;
        var third = (await fixture.Store.EnsureAsync(
            CreatePlan(thirdArtifact),
            thirdArtifact.ArchivePath)).Receipt;
        var now = DateTimeOffset.Parse("2026-08-29T12:00:00Z");
        var cache = new ExecutionRuntimeLifecyclePolicyCache(fixture.Layout);
        await cache.ObserveAsync([
            CreatePolicyObservation(first, now, revision: 9),
            CreatePolicyObservation(second, now, revision: 9),
            CreatePolicyObservation(third, now, revision: 9),
        ]);
        var resolver = new LocalExecutionRuntimeBindingResolver(
            fixture.Store,
            cache,
            new MutableTimeProvider(now));

        await using var snapshot = await resolver.CaptureAsync();
        var binding = Assert.Single(await snapshot.ResolveInstalledAsync(
            CreateRuntimePackageManifest(rid, ">=10.0.0 <11.0.0"),
            CancellationToken.None));

        Assert.Equal("10.0.2", binding.RuntimeVersion);
        Assert.Equal(2, binding.RuntimePackagingRevision);
        Assert.Equal(third.ArtifactSha256, binding.RuntimeArtifactSha256);
        Assert.Equal(9, binding.RuntimePolicyRevision);
        Assert.Equal(RuntimeExecutionRuntimeAcquisitionSources.Registry, binding.AcquisitionSource);
    }

    [Fact]
    public async Task LocalBinding_IgnoresNonCurrentRidTargets()
    {
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        var alternateRid = rid == "win-x64" ? "linux-x64" : "win-x64";
        using var fixture = await StoreFixture.CreateAsync(runtimeIdentifier: rid);
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var now = DateTimeOffset.Parse("2026-08-29T12:00:00Z");
        var cache = new ExecutionRuntimeLifecyclePolicyCache(fixture.Layout);
        await cache.ObserveAsync([CreatePolicyObservation(receipt, now, revision: 7)]);
        var resolver = new LocalExecutionRuntimeBindingResolver(
            fixture.Store,
            cache,
            new MutableTimeProvider(now));

        await using var snapshot = await resolver.CaptureAsync();
        var binding = Assert.Single(await snapshot.ResolveInstalledAsync(
            CreateRuntimePackageManifest(rid, "=10.0.1", alternateRid),
            CancellationToken.None));

        Assert.Equal(rid, binding.Rid);
    }

    [Fact]
    public async Task LocalPackageStage_PersistsExactBindingAndRetainsRootUntilCommit()
    {
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        using var fixture = await StoreFixture.CreateAsync(runtimeIdentifier: rid);
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var existingArtifact = await CreateArtifactAsync(
            fixture.RootPath,
            payloadByte: 0x43,
            runtimeVersion: "10.0.0",
            packagingRevision: 1,
            runtimeIdentifier: rid);
        var existingReceipt = (await fixture.Store.EnsureAsync(
            CreatePlan(existingArtifact),
            existingArtifact.ArchivePath)).Receipt;
        var now = DateTimeOffset.Parse("2026-08-29T12:00:00Z");
        var cache = new ExecutionRuntimeLifecyclePolicyCache(fixture.Layout);
        await cache.ObserveAsync([
            CreatePolicyObservation(receipt, now, revision: 7),
            CreatePolicyObservation(existingReceipt, now, revision: 7),
        ]);
        var resolver = new LocalExecutionRuntimeBindingResolver(
            fixture.Store,
            cache,
            new MutableTimeProvider(now));
        var packageSource = Path.Combine(fixture.RootPath, "local-package");
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            packageSource,
            "local.binding.package",
            "1.0.0",
            typeof(ExecutionRuntimeStoreTests).Assembly.Location,
            roles: [SunderPackageFormat.RuntimeHostRole],
            runtimeIdentifiers: [rid]);
        var archivePath = await CanonicalPackageTestBuilder.CreateArchiveAsync(packageSource);
        var existingPackageSource = Path.Combine(fixture.RootPath, "existing-package");
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            existingPackageSource,
            "existing.binding.package",
            "1.0.0",
            typeof(ExecutionRuntimeStoreTests).Assembly.Location,
            roles: [SunderPackageFormat.RuntimeHostRole],
            runtimeIdentifiers: [rid]);
        var existingArchivePath = await CanonicalPackageTestBuilder.CreateArchiveAsync(existingPackageSource);
        try
        {
            var paths = new RuntimePackagePaths(fixture.Layout.RuntimeStateRootPath);
            var installed = new InstalledPackageStore(paths);
            var coordinator = new PackageStoreCoordinator(
                paths,
                installed,
                new SunderPackageArchiveInstaller(paths));
            await coordinator.InitializeAsync();
            var existingInstall = await coordinator.ExecuteAsync([
                new PackageStoreMutation(
                    PackageStoreMutationKind.Install,
                    ArchiveFilePath: existingArchivePath,
                    ExecutionRuntimeBindings: [CreateInstalledBinding(existingReceipt)]),
            ]);
            Assert.True(
                existingInstall.Success,
                string.Join(Environment.NewLine, existingInstall.Errors.Prepend(existingInstall.Message)));
            var authority = await resolver.CaptureAsync();

            var preparation = await coordinator.PrepareStageAsync(
                [new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archivePath)],
                authority,
                CancellationToken.None);

            var stage = Assert.IsType<PackageStorePreparedStage>(preparation.Stage);
            var stagedPackage = Assert.Single(
                stage.ProspectivePackages,
                static package => package.PackageId == "local.binding.package");
            var stagedBinding = Assert.Single(stagedPackage.ExecutionRuntimeBindings);
            Assert.Equal(receipt.Identity.RuntimeId, stagedBinding.RuntimeId);
            Assert.Equal(receipt.Identity.Version, stagedBinding.RuntimeVersion);
            Assert.Equal(receipt.Identity.PackagingRevision, stagedBinding.RuntimePackagingRevision);
            Assert.Equal(receipt.Identity.Rid, stagedBinding.Rid);
            Assert.Equal(receipt.Identity.ModuleAbi, stagedBinding.ModuleAbi);
            var stagedAuthority = Assert.IsType<LocalExecutionRuntimePolicyAuthority>(
                stage.LocalRuntimePolicyAuthority);
            Assert.Equal(cache.CaptureSnapshot().Generation, stagedAuthority.Generation);
            Assert.Equal(receipt.Identity, Assert.Single(stagedAuthority.InstallationIdentities));
            Assert.Equal(
                new[] { existingReceipt.Identity.SingleFlightKey, receipt.Identity.SingleFlightKey }
                    .Order(StringComparer.Ordinal),
                stagedAuthority.ActivationIdentities
                    .Select(static identity => identity.SingleFlightKey)
                    .Order(StringComparer.Ordinal));
            using (var blocked = RuntimeFileLock.TryAcquireExclusive(
                       fixture.Layout.GarbageCollectionRetentionFencePath))
            {
                Assert.Null(blocked);
            }

            var commit = await coordinator.CommitStageAsync(stage.StageId);

            Assert.True(commit.Success, commit.Message);
            var durablePackage = Assert.Single(
                await installed.ListAsync(),
                static package => package.PackageId == "local.binding.package");
            Assert.Equal(stagedBinding, Assert.Single(durablePackage.ExecutionRuntimeBindings));
            using var available = RuntimeFileLock.TryAcquireExclusive(
                fixture.Layout.GarbageCollectionRetentionFencePath);
            Assert.NotNull(available);
        }
        finally
        {
            File.Delete(archivePath);
            File.Delete(existingArchivePath);
        }
    }

    [Fact]
    public async Task LocalBinding_AuthorizesLocalImportOnlyAfterMatchingRegistryPolicy()
    {
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        using var fixture = await StoreFixture.CreateAsync(runtimeIdentifier: rid);
        var localPlan = fixture.Plan with
        {
            AcquisitionSource = RuntimeExecutionRuntimeAcquisitionSources.LocalImport,
            RegistryOrigin = null,
        };
        var receipt = (await fixture.Store.ImportLocalArchiveAsync(localPlan, fixture.ArchivePath)).Receipt;
        var now = DateTimeOffset.Parse("2026-08-29T12:00:00Z");
        var cache = new ExecutionRuntimeLifecyclePolicyCache(fixture.Layout);
        var resolver = new LocalExecutionRuntimeBindingResolver(
            fixture.Store,
            cache,
            new MutableTimeProvider(now));
        var manifest = CreateRuntimePackageManifest(rid, "=10.0.1");

        await using (var beforePolicy = await resolver.CaptureAsync())
        {
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                beforePolicy.ResolveInstalledAsync(manifest, CancellationToken.None));
            Assert.StartsWith(ExecutionRuntimeErrorCodes.PolicyRequired, failure.Message, StringComparison.Ordinal);
        }

        await cache.ObserveAsync([CreatePolicyObservation(receipt, now, revision: 7)]);
        await using var authorized = await resolver.CaptureAsync();
        var binding = Assert.Single(await authorized.ResolveInstalledAsync(manifest, CancellationToken.None));
        Assert.Equal(RuntimeExecutionRuntimeAcquisitionSources.LocalImport, binding.AcquisitionSource);
        Assert.Equal("https://registry.example/", binding.RegistryOrigin);
    }

    [Fact]
    public async Task EphemeralDevelopmentReceipt_RequiresExplicitPolicyAuthority()
    {
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        using var fixture = await StoreFixture.CreateAsync(
            runtimeIdentifier: rid,
            developmentTrustEnabled: true);
        await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var cache = new ExecutionRuntimeLifecyclePolicyCache(fixture.Layout);
        var resolver = new LocalExecutionRuntimeBindingResolver(
            fixture.Store,
            cache,
            TimeProvider.System);
        var target = Assert.Single(CreateRuntimePackageManifest(rid, "=10.0.1").Targets!)!;
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            resolver.ResolveEphemeralBindingAsync(
                new SunderPackageTargetKey(SunderPackageFormat.RuntimeHostRole, rid),
                target,
                CancellationToken.None));

        Assert.StartsWith(ExecutionRuntimeErrorCodes.PolicyRequired, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EphemeralDevelopmentBinding_SelectsOnlyAuthoritativelyAssociatedDevelopmentReceipt()
    {
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        using var fixture = await StoreFixture.CreateAsync(
            runtimeIdentifier: rid,
            developmentTrustEnabled: true);
        var receipt = (await fixture.Store.EnsureAsync(
            fixture.Plan with { RegistryOrigin = "https://registry.example/" },
            fixture.ArchivePath)).Receipt;
        var now = DateTimeOffset.Parse("2026-08-29T12:00:00Z");
        var cache = new ExecutionRuntimeLifecyclePolicyCache(fixture.Layout);
        await cache.ObserveAsync([CreatePolicyObservation(receipt, now, revision: 7)]);
        var resolver = new LocalExecutionRuntimeBindingResolver(
            fixture.Store,
            cache,
            new MutableTimeProvider(now));
        var manifest = CreateRuntimePackageManifest(rid, "=10.0.1");

        await using (var installedSnapshot = await resolver.CaptureAsync())
        {
            var installedFailure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                installedSnapshot.ResolveInstalledAsync(manifest, CancellationToken.None));
            Assert.StartsWith(
                ExecutionRuntimeErrorCodes.PolicyRequired,
                installedFailure.Message,
                StringComparison.Ordinal);
        }

        var target = Assert.Single(manifest.Targets!)!;
        await using var resolved = await resolver.ResolveEphemeralBindingAsync(
            new SunderPackageTargetKey(SunderPackageFormat.RuntimeHostRole, rid),
            target,
            CancellationToken.None);

        Assert.Equal(RuntimeExecutionRuntimeAcquisitionSources.Development, resolved.Binding.AcquisitionSource);
        Assert.Equal("https://registry.example/", resolved.Binding.RegistryOrigin);
        Assert.Equal(receipt.SigningKeyId, resolved.Binding.SigningKeyId);
    }

    [Fact]
    public async Task EphemeralPolicyAuthorizedBinding_RetainsRootUntilActivationOwnershipTransfers()
    {
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        using var fixture = await StoreFixture.CreateAsync(runtimeIdentifier: rid);
        var receipt = (await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)).Receipt;
        var now = DateTimeOffset.Parse("2026-08-29T12:00:00Z");
        var cache = new ExecutionRuntimeLifecyclePolicyCache(fixture.Layout);
        await cache.ObserveAsync([CreatePolicyObservation(receipt, now, revision: 7)]);
        var resolver = new LocalExecutionRuntimeBindingResolver(
            fixture.Store,
            cache,
            new MutableTimeProvider(now));
        var target = Assert.Single(CreateRuntimePackageManifest(rid, "=10.0.1").Targets!)!;
        var resolved = await resolver.ResolveEphemeralBindingAsync(
            new SunderPackageTargetKey(SunderPackageFormat.RuntimeHostRole, rid),
            target,
            CancellationToken.None);

        using (var blocked = RuntimeFileLock.TryAcquireExclusive(
                   fixture.Layout.GarbageCollectionRetentionFencePath))
        {
            Assert.Null(blocked);
        }

        await resolved.DisposeAsync();
        using var available = RuntimeFileLock.TryAcquireExclusive(
            fixture.Layout.GarbageCollectionRetentionFencePath);
        Assert.NotNull(available);
    }

    [Fact]
    public async Task Ensure_ExactInstallPublishesContentAddressesAndWarmReuseDoesNotReadSource()
    {
        using var fixture = await StoreFixture.CreateAsync();

        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var receiptBytes = await File.ReadAllBytesAsync(
            fixture.Layout.GetReceiptPath(fixture.Plan.Identity));
        File.Delete(fixture.ArchivePath);
        var reused = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);

        Assert.False(installed.AlreadyPresent);
        Assert.True(reused.AlreadyPresent);
        Assert.Equal(installed.Receipt.ContentIdentity, reused.Receipt.ContentIdentity);
        Assert.True(File.Exists(fixture.Layout.GetArchivePath(fixture.Plan.ArtifactSha256)));
        Assert.True(File.Exists(fixture.Layout.GetManifestPath(fixture.Plan.ManifestSha256)));
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
        Assert.Equal(
            receiptBytes,
            await File.ReadAllBytesAsync(fixture.Layout.GetReceiptPath(fixture.Plan.Identity)));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.StagingRootPath));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.TransactionRootPath));
    }

    [Fact]
    public async Task Ensure_PublishedRootRejectsOrdinaryWritesAndPreservesLauncherMode()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var rootPath = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        var launcherPath = GetLauncherPath(rootPath, installed.Receipt.LauncherPath);

        AssertRuntimeRootSealed(rootPath, installed.Receipt);
        Assert.Throws<UnauthorizedAccessException>(() =>
        {
            using var stream = new FileStream(
                launcherPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.None);
        });
        Assert.Throws<UnauthorizedAccessException>(() =>
            File.WriteAllText(Path.Combine(rootPath, "unexpected.txt"), "mutation"));
    }

    [Fact]
    public async Task Verify_WritablePermissionDriftFailsWarmAndDeepVerification()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var rootPath = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        ExecutionRuntimeRootFileSystem.MakeOwnedTreeWritable(rootPath);

        var warm = await fixture.Store.VerifyAsync(installed.Receipt.Identity, deep: false);
        var deep = await fixture.Store.VerifyAsync(installed.Receipt.Identity, deep: true);

        Assert.False(warm.Healthy);
        Assert.False(warm.DeepVerified);
        Assert.Contains("permission drift", warm.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(deep.Healthy);
        Assert.True(deep.DeepVerified);
        Assert.Contains("permission drift", deep.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ensure_HealthyWarmReuseIgnoresExistingInvalidSourceArchive()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        await File.WriteAllBytesAsync(fixture.ArchivePath, [0x00]);

        var reused = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);

        Assert.True(reused.AlreadyPresent);
        Assert.Equal(installed.Receipt.ContentIdentity, reused.Receipt.ContentIdentity);
    }

    [Fact]
    public async Task Ensure_MissingMarkerDeepRepairsExistingRootWithoutReadingInvalidSource()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        File.Delete(fixture.Layout.GetHealthPath(installed.Receipt.ContentIdentity));
        await File.WriteAllBytesAsync(fixture.ArchivePath, [0x00]);

        var reused = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);

        Assert.True(reused.AlreadyPresent);
        Assert.Equal(installed.Receipt.ContentIdentity, reused.Receipt.ContentIdentity);
        Assert.True(File.Exists(fixture.Layout.GetHealthPath(installed.Receipt.ContentIdentity)));
    }

    [Fact]
    public async Task Ensure_IndependentStoreInstancesSerializeExactIdentityBeforeValidation()
    {
        using var leaderInjector = new BlockingFaultInjector(ExecutionRuntimeStoreFaultPoint.ArchiveSourceOpening);
        using var fixture = await StoreFixture.CreateAsync(leaderInjector);
        var followerEnteredValidation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followerVerifier = new CountingSignatureVerifier();
        var follower = fixture.CreateStore(
            signatureVerifier: followerVerifier,
            faultInjector: new CallbackFaultInjector(
                ExecutionRuntimeStoreFaultPoint.ArchiveSourceOpening,
                followerEnteredValidation.SetResult));
        var leaderTask = fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        try
        {
            Assert.True(leaderInjector.WaitUntilBlocked(TimeSpan.FromSeconds(10)));
            var followerTask = follower.EnsureAsync(fixture.Plan, fixture.ArchivePath);

            var early = await Task.WhenAny(
                followerEnteredValidation.Task,
                Task.Delay(TimeSpan.FromMilliseconds(250)));
            Assert.NotSame(followerEnteredValidation.Task, early);
            Assert.False(followerTask.IsCompleted);

            leaderInjector.Release();
            var results = await Task.WhenAll(leaderTask, followerTask).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Single(results, result => !result.AlreadyPresent);
            Assert.Single(results, result => result.AlreadyPresent);
            Assert.Equal(0, followerVerifier.Count);
            Assert.False(followerEnteredValidation.Task.IsCompleted);
        }
        finally
        {
            leaderInjector.Release();
        }
    }

    [Fact]
    public async Task Ensure_CancellationWhileWaitingForIdentityLockPublishesNothing()
    {
        using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.InitializeAsync();
        using var identityLock = RuntimeFileLock.AcquireExclusive(
            fixture.Layout.GetSingleFlightLockPath(fixture.Plan.Identity));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath, cancellation.Token));

        await WaitUntilAsync(() => !Directory.EnumerateDirectories(fixture.Layout.StagingRootPath).Any());
        Assert.False(File.Exists(fixture.Layout.GetReceiptPath(fixture.Plan.Identity)));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.TransactionRootPath));
    }

    [Fact]
    public async Task GetReceipt_CancellationWhileWaitingForCrossProcessStoreFenceMutatesNothing()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        using var storeFence = RuntimeFileLock.AcquireExclusive(fixture.Layout.StoreFencePath);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Store.GetReceiptAsync(fixture.Plan.Identity, cancellation.Token));

        Assert.True(File.Exists(fixture.Layout.GetReceiptPath(fixture.Plan.Identity)));
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
    }

    [Theory]
    [InlineData("exclusive")]
    [InlineData("shared")]
    public async Task RuntimeFileLock_CoordinatesAcrossProcessesAndReleasesOnExit(string mode)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "SunderRuntimeFileLockTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Process? holder = null;
        try
        {
            var lockPath = Path.Combine(root, "cross-process.lock");
            var startInfo = TestWorkerFixture.CreateProcessStartInfo(root);
            startInfo.ArgumentList.Add("--sunder-test-hold-file-lock");
            startInfo.ArgumentList.Add(mode);
            startInfo.ArgumentList.Add(lockPath);
            holder = Process.Start(startInfo)
                     ?? throw new InvalidOperationException("The file-lock test process did not start.");
            Assert.Equal("ready", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));

            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                {
                    using var blocked = await RuntimeFileLock.AcquireExclusiveAsync(
                        lockPath,
                        cancellation.Token);
                });
            }

            await holder.StandardInput.WriteLineAsync("release");
            await holder.StandardInput.FlushAsync();
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, holder.ExitCode);
            using var acquired = RuntimeFileLock.TryAcquireExclusive(lockPath);
            Assert.NotNull(acquired);
        }
        finally
        {
            if (holder is { HasExited: false })
            {
                holder.Kill(entireProcessTree: true);
                await holder.WaitForExitAsync();
            }
            holder?.Dispose();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task GetReceipt_ValidatesDurableStateAndCarriesLauncherArguments()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);

        Assert.Equal(
            [SunderExecutionRuntimeFormat.DotNetExecArgument, SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder],
            installed.Receipt.LauncherArguments);

        var receiptPath = fixture.Layout.GetReceiptPath(fixture.Plan.Identity);
        var receiptJson = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!.AsObject();
        receiptJson["artifactSize"] = 0;
        await File.WriteAllTextAsync(receiptPath, receiptJson.ToJsonString(ExecutionRuntimeStore.JsonOptions));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.GetReceiptAsync(fixture.Plan.Identity));

        receiptJson["artifactSize"] = installed.Receipt.ArtifactSize;
        receiptJson["launcherArguments"]!.AsArray()[1] = "--inspect";
        await File.WriteAllTextAsync(receiptPath, receiptJson.ToJsonString(ExecutionRuntimeStore.JsonOptions));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.GetReceiptAsync(fixture.Plan.Identity));
    }

    [Fact]
    public async Task Verify_DeepTelemetryByteAccountingCannotOverflowOnCorruptReceipt()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var receiptPath = fixture.Layout.GetReceiptPath(fixture.Plan.Identity);
        var receiptJson = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!.AsObject();
        var nonLauncherContent = receiptJson["content"]!.AsArray()
            .Select(static item => item!.AsObject())
            .First(item => !string.Equals(
                (string?)item["path"],
                installed.Receipt.LauncherPath,
                StringComparison.Ordinal));
        nonLauncherContent["size"] = long.MaxValue;
        await File.WriteAllTextAsync(receiptPath, receiptJson.ToJsonString(ExecutionRuntimeStore.JsonOptions));

        var verification = await fixture.Store.VerifyAsync(fixture.Plan.Identity, deep: true);

        Assert.False(verification.Healthy);
        Assert.True(verification.DeepVerified);
    }

    [Fact]
    public async Task Ensure_ArchivePlanMismatchPublishesNothing()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var mismatch = fixture.Plan with
        {
            Identity = fixture.Plan.Identity with { Version = "10.0.2" },
        };

        await Assert.ThrowsAsync<ExecutionRuntimePlanMismatchException>(
            () => fixture.Store.EnsureAsync(mismatch, fixture.ArchivePath));

        Assert.False(File.Exists(fixture.Layout.GetReceiptPath(mismatch.Identity)));
        Assert.False(File.Exists(fixture.Layout.GetArchivePath(mismatch.ArtifactSha256)));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.StagingRootPath));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.TransactionRootPath));
    }

    [Fact]
    public async Task Ensure_CorruptWarmRootIsQuarantinedAndRebuiltFromExactArchive()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var first = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var launcherPath = Path.Combine(
            [fixture.Layout.GetContentRootPath(first.Receipt.ContentIdentity), .. first.Receipt.LauncherPath.Split('/')]);
        ExecutionRuntimeRootFileSystem.MakeOwnedTreeWritable(
            fixture.Layout.GetContentRootPath(first.Receipt.ContentIdentity));
        await File.WriteAllBytesAsync(launcherPath, new byte[first.Receipt.LauncherSize]);
        ExecutionRuntimeRootFileSystem.Seal(
            fixture.Layout.GetContentRootPath(first.Receipt.ContentIdentity),
            launcherPath);

        var repaired = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var verification = await fixture.Store.VerifyAsync(fixture.Plan.Identity, deep: true);

        Assert.False(repaired.AlreadyPresent);
        Assert.True(verification.Healthy, verification.Message);
        Assert.Contains(
            Directory.EnumerateFileSystemEntries(fixture.Layout.QuarantineRootPath),
            path => Path.GetFileName(path).Contains("unhealthy-root", StringComparison.Ordinal));
        Assert.Equal(first.Receipt.LauncherSha256, await HashFileAsync(launcherPath));
    }

    [Fact]
    public async Task Repair_CorruptRootReusesImmutableCachedArchive()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var first = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var launcherPath = GetLauncherPath(
            fixture.Layout.GetContentRootPath(first.Receipt.ContentIdentity),
            first.Receipt.LauncherPath);
        File.Delete(fixture.ArchivePath);
        ExecutionRuntimeRootFileSystem.MakeOwnedTreeWritable(
            fixture.Layout.GetContentRootPath(first.Receipt.ContentIdentity));
        await File.WriteAllBytesAsync(launcherPath, new byte[first.Receipt.LauncherSize]);
        ExecutionRuntimeRootFileSystem.Seal(
            fixture.Layout.GetContentRootPath(first.Receipt.ContentIdentity),
            launcherPath);

        var repaired = await fixture.Store.RepairAsync(first.Receipt.Identity);
        var verification = await fixture.Store.VerifyAsync(first.Receipt.Identity, deep: true);

        Assert.NotNull(repaired);
        Assert.False(repaired.AlreadyPresent);
        Assert.True(verification.Healthy, verification.Message);
        Assert.False(File.Exists(fixture.ArchivePath));
        Assert.Equal(first.Receipt.LauncherSha256, await HashFileAsync(launcherPath));
        Assert.Contains(
            Directory.EnumerateFileSystemEntries(fixture.Layout.QuarantineRootPath),
            path => Path.GetFileName(path).Contains("unhealthy-root", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Repair_WritablePermissionDriftReinstallsAndResealsRoot()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var rootPath = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        ExecutionRuntimeRootFileSystem.MakeOwnedTreeWritable(rootPath);

        var repaired = await fixture.Store.RepairAsync(installed.Receipt.Identity);

        Assert.NotNull(repaired);
        Assert.False(repaired.AlreadyPresent);
        AssertRuntimeRootSealed(rootPath, repaired.Receipt);
        var verification = await fixture.Store.VerifyAsync(installed.Receipt.Identity, deep: true);
        Assert.True(verification.Healthy, verification.Message);
    }

    [Fact]
    public async Task Ensure_CancellationAfterValidationLeavesNoPublicationOrJournal()
    {
        using var cancellation = new CancellationTokenSource();
        var injector = new CallbackFaultInjector(
            ExecutionRuntimeStoreFaultPoint.Validated,
            cancellation.Cancel);
        using var fixture = await StoreFixture.CreateAsync(injector);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath, cancellation.Token));

        await WaitUntilAsync(() =>
            !File.Exists(fixture.Layout.GetReceiptPath(fixture.Plan.Identity))
            && !File.Exists(fixture.Layout.GetArchivePath(fixture.Plan.ArtifactSha256))
            && !Directory.EnumerateDirectories(fixture.Layout.StagingRootPath).Any()
            && !Directory.EnumerateDirectories(fixture.Layout.TransactionRootPath).Any());

        Assert.False(File.Exists(fixture.Layout.GetReceiptPath(fixture.Plan.Identity)));
        Assert.False(File.Exists(fixture.Layout.GetArchivePath(fixture.Plan.ArtifactSha256)));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.StagingRootPath));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.TransactionRootPath));
    }

    [Fact]
    public async Task Ensure_CorruptContentAddressConflictIsQuarantinedBeforeAtomicPublication()
    {
        using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.InitializeAsync();
        var archiveObjectPath = fixture.Layout.GetArchivePath(fixture.Plan.ArtifactSha256);
        Directory.CreateDirectory(Path.GetDirectoryName(archiveObjectPath)!);
        await File.WriteAllBytesAsync(archiveObjectPath, [0x00, 0x01]);

        var result = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);

        Assert.False(result.AlreadyPresent);
        Assert.Equal(fixture.Plan.ArtifactSha256, await HashFileAsync(archiveObjectPath));
        Assert.Contains(
            Directory.EnumerateFileSystemEntries(fixture.Layout.QuarantineRootPath),
            path => Path.GetFileName(path).Contains("archive-conflict", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ensure_ExactIdentityConflictDoesNotReplaceImmutableReceipt()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var original = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var conflictingArtifact = await CreateArtifactAsync(fixture.RootPath, payloadByte: 0xA5);
        var conflictingPlan = CreatePlan(conflictingArtifact);

        await Assert.ThrowsAsync<ExecutionRuntimeStoreConflictException>(
            () => fixture.Store.EnsureAsync(conflictingPlan, conflictingArtifact.ArchivePath));

        var retained = await fixture.Store.GetReceiptAsync(fixture.Plan.Identity);
        Assert.NotNull(retained);
        Assert.Equal(original.Receipt.ArtifactSha256, retained.ArtifactSha256);
    }

    [Theory]
    [InlineData((int)ExecutionRuntimeStoreFaultPoint.ArchiveSourceOpening, false)]
    [InlineData((int)ExecutionRuntimeStoreFaultPoint.Validated, false)]
    [InlineData((int)ExecutionRuntimeStoreFaultPoint.JournalPrepared, false)]
    [InlineData((int)ExecutionRuntimeStoreFaultPoint.ArchivePublished, false)]
    [InlineData((int)ExecutionRuntimeStoreFaultPoint.ManifestPublished, false)]
    [InlineData((int)ExecutionRuntimeStoreFaultPoint.RootSealed, false)]
    [InlineData((int)ExecutionRuntimeStoreFaultPoint.RootMoved, true)]
    [InlineData((int)ExecutionRuntimeStoreFaultPoint.RootPublished, true)]
    [InlineData((int)ExecutionRuntimeStoreFaultPoint.ReceiptPublished, true)]
    [InlineData((int)ExecutionRuntimeStoreFaultPoint.HealthPublished, true)]
    [InlineData((int)ExecutionRuntimeStoreFaultPoint.Cleanup, true)]
    public async Task Initialize_RecoversOrSafelyCollectsEveryPublicationBoundary(
        int faultPointValue,
        bool expectCommittedRuntime)
    {
        var faultPoint = (ExecutionRuntimeStoreFaultPoint)faultPointValue;
        var injector = new ThrowingFaultInjector(faultPoint);
        using var fixture = await StoreFixture.CreateAsync(injector);

        await Assert.ThrowsAsync<ExecutionRuntimeStoreSimulatedCrashException>(
            () => fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath));

        var transactionPath = Assert.Single(Directory.EnumerateDirectories(
            fixture.Layout.TransactionRootPath));
        ExecutionRuntimeStoreTransactionJournal? journal = null;
        if (faultPointValue >= (int)ExecutionRuntimeStoreFaultPoint.JournalPrepared)
        {
            journal = JsonSerializer.Deserialize<ExecutionRuntimeStoreTransactionJournal>(
                await File.ReadAllTextAsync(Path.Combine(transactionPath, "journal.json")),
                ExecutionRuntimeStore.JsonOptions);
            Assert.NotNull(journal);
        }
        if (faultPointValue >= (int)ExecutionRuntimeStoreFaultPoint.ArchivePublished)
        {
            Assert.True(File.Exists(fixture.Layout.GetArchivePath(fixture.Plan.ArtifactSha256)));
        }
        if (faultPointValue >= (int)ExecutionRuntimeStoreFaultPoint.ManifestPublished)
        {
            Assert.True(File.Exists(fixture.Layout.GetManifestPath(fixture.Plan.ManifestSha256)));
        }
        if (faultPoint == ExecutionRuntimeStoreFaultPoint.RootSealed)
        {
            Assert.Equal(ExecutionRuntimeStoreTransactionPhase.RootSealed, journal!.Phase);
            var stagingPath = Assert.Single(Directory.EnumerateDirectories(fixture.Layout.StagingRootPath));
            var stagedRootPath = Path.Combine(stagingPath, "root");
            ExecutionRuntimeRootFileSystem.VerifyPreparedForPublication(
                stagedRootPath,
                GetLauncherPath(stagedRootPath, journal.Receipt.LauncherPath));
        }
        if (faultPoint == ExecutionRuntimeStoreFaultPoint.RootMoved)
        {
            Assert.Equal(ExecutionRuntimeStoreTransactionPhase.RootSealed, journal!.Phase);
            var movedRootPath = fixture.Layout.GetContentRootPath(journal.Receipt.ContentIdentity);
            Assert.Throws<InvalidDataException>(() => ExecutionRuntimeRootFileSystem.VerifySealed(
                movedRootPath,
                GetLauncherPath(movedRootPath, journal.Receipt.LauncherPath)));
        }
        if (faultPoint == ExecutionRuntimeStoreFaultPoint.RootMoved
            || faultPointValue >= (int)ExecutionRuntimeStoreFaultPoint.RootPublished)
        {
            Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(journal!.Receipt.ContentIdentity)));
        }
        if (faultPointValue >= (int)ExecutionRuntimeStoreFaultPoint.ReceiptPublished)
        {
            Assert.True(File.Exists(fixture.Layout.GetReceiptPath(fixture.Plan.Identity)));
        }
        if (faultPointValue >= (int)ExecutionRuntimeStoreFaultPoint.HealthPublished)
        {
            Assert.True(File.Exists(fixture.Layout.GetHealthPath(journal!.Receipt.ContentIdentity)));
        }

        var recoveredStore = fixture.CreateStore();
        await recoveredStore.InitializeAsync();
        var receipt = await recoveredStore.GetReceiptAsync(fixture.Plan.Identity);

        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.TransactionRootPath));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.StagingRootPath));
        if (expectCommittedRuntime)
        {
            Assert.NotNull(receipt);
            Assert.Equal(fixture.Plan.ArtifactSha256, receipt.ArtifactSha256);
            Assert.True((await recoveredStore.VerifyAsync(fixture.Plan.Identity, deep: true)).Healthy);
            return;
        }

        Assert.Null(receipt);
        await recoveredStore.CollectGarbageAsync(TimeSpan.Zero);
        Assert.False(File.Exists(fixture.Layout.GetArchivePath(fixture.Plan.ArtifactSha256)));
        Assert.False(File.Exists(fixture.Layout.GetManifestPath(fixture.Plan.ManifestSha256)));
    }

    [Fact]
    public async Task Ensure_FailedTransactionRecoveryDoesNotDeadlockColdInitializer()
    {
        using var injector = new BlockingThrowingFaultInjector(ExecutionRuntimeStoreFaultPoint.JournalPrepared);
        using var fixture = await StoreFixture.CreateAsync(injector);
        var leaderTask = fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        Assert.True(injector.WaitUntilBlocked(TimeSpan.FromSeconds(5)));
        var follower = fixture.CreateStore();
        var followerInitialization = follower.InitializeAsync();
        await Task.Delay(100);
        Assert.False(followerInitialization.IsCompleted);

        injector.Release();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => leaderTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await followerInitialization.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.TransactionRootPath));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.StagingRootPath));
    }

    [Fact]
    public async Task Initialize_QuarantinesUnownedAndAmbiguousPartialState()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var ambiguousTransactionId = Guid.NewGuid().ToString("N");
        var ambiguousTransactionPath = fixture.Layout.GetTransactionPath(ambiguousTransactionId);
        var ambiguousStagingPath = fixture.Layout.GetStagingPath(ambiguousTransactionId);
        var unownedStagingPath = fixture.Layout.GetStagingPath(Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ambiguousTransactionPath);
        Directory.CreateDirectory(ambiguousStagingPath);
        Directory.CreateDirectory(unownedStagingPath);
        await File.WriteAllTextAsync(Path.Combine(ambiguousTransactionPath, "partial"), "transaction");
        await File.WriteAllTextAsync(Path.Combine(ambiguousStagingPath, "partial"), "staging");
        await File.WriteAllTextAsync(Path.Combine(unownedStagingPath, "partial"), "unowned");

        await fixture.Store.InitializeAsync();

        Assert.False(Directory.Exists(ambiguousTransactionPath));
        Assert.False(Directory.Exists(ambiguousStagingPath));
        Assert.False(Directory.Exists(unownedStagingPath));
        var quarantined = Directory.EnumerateDirectories(fixture.Layout.QuarantineRootPath)
            .Select(Path.GetFileName)
            .ToArray();
        Assert.Contains(quarantined, name => name!.Contains("ambiguous-transaction", StringComparison.Ordinal));
        Assert.Contains(quarantined, name => name!.Contains("ambiguous-staging", StringComparison.Ordinal));
        Assert.Contains(quarantined, name => name!.Contains("unowned-staging", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Initialize_QuarantinesOversizedTransactionJournalWithoutReadingIt()
    {
        using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.InitializeAsync();
        var transactionId = Guid.NewGuid().ToString("N");
        var transactionPath = fixture.Layout.GetTransactionPath(transactionId);
        var stagingPath = fixture.Layout.GetStagingPath(transactionId);
        Directory.CreateDirectory(transactionPath);
        Directory.CreateDirectory(stagingPath);
        await using (var stream = new FileStream(
                         fixture.Layout.GetTransactionJournalPath(transactionId),
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None))
        {
            stream.SetLength(SunderExecutionRuntimeFormat.MaxMetadataBytes + 1L);
        }

        var recovered = fixture.CreateStore();
        await recovered.InitializeAsync();

        Assert.False(Directory.Exists(transactionPath));
        Assert.False(Directory.Exists(stagingPath));
        var quarantined = Directory.EnumerateDirectories(fixture.Layout.QuarantineRootPath)
            .Select(Path.GetFileName)
            .ToArray();
        Assert.Contains(quarantined, name => name!.Contains("invalid-transaction", StringComparison.Ordinal));
        Assert.Contains(quarantined, name => name!.Contains("ambiguous-staging", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GarbageCollection_EnumerationFailureDoesNotReportFalseSuccess()
    {
        using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.InitializeAsync();
        Directory.Delete(fixture.Layout.ReceiptRootPath);
        await File.WriteAllTextAsync(fixture.Layout.ReceiptRootPath, "not-a-directory");

        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Store.CollectGarbageAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task GarbageCollection_QuarantinesUnownedMalformedReceiptAndContinues()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var receiptPath = fixture.Layout.GetReceiptPath(installed.Receipt.Identity);
        await File.WriteAllTextAsync(receiptPath, "{");

        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);

        Assert.False(File.Exists(receiptPath));
        Assert.False(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
        var quarantinedReceipt = Assert.Single(
            Directory.EnumerateFiles(fixture.Layout.QuarantineRootPath),
            path => Path.GetFileName(path).Contains("invalid-receipt", StringComparison.Ordinal));
        Assert.Equal("{", await File.ReadAllTextAsync(quarantinedReceipt));
    }

    [Fact]
    public async Task GarbageCollection_AuthoritativeBindingWithoutExactReceiptFailsClosed()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        fixture.GarbageCollectionAuthority.SetBindings(
            CreateInstalledBinding(installed.Receipt) with { ModuleAbi = "net10.0-preview" });

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.CollectGarbageAsync(TimeSpan.Zero));

        Assert.True(File.Exists(fixture.Layout.GetReceiptPath(fixture.Plan.Identity)));
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
        Assert.True(File.Exists(fixture.Layout.GetArchivePath(installed.Receipt.ArtifactSha256)));
        Assert.True(File.Exists(fixture.Layout.GetManifestPath(installed.Receipt.ManifestSha256)));
    }

    [Fact]
    public async Task GarbageCollection_AuthoritativeMalformedReceiptFailsClosedWithoutMutation()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        fixture.GarbageCollectionAuthority.SetBindings(CreateInstalledBinding(installed.Receipt));
        var receiptPath = fixture.Layout.GetReceiptPath(installed.Receipt.Identity);
        await File.WriteAllTextAsync(receiptPath, "{");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.CollectGarbageAsync(TimeSpan.Zero));

        Assert.Equal("{", await File.ReadAllTextAsync(receiptPath));
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
        Assert.True(File.Exists(fixture.Layout.GetArchivePath(installed.Receipt.ArtifactSha256)));
        Assert.True(File.Exists(fixture.Layout.GetManifestPath(installed.Receipt.ManifestSha256)));
        Assert.True(File.Exists(fixture.Layout.GetHealthPath(installed.Receipt.ContentIdentity)));
        Assert.False(File.Exists(fixture.Layout.GetGarbageCollectionMarkPath(installed.Receipt.ContentIdentity)));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(fixture.Layout.QuarantineRootPath),
            path => Path.GetFileName(path).Contains("invalid-receipt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GarbageCollection_AuthoritativeBindingRetainsRootReceiptAndContentAddresses()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        fixture.GarbageCollectionAuthority.SetBindings(CreateInstalledBinding(installed.Receipt));
        fixture.GarbageCollectionAuthority.SetLifecycle(
            ExecutionRuntimeLifecycleStates.Deprecated,
            installed.Receipt);

        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);

        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
        Assert.True(File.Exists(fixture.Layout.GetReceiptPath(installed.Receipt.Identity)));
        Assert.True(File.Exists(fixture.Layout.GetArchivePath(installed.Receipt.ArtifactSha256)));
        Assert.True(File.Exists(fixture.Layout.GetManifestPath(installed.Receipt.ManifestSha256)));
    }

    [Fact]
    public async Task GarbageCollection_UnreferencedRuntimeDoesNotRequireLifecyclePolicy()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        AssertRuntimeRootSealed(
            fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity),
            installed.Receipt);

        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        var result = await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);

        Assert.Equal(new ExecutionRuntimeGarbageCollectionResult(1, 1, 1), result);
        Assert.False(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
        Assert.False(File.Exists(fixture.Layout.GetReceiptPath(installed.Receipt.Identity)));
        Assert.False(File.Exists(fixture.Layout.GetArchivePath(installed.Receipt.ArtifactSha256)));
        Assert.False(File.Exists(fixture.Layout.GetManifestPath(installed.Receipt.ManifestSha256)));
    }

    [Fact]
    public async Task GarbageCollection_LeasedMalformedReceiptFailsClosedWithoutMutation()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        await using var lease = await ReserveAsync(fixture, installed.Receipt, "malformed.receipt");
        var receiptPath = fixture.Layout.GetReceiptPath(installed.Receipt.Identity);
        await File.WriteAllTextAsync(receiptPath, "{");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.CollectGarbageAsync(TimeSpan.Zero));

        Assert.Equal("{", await File.ReadAllTextAsync(receiptPath));
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
        Assert.True(File.Exists(fixture.Layout.GetArchivePath(installed.Receipt.ArtifactSha256)));
        Assert.True(File.Exists(fixture.Layout.GetManifestPath(installed.Receipt.ManifestSha256)));
        Assert.True(File.Exists(fixture.Layout.GetHealthPath(installed.Receipt.ContentIdentity)));
        Assert.False(File.Exists(fixture.Layout.GetGarbageCollectionMarkPath(installed.Receipt.ContentIdentity)));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(fixture.Layout.QuarantineRootPath),
            path => Path.GetFileName(path).Contains("invalid-receipt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GarbageCollection_SupportedLifecycleDoesNotRetainUnreferencedRuntime()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        fixture.GarbageCollectionAuthority.SetLifecycle(
            ExecutionRuntimeLifecycleStates.Supported,
            installed.Receipt);

        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        var result = await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);

        Assert.Equal(new ExecutionRuntimeGarbageCollectionResult(1, 1, 1), result);
        Assert.False(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
        Assert.False(File.Exists(fixture.Layout.GetReceiptPath(installed.Receipt.Identity)));
        Assert.False(File.Exists(fixture.Layout.GetArchivePath(installed.Receipt.ArtifactSha256)));
        Assert.False(File.Exists(fixture.Layout.GetManifestPath(installed.Receipt.ManifestSha256)));
    }

    [Fact]
    public async Task GarbageCollection_CollectsReceiptlessRootAndContentAddressedObjects()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var rootPath = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        File.Delete(fixture.Layout.GetReceiptPath(installed.Receipt.Identity));

        var marked = await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        var removed = await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);

        Assert.Equal(0, marked.RootsRemoved);
        Assert.Equal(1, removed.RootsRemoved);
        Assert.False(Directory.Exists(rootPath));
        Assert.False(File.Exists(fixture.Layout.GetArchivePath(installed.Receipt.ArtifactSha256)));
        Assert.False(File.Exists(fixture.Layout.GetManifestPath(installed.Receipt.ManifestSha256)));
    }

    [Fact]
    public async Task GarbageCollection_DurablePolicyFinishesAssetsAfterReceiptWasRemoved()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        fixture.GarbageCollectionAuthority.SetLifecycle(
            ExecutionRuntimeLifecycleStates.Deprecated,
            installed.Receipt);
        File.Delete(fixture.Layout.GetReceiptPath(installed.Receipt.Identity));
        ExecutionRuntimeRootFileSystem.DeleteOwnedTree(
            fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity));
        var archivePath = fixture.Layout.GetArchivePath(installed.Receipt.ArtifactSha256);
        var manifestPath = fixture.Layout.GetManifestPath(installed.Receipt.ManifestSha256);
        File.SetLastWriteTimeUtc(archivePath, DateTime.UtcNow.AddMinutes(-1));
        File.SetLastWriteTimeUtc(manifestPath, DateTime.UtcNow.AddMinutes(-1));

        var result = await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);

        Assert.Equal(new ExecutionRuntimeGarbageCollectionResult(0, 1, 1), result);
        Assert.False(File.Exists(archivePath));
        Assert.False(File.Exists(manifestPath));
    }

    [Fact]
    public async Task GarbageCollection_AuthorityFailureMutatesNothing()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        fixture.GarbageCollectionAuthority.FailWith(new InvalidDataException("catalog unavailable"));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.CollectGarbageAsync(TimeSpan.Zero));

        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
        Assert.True(File.Exists(fixture.Layout.GetReceiptPath(installed.Receipt.Identity)));
        Assert.False(File.Exists(fixture.Layout.GetGarbageCollectionMarkPath(installed.Receipt.ContentIdentity)));
    }

    [Fact]
    public async Task GarbageCollection_TamperedAuthoritativeContentIdentityFailsBeforeSweep()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        fixture.GarbageCollectionAuthority.SetBindings(CreateInstalledBinding(installed.Receipt));
        var receiptPath = fixture.Layout.GetReceiptPath(installed.Receipt.Identity);
        var receiptJson = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!.AsObject();
        var tamperedContentIdentity = new string('f', 64);
        receiptJson["contentIdentity"] = tamperedContentIdentity;
        await File.WriteAllTextAsync(receiptPath, receiptJson.ToJsonString(ExecutionRuntimeStore.JsonOptions));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.CollectGarbageAsync(TimeSpan.Zero));

        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
        Assert.True(File.Exists(receiptPath));
        Assert.True(File.Exists(fixture.Layout.GetArchivePath(installed.Receipt.ArtifactSha256)));
        Assert.True(File.Exists(fixture.Layout.GetManifestPath(installed.Receipt.ManifestSha256)));
        Assert.False(File.Exists(fixture.Layout.GetGarbageCollectionMarkPath(installed.Receipt.ContentIdentity)));
        Assert.False(File.Exists(fixture.Layout.GetHealthPath(tamperedContentIdentity)));
    }

    [Theory]
    [InlineData("receipts")]
    [InlineData("archives")]
    [InlineData("manifests")]
    [InlineData("roots")]
    [InlineData("health")]
    [InlineData("policy")]
    [InlineData("marks")]
    [InlineData("tombstones")]
    public async Task GarbageCollection_RejectsLinkedStoreSubtreeWithoutTouchingTarget(string subtree)
    {
        using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.InitializeAsync();
        var externalPath = Path.Combine(fixture.RootPath, "external-" + subtree);
        Directory.CreateDirectory(externalPath);
        var temporaryMarkPath = Path.Combine(
            fixture.Layout.GarbageCollectionMarkRootPath,
            $"orphan.json.tmp-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(temporaryMarkPath, "temporary");
        var targetFileName = subtree == "archives"
            ? new string('a', 64) + ".sunderruntime"
            : "outside.json";
        var targetPath = Path.Combine(externalPath, targetFileName);
        await File.WriteAllTextAsync(targetPath, "outside");
        var storeRoot = subtree switch
        {
            "receipts" => fixture.Layout.ReceiptRootPath,
            "archives" => fixture.Layout.ArchiveRootPath,
            "manifests" => fixture.Layout.ManifestRootPath,
            "roots" => fixture.Layout.ContentRootPath,
            "health" => fixture.Layout.HealthRootPath,
            "policy" => fixture.Layout.PolicyRootPath,
            "marks" => fixture.Layout.GarbageCollectionMarkRootPath,
            "tombstones" => fixture.Layout.TombstoneRootPath,
            _ => throw new ArgumentOutOfRangeException(nameof(subtree)),
        };
        var linkPath = Path.Combine(storeRoot, "linked");
        try
        {
            Directory.CreateSymbolicLink(linkPath, externalPath);
        }
        catch (Exception exception) when (OperatingSystem.IsWindows()
                                          && exception is (IOException or UnauthorizedAccessException))
        {
            return;
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.CollectGarbageAsync(TimeSpan.Zero));

        Assert.True(File.Exists(targetPath));
        Assert.Equal("outside", await File.ReadAllTextAsync(targetPath));
        Assert.True(File.Exists(temporaryMarkPath));
    }

    [Fact]
    public async Task OwnedTreeDeletion_UnsealsEntriesWithoutFollowingLinkedChild()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var ownedPath = Path.Combine(fixture.RootPath, "owned-tree");
        var nestedPath = Path.Combine(ownedPath, "nested");
        var payloadPath = Path.Combine(nestedPath, "payload.bin");
        var externalPath = Path.Combine(fixture.RootPath, "external-target");
        var externalPayloadPath = Path.Combine(externalPath, "preserve.bin");
        Directory.CreateDirectory(nestedPath);
        Directory.CreateDirectory(externalPath);
        await File.WriteAllTextAsync(payloadPath, "owned");
        await File.WriteAllTextAsync(externalPayloadPath, "external");
        var linkPath = Path.Combine(ownedPath, "linked");
        try
        {
            Directory.CreateSymbolicLink(linkPath, externalPath);
        }
        catch (Exception exception) when (OperatingSystem.IsWindows()
                                          && exception is (IOException or UnauthorizedAccessException))
        {
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(payloadPath, File.GetAttributes(payloadPath) | FileAttributes.ReadOnly);
            File.SetAttributes(nestedPath, File.GetAttributes(nestedPath) | FileAttributes.ReadOnly);
            File.SetAttributes(ownedPath, File.GetAttributes(ownedPath) | FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(payloadPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            var directoryMode = UnixFileMode.UserRead | UnixFileMode.UserExecute
                                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(nestedPath, directoryMode);
            File.SetUnixFileMode(ownedPath, directoryMode);
        }

        ExecutionRuntimeRootFileSystem.DeleteOwnedTree(ownedPath);

        Assert.False(Directory.Exists(ownedPath));
        Assert.True(File.Exists(externalPayloadPath));
        Assert.Equal("external", await File.ReadAllTextAsync(externalPayloadPath));
    }

    [Fact]
    public async Task GarbageCollection_WaitsForRuntimeTransactionPublicationFence()
    {
        using var injector = new BlockingFaultInjector(ExecutionRuntimeStoreFaultPoint.JournalPrepared);
        using var fixture = await StoreFixture.CreateAsync(injector);
        var authorityEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.GarbageCollectionAuthority.Executing = authorityEntered.SetResult;
        var ensureTask = fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        try
        {
            Assert.True(injector.WaitUntilBlocked(TimeSpan.FromSeconds(10)));
            var collectionTask = fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
            await authorityEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(collectionTask.IsCompleted);
            injector.Release();
            var installed = await ensureTask.WaitAsync(TimeSpan.FromSeconds(10));
            await collectionTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
        }
        finally
        {
            injector.Release();
            fixture.GarbageCollectionAuthority.Executing = null;
        }
    }

    [Fact]
    public async Task GarbageCollection_TransientRetentionBridgesAuthorityHandoff()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        fixture.GarbageCollectionAuthority.SetLifecycle(
            ExecutionRuntimeLifecycleStates.Deprecated,
            installed.Receipt);
        var retention = await fixture.Store.RetainAsync(new ExecutionRuntimeRetention(
            installed.Receipt.Identity,
            installed.Receipt.ContentIdentity,
            installed.Receipt.ArtifactSha256,
            installed.Receipt.ArtifactSize,
            installed.Receipt.ManifestSha256));

        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));

        await retention.DisposeAsync();
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        Assert.False(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
    }

    [Fact]
    public async Task GarbageCollection_RetentionFromIndependentStoreDefersSweep()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        fixture.GarbageCollectionAuthority.SetLifecycle(
            ExecutionRuntimeLifecycleStates.Deprecated,
            installed.Receipt);
        var retention = await fixture.Store.RetainAsync(new ExecutionRuntimeRetention(
            installed.Receipt.Identity,
            installed.Receipt.ContentIdentity,
            installed.Receipt.ArtifactSha256,
            installed.Receipt.ArtifactSize,
            installed.Receipt.ManifestSha256));
        var collector = fixture.CreateStore();

        Assert.Equal(
            new ExecutionRuntimeGarbageCollectionResult(0, 0, 0),
            await collector.CollectGarbageAsync(TimeSpan.Zero));
        Assert.Equal(
            new ExecutionRuntimeGarbageCollectionResult(0, 0, 0),
            await collector.CollectGarbageAsync(TimeSpan.Zero));
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));

        await retention.DisposeAsync();
        await collector.CollectGarbageAsync(TimeSpan.Zero);
        await collector.CollectGarbageAsync(TimeSpan.Zero);
        Assert.False(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
    }

    [Fact]
    public async Task ProcessLease_ReservesDurablyAndReleasesBeforeContainedLaunch()
    {
        var hostInstanceId = Guid.NewGuid();
        using var fixture = await StoreFixture.CreateAsync(hostInstanceId: hostInstanceId);
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var runtimeRoot = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        var launcherPath = GetLauncherPath(runtimeRoot, installed.Receipt.LauncherPath);
        var activationId = Guid.NewGuid();
        var generationId = $"20260828120000-{Environment.ProcessId}-{Guid.NewGuid():N}";
        await using var lease = await fixture.Store.ReserveProcessLeaseAsync(
            new ExecutionRuntimeLeaseOwner("lease.test", generationId, activationId),
            installed.Receipt.ContentIdentity,
            runtimeRoot,
            launcherPath,
            CancellationToken.None);
        var leasePath = fixture.Layout.GetLeasePath(lease.LeaseId);
        var reserved = await ReadLeaseAsync(leasePath);
        Assert.Equal(hostInstanceId, reserved.HostInstanceId);
        Assert.Equal("lease.test", reserved.PackageId);
        Assert.Equal(generationId, reserved.GenerationId);
        Assert.Equal(activationId, reserved.ActivationId);
        Assert.False(reserved.IsAttached);
        Assert.False(reserved.HasContainment);
        Assert.Null(reserved.RuntimeRootPath);

        await lease.DisposeAsync();
        Assert.False(File.Exists(leasePath));
    }

    [Fact]
    public async Task Initialize_RecoversInterruptedAtomicLeaseAttachmentBeforeReapingOrphan()
    {
        var containment = new FakeContainmentController
        {
            ProbeResult = ExecutionRuntimeContainmentProbe.Active,
        };
        using var fixture = await StoreFixture.CreateAsync(
            hostInstanceId: Guid.NewGuid(),
            containmentController: containment);
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var runtimeRoot = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        var launcherPath = GetLauncherPath(runtimeRoot, installed.Receipt.LauncherPath);
        await using var lease = await fixture.Store.ReserveProcessLeaseAsync(
            new ExecutionRuntimeLeaseOwner(
                "interrupted.attachment",
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid()),
            installed.Receipt.ContentIdentity,
            runtimeRoot,
            launcherPath,
            CancellationToken.None);
        var leasePath = fixture.Layout.GetLeasePath(lease.LeaseId);
        var reservation = await ReadLeaseAsync(leasePath);
        var candidatePath = leasePath + ".tmp-" + Guid.NewGuid().ToString("N");
        await DurableJsonDocument.WriteAsync(
            candidatePath,
            CreateAttachedContainment(reservation, runtimeRoot, launcherPath, 151),
            ExecutionRuntimeStore.JsonOptions);

        var recovered = fixture.CreateStore(
            hostInstanceId: Guid.NewGuid(),
            containmentController: containment);
        await recovered.InitializeAsync();

        Assert.Equal(1, containment.TerminateCalls);
        Assert.False(File.Exists(leasePath));
        Assert.False(File.Exists(candidatePath));
    }

    [Fact]
    public async Task Initialize_QuarantinesMalformedLeaseAndFailsClosed()
    {
        using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.InitializeAsync();
        var leasePath = fixture.Layout.GetLeasePath(Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(leasePath, "{\"schemaVersion\":999}");

        var recovered = fixture.CreateStore(hostInstanceId: Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidDataException>(() => recovered.InitializeAsync());

        Assert.False(File.Exists(leasePath));
        Assert.True(File.Exists(fixture.Layout.LeaseRecoveryFailurePath));
        Assert.Contains(
            Directory.EnumerateFileSystemEntries(fixture.Layout.QuarantineRootPath),
            path => Path.GetFileName(path).Contains("invalid-lease", StringComparison.Ordinal));
        var restarted = fixture.CreateStore(hostInstanceId: Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.InitializeAsync());
    }

    [Fact]
    public async Task GarbageCollection_ActiveLeasePreventsZeroGraceSweep()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        fixture.GarbageCollectionAuthority.SetLifecycle(
            ExecutionRuntimeLifecycleStates.Deprecated,
            installed.Receipt);
        var rootPath = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        var lease = await ReserveAsync(fixture, installed.Receipt, "active.lease");

        var retained = await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);

        Assert.Equal(0, retained.RootsRemoved);
        Assert.True(Directory.Exists(rootPath));
        Assert.True(File.Exists(fixture.Layout.GetReceiptPath(fixture.Plan.Identity)));

        await lease.DisposeAsync();
        var firstUnreferenced = await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        var removed = await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        Assert.Equal(0, firstUnreferenced.RootsRemoved);
        Assert.Equal(1, removed.RootsRemoved);
        Assert.False(Directory.Exists(rootPath));
    }

    [Fact]
    public async Task GarbageCollection_PartialTombstoneMoveIsRestoredBeforeReturning()
    {
        ExecutionRuntimeStoreLayout? layout = null;
        var injector = new CallbackFaultInjector(
            ExecutionRuntimeStoreFaultPoint.TombstoneRootMoved,
            () =>
            {
                var tombstonePath = Directory.EnumerateDirectories(layout!.TombstoneRootPath)
                    .Single(path => !string.Equals(
                        Path.GetFullPath(path),
                        Path.GetFullPath(layout.GarbageCollectionMarkRootPath),
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal));
                File.WriteAllText(Path.Combine(tombstonePath, "health.json"), "collision");
            });
        using var fixture = await StoreFixture.CreateAsync(injector);
        layout = fixture.Layout;
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        fixture.GarbageCollectionAuthority.SetLifecycle(
            ExecutionRuntimeLifecycleStates.Deprecated,
            installed.Receipt);
        await File.WriteAllTextAsync(
            fixture.Layout.GetHealthPath(installed.Receipt.ContentIdentity),
            "live-health");
        Assert.True(File.Exists(fixture.Layout.GetHealthPath(installed.Receipt.ContentIdentity)));
        var marked = await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        Assert.Equal(new ExecutionRuntimeGarbageCollectionResult(0, 0, 0), marked);
        var result = await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);

        Assert.Equal(new ExecutionRuntimeGarbageCollectionResult(0, 0, 0), result);
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
        Assert.True(File.Exists(fixture.Layout.GetReceiptPath(installed.Receipt.Identity)));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(fixture.Layout.TombstoneRootPath),
            path => !string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(fixture.Layout.GarbageCollectionMarkRootPath),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal));
    }

    [Fact]
    public async Task Initialize_ResealsRootAfterCrashBeforeTombstoneMove()
    {
        var injector = new ThrowingFaultInjector(ExecutionRuntimeStoreFaultPoint.TombstoneRootMovable);
        using var fixture = await StoreFixture.CreateAsync(injector);
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var rootPath = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        fixture.GarbageCollectionAuthority.SetLifecycle(
            ExecutionRuntimeLifecycleStates.Deprecated,
            installed.Receipt);
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);

        await Assert.ThrowsAsync<ExecutionRuntimeStoreSimulatedCrashException>(
            () => fixture.Store.CollectGarbageAsync(TimeSpan.Zero));

        Assert.True(Directory.Exists(rootPath));
        Assert.Throws<InvalidDataException>(() => ExecutionRuntimeRootFileSystem.VerifySealed(
            rootPath,
            GetLauncherPath(rootPath, installed.Receipt.LauncherPath)));
        var recovered = fixture.CreateStore();
        await recovered.InitializeAsync();

        AssertRuntimeRootSealed(rootPath, installed.Receipt);
        Assert.NotNull(await recovered.GetReceiptAsync(installed.Receipt.Identity));
    }

    [Fact]
    public async Task Initialize_CompletesDeletionAfterCommittedTombstoneCrash()
    {
        var injector = new ThrowingFaultInjector(
            ExecutionRuntimeStoreFaultPoint.TombstoneCommittedRootDeleted);
        using var fixture = await StoreFixture.CreateAsync(injector);
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        fixture.GarbageCollectionAuthority.SetLifecycle(
            ExecutionRuntimeLifecycleStates.Deprecated,
            installed.Receipt);
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);

        await Assert.ThrowsAsync<ExecutionRuntimeStoreSimulatedCrashException>(
            () => fixture.Store.CollectGarbageAsync(TimeSpan.Zero));

        var tombstonePath = Assert.Single(
            Directory.EnumerateDirectories(fixture.Layout.TombstoneRootPath),
            path => !string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(fixture.Layout.GarbageCollectionMarkRootPath),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal));
        var document = JsonSerializer.Deserialize<ExecutionRuntimeRootTombstoneV1>(
            await File.ReadAllBytesAsync(Path.Combine(tombstonePath, "tombstone.json")),
            ExecutionRuntimeStore.JsonOptions);
        Assert.NotNull(document);
        Assert.Equal(ExecutionRuntimeRootTombstonePhase.DeletionCommitted, document.Phase);
        Assert.False(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
        Assert.False(File.Exists(fixture.Layout.GetReceiptPath(installed.Receipt.Identity)));

        var recovered = fixture.CreateStore();
        await recovered.InitializeAsync();

        Assert.False(Directory.Exists(tombstonePath));
        Assert.False(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
        Assert.False(File.Exists(fixture.Layout.GetReceiptPath(installed.Receipt.Identity)));

        var reinstalled = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        Assert.False(reinstalled.AlreadyPresent);
    }

    [Fact]
    public async Task Initialize_BoundsSharingViolationRetriesAndDefersCommittedTombstone()
    {
        var crash = new ThrowingFaultInjector(
            ExecutionRuntimeStoreFaultPoint.TombstoneCommittedRootDeleted);
        using var fixture = await StoreFixture.CreateAsync(crash);
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        await Assert.ThrowsAsync<ExecutionRuntimeStoreSimulatedCrashException>(
            () => fixture.Store.CollectGarbageAsync(TimeSpan.Zero));
        var tombstonePath = Assert.Single(
            Directory.EnumerateDirectories(fixture.Layout.TombstoneRootPath),
            path => !string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(fixture.Layout.GarbageCollectionMarkRootPath),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal));
        var sharingViolations = new RepeatedIOExceptionFaultInjector(
            ExecutionRuntimeStoreFaultPoint.TombstoneCommittedRootDeleted);
        var recovered = fixture.CreateStore(faultInjector: sharingViolations);

        await recovered.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(5, sharingViolations.Count);
        Assert.True(Directory.Exists(tombstonePath));
        Assert.Null(await recovered.GetReceiptAsync(installed.Receipt.Identity)
            .WaitAsync(TimeSpan.FromSeconds(5)));

        var retry = fixture.CreateStore();
        await retry.InitializeAsync();
        Assert.False(Directory.Exists(tombstonePath));
    }

    [Fact]
    public async Task Ensure_RecoversPreparedTombstoneAfterSameStoreGcFailure()
    {
        var injector = new ThrowingFaultInjector(ExecutionRuntimeStoreFaultPoint.TombstoneRootMoved);
        using var fixture = await StoreFixture.CreateAsync(injector);
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var staleInitializedStore = fixture.CreateStore();
        await staleInitializedStore.InitializeAsync();
        fixture.GarbageCollectionAuthority.SetLifecycle(
            ExecutionRuntimeLifecycleStates.Deprecated,
            installed.Receipt);
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        await Assert.ThrowsAsync<ExecutionRuntimeStoreSimulatedCrashException>(
            () => fixture.Store.CollectGarbageAsync(TimeSpan.Zero));

        var reused = await staleInitializedStore.EnsureAsync(fixture.Plan, fixture.ArchivePath);

        Assert.True(reused.AlreadyPresent);
        Assert.True(Directory.Exists(fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity)));
        Assert.True(File.Exists(fixture.Layout.GetReceiptPath(installed.Receipt.Identity)));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(fixture.Layout.TombstoneRootPath),
            path => !string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(fixture.Layout.GarbageCollectionMarkRootPath),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal));
    }

    [Fact]
    public async Task Initialize_RemovesEmptyTombstoneLeftAfterCommittedDocumentDeletion()
    {
        using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.InitializeAsync();
        var tombstonePath = fixture.Layout.GetTombstonePath(Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tombstonePath);

        var recovered = fixture.CreateStore();
        await recovered.InitializeAsync();

        Assert.False(Directory.Exists(tombstonePath));
    }

    [Fact]
    public async Task Initialize_RemovesTemporaryOnlyPreparedTombstone()
    {
        using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.InitializeAsync();
        var tombstonePath = fixture.Layout.GetTombstonePath(Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tombstonePath);
        await File.WriteAllTextAsync(
            Path.Combine(tombstonePath, "tombstone.json.tmp-" + Guid.NewGuid().ToString("N")),
            "partial");

        var recovered = fixture.CreateStore();
        await recovered.InitializeAsync();

        Assert.False(Directory.Exists(tombstonePath));
    }

    [Fact]
    public async Task GarbageCollection_SharedRootRemainsUntilEveryLeaseIsReleased()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        fixture.GarbageCollectionAuthority.SetLifecycle(
            ExecutionRuntimeLifecycleStates.Deprecated,
            installed.Receipt);
        var rootPath = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        var first = await ReserveAsync(fixture, installed.Receipt, "shared.first");
        var second = await ReserveAsync(fixture, installed.Receipt, "shared.second");

        await first.DisposeAsync();
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        Assert.True(Directory.Exists(rootPath));

        await second.DisposeAsync();
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        await fixture.Store.CollectGarbageAsync(TimeSpan.Zero);
        Assert.False(Directory.Exists(rootPath));
    }

    [Fact]
    public async Task GarbageCollection_GraceStartsWhenRootBecomesUnmarkedAndResetsWhenRetained()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-28T12:00:00Z"));
        using var fixture = await StoreFixture.CreateAsync(timeProvider: clock);
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        fixture.GarbageCollectionAuthority.SetLifecycle(
            ExecutionRuntimeLifecycleStates.Deprecated,
            installed.Receipt);
        var rootPath = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        var minimumAge = TimeSpan.FromHours(1);

        await fixture.Store.CollectGarbageAsync(minimumAge);
        clock.Advance(TimeSpan.FromMinutes(30));
        fixture.GarbageCollectionAuthority.SetBindings(CreateInstalledBinding(installed.Receipt));
        await fixture.Store.CollectGarbageAsync(minimumAge);
        fixture.GarbageCollectionAuthority.SetBindings();
        clock.Advance(TimeSpan.FromHours(2));
        await fixture.Store.CollectGarbageAsync(minimumAge);
        Assert.True(Directory.Exists(rootPath));

        clock.Advance(TimeSpan.FromMinutes(59));
        await fixture.Store.CollectGarbageAsync(minimumAge);
        Assert.True(Directory.Exists(rootPath));
        clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Store.CollectGarbageAsync(minimumAge);
        Assert.False(Directory.Exists(rootPath));
    }

    [Fact]
    public async Task Initialize_RemovesNeverStartedLeaseReservation()
    {
        using var fixture = await StoreFixture.CreateAsync(hostInstanceId: Guid.NewGuid());
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        await using var lease = await ReserveAsync(fixture, installed.Receipt, "stale.reservation");
        var leasePath = fixture.Layout.GetLeasePath(lease.LeaseId);
        Assert.True(File.Exists(leasePath));

        var recovered = fixture.CreateStore(hostInstanceId: Guid.NewGuid());
        await recovered.InitializeAsync();

        Assert.False(File.Exists(leasePath));
    }

    [Fact]
    public async Task Initialize_RemovesLaunchIntentThatNeverAttachedContainment()
    {
        var containment = new FakeContainmentController();
        using var fixture = await StoreFixture.CreateAsync(
            hostInstanceId: Guid.NewGuid(),
            containmentController: containment);
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        await using var lease = await ReserveAsync(fixture, installed.Receipt, "unattached.containment");
        var leasePath = fixture.Layout.GetLeasePath(lease.LeaseId);
        var reservation = await ReadLeaseAsync(leasePath);
        await DurableJsonDocument.WriteAsync(
            leasePath,
            reservation with
            {
                ProcessStartRequestedAtUtc = reservation.ReservedAtUtc.AddMilliseconds(100),
                ContainmentGeneration = Guid.NewGuid(),
            },
            ExecutionRuntimeStore.JsonOptions);

        var recovered = fixture.CreateStore(
            hostInstanceId: Guid.NewGuid(),
            containmentController: containment);
        await recovered.InitializeAsync();

        Assert.Equal(0, containment.TerminateCalls);
        Assert.False(File.Exists(leasePath));
    }

    [Fact]
    public async Task Initialize_TerminatesContainmentAttachedBeforePayloadAuthorization()
    {
        var containment = new FakeContainmentController
        {
            ProbeResult = ExecutionRuntimeContainmentProbe.Active,
        };
        using var fixture = await StoreFixture.CreateAsync(
            hostInstanceId: Guid.NewGuid(),
            containmentController: containment);
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var runtimeRoot = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        var launcherPath = GetLauncherPath(runtimeRoot, installed.Receipt.LauncherPath);
        await using var lease = await ReserveAsync(fixture, installed.Receipt, "contained.nopayload");
        var leasePath = fixture.Layout.GetLeasePath(lease.LeaseId);
        var reservation = await ReadLeaseAsync(leasePath);
        var contained = CreateAttachedContainment(reservation, runtimeRoot, launcherPath, 181) with
        {
            ProcessId = null,
            ProcessBirthIdentity = null,
            ProcessStartedAtUtc = null,
            ExecutablePath = null,
            RuntimeRootPath = null,
            AttachedAtUtc = null,
        };
        await DurableJsonDocument.WriteAsync(
            leasePath,
            contained,
            ExecutionRuntimeStore.JsonOptions);

        var recovered = fixture.CreateStore(
            hostInstanceId: Guid.NewGuid(),
            containmentController: containment);
        await recovered.InitializeAsync();

        Assert.Equal(1, containment.TerminateCalls);
        Assert.False(File.Exists(leasePath));
    }

    [Fact]
    public async Task Initialize_PreservesLeaseOwnedByCurrentHostInstance()
    {
        var hostInstanceId = Guid.NewGuid();
        using var fixture = await StoreFixture.CreateAsync(hostInstanceId: hostInstanceId);
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        await using var lease = await ReserveAsync(fixture, installed.Receipt, "current.host");
        var leasePath = fixture.Layout.GetLeasePath(lease.LeaseId);

        var sameHostStore = fixture.CreateStore(hostInstanceId: hostInstanceId);
        await sameHostStore.InitializeAsync();

        Assert.True(File.Exists(leasePath));
    }

    [Fact]
    public async Task Initialize_EmptyContainmentAfterPidReuseNeverSignalsProcess()
    {
        var containment = new FakeContainmentController
        {
            ProbeResult = ExecutionRuntimeContainmentProbe.Empty,
        };
        using var fixture = await StoreFixture.CreateAsync(
            hostInstanceId: Guid.NewGuid(),
            containmentController: containment);
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var runtimeRoot = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        var launcherPath = GetLauncherPath(runtimeRoot, installed.Receipt.LauncherPath);
        await using var lease = await fixture.Store.ReserveProcessLeaseAsync(
            new ExecutionRuntimeLeaseOwner(
                "pid.reuse",
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid()),
            installed.Receipt.ContentIdentity,
            runtimeRoot,
            launcherPath,
            CancellationToken.None);
        var leasePath = fixture.Layout.GetLeasePath(lease.LeaseId);
        var reservation = await ReadLeaseAsync(leasePath);
        await DurableJsonDocument.WriteAsync(
            leasePath,
            CreateAttachedContainment(reservation, runtimeRoot, launcherPath, 202),
            ExecutionRuntimeStore.JsonOptions);

        var recovered = fixture.CreateStore(
            hostInstanceId: Guid.NewGuid(),
            containmentController: containment);
        await recovered.InitializeAsync();

        Assert.Equal(0, containment.TerminateCalls);
        Assert.False(File.Exists(leasePath));
    }

    [Fact]
    public async Task Initialize_ContainmentIdentityMismatchNeverSignalsProcess()
    {
        var containment = new FakeContainmentController
        {
            ProbeResult = ExecutionRuntimeContainmentProbe.IdentityMismatch,
        };
        using var fixture = await StoreFixture.CreateAsync(
            hostInstanceId: Guid.NewGuid(),
            containmentController: containment);
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var runtimeRoot = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        var launcherPath = GetLauncherPath(runtimeRoot, installed.Receipt.LauncherPath);
        await using var lease = await fixture.Store.ReserveProcessLeaseAsync(
            new ExecutionRuntimeLeaseOwner(
                "attachment.mismatch",
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid()),
            installed.Receipt.ContentIdentity,
            runtimeRoot,
            launcherPath,
            CancellationToken.None);
        var leasePath = fixture.Layout.GetLeasePath(lease.LeaseId);
        var reservation = await ReadLeaseAsync(leasePath);
        await DurableJsonDocument.WriteAsync(
            leasePath,
            CreateAttachedContainment(reservation, runtimeRoot, launcherPath, 252),
            ExecutionRuntimeStore.JsonOptions);

        var recovered = fixture.CreateStore(
            hostInstanceId: Guid.NewGuid(),
            containmentController: containment);
        await Assert.ThrowsAsync<IOException>(() => recovered.InitializeAsync());

        Assert.Equal(0, containment.TerminateCalls);
        Assert.True(File.Exists(leasePath));
        containment.ProbeResult = ExecutionRuntimeContainmentProbe.Empty;
        await lease.ReleaseAfterProcessTreeTerminationAsync();
    }

    [Fact]
    public async Task Initialize_TerminatesAndReapsVerifiedOrphanProcessTree()
    {
        var containment = new FakeContainmentController
        {
            ProbeResult = ExecutionRuntimeContainmentProbe.Active,
        };
        using var fixture = await StoreFixture.CreateAsync(
            hostInstanceId: Guid.NewGuid(),
            containmentController: containment);
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var runtimeRoot = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        var launcherPath = GetLauncherPath(runtimeRoot, installed.Receipt.LauncherPath);
        await using var lease = await fixture.Store.ReserveProcessLeaseAsync(
            new ExecutionRuntimeLeaseOwner(
                "orphan.worker",
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid()),
            installed.Receipt.ContentIdentity,
            runtimeRoot,
            launcherPath,
            CancellationToken.None);
        var leasePath = fixture.Layout.GetLeasePath(lease.LeaseId);
        var reservation = await ReadLeaseAsync(leasePath);
        await DurableJsonDocument.WriteAsync(
            leasePath,
            CreateAttachedContainment(reservation, runtimeRoot, launcherPath, 303),
            ExecutionRuntimeStore.JsonOptions);

        var recovered = fixture.CreateStore(
            hostInstanceId: Guid.NewGuid(),
            containmentController: containment);
        await recovered.InitializeAsync();

        Assert.Equal(1, containment.TerminateCalls);
        Assert.False(File.Exists(leasePath));
    }

    [Fact]
    public async Task Initialize_RestoresInterruptedRootTombstoneForAuthorityReconciliation()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var rootPath = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        var receiptPath = fixture.Layout.GetReceiptPath(fixture.Plan.Identity);
        var tombstoneId = Guid.NewGuid().ToString("N");
        var tombstonePath = fixture.Layout.GetTombstonePath(tombstoneId);
        Directory.CreateDirectory(tombstonePath);
        var tombstone = new ExecutionRuntimeRootTombstoneV1(
            ExecutionRuntimeRootTombstoneV1.CurrentSchemaVersion,
            tombstoneId,
            ExecutionRuntimeRootTombstonePhase.Prepared,
            installed.Receipt.ContentIdentity,
            [fixture.Plan.Identity],
            DateTimeOffset.UtcNow);
        await DurableJsonDocument.WriteAsync(
            Path.Combine(tombstonePath, "tombstone.json"),
            tombstone,
            ExecutionRuntimeStore.JsonOptions);
        ExecutionRuntimeRootFileSystem.MakeOwnedRootMovable(rootPath);
        Directory.Move(rootPath, Path.Combine(tombstonePath, "root"));
        Assert.True(File.Exists(receiptPath));

        var recovered = fixture.CreateStore();
        await recovered.InitializeAsync();

        Assert.False(Directory.Exists(tombstonePath));
        Assert.True(Directory.Exists(rootPath));
        Assert.True(File.Exists(receiptPath));
    }

    [Fact]
    public async Task Initialize_DefersUnmovedTombstoneUntilGcReconfirmsRootIsUnmarked()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var rootPath = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        var tombstoneId = Guid.NewGuid().ToString("N");
        var tombstonePath = fixture.Layout.GetTombstonePath(tombstoneId);
        Directory.CreateDirectory(tombstonePath);
        await DurableJsonDocument.WriteAsync(
            Path.Combine(tombstonePath, "tombstone.json"),
            new ExecutionRuntimeRootTombstoneV1(
                ExecutionRuntimeRootTombstoneV1.CurrentSchemaVersion,
                tombstoneId,
                ExecutionRuntimeRootTombstonePhase.Prepared,
                installed.Receipt.ContentIdentity,
                [fixture.Plan.Identity],
                DateTimeOffset.UtcNow),
            ExecutionRuntimeStore.JsonOptions);

        var recovered = fixture.CreateStore();
        await recovered.InitializeAsync();
        Assert.True(Directory.Exists(rootPath));
        Assert.False(Directory.Exists(tombstonePath));

        var additionalIdentity = installed.Receipt.Identity with
        {
            PackagingRevision = installed.Receipt.Identity.PackagingRevision + 1,
        };
        var additionalReceiptPath = fixture.Layout.GetReceiptPath(additionalIdentity);
        Directory.CreateDirectory(Path.GetDirectoryName(additionalReceiptPath)!);
        var additionalReceipt = new ExecutionRuntimeReceiptV1(
            installed.Receipt.SchemaVersion,
            additionalIdentity,
            installed.Receipt.WorkerProtocol,
            installed.Receipt.ArtifactSha256,
            installed.Receipt.ArtifactSize,
            installed.Receipt.ManifestSha256,
            installed.Receipt.ContentIdentity,
            installed.Receipt.LauncherPath,
            installed.Receipt.LauncherSha256,
            installed.Receipt.LauncherSize,
            installed.Receipt.LauncherArguments,
            installed.Receipt.Content,
            installed.Receipt.NativeAttestationSha256,
            installed.Receipt.NativeAttestationKeyId,
            installed.Receipt.SigningKeyId,
            installed.Receipt.TrustGeneration,
            installed.Receipt.TrustEnvironment,
            installed.Receipt.AcquisitionSource,
            installed.Receipt.RegistryOrigin,
            installed.Receipt.InstalledAtUtc);
        await DurableJsonDocument.WriteAsync(
            additionalReceiptPath,
            additionalReceipt,
            ExecutionRuntimeStore.JsonOptions);
        fixture.GarbageCollectionAuthority.SetLifecycle(
            ExecutionRuntimeLifecycleStates.Deprecated,
            installed.Receipt,
            additionalReceipt);

        fixture.GarbageCollectionAuthority.SetBindings(CreateInstalledBinding(installed.Receipt));
        await recovered.CollectGarbageAsync(TimeSpan.Zero);
        Assert.True(Directory.Exists(rootPath));
        Assert.True(File.Exists(additionalReceiptPath));

        fixture.GarbageCollectionAuthority.SetBindings();
        await recovered.CollectGarbageAsync(TimeSpan.Zero);
        await recovered.CollectGarbageAsync(TimeSpan.Zero);
        Assert.False(Directory.Exists(rootPath));
        Assert.False(Directory.Exists(tombstonePath));
        Assert.False(File.Exists(additionalReceiptPath));
    }

    [Fact]
    public async Task Ensure_UnhealthyActivelyLeasedRootDefersQuarantine()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var rootPath = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        var launcherPath = GetLauncherPath(rootPath, installed.Receipt.LauncherPath);
        await using var lease = await ReserveAsync(fixture, installed.Receipt, "repair.defer");
        ExecutionRuntimeRootFileSystem.MakeOwnedTreeWritable(rootPath);
        await File.WriteAllBytesAsync(launcherPath, new byte[installed.Receipt.LauncherSize]);
        ExecutionRuntimeRootFileSystem.Seal(rootPath, launcherPath);

        await Assert.ThrowsAsync<ExecutionRuntimeRootInUseException>(
            () => fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath));

        Assert.True(Directory.Exists(rootPath));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(fixture.Layout.QuarantineRootPath),
            path => Path.GetFileName(path).Contains("unhealthy-root", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ensure_ConcurrentExactRequestsShareOneAcquisition()
    {
        var counter = new CountingFaultInjector(ExecutionRuntimeStoreFaultPoint.Validated);
        using var fixture = await StoreFixture.CreateAsync(counter);

        var requests = Enumerable.Range(0, 16)
            .Select(_ => fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath))
            .ToArray();
        var results = await Task.WhenAll(requests);

        Assert.Equal(1, counter.Count);
        Assert.All(results, result => Assert.Equal(results[0].Receipt.ContentIdentity, result.Receipt.ContentIdentity));
        Assert.All(results, result => Assert.False(result.AlreadyPresent));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.TransactionRootPath));
    }

    [Fact]
    public async Task Ensure_CancelledLeaderRetainsOwnedSourceWhileWaitingForRecoveryFence()
    {
        using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.InitializeAsync();
        using var recoveryFence = await RuntimeFileLock.AcquireExclusiveAsync(
            fixture.Layout.RecoveryFencePath,
            CancellationToken.None);
        var leaderDirectory = Path.Combine(fixture.RootPath, "waiting-leader-owned-archive");
        var followerDirectory = Path.Combine(fixture.RootPath, "waiting-follower-owned-archive");
        Directory.CreateDirectory(leaderDirectory);
        Directory.CreateDirectory(followerDirectory);
        var leaderPath = Path.Combine(leaderDirectory, "runtime.sunderruntime");
        var followerPath = Path.Combine(followerDirectory, "runtime.sunderruntime");
        File.Copy(fixture.ArchivePath, leaderPath);
        File.Copy(fixture.ArchivePath, followerPath);
        var leaderReleases = 0;
        var followerReleases = 0;
        var followerReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var leader = fixture.Store.EnsureOwnedArchiveAsync(
            fixture.Plan,
            new ExecutionRuntimeOwnedArchive(leaderPath, () =>
            {
                Interlocked.Increment(ref leaderReleases);
                ExecutionRuntimeStore.TryDeleteDirectory(leaderDirectory);
            }),
            cancellation.Token);
        var follower = fixture.Store.EnsureOwnedArchiveAsync(
            fixture.Plan,
            new ExecutionRuntimeOwnedArchive(followerPath, () =>
            {
                Interlocked.Increment(ref followerReleases);
                ExecutionRuntimeStore.TryDeleteDirectory(followerDirectory);
                followerReleased.TrySetResult();
            }),
            CancellationToken.None);
        await followerReleased.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => leader.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref leaderReleases));
        Assert.Equal(1, Volatile.Read(ref followerReleases));
        Assert.True(File.Exists(leaderPath));

        recoveryFence.Dispose();
        var installed = await follower.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(installed.AlreadyPresent);
        Assert.Equal(1, Volatile.Read(ref leaderReleases));
        Assert.False(Directory.Exists(leaderDirectory));
        Assert.False(Directory.Exists(followerDirectory));
    }

    [Fact]
    public async Task Ensure_CancelledLeaderUsesStableLocalSourceAfterUnixUnlink()
    {
        using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.InitializeAsync();
        using var recoveryFence = await RuntimeFileLock.AcquireExclusiveAsync(
            fixture.Layout.RecoveryFencePath,
            CancellationToken.None);
        var leaderPath = Path.Combine(fixture.RootPath, "waiting-local-source.sunderruntime");
        File.Copy(fixture.ArchivePath, leaderPath);
        using var cancellation = new CancellationTokenSource();
        var leader = fixture.Store.EnsureAsync(fixture.Plan, leaderPath, cancellation.Token);
        var follower = fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => leader.WaitAsync(TimeSpan.FromSeconds(5)));
        if (!OperatingSystem.IsWindows())
        {
            File.Delete(leaderPath);
        }

        recoveryFence.Dispose();
        var installed = await follower.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(installed.AlreadyPresent);
        Assert.Equal(fixture.Plan.ArtifactSha256, installed.Receipt.ArtifactSha256);
    }

    [Fact]
    public async Task Ensure_CancelledLeaderKeepsOwnedArchiveUntilJoinedFlightCompletes()
    {
        using var injector = new BlockingFaultInjector(ExecutionRuntimeStoreFaultPoint.ArchiveSourceOpening);
        using var fixture = await StoreFixture.CreateAsync(injector);
        await fixture.Store.InitializeAsync();
        var leaderDirectory = Path.Combine(fixture.RootPath, "leader-owned-archive");
        var followerDirectory = Path.Combine(fixture.RootPath, "follower-owned-archive");
        Directory.CreateDirectory(leaderDirectory);
        Directory.CreateDirectory(followerDirectory);
        var leaderPath = Path.Combine(leaderDirectory, "runtime.sunderruntime");
        var followerPath = Path.Combine(followerDirectory, "runtime.sunderruntime");
        File.Copy(fixture.ArchivePath, leaderPath);
        File.Copy(fixture.ArchivePath, followerPath);
        var leaderReleases = 0;
        var followerReleases = 0;
        var followerReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var leader = Task.Run(() => fixture.Store.EnsureOwnedArchiveAsync(
                fixture.Plan,
                new ExecutionRuntimeOwnedArchive(leaderPath, () =>
                {
                    Interlocked.Increment(ref leaderReleases);
                    ExecutionRuntimeStore.TryDeleteDirectory(leaderDirectory);
                }),
                cancellation.Token),
            CancellationToken.None);
        try
        {
            Assert.True(injector.WaitUntilBlocked(TimeSpan.FromSeconds(10)));
            var follower = fixture.Store.EnsureOwnedArchiveAsync(
                fixture.Plan,
                new ExecutionRuntimeOwnedArchive(followerPath, () =>
                {
                    Interlocked.Increment(ref followerReleases);
                    ExecutionRuntimeStore.TryDeleteDirectory(followerDirectory);
                    followerReleased.TrySetResult();
                }),
                CancellationToken.None);
            await followerReleased.Task.WaitAsync(TimeSpan.FromSeconds(10));

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leader);
            Assert.Equal(0, Volatile.Read(ref leaderReleases));
            Assert.True(File.Exists(leaderPath));

            injector.Release();
            var installed = await follower.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(installed.AlreadyPresent);
            Assert.Equal(1, Volatile.Read(ref leaderReleases));
            Assert.Equal(1, Volatile.Read(ref followerReleases));
            Assert.False(Directory.Exists(leaderDirectory));
            Assert.False(Directory.Exists(followerDirectory));
        }
        finally
        {
            injector.Release();
        }
    }

    [Fact]
    public async Task Ensure_CancelledWaitersOnlyCancelFlightWhenFinalWaiterLeaves()
    {
        using var injector = new BlockingFaultInjector(ExecutionRuntimeStoreFaultPoint.ArchiveSourceOpening);
        using var fixture = await StoreFixture.CreateAsync(injector);
        await fixture.Store.InitializeAsync();
        var leaderDirectory = Path.Combine(fixture.RootPath, "cancelled-leader-owned-archive");
        var followerDirectory = Path.Combine(fixture.RootPath, "cancelled-follower-owned-archive");
        Directory.CreateDirectory(leaderDirectory);
        Directory.CreateDirectory(followerDirectory);
        var leaderPath = Path.Combine(leaderDirectory, "runtime.sunderruntime");
        var followerPath = Path.Combine(followerDirectory, "runtime.sunderruntime");
        File.Copy(fixture.ArchivePath, leaderPath);
        File.Copy(fixture.ArchivePath, followerPath);
        var leaderReleases = 0;
        var followerReleases = 0;
        var producerCancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var followerReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration producerCancellationRegistration = default;
        using var leaderCancellation = new CancellationTokenSource();
        using var followerCancellation = new CancellationTokenSource();
        var leader = fixture.Store.EnsureArchiveAsync(
            fixture.Plan,
            operationToken =>
            {
                producerCancellationRegistration = operationToken.Register(
                    () => producerCancellationObserved.TrySetResult());
                return Task.FromResult(new ExecutionRuntimeOwnedArchive(leaderPath, () =>
                {
                    producerCancellationRegistration.Dispose();
                    Interlocked.Increment(ref leaderReleases);
                    ExecutionRuntimeStore.TryDeleteDirectory(leaderDirectory);
                }));
            },
            leaderCancellation.Token);
        try
        {
            Assert.True(injector.WaitUntilBlocked(TimeSpan.FromSeconds(10)));
            var follower = fixture.Store.EnsureOwnedArchiveAsync(
                fixture.Plan,
                new ExecutionRuntimeOwnedArchive(followerPath, () =>
                {
                    Interlocked.Increment(ref followerReleases);
                    ExecutionRuntimeStore.TryDeleteDirectory(followerDirectory);
                    followerReleased.TrySetResult();
                }),
                followerCancellation.Token);
            await followerReleased.Task.WaitAsync(TimeSpan.FromSeconds(10));

            leaderCancellation.Cancel();
            var leaderException = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => leader.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(leaderCancellation.Token, leaderException.CancellationToken);
            Assert.False(producerCancellationObserved.Task.IsCompleted);
            Assert.False(follower.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref leaderReleases));
            Assert.Equal(1, Volatile.Read(ref followerReleases));
            Assert.True(File.Exists(leaderPath));

            followerCancellation.Cancel();
            await producerCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(follower.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref leaderReleases));
            Assert.True(File.Exists(leaderPath));

            injector.Release();
            var followerException = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => follower.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal(followerCancellation.Token, followerException.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref leaderReleases));
            Assert.Equal(1, Volatile.Read(ref followerReleases));
            Assert.False(Directory.Exists(leaderDirectory));
            Assert.False(Directory.Exists(followerDirectory));
            Assert.False(File.Exists(fixture.Layout.GetReceiptPath(fixture.Plan.Identity)));
        }
        finally
        {
            injector.Release();
            producerCancellationRegistration.Dispose();
        }
    }

    [Fact]
    public async Task Ensure_LastCancelledWaiterWaitsForFaultedProducerCleanupAndAllowsNewFlight()
    {
        using var injector = new BlockingThrowingFaultInjector(
            ExecutionRuntimeStoreFaultPoint.ArchiveSourceOpening);
        using var fixture = await StoreFixture.CreateAsync(injector);
        await fixture.Store.InitializeAsync();
        var ownedDirectory = Path.Combine(fixture.RootPath, "faulted-cancelled-owned-archive");
        Directory.CreateDirectory(ownedDirectory);
        var ownedPath = Path.Combine(ownedDirectory, "runtime.sunderruntime");
        File.Copy(fixture.ArchivePath, ownedPath);
        var releases = 0;
        var producerCancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration producerCancellationRegistration = default;
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Store.EnsureArchiveAsync(
            fixture.Plan,
            operationToken =>
            {
                producerCancellationRegistration = operationToken.Register(
                    () => producerCancellationObserved.TrySetResult());
                return Task.FromResult(new ExecutionRuntimeOwnedArchive(ownedPath, () =>
                {
                    producerCancellationRegistration.Dispose();
                    Interlocked.Increment(ref releases);
                    ExecutionRuntimeStore.TryDeleteDirectory(ownedDirectory);
                }));
            },
            cancellation.Token);
        try
        {
            Assert.True(injector.WaitUntilBlocked(TimeSpan.FromSeconds(10)));
            cancellation.Cancel();
            await producerCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(pending.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref releases));
            Assert.True(File.Exists(ownedPath));

            injector.Release();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => pending.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref releases));
            Assert.False(Directory.Exists(ownedDirectory));
            Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.StagingRootPath));
            Assert.Empty(Directory.EnumerateDirectories(fixture.Layout.TransactionRootPath));
            Assert.False(File.Exists(fixture.Layout.GetReceiptPath(fixture.Plan.Identity)));
            using (var lockCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (await RuntimeFileLock.AcquireExclusiveAsync(
                       fixture.Layout.GetSingleFlightLockPath(fixture.Plan.Identity),
                       lockCancellation.Token))
            {
            }

            var retry = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(retry.AlreadyPresent);
            Assert.Equal(1, Volatile.Read(ref releases));
        }
        finally
        {
            injector.Release();
            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
            }
            producerCancellationRegistration.Dispose();
        }
    }

    [Fact]
    public async Task Verify_DeepVerificationRejectsReceiptContentNotBoundToSignedIndex()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var content = Assert.Single(
            installed.Receipt.Content,
            static item => item.Path == "payload/runtime/runtime.bin");
        var contentPath = Path.Combine(
            [fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity), .. content.Path.Split('/')]);
        var rootPath = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        ExecutionRuntimeRootFileSystem.MakeOwnedTreeWritable(rootPath);
        await File.WriteAllBytesAsync(contentPath, Enumerable.Repeat((byte)0xA5, checked((int)content.Size)).ToArray());
        ExecutionRuntimeRootFileSystem.Seal(rootPath, GetLauncherPath(rootPath, installed.Receipt.LauncherPath));
        var receiptPath = fixture.Layout.GetReceiptPath(installed.Receipt.Identity);
        var receiptJson = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!.AsObject();
        var contentJson = receiptJson["content"]!.AsArray()
            .Select(static node => node!.AsObject())
            .Single(node => node["path"]!.GetValue<string>() == content.Path);
        contentJson["sha256"] = await HashFileAsync(contentPath);
        await File.WriteAllTextAsync(receiptPath, receiptJson.ToJsonString(ExecutionRuntimeStore.JsonOptions));

        var verification = await fixture.Store.VerifyAsync(installed.Receipt.Identity, deep: true);

        Assert.False(verification.Healthy);
        Assert.Contains("content index", verification.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetReceipt_NullContentPathIsRejectedAsInvalidData()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var receiptPath = fixture.Layout.GetReceiptPath(installed.Receipt.Identity);
        var receiptJson = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!.AsObject();
        receiptJson["content"]!.AsArray()[0]!["path"] = null;
        await File.WriteAllTextAsync(receiptPath, receiptJson.ToJsonString(ExecutionRuntimeStore.JsonOptions));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.GetReceiptAsync(installed.Receipt.Identity));
    }

    [Fact]
    public async Task Verify_WarmReceiptMismatchFailsClosedWithoutHashingPayload()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var receiptPath = fixture.Layout.GetReceiptPath(installed.Receipt.Identity);
        var receiptJson = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!.AsObject();
        receiptJson["installedAtUtc"] = "2026-08-28T12:00:00+00:00";
        await File.WriteAllTextAsync(receiptPath, receiptJson.ToJsonString(ExecutionRuntimeStore.JsonOptions));
        var verifier = new CountingSignatureVerifier();
        var io = new CountingVerificationIoObserver();
        var store = fixture.CreateStore(signatureVerifier: verifier, verificationIoObserver: io);

        var verification = await store.VerifyAsync(installed.Receipt.Identity, deep: false);

        Assert.False(verification.Healthy);
        Assert.False(verification.DeepVerified);
        Assert.Contains("health marker", verification.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, verifier.Count);
        Assert.Equal(0, io.PayloadBytesRead);
        Assert.Equal(0, io.PayloadBytesHashed);
    }

    [Fact]
    public async Task Verify_StaleRootMetadataFailsWarmWithoutHashingPayload()
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var payload = Assert.Single(
            installed.Receipt.Content,
            static item => item.Path == "payload/runtime/runtime.bin");
        var payloadPath = Path.Combine(
            [fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity), .. payload.Path.Split('/')]);
        var rootPath = fixture.Layout.GetContentRootPath(installed.Receipt.ContentIdentity);
        ExecutionRuntimeRootFileSystem.MakeOwnedTreeWritable(rootPath);
        File.SetLastWriteTimeUtc(payloadPath, File.GetLastWriteTimeUtc(payloadPath).AddMinutes(1));
        ExecutionRuntimeRootFileSystem.Seal(rootPath, GetLauncherPath(rootPath, installed.Receipt.LauncherPath));
        var io = new CountingVerificationIoObserver();
        var store = fixture.CreateStore(verificationIoObserver: io);

        var verification = await store.VerifyAsync(installed.Receipt.Identity, deep: false);

        Assert.False(verification.Healthy);
        Assert.False(verification.DeepVerified);
        Assert.Contains("root/launcher metadata", verification.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, io.PayloadBytesRead);
        Assert.Equal(0, io.PayloadBytesHashed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verify_MissingOrMalformedWarmMarkerRequiresExplicitDeepRepair(bool malformed)
    {
        using var fixture = await StoreFixture.CreateAsync();
        var installed = await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var healthPath = fixture.Layout.GetHealthPath(installed.Receipt.ContentIdentity);
        if (malformed)
        {
            await File.WriteAllTextAsync(healthPath, "{");
        }
        else
        {
            File.Delete(healthPath);
        }
        var io = new CountingVerificationIoObserver();
        var store = fixture.CreateStore(verificationIoObserver: io);

        var warmFailure = await store.VerifyAsync(installed.Receipt.Identity, deep: false);

        Assert.False(warmFailure.Healthy);
        Assert.False(warmFailure.DeepVerified);
        Assert.Contains("health marker", warmFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, io.PayloadBytesRead);
        Assert.Equal(0, io.PayloadBytesHashed);

        var repaired = await store.VerifyAsync(installed.Receipt.Identity, deep: true);

        Assert.True(repaired.Healthy, repaired.Message);
        Assert.True(repaired.DeepVerified);
        Assert.True(io.PayloadBytesRead > 0);
        Assert.Equal(io.PayloadBytesRead, io.PayloadBytesHashed);
        Assert.True(File.Exists(healthPath));

        io.Reset();
        var warm = await store.VerifyAsync(installed.Receipt.Identity, deep: false);

        Assert.True(warm.Healthy, warm.Message);
        Assert.False(warm.DeepVerified);
        Assert.Equal(0, io.PayloadBytesRead);
        Assert.Equal(0, io.PayloadBytesHashed);
    }

    [Fact]
    public async Task Verify_WarmMarkerIsInvalidatedByCurrentTrustGeneration()
    {
        using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var verifier = new CountingSignatureVerifier();
        var io = new CountingVerificationIoObserver();
        var changedTrust = new ExecutionRuntimeHostTrustService(
            "store-test",
            4,
            developmentTrustEnabled: false,
            productionKeys: [new ExecutionRuntimeHostTrustKey(ReleaseKeyId, ReleaseKey, 1)]);
        var store = fixture.CreateStore(changedTrust, verifier, verificationIoObserver: io);

        var reverified = await store.VerifyAsync(fixture.Plan.Identity, deep: false);
        var warm = await store.VerifyAsync(fixture.Plan.Identity, deep: false);

        Assert.True(reverified.Healthy, reverified.Message);
        Assert.False(reverified.DeepVerified);
        Assert.True(warm.Healthy, warm.Message);
        Assert.False(warm.DeepVerified);
        Assert.Equal(1, verifier.Count);
        Assert.Equal(0, io.PayloadBytesRead);
        Assert.Equal(0, io.PayloadBytesHashed);
        var marker = JsonSerializer.Deserialize<ExecutionRuntimeHealthMarkerV1>(
            await File.ReadAllTextAsync(fixture.Layout.GetHealthPath(
                (await fixture.Store.GetReceiptAsync(fixture.Plan.Identity))!.ContentIdentity)),
            ExecutionRuntimeStore.JsonOptions);
        Assert.NotNull(marker);
        Assert.Equal(4, marker.TrustGeneration);
    }

    [Fact]
    public async Task Verify_WarmMarkerDoesNotBypassChangedSigningKeyMaterial()
    {
        using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var verifier = new CountingSignatureVerifier();
        var io = new CountingVerificationIoObserver();
        var changedKey = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        var changedTrust = new ExecutionRuntimeHostTrustService(
            "store-test",
            3,
            developmentTrustEnabled: false,
            productionKeys: [new ExecutionRuntimeHostTrustKey(ReleaseKeyId, changedKey, 1)]);
        var store = fixture.CreateStore(changedTrust, verifier, verificationIoObserver: io);

        var verification = await store.VerifyAsync(fixture.Plan.Identity, deep: false);

        Assert.False(verification.Healthy);
        Assert.Contains("no longer verifies", verification.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, verifier.Count);
        Assert.Equal(0, io.PayloadBytesRead);
        Assert.Equal(0, io.PayloadBytesHashed);
    }

    [Fact]
    public async Task Verify_RemovedSigningKeyBlocksWarmLaunchWithoutHashingPayload()
    {
        using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath);
        var verifier = new CountingSignatureVerifier();
        var io = new CountingVerificationIoObserver();
        var changedTrust = new ExecutionRuntimeHostTrustService(
            "store-test",
            4,
            developmentTrustEnabled: false,
            productionKeys: []);
        var store = fixture.CreateStore(changedTrust, verifier, verificationIoObserver: io);

        var verification = await store.VerifyAsync(fixture.Plan.Identity, deep: false);

        Assert.False(verification.Healthy);
        Assert.Contains("not trusted", verification.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, verifier.Count);
        Assert.Equal(0, io.PayloadBytesRead);
        Assert.Equal(0, io.PayloadBytesHashed);
    }

    [Fact]
    public async Task Ensure_UntrustedSigningKeyFailsBeforeArchivePublication()
    {
        var wrongKey = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        using var fixture = await StoreFixture.CreateAsync(
            trustKeys: [new ExecutionRuntimeHostTrustKey(ReleaseKeyId, wrongKey, 1)]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.EnsureAsync(fixture.Plan, fixture.ArchivePath));

        Assert.Contains("does not verify", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fixture.Layout.GetReceiptPath(fixture.Plan.Identity)));
        Assert.False(File.Exists(fixture.Layout.GetArchivePath(fixture.Plan.ArtifactSha256)));
    }

    [Fact]
    public void Ed25519Verifier_AcceptsRfc8032VectorAndRejectsTamper()
    {
        var publicKey = Convert.FromHexString(
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        var signature = Convert.FromHexString(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
            "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        var verifier = new ExecutionRuntimeEd25519SignatureVerifier();

        Assert.True(verifier.Verify([], signature, publicKey));
        signature[0] ^= 0x01;
        Assert.False(verifier.Verify([], signature, publicKey));
    }

    [Fact]
    public void HostTrust_ClassifiesOnlyActiveConfiguredSigningKeys()
    {
        var developmentKey = new ExecutionRuntimeHostTrustKey(
            "sunder.release.development.v1",
            Enumerable.Repeat((byte)0xA5, 32).ToArray(),
            2);
        var futureKey = new ExecutionRuntimeHostTrustKey(
            "sunder.release.future.v1",
            Enumerable.Repeat((byte)0xB5, 32).ToArray(),
            4);
        var retiredKey = new ExecutionRuntimeHostTrustKey(
            "sunder.release.retired.v1",
            Enumerable.Repeat((byte)0xC5, 32).ToArray(),
            1,
            retiredAfterGeneration: 2);
        var service = new ExecutionRuntimeHostTrustService(
            "development",
            3,
            developmentTrustEnabled: true,
            productionKeys: [new ExecutionRuntimeHostTrustKey(ReleaseKeyId, ReleaseKey, 1)],
            developmentKeys: [developmentKey, futureKey, retiredKey]);

        Assert.Equal(
            ExecutionRuntimeTrustEnvironment.Production,
            service.Classify(ReleaseKeyId, minimumHostTrustGeneration: 1));
        Assert.Equal(
            ExecutionRuntimeTrustEnvironment.Development,
            service.Classify(developmentKey.KeyId, minimumHostTrustGeneration: 2));
        Assert.Throws<ExecutionRuntimeTrustException>(() => service.Classify(
            "sunder.release.unknown.v1",
            minimumHostTrustGeneration: 1));
        Assert.Throws<ExecutionRuntimeTrustException>(() => service.Classify(
            futureKey.KeyId,
            minimumHostTrustGeneration: 1));
        Assert.Throws<ExecutionRuntimeTrustException>(() => service.Classify(
            retiredKey.KeyId,
            minimumHostTrustGeneration: 1));
        Assert.Throws<ExecutionRuntimeTrustException>(() => service.Classify(
            ReleaseKeyId,
            minimumHostTrustGeneration: 4));
    }

    [Fact]
    public void HostTrust_RejectsDuplicateIdsAndCrossEnvironmentPublicKeyMaterial()
    {
        var alternateKey = Enumerable.Repeat((byte)0xA5, 32).ToArray();

        Assert.Throws<ArgumentException>(() => new ExecutionRuntimeHostTrustService(
            "development",
            3,
            developmentTrustEnabled: true,
            productionKeys:
            [
                new ExecutionRuntimeHostTrustKey(ReleaseKeyId, ReleaseKey, 1),
                new ExecutionRuntimeHostTrustKey(ReleaseKeyId, alternateKey, 1),
            ],
            developmentKeys: []));
        Assert.Throws<ArgumentException>(() => new ExecutionRuntimeHostTrustService(
            "development",
            3,
            developmentTrustEnabled: true,
            productionKeys: [new ExecutionRuntimeHostTrustKey(ReleaseKeyId, ReleaseKey, 1)],
            developmentKeys: [new ExecutionRuntimeHostTrustKey(ReleaseKeyId, alternateKey, 1)]));
        Assert.Throws<ArgumentException>(() => new ExecutionRuntimeHostTrustService(
            "development",
            3,
            developmentTrustEnabled: true,
            productionKeys: [new ExecutionRuntimeHostTrustKey(ReleaseKeyId, ReleaseKey, 1)],
            developmentKeys:
            [
                new ExecutionRuntimeHostTrustKey(
                    "sunder.release.development.v1",
                    ReleaseKey,
                    1),
            ]));
    }

    [Fact]
    public void HostTrust_ProductionAndDevelopmentKeySetsCannotBeConfused()
    {
        var developmentKey = new ExecutionRuntimeHostTrustKey(
            "sunder.release.development.v1",
            ReleaseKey,
            1);
        var service = new ExecutionRuntimeHostTrustService(
            "production",
            3,
            developmentTrustEnabled: false,
            productionKeys: [new ExecutionRuntimeHostTrustKey(ReleaseKeyId, ReleaseKey, 1)]);

        Assert.Throws<ExecutionRuntimeTrustException>(() => service.Resolve(
            developmentKey.KeyId,
            ExecutionRuntimeTrustEnvironment.Development,
            minimumHostTrustGeneration: 1));
        Assert.Throws<ArgumentException>(() => new ExecutionRuntimeHostTrustService(
            "production",
            3,
            developmentTrustEnabled: false,
            productionKeys: [],
            developmentKeys: [developmentKey]));
    }

    [Fact]
    public async Task AcquisitionPlan_RequiresDevelopmentTrustAndSourceTogether()
    {
        using var fixture = await StoreFixture.CreateAsync();

        Assert.Throws<ArgumentException>(() => (fixture.Plan with
        {
            AcquisitionSource = RuntimeExecutionRuntimeAcquisitionSources.Development,
        }).Validate());
        Assert.Throws<ArgumentException>(() => (fixture.Plan with
        {
            TrustEnvironment = ExecutionRuntimeTrustEnvironment.Development,
        }).Validate());
    }

    private static SunderPackageManifest CreateRuntimePackageManifest(
        string rid,
        string versionRange,
        string? alternateRid = null)
    {
        SunderPackageTargetManifest CreateTarget(string targetRid) => new()
        {
            Role = SunderPackageFormat.RuntimeHostRole,
            Rid = targetRid,
            Kind = SunderPackageFormat.WorkerTargetKind,
            EntryPoint = "worker.dll",
            TargetFramework = "net10.0",
            SdkVersion = "1.1.0",
            RequiredHostCapabilities =
            [
                "core.v1",
                "execution-runtime.v1",
                "rpc.v1",
                "sdk-baseline-1-1.v1",
                "worker-protocol.v1",
            ],
            ExecutionRuntime = new SunderPackageExecutionRuntimeManifest
            {
                Id = SunderExecutionRuntimeFormat.DotNetRuntimeId,
                VersionRange = versionRange,
                ModuleAbi = SunderExecutionRuntimeFormat.DotNetModuleAbi,
            },
        };

        return new SunderPackageManifest
        {
            ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion,
            ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
            Id = "local.binding.test",
            Name = "Local Binding Test",
            Version = "1.0.0",
            Targets = alternateRid is null
                ? [CreateTarget(rid)]
                : [CreateTarget(rid), CreateTarget(alternateRid)],
            DependsOn = [],
            ContractBundles = [],
            UsesContracts = [],
            Provides = [],
        };
    }

    private static ExecutionRuntimeLifecyclePolicyObservation CreatePolicyObservation(
        ExecutionRuntimeReceiptV1 receipt,
        DateTimeOffset observedAtUtc,
        long revision,
        string lifecycleState = ExecutionRuntimeLifecycleStates.Supported,
        DateTimeOffset? installBlockedAtUtc = null,
        DateTimeOffset? activationBlockedAtUtc = null)
        => new(
            receipt,
            "https://registry.example/",
            new RegistryExecutionRuntimeLifecycle(
                lifecycleState,
                observedAtUtc.AddDays(-2),
                observedAtUtc.AddDays(-1),
                DeprecatedAtUtc: lifecycleState is ExecutionRuntimeLifecycleStates.Deprecated
                    or ExecutionRuntimeLifecycleStates.Retired
                        ? observedAtUtc.AddHours(-12)
                        : null,
                InstallBlockedAtUtc: installBlockedAtUtc,
                ActivationBlockedAtUtc: activationBlockedAtUtc,
                RevokedAtUtc: lifecycleState == ExecutionRuntimeLifecycleStates.Revoked ? observedAtUtc : null,
                Message: null),
            revision,
            MinimumHostTrustGeneration: 1,
            observedAtUtc);

    private static async Task<RuntimeArtifact> CreateArtifactAsync(
        string rootPath,
        byte payloadByte = 0x42,
        string runtimeVersion = "10.0.1",
        int packagingRevision = 7,
        string? runtimeIdentifier = null)
    {
        var artifactRoot = Path.Combine(rootPath, "artifact-" + Guid.NewGuid().ToString("N"));
        var sources = Path.Combine(artifactRoot, "sources");
        Directory.CreateDirectory(sources);
        var launcherSourcePath = Path.Combine(sources, "launcher.bin");
        var runtimeSourcePath = Path.Combine(sources, "runtime.bin");
        var attestationSourcePath = Path.Combine(sources, "attestation.json");
        await File.WriteAllBytesAsync(launcherSourcePath, [0x53, 0x55, 0x4e, 0x44, 0x45, 0x52]);
        await File.WriteAllBytesAsync(runtimeSourcePath, Enumerable.Repeat(payloadByte, 64).ToArray());

        var rid = runtimeIdentifier ?? "linux-x64";
        var upstreamSha256 = new string('a', 64);
        var attestation = new SunderExecutionRuntimeNativeAttestation
        {
            SchemaVersion = SunderExecutionRuntimeFormat.CurrentNativeAttestationVersion,
            RuntimeId = SunderExecutionRuntimeFormat.DotNetRuntimeId,
            RuntimeVersion = runtimeVersion,
            PackagingRevision = packagingRevision,
            Rid = rid,
            UpstreamSha256 = upstreamSha256,
            Workflow = new SunderExecutionRuntimeAttestationWorkflow
            {
                Name = "sunder.runtime.release",
                RunId = "store-test-run",
                SourceRevision = new string('b', 40),
            },
            Toolchain = new SunderExecutionRuntimeAttestationToolchain
            {
                Name = "dotnet-sdk",
                Version = "10.0.100",
            },
            LauncherSmoke = new SunderExecutionRuntimeAttestationLauncherSmoke
            {
                Result = SunderExecutionRuntimeFormat.LauncherSmokePassed,
            },
            Platform = new SunderExecutionRuntimeAttestationPlatform
            {
                Signature = rid.StartsWith("linux-", StringComparison.Ordinal)
                    ? SunderExecutionRuntimeFormat.PlatformCheckNotApplicable
                    : SunderExecutionRuntimeFormat.PlatformCheckPassed,
                Notarization = rid.StartsWith("osx-", StringComparison.Ordinal)
                    ? SunderExecutionRuntimeFormat.PlatformCheckPassed
                    : SunderExecutionRuntimeFormat.PlatformCheckNotApplicable,
            },
            Signing = new SunderExecutionRuntimeAttestationSigning
            {
                Algorithm = SunderExecutionRuntimeFormat.SigningAlgorithm,
                KeyId = AttestationKeyId,
                Signature = Convert.ToBase64String(new byte[SunderExecutionRuntimeFormat.Ed25519SignatureBytes]),
            },
        };
        var attestationBytes = SunderExecutionRuntimeCanonicalJson.SerializeNativeAttestation(attestation);
        await File.WriteAllBytesAsync(attestationSourcePath, attestationBytes);
        var manifest = new SunderExecutionRuntimeManifest
        {
            SchemaVersion = SunderExecutionRuntimeFormat.CurrentSchemaVersion,
            Id = SunderExecutionRuntimeFormat.DotNetRuntimeId,
            Version = runtimeVersion,
            PackagingRevision = packagingRevision,
            Rid = rid,
            ModuleAbi = SunderExecutionRuntimeFormat.DotNetModuleAbi,
            WorkerProtocol = SunderExecutionRuntimeFormat.WorkerProtocol,
            Launch = new SunderExecutionRuntimeLaunch
            {
                Executable = "payload/bin/sunder-runtime",
                Arguments =
                [
                    SunderExecutionRuntimeFormat.DotNetExecArgument,
                    SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder,
                ],
            },
            ContentIndex = SunderExecutionRuntimeFormat.ContentIndexPath,
            Upstream = new SunderExecutionRuntimeUpstream
            {
                Name = "dotnet",
                Version = runtimeVersion,
                SourceUri = "https://example.test/dotnet-runtime.tar.gz",
                SourceSha256 = upstreamSha256,
            },
            Provenance = new SunderExecutionRuntimeProvenance
            {
                AttestationPath = SunderExecutionRuntimeFormat.NativeAttestationPath,
                AttestationSha256 = Convert.ToHexString(SHA256.HashData(attestationBytes)).ToLowerInvariant(),
                AttestationKeyId = AttestationKeyId,
            },
            Licenses = ["MIT"],
            Signing = new SunderExecutionRuntimeSigning
            {
                Algorithm = SunderExecutionRuntimeFormat.SigningAlgorithm,
                KeyId = ReleaseKeyId,
                Signature = SunderExecutionRuntimeFormat.SignaturePath,
            },
        };
        var archivePath = Path.Combine(artifactRoot, "runtime.sunderruntime");
        await SunderExecutionRuntimeArchiveWriter.WriteAsync(
            manifest,
            archivePath,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [manifest.Launch.Executable!] = launcherSourcePath,
                ["payload/runtime/runtime.bin"] = runtimeSourcePath,
                [SunderExecutionRuntimeFormat.NativeAttestationPath] = attestationSourcePath,
            },
            new TestSigner(ReleaseKeyId, ReleaseKey));
        return new RuntimeArtifact(
            archivePath,
            manifest,
            await HashFileAsync(archivePath),
            new FileInfo(archivePath).Length,
            Convert.ToHexString(SHA256.HashData(SunderExecutionRuntimeCanonicalJson.SerializeManifest(manifest)))
                .ToLowerInvariant());
    }

    private static ExecutionRuntimeAcquisitionPlan CreatePlan(RuntimeArtifact artifact)
        => new(
            new ExecutionRuntimeIdentity(
                artifact.Manifest.Id!,
                artifact.Manifest.Version!,
                artifact.Manifest.PackagingRevision,
                artifact.Manifest.Rid!,
                artifact.Manifest.ModuleAbi!),
            artifact.ArtifactSha256,
            artifact.ArtifactSize,
            artifact.ManifestSha256,
            ContentIdentity: null,
            ReleaseKeyId,
            MinimumHostTrustGeneration: 1,
            ExecutionRuntimeTrustEnvironment.Production,
            RuntimeExecutionRuntimeAcquisitionSources.Registry,
            "https://registry.example/");

    private static InstalledPackageExecutionRuntimeBindingRecord CreateInstalledBinding(
        ExecutionRuntimeReceiptV1 receipt)
    {
        var observedAtUtc = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        return new InstalledPackageExecutionRuntimeBindingRecord(
            SunderPackageFormat.RuntimeHostRole,
            receipt.Identity.Rid,
            receipt.Identity.RuntimeId,
            receipt.Identity.Version,
            receipt.Identity.PackagingRevision,
            receipt.Identity.ModuleAbi,
            receipt.ManifestSha256,
            receipt.ArtifactSha256,
            receipt.ArtifactSize,
            receipt.RegistryOrigin ?? "https://registry.example/",
            receipt.AcquisitionSource,
            receipt.SigningKeyId,
            receipt.TrustGeneration,
            RuntimePolicyRevision: 1,
            RuntimeLifecycleState: "supported",
            PolicyObservedAtUtc: observedAtUtc,
            ResolvedAtUtc: observedAtUtc);
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static void AssertRuntimeRootSealed(
        string rootPath,
        ExecutionRuntimeReceiptV1 receipt)
    {
        var launcherPath = GetLauncherPath(rootPath, receipt.LauncherPath);
        ExecutionRuntimeRootFileSystem.VerifySealed(rootPath, launcherPath);
        var files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories).ToArray();
        var directories = Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories)
            .Prepend(rootPath)
            .ToArray();
        if (OperatingSystem.IsWindows())
        {
            Assert.All(files, path => Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0));
            Assert.All(directories, path => Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0));
            return;
        }

        AssertUnixRuntimeRootSealed(files, directories, launcherPath);
    }

    [UnsupportedOSPlatform("windows")]
    private static void AssertUnixRuntimeRootSealed(
        IReadOnlyList<string> files,
        IReadOnlyList<string> directories,
        string launcherPath)
    {
        var fileMode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        Assert.All(files, path => Assert.Equal(
            string.Equals(Path.GetFullPath(path), Path.GetFullPath(launcherPath), StringComparison.Ordinal)
                ? fileMode | UnixFileMode.UserExecute
                : fileMode,
            File.GetUnixFileMode(path)));
        var directoryMode = UnixFileMode.UserRead | UnixFileMode.UserExecute
                            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        Assert.All(directories, path => Assert.Equal(directoryMode, File.GetUnixFileMode(path)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed >= TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException("The expected execution-runtime store cleanup did not complete.");
            }
            await Task.Delay(10);
        }
    }

    private static async Task<IExecutionRuntimeProcessLease> ReserveAsync(
        StoreFixture fixture,
        ExecutionRuntimeReceiptV1 receipt,
        string packageId)
    {
        var runtimeRoot = fixture.Layout.GetContentRootPath(receipt.ContentIdentity);
        return await fixture.Store.ReserveProcessLeaseAsync(
            new ExecutionRuntimeLeaseOwner(
                packageId,
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid()),
            receipt.ContentIdentity,
            runtimeRoot,
            GetLauncherPath(runtimeRoot, receipt.LauncherPath),
            CancellationToken.None);
    }

    private static string GetLauncherPath(string runtimeRoot, string launcherPath)
        => Path.Combine([runtimeRoot, .. launcherPath.Split('/')]);

    private static ExecutionRuntimeProcessLeaseV1 CreateAttachedContainment(
        ExecutionRuntimeProcessLeaseV1 reservation,
        string runtimeRoot,
        string launcherPath,
        long birthIdentity)
    {
        var containmentKind = OperatingSystem.IsWindows()
            ? SystemExecutionRuntimeProcessContainmentController.WindowsJobKind
            : SystemExecutionRuntimeProcessContainmentController.UnixProcessGroupKind;
        var containmentIdentity = OperatingSystem.IsWindows()
            ? $@"Local\Sunder.ExecutionRuntime.{reservation.LeaseId}"
            : Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var startRequestedAtUtc = reservation.ReservedAtUtc.AddMilliseconds(100);
        var containmentAttachedAtUtc = reservation.ReservedAtUtc.AddMilliseconds(200);
        return reservation with
        {
            ProcessStartRequestedAtUtc = startRequestedAtUtc,
            ContainmentGeneration = Guid.NewGuid(),
            ContainmentKind = containmentKind,
            ContainmentIdentity = containmentIdentity,
            ContainmentProcessId = Environment.ProcessId,
            ContainmentProcessBirthIdentity =
                ExecutionRuntimeProcessBirthIdentity.CreateSyntheticForCurrentPlatform(birthIdentity),
            ContainmentProcessStartedAtUtc = startRequestedAtUtc,
            ContainmentExecutablePath = Path.GetFullPath(typeof(object).Assembly.Location),
            ContainmentAttachedAtUtc = containmentAttachedAtUtc,
            ProcessId = Environment.ProcessId,
            ProcessBirthIdentity =
                ExecutionRuntimeProcessBirthIdentity.CreateSyntheticForCurrentPlatform(birthIdentity + 1),
            ProcessStartedAtUtc = containmentAttachedAtUtc,
            ExecutablePath = launcherPath,
            RuntimeRootPath = runtimeRoot,
            AttachedAtUtc = reservation.ReservedAtUtc.AddMilliseconds(300),
        };
    }

    private static async Task<ExecutionRuntimeProcessLeaseV1> ReadLeaseAsync(string path)
        => JsonSerializer.Deserialize<ExecutionRuntimeProcessLeaseV1>(
               await File.ReadAllTextAsync(path),
               ExecutionRuntimeStore.JsonOptions)
           ?? throw new InvalidDataException($"Lease '{path}' is empty.");

    private static byte[] CreateTestSignature(ReadOnlySpan<byte> input, ReadOnlySpan<byte> key)
    {
        var bytes = new byte[key.Length + input.Length];
        key.CopyTo(bytes);
        input.CopyTo(bytes.AsSpan(key.Length));
        return SHA512.HashData(bytes);
    }

    private sealed class TestSigner(string keyId, byte[] key) : ISunderExecutionRuntimeArchiveSigner
    {
        public string KeyId { get; } = keyId;

        public byte[] Sign(ReadOnlySpan<byte> signatureInput)
            => CreateTestSignature(signatureInput, key);
    }

    private sealed class TestVerifier : ISunderExecutionRuntimeArchiveSignatureVerifier
    {
        public bool Verify(
            ReadOnlySpan<byte> signatureInput,
            ReadOnlySpan<byte> signature,
            ReadOnlySpan<byte> trustedPublicKey)
            => CryptographicOperations.FixedTimeEquals(
                signature,
                CreateTestSignature(signatureInput, trustedPublicKey));
    }

    private sealed class CountingSignatureVerifier : ISunderExecutionRuntimeArchiveSignatureVerifier
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public bool Verify(
            ReadOnlySpan<byte> signatureInput,
            ReadOnlySpan<byte> signature,
            ReadOnlySpan<byte> trustedPublicKey)
        {
            Interlocked.Increment(ref _count);
            return CryptographicOperations.FixedTimeEquals(
                signature,
                CreateTestSignature(signatureInput, trustedPublicKey));
        }
    }

    private sealed class CountingVerificationIoObserver : IExecutionRuntimeVerificationIoObserver
    {
        private long _payloadBytesRead;
        private long _payloadBytesHashed;

        public long PayloadBytesRead => Interlocked.Read(ref _payloadBytesRead);
        public long PayloadBytesHashed => Interlocked.Read(ref _payloadBytesHashed);

        public void RecordPayloadContent(string relativePath, long bytesRead, long bytesHashed)
        {
            Interlocked.Add(ref _payloadBytesRead, bytesRead);
            Interlocked.Add(ref _payloadBytesHashed, bytesHashed);
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _payloadBytesRead, 0);
            Interlocked.Exchange(ref _payloadBytesHashed, 0);
        }
    }

    private sealed class CallbackFaultInjector(
        ExecutionRuntimeStoreFaultPoint point,
        Action callback) : IExecutionRuntimeStoreFaultInjector
    {
        private int _hit;

        public void Hit(ExecutionRuntimeStoreFaultPoint current)
        {
            if (current == point && Interlocked.Exchange(ref _hit, 1) == 0)
            {
                callback();
            }
        }
    }

    private sealed class ThrowingFaultInjector(ExecutionRuntimeStoreFaultPoint point)
        : IExecutionRuntimeStoreFaultInjector
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

    private sealed class CountingFaultInjector(ExecutionRuntimeStoreFaultPoint point)
        : IExecutionRuntimeStoreFaultInjector
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public void Hit(ExecutionRuntimeStoreFaultPoint current)
        {
            if (current == point)
            {
                Interlocked.Increment(ref _count);
            }
        }
    }

    private sealed class RepeatedIOExceptionFaultInjector(ExecutionRuntimeStoreFaultPoint point)
        : IExecutionRuntimeStoreFaultInjector
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public void Hit(ExecutionRuntimeStoreFaultPoint current)
        {
            if (current != point)
            {
                return;
            }
            Interlocked.Increment(ref _count);
            throw new IOException(
                "Injected sharing violation.",
                unchecked((int)0x80070020));
        }
    }

    private sealed class BlockingFaultInjector(ExecutionRuntimeStoreFaultPoint point)
        : IExecutionRuntimeStoreFaultInjector, IDisposable
    {
        private readonly ManualResetEventSlim _blocked = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);
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

    private sealed class BlockingThrowingFaultInjector(ExecutionRuntimeStoreFaultPoint point)
        : IExecutionRuntimeStoreFaultInjector, IDisposable
    {
        private readonly ManualResetEventSlim _blocked = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private int _hit;

        public void Hit(ExecutionRuntimeStoreFaultPoint current)
        {
            if (current != point || Interlocked.Exchange(ref _hit, 1) != 0)
            {
                return;
            }
            _blocked.Set();
            _release.Wait();
            throw new InvalidOperationException("Injected execution-runtime store failure.");
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

    private sealed class StoreFixture : IDisposable
    {
        private StoreFixture(
            string rootPath,
            string archivePath,
            ExecutionRuntimeStoreLayout layout,
            ExecutionRuntimeAcquisitionPlan plan,
            ExecutionRuntimeHostTrustService trust,
            TestExecutionRuntimeGarbageCollectionAuthority garbageCollectionAuthority,
            ExecutionRuntimeStore store)
        {
            RootPath = rootPath;
            ArchivePath = archivePath;
            Layout = layout;
            Plan = plan;
            Trust = trust;
            GarbageCollectionAuthority = garbageCollectionAuthority;
            Store = store;
        }

        public string RootPath { get; }
        public string ArchivePath { get; }
        public ExecutionRuntimeStoreLayout Layout { get; }
        public ExecutionRuntimeAcquisitionPlan Plan { get; }
        public ExecutionRuntimeHostTrustService Trust { get; }
        public TestExecutionRuntimeGarbageCollectionAuthority GarbageCollectionAuthority { get; }
        public ExecutionRuntimeStore Store { get; }

        public static async Task<StoreFixture> CreateAsync(
            IExecutionRuntimeStoreFaultInjector? faultInjector = null,
            IReadOnlyList<ExecutionRuntimeHostTrustKey>? trustKeys = null,
            TimeProvider? timeProvider = null,
            Guid? hostInstanceId = null,
            IExecutionRuntimeProcessController? processController = null,
            IExecutionRuntimeProcessContainmentController? containmentController = null,
            string runtimeVersion = "10.0.1",
            int packagingRevision = 7,
            string? runtimeIdentifier = null,
            bool developmentTrustEnabled = false)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "SunderExecutionRuntimeStoreTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var artifact = await CreateArtifactAsync(
                root,
                runtimeVersion: runtimeVersion,
                packagingRevision: packagingRevision,
                runtimeIdentifier: runtimeIdentifier);
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            var trust = new ExecutionRuntimeHostTrustService(
                "store-test",
                3,
                developmentTrustEnabled,
                productionKeys: developmentTrustEnabled
                    ? []
                    : trustKeys ?? [new ExecutionRuntimeHostTrustKey(ReleaseKeyId, ReleaseKey, 1)],
                developmentKeys: developmentTrustEnabled
                    ? trustKeys ?? [new ExecutionRuntimeHostTrustKey(ReleaseKeyId, ReleaseKey, 1)]
                    : null);
            var garbageCollectionAuthority = new TestExecutionRuntimeGarbageCollectionAuthority();
            var store = new ExecutionRuntimeStore(
                layout,
                trust,
                garbageCollectionAuthority,
                timeProvider,
                faultInjector: faultInjector,
                signatureVerifier: new TestVerifier(),
                hostInstanceId: hostInstanceId,
                processController: processController,
                containmentController: containmentController);
            var plan = CreatePlan(artifact);
            if (developmentTrustEnabled)
            {
                plan = plan with
                {
                    TrustEnvironment = ExecutionRuntimeTrustEnvironment.Development,
                    AcquisitionSource = RuntimeExecutionRuntimeAcquisitionSources.Development,
                    RegistryOrigin = null,
                };
            }
            return new StoreFixture(
                root,
                artifact.ArchivePath,
                layout,
                plan,
                trust,
                garbageCollectionAuthority,
                store);
        }

        public ExecutionRuntimeStore CreateStore(
            ExecutionRuntimeHostTrustService? trust = null,
            ISunderExecutionRuntimeArchiveSignatureVerifier? signatureVerifier = null,
            TimeProvider? timeProvider = null,
            Guid? hostInstanceId = null,
            IExecutionRuntimeProcessController? processController = null,
            IExecutionRuntimeProcessContainmentController? containmentController = null,
            IExecutionRuntimeStoreFaultInjector? faultInjector = null,
            IExecutionRuntimeVerificationIoObserver? verificationIoObserver = null)
            => new(
                Layout,
                trust ?? Trust,
                GarbageCollectionAuthority,
                timeProvider,
                faultInjector: faultInjector,
                signatureVerifier: signatureVerifier ?? new TestVerifier(),
                hostInstanceId: hostInstanceId,
                processController: processController,
                containmentController: containmentController,
                verificationIoObserver: verificationIoObserver);

        public void Dispose()
        {
            _ = ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(RootPath);
        }
    }

    private sealed record RuntimeArtifact(
        string ArchivePath,
        SunderExecutionRuntimeManifest Manifest,
        string ArtifactSha256,
        long ArtifactSize,
        string ManifestSha256);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class FakeContainmentController : IExecutionRuntimeProcessContainmentController
    {
        public ExecutionRuntimeContainmentProbe ProbeResult { get; set; } =
            ExecutionRuntimeContainmentProbe.Empty;

        public int TerminateCalls { get; private set; }

        public Task<IExecutionRuntimePendingContainedProcess> StartAsync(
            string leaseId,
            Guid generation,
            ProcessStartInfo payloadStartInfo,
            string payloadReportPath,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("This fake only exercises durable containment recovery.");

        public ExecutionRuntimeContainmentProbe Probe(ExecutionRuntimeProcessLeaseV1 lease)
            => ProbeResult;

        public Task<bool> TerminateAndDrainAsync(
            ExecutionRuntimeProcessLeaseV1 lease,
            CancellationToken cancellationToken)
        {
            TerminateCalls++;
            ProbeResult = ExecutionRuntimeContainmentProbe.Empty;
            return Task.FromResult(true);
        }
    }
}
