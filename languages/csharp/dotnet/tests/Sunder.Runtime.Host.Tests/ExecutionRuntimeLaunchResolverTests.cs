using System.Security.Cryptography;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ExecutionRuntimeLaunchResolverTests
{
    [Fact]
    public async Task ActivePolicy_RetiredCutoffUsesContinuousLeaseAndKeepsOriginalBindingTime()
    {
        using var fixture = LaunchFixture.Create(nodeStyle: false);
        using var policy = new ActivePolicyTestContext();
        var binding = fixture.Prepared.ExecutionRuntimeBinding! with
        {
            RuntimeLifecycleState = "retired",
            PolicyObservedAtUtc = ActivePolicyTestContext.ObservedAt,
        };
        var subject = PackagePolicyAdmission.RuntimeSubject(binding);
        policy.Response = request => ActivePolicyTestContext.Allowed(request, binding.RuntimePolicyRevision) with
        { Runtimes = [new(subject, "retired", "Allowed", null, ActivePolicyTestContext.ObservedAt.AddMinutes(5), null)] };
        await policy.RefreshAsync([], [], [subject]);
        var resolver = new ExecutionRuntimeLaunchResolver(fixture.Store, lifecyclePolicies: null, policy.Time, activePolicies: policy.Cache);
        policy.Time.UtcNow = ActivePolicyTestContext.ObservedAt.AddYears(5);
        await using var launch = await resolver.ResolveAsync(fixture.Prepared with { ExecutionRuntimeBinding = binding }, Guid.NewGuid(), default);
        Assert.Contains("retired", launch.ActivationWarning!, StringComparison.OrdinalIgnoreCase);
        policy.Clock.Advance(TimeSpan.FromMinutes(5));
        var denied = await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => resolver.ResolveAsync(fixture.Prepared with { ExecutionRuntimeBinding = binding }, Guid.NewGuid(), default));
        Assert.Equal(ExecutionRuntimeErrorCodes.Retired, denied.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolve_UsesExactReceiptRecipeAndWarmVerification(bool nodeStyle)
    {
        using var fixture = LaunchFixture.Create(nodeStyle);

        await using var launch = await fixture.Resolver.ResolveAsync(
            fixture.Prepared,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal(fixture.RuntimeRoot, launch.RuntimeRootPath);
        Assert.Equal(fixture.LauncherPath, launch.LauncherPath);
        Assert.Equal(
            nodeStyle
                ? [fixture.EntryPoint]
                : [SunderExecutionRuntimeFormat.DotNetExecArgument, fixture.EntryPoint],
            launch.ArgumentTokens);
        Assert.Equal(1, fixture.Store.GetReceiptCount);
        Assert.Equal([false], fixture.Store.VerificationDepths);
        Assert.Null(launch.ActivationWarning);
    }

    [Fact]
    public async Task Resolve_DoesNotRehashRuntimeLauncherAfterWarmVerification()
    {
        using var fixture = LaunchFixture.Create(nodeStyle: false);
        var launcherLength = checked((int)new FileInfo(fixture.LauncherPath).Length);
        await File.WriteAllBytesAsync(
            fixture.LauncherPath,
            Enumerable.Repeat((byte)0xA5, launcherLength).ToArray());

        await using var launch = await fixture.Resolver.ResolveAsync(
            fixture.Prepared,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal(fixture.LauncherPath, launch.LauncherPath);
        Assert.Equal([false], fixture.Store.VerificationDepths);
    }

    [Fact]
    public async Task Resolve_WarmHealthFailureBlocksBeforeLeaseReservation()
    {
        using var fixture = LaunchFixture.Create(nodeStyle: false);
        fixture.Store.VerificationResult = new ExecutionRuntimeVerificationResult(
            false,
            false,
            DateTimeOffset.Parse("2026-08-01T11:00:00Z"),
            "The execution runtime health marker is missing.");

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Resolver.ResolveAsync(
                fixture.Prepared,
                Guid.NewGuid(),
                CancellationToken.None));

        Assert.Contains("health marker is missing", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Store.Leases);
        Assert.Equal([false], fixture.Store.VerificationDepths);
    }

    [Fact]
    public async Task Resolve_RejectsReceiptThatDiffersFromCommittedBinding()
    {
        using var fixture = LaunchFixture.Create(nodeStyle: false);
        var mismatch = fixture.Prepared with
        {
            ExecutionRuntimeBinding = fixture.Prepared.ExecutionRuntimeBinding! with
            {
                RuntimeArtifactSha256 = new string('f', 64),
            },
        };

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Resolver.ResolveAsync(mismatch, Guid.NewGuid(), CancellationToken.None));

        Assert.Contains("receipt does not match", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Store.VerificationDepths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolve_RejectsProjectedEntryPointEscapeOrHashMismatch(bool hashMismatch)
    {
        using var fixture = LaunchFixture.Create(nodeStyle: false);
        PreparedRuntimePackage package;
        if (hashMismatch)
        {
            await File.WriteAllTextAsync(fixture.EntryPoint, "changed after preparation");
            package = fixture.Prepared;
        }
        else
        {
            var escaped = Path.Combine(fixture.RootPath, "escaped.dll");
            await File.WriteAllBytesAsync(escaped, fixture.EntryPointBytes);
            package = fixture.Prepared with { EntryAssemblyPath = escaped };
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Resolver.ResolveAsync(package, Guid.NewGuid(), CancellationToken.None));

        Assert.Contains("projected package", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Store.Leases);
        Assert.True(fixture.Store.Leases[0].Released);
    }

    [Fact]
    public async Task Resolve_RejectsLifecycleThatDoesNotPermitActivation()
    {
        using var fixture = LaunchFixture.Create(nodeStyle: false);
        var retired = fixture.Prepared with
        {
            ExecutionRuntimeBinding = fixture.Prepared.ExecutionRuntimeBinding! with
            {
                RuntimeLifecycleState = "retired",
            },
        };

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Resolver.ResolveAsync(retired, Guid.NewGuid(), CancellationToken.None));

        Assert.Contains("does not permit activation", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Store.GetReceiptCount);
    }

    [Fact]
    public async Task Resolve_RejectsMalformedStoredPolicyEvidence()
    {
        using var fixture = LaunchFixture.Create(nodeStyle: false);
        var invalid = fixture.Prepared with
        {
            ExecutionRuntimeBinding = fixture.Prepared.ExecutionRuntimeBinding! with
            {
                RuntimePolicyRevision = 0,
            },
        };

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Resolver.ResolveAsync(invalid, Guid.NewGuid(), CancellationToken.None));

        Assert.Contains("policy evidence", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Store.GetReceiptCount);
        Assert.Empty(fixture.Store.VerificationDepths);
    }

    [Fact]
    public async Task Resolve_CachedRevocationOverridesSupportedInstalledBinding()
    {
        using var fixture = LaunchFixture.Create(nodeStyle: false);
        var cache = new ExecutionRuntimeLifecyclePolicyCache(fixture.Store.Layout);
        var now = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        await cache.ObserveAsync([
            CreatePolicyObservation(
                fixture.Receipt,
                new RegistryExecutionRuntimeLifecycle(
                    ExecutionRuntimeLifecycleStates.Revoked,
                    now.AddDays(-4),
                    now.AddDays(-3),
                    now.AddDays(-2),
                    now.AddDays(-1),
                    now.AddDays(-1),
                    now,
                    "compromised"),
                now),
        ]);
        var resolver = new ExecutionRuntimeLaunchResolver(
            fixture.Store,
            cache,
            new FixedTimeProvider(now));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => resolver.ResolveAsync(fixture.Prepared, Guid.NewGuid(), CancellationToken.None));

        Assert.Contains("revoked", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Store.VerificationDepths);
    }

    [Fact]
    public async Task Resolve_CachedDeprecatedActivationDeadlineBlocksLaunch()
    {
        using var fixture = LaunchFixture.Create(nodeStyle: false);
        var cache = new ExecutionRuntimeLifecyclePolicyCache(fixture.Store.Layout);
        var now = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        await cache.ObserveAsync([
            CreatePolicyObservation(
                fixture.Receipt,
                new RegistryExecutionRuntimeLifecycle(
                    ExecutionRuntimeLifecycleStates.Deprecated,
                    now.AddDays(-4),
                    now.AddDays(-3),
                    now.AddDays(-2),
                    InstallBlockedAtUtc: null,
                    ActivationBlockedAtUtc: now.AddMinutes(-1),
                    RevokedAtUtc: null,
                    Message: null),
                now),
        ]);
        var resolver = new ExecutionRuntimeLaunchResolver(
            fixture.Store,
            cache,
            new FixedTimeProvider(now));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => resolver.ResolveAsync(fixture.Prepared, Guid.NewGuid(), CancellationToken.None));

        Assert.Contains("activation is blocked", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Store.VerificationDepths);
    }

    [Fact]
    public async Task Resolve_CachedDeprecatedBeforeDeadlineReturnsDeterministicActionableWarning()
    {
        const string registryMessage = "registry-controlled advisory must not be surfaced";
        using var fixture = LaunchFixture.Create(nodeStyle: false);
        var cache = new ExecutionRuntimeLifecyclePolicyCache(fixture.Store.Layout);
        var now = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        await cache.ObserveAsync([
            CreatePolicyObservation(
                fixture.Receipt,
                new RegistryExecutionRuntimeLifecycle(
                    ExecutionRuntimeLifecycleStates.Deprecated,
                    now.AddDays(-4),
                    now.AddDays(-3),
                    now.AddDays(-2),
                    InstallBlockedAtUtc: null,
                    ActivationBlockedAtUtc: now.AddDays(1),
                    RevokedAtUtc: null,
                    Message: registryMessage),
                now),
        ]);
        var resolver = new ExecutionRuntimeLaunchResolver(
            fixture.Store,
            cache,
            new FixedTimeProvider(now));

        await using var launch = await resolver.ResolveAsync(
            fixture.Prepared,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal(
            $"Package '{fixture.Prepared.PackageId}' uses deprecated execution runtime "
            + $"'{fixture.Receipt.Identity.RuntimeId}' version '{fixture.Receipt.Identity.Version}' "
            + $"packaging revision {fixture.Receipt.Identity.PackagingRevision} for RID "
            + $"'{fixture.Receipt.Identity.Rid}'. Update the package or rebind it to a supported execution runtime.",
            launch.ActivationWarning);
        Assert.DoesNotContain(registryMessage, launch.ActivationWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_StagedDeprecatedActivationDeadlineBlocksCandidateLaunch()
    {
        using var fixture = LaunchFixture.Create(nodeStyle: false);
        var cache = new ExecutionRuntimeLifecyclePolicyCache(fixture.Store.Layout);
        var now = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        var update = await cache.PrepareUpdateAsync([
            CreatePolicyObservation(
                fixture.Receipt,
                new RegistryExecutionRuntimeLifecycle(
                    ExecutionRuntimeLifecycleStates.Deprecated,
                    now.AddDays(-4),
                    now.AddDays(-3),
                    now.AddDays(-2),
                    InstallBlockedAtUtc: null,
                    ActivationBlockedAtUtc: now,
                    RevokedAtUtc: null,
                    Message: null),
                now),
        ]);
        var candidate = fixture.Prepared with
        {
            ExecutionRuntimePolicyAuthority = Assert.Single(update.Authority.Entries),
        };
        var resolver = new ExecutionRuntimeLaunchResolver(
            fixture.Store,
            cache,
            new FixedTimeProvider(now));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => resolver.ResolveAsync(candidate, Guid.NewGuid(), CancellationToken.None));

        Assert.Contains("activation is blocked", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Store.VerificationDepths);
        Assert.Empty(cache.CaptureSnapshot().Entries);
    }

    [Fact]
    public async Task Resolve_NewerDurableTrustDenialOverridesOlderStagedAuthority()
    {
        using var fixture = LaunchFixture.Create(nodeStyle: false);
        var cache = new ExecutionRuntimeLifecyclePolicyCache(fixture.Store.Layout);
        var now = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        var staged = await cache.PrepareUpdateAsync([
            CreatePolicyObservation(
                fixture.Receipt,
                SupportedLifecycle(now),
                now,
                runtimePolicyRevision: 7),
        ]);
        await cache.ObserveAsync([
            CreatePolicyObservation(
                fixture.Receipt,
                SupportedLifecycle(now),
                now,
                runtimePolicyRevision: 8,
                trustDisposition: ExecutionRuntimeTrustDispositions.Denied),
        ]);
        var candidate = fixture.Prepared with
        {
            ExecutionRuntimePolicyAuthority = Assert.Single(staged.Authority.Entries),
        };
        var resolver = new ExecutionRuntimeLaunchResolver(
            fixture.Store,
            cache,
            new FixedTimeProvider(now));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => resolver.ResolveAsync(candidate, Guid.NewGuid(), CancellationToken.None));

        Assert.Contains("trust policy denies", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Store.VerificationDepths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolve_CachedRetirementHonorsActivationDeadline(bool deadlineElapsed)
    {
        using var fixture = LaunchFixture.Create(nodeStyle: false);
        var cache = new ExecutionRuntimeLifecyclePolicyCache(fixture.Store.Layout);
        var now = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        await cache.ObserveAsync([
            CreatePolicyObservation(
                fixture.Receipt,
                new RegistryExecutionRuntimeLifecycle(
                    ExecutionRuntimeLifecycleStates.Retired,
                    now.AddDays(-4),
                    now.AddDays(-3),
                    now.AddDays(-2),
                    now.AddDays(-1),
                    deadlineElapsed ? now.AddMinutes(-1) : now.AddMinutes(1),
                    RevokedAtUtc: null,
                    Message: "registry-controlled retirement advisory"),
                now),
        ]);
        var resolver = new ExecutionRuntimeLaunchResolver(
            fixture.Store,
            cache,
            new FixedTimeProvider(now));

        if (deadlineElapsed)
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(
                () => resolver.ResolveAsync(fixture.Prepared, Guid.NewGuid(), CancellationToken.None));
            Assert.Contains("deadline", error.Message, StringComparison.Ordinal);
            Assert.Empty(fixture.Store.VerificationDepths);
        }
        else
        {
            await using var launch = await resolver.ResolveAsync(
                fixture.Prepared,
                Guid.NewGuid(),
                CancellationToken.None);
            Assert.Single(fixture.Store.VerificationDepths);
            var warning = Assert.IsType<string>(launch.ActivationWarning);
            Assert.Contains("uses retired execution runtime", warning, StringComparison.Ordinal);
            Assert.Contains("Activation is allowed only until", warning, StringComparison.Ordinal);
            Assert.Contains("Update the package or rebind it", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("registry-controlled retirement advisory", warning, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Resolve_AuthoritativeDevelopmentBindingLaunchesAndHonorsCachedRevocation()
    {
        using var fixture = LaunchFixture.Create(nodeStyle: false, development: true);
        var cache = new ExecutionRuntimeLifecyclePolicyCache(fixture.Store.Layout);
        var now = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        await cache.ObserveAsync([
            CreatePolicyObservation(
                fixture.Receipt,
                SupportedLifecycle(now),
                now,
                runtimePolicyRevision: 7),
        ]);
        var resolver = new ExecutionRuntimeLaunchResolver(
            fixture.Store,
            cache,
            new FixedTimeProvider(now));

        await using (var launch = await resolver.ResolveAsync(
                         fixture.Prepared,
                         Guid.NewGuid(),
                         CancellationToken.None))
        {
            Assert.Equal(fixture.LauncherPath, launch.LauncherPath);
        }

        await cache.ObserveAsync([
            CreatePolicyObservation(
                fixture.Receipt,
                new RegistryExecutionRuntimeLifecycle(
                    ExecutionRuntimeLifecycleStates.Revoked,
                    now.AddDays(-4),
                    now.AddDays(-3),
                    now.AddDays(-2),
                    now.AddDays(-1),
                    now,
                    now,
                    "development runtime revoked"),
                now,
                runtimePolicyRevision: 8),
        ]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => resolver.ResolveAsync(
            fixture.Prepared,
            Guid.NewGuid(),
            CancellationToken.None));
        Assert.Contains("revoked", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_DevelopmentPackageWithoutExplicitEphemeralBindingFailsClosed()
    {
        using var fixture = LaunchFixture.Create(nodeStyle: false);
        var unbound = fixture.Prepared with
        {
            Source = fixture.Prepared.Source with { Kind = PackageSourceKind.Dev },
            ExecutionRuntimeBinding = null,
        };

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Resolver.ResolveAsync(unbound, Guid.NewGuid(), CancellationToken.None));

        Assert.Contains("explicitly supplied ephemeral", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Store.GetReceiptCount);
    }

    private static ExecutionRuntimeLifecyclePolicyObservation CreatePolicyObservation(
        ExecutionRuntimeReceiptV1 receipt,
        RegistryExecutionRuntimeLifecycle lifecycle,
        DateTimeOffset observedAtUtc,
        long runtimePolicyRevision = 8,
        string trustDisposition = ExecutionRuntimeTrustDispositions.Trusted)
        => new(
            receipt,
            receipt.RegistryOrigin!,
            lifecycle,
            RuntimePolicyRevision: runtimePolicyRevision,
            MinimumHostTrustGeneration: 1,
            PolicyObservedAtUtc: observedAtUtc,
            TrustDisposition: trustDisposition);

    private static RegistryExecutionRuntimeLifecycle SupportedLifecycle(DateTimeOffset now)
        => new(
            ExecutionRuntimeLifecycleStates.Supported,
            now.AddDays(-4),
            now.AddDays(-3),
            DeprecatedAtUtc: null,
            InstallBlockedAtUtc: null,
            ActivationBlockedAtUtc: null,
            RevokedAtUtc: null,
            Message: null);

    private sealed class LaunchFixture : IDisposable
    {
        private LaunchFixture(
            string rootPath,
            string runtimeRoot,
            string launcherPath,
            string entryPoint,
            byte[] entryPointBytes,
            PreparedRuntimePackage prepared,
            FakeLaunchStore store)
        {
            RootPath = rootPath;
            RuntimeRoot = runtimeRoot;
            LauncherPath = launcherPath;
            EntryPoint = entryPoint;
            EntryPointBytes = entryPointBytes;
            Prepared = prepared;
            Store = store;
            Receipt = store.Receipt;
            Resolver = new ExecutionRuntimeLaunchResolver(store);
        }

        public string RootPath { get; }
        public string RuntimeRoot { get; }
        public string LauncherPath { get; }
        public string EntryPoint { get; }
        public byte[] EntryPointBytes { get; }
        public PreparedRuntimePackage Prepared { get; }
        public FakeLaunchStore Store { get; }
        public ExecutionRuntimeReceiptV1 Receipt { get; }
        public ExecutionRuntimeLaunchResolver Resolver { get; }

        public static LaunchFixture Create(bool nodeStyle, bool development = false)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "sunder-runtime-launch-resolver-tests",
                Guid.NewGuid().ToString("N"));
            var projection = Path.Combine(root, "projection");
            Directory.CreateDirectory(projection);
            var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
            var runtimeId = nodeStyle
                ? SunderExecutionRuntimeFormat.NodeRuntimeId
                : SunderExecutionRuntimeFormat.DotNetRuntimeId;
            var moduleAbi = nodeStyle
                ? SunderExecutionRuntimeFormat.NodeModuleAbi
                : SunderExecutionRuntimeFormat.DotNetModuleAbi;
            var runtimeVersion = nodeStyle ? "24.18.1" : "10.0.1";
            var versionRange = nodeStyle ? ">=24.18.1 <25.0.0" : ">=10.0.0 <11.0.0";
            var entryPointName = nodeStyle ? "worker.cjs" : "worker.dll";
            var entryPoint = Path.Combine(projection, entryPointName);
            var entryBytes = nodeStyle ? "module worker"u8.ToArray() : "managed worker"u8.ToArray();
            File.WriteAllBytes(entryPoint, entryBytes);
            var physicalEntryPoint = $"payload/{SunderPackageFormat.RuntimeHostRole}/{rid}/{entryPointName}";
            var entrySha256 = Hash(entryBytes);
            var contentIndex = new SunderPackageContentIndex(
                SunderPackageFormat.CurrentContentIndexVersion,
                [new SunderPackageContentIndexEntry(physicalEntryPoint, entrySha256, entryBytes.Length)]);
            var target = new SunderPackageTargetManifest
            {
                Role = SunderPackageFormat.RuntimeHostRole,
                Rid = rid,
                Kind = SunderPackageFormat.WorkerTargetKind,
                EntryPoint = entryPointName,
                TargetFramework = nodeStyle ? "node24" : "net10.0",
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
                    Id = runtimeId,
                    VersionRange = versionRange,
                    ModuleAbi = moduleAbi,
                },
            };
            var manifest = new SunderPackageManifest
            {
                ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion,
                ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
                Id = "launch.test",
                Name = "Launch Test",
                Version = "1.0.0",
                Targets = [target],
                DependsOn = [],
                ContractBundles = [],
                UsesContracts = [],
                Provides = [],
            };
            var key = new SunderPackageTargetKey(SunderPackageFormat.RuntimeHostRole, rid);
            var source = new RuntimePackageSource(
                manifest.Id,
                development ? PackageSourceKind.Dev : PackageSourceKind.Installed,
                projection,
                projection,
                PackageHostRoles.Runtime,
                [],
                new string('c', 64),
                manifest,
                contentIndex,
                key,
                target);
            var identity = new ExecutionRuntimeIdentity(runtimeId, runtimeVersion, 3, rid, moduleAbi);
            var binding = new RuntimePackageExecutionRuntimeBinding(
                key.Role,
                key.Rid,
                identity.RuntimeId,
                identity.Version,
                identity.PackagingRevision,
                identity.ModuleAbi,
                new string('a', 64),
                new string('b', 64),
                4096,
                "https://registry.example/",
                development
                    ? RuntimeExecutionRuntimeAcquisitionSources.Development
                    : RuntimeExecutionRuntimeAcquisitionSources.Registry,
                "sunder.release.test.v1",
                3,
                7,
                "supported",
                DateTimeOffset.Parse("2026-08-01T10:00:00Z"),
                DateTimeOffset.Parse("2026-08-01T10:01:00Z"));
            var prepared = new PreparedRuntimePackage(
                projection,
                source,
                projection,
                manifest.Id,
                manifest.Version,
                PackageHostRoles.Runtime,
                new RuntimePackageActivationState(
                    manifest.Id,
                    manifest.Name,
                    manifest.Version,
                    PackageHostRoles.Runtime,
                    null,
                    key,
                    target),
                entryPoint,
                key,
                target,
                [],
                new Dictionary<string, Sunder.Sdk.Rpc.SunderRpcContractDescriptor>(StringComparer.Ordinal),
                new string('d', 64))
            {
                ExecutionRuntimeBinding = binding,
            };

            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            var contentIdentity = new string('e', 64);
            var runtimeRoot = layout.GetContentRootPath(contentIdentity);
            var launcherRelative = nodeStyle ? "payload/bin/node" : "payload/bin/dotnet";
            var launcherPath = Path.Combine(runtimeRoot, "payload", "bin", nodeStyle ? "node" : "dotnet");
            Directory.CreateDirectory(Path.GetDirectoryName(launcherPath)!);
            var launcherBytes = nodeStyle ? "node launcher"u8.ToArray() : "dotnet launcher"u8.ToArray();
            File.WriteAllBytes(launcherPath, launcherBytes);
            var receipt = new ExecutionRuntimeReceiptV1(
                ExecutionRuntimeReceiptV1.CurrentSchemaVersion,
                identity,
                SunderExecutionRuntimeFormat.WorkerProtocol,
                binding.RuntimeArtifactSha256,
                binding.RuntimeArtifactSize,
                binding.RuntimeManifestSha256,
                contentIdentity,
                launcherRelative,
                Hash(launcherBytes),
                launcherBytes.Length,
                nodeStyle
                    ? [SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder]
                    :
                    [
                        SunderExecutionRuntimeFormat.DotNetExecArgument,
                        SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder,
                    ],
                [new ExecutionRuntimeReceiptContentV1(launcherRelative, Hash(launcherBytes), launcherBytes.Length)],
                new string('f', 64),
                "sunder.ci.test.v1",
                binding.SigningKeyId,
                binding.TrustStoreGeneration,
                development
                    ? ExecutionRuntimeTrustEnvironment.Development
                    : ExecutionRuntimeTrustEnvironment.Production,
                binding.AcquisitionSource,
                "https://registry.example/",
                DateTimeOffset.Parse("2026-08-01T09:00:00Z"));
            var store = new FakeLaunchStore(layout, receipt);
            return new LaunchFixture(root, runtimeRoot, launcherPath, entryPoint, entryBytes, prepared, store);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch
            {
            }
        }

        private static string Hash(ReadOnlySpan<byte> bytes)
            => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class FakeLaunchStore(
        ExecutionRuntimeStoreLayout layout,
        ExecutionRuntimeReceiptV1 receipt) : IExecutionRuntimeLaunchStore
    {
        private readonly List<bool> _verificationDepths = [];
        private int _getReceiptCount;

        public ExecutionRuntimeStoreLayout Layout { get; } = layout;
        public ExecutionRuntimeReceiptV1 Receipt => receipt;
        public int GetReceiptCount => Volatile.Read(ref _getReceiptCount);
        public IReadOnlyList<bool> VerificationDepths => _verificationDepths;
        public List<TestExecutionRuntimeProcessLease> Leases { get; } = [];
        public ExecutionRuntimeVerificationResult VerificationResult { get; set; } = new(
            true,
            false,
            DateTimeOffset.Parse("2026-08-01T11:00:00Z"),
            null);

        public Task<ExecutionRuntimeReceiptV1?> GetReceiptAsync(
            ExecutionRuntimeIdentity identity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _getReceiptCount);
            return Task.FromResult<ExecutionRuntimeReceiptV1?>(Receipt);
        }

        public Task<ExecutionRuntimeVerificationResult> VerifyAsync(
            ExecutionRuntimeIdentity identity,
            bool deep,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _verificationDepths.Add(deep);
            return Task.FromResult(VerificationResult with { DeepVerified = deep });
        }

        public Task<IExecutionRuntimeProcessLease> ReserveProcessLeaseAsync(
            ExecutionRuntimeLeaseOwner owner,
            string contentIdentity,
            string runtimeRootPath,
            string executablePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = new TestExecutionRuntimeProcessLease(runtimeRootPath);
            Leases.Add(lease);
            return Task.FromResult<IExecutionRuntimeProcessLease>(lease);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
