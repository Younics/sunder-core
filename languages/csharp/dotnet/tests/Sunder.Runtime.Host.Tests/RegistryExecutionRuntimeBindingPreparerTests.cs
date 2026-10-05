using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RegistryExecutionRuntimeBindingPreparerTests
{
    private static readonly DateTimeOffset ObservedAtUtc = DateTimeOffset.Parse("2026-08-28T12:00:00Z");

    [Fact]
    public async Task PrepareAsync_DeduplicatesExactArtifactsAcrossPlansAndUsesReceiptEvidence()
    {
        var clock = new ManualTimeProvider(ObservedAtUtc);
        var calls = new List<(Uri Origin, RegistryResolvedExecutionRuntimeArtifact Artifact)>();
        var artifact = RuntimeArtifact();
        var first = InstallItem("first.package", RuntimeBinding(artifact.Identity));
        var second = InstallItem("second.package", RuntimeBinding(artifact.Identity));
        var retentions = new TrackingRetentionManager();
        var preparer = CreatePreparer(clock, calls, retentions);

        var result = await preparer.PrepareAsync(
            [
                new RegistryPackagePlanResolution(
                    new Uri("https://REGISTRY.example"),
                    Plan([first], [artifact])),
                new RegistryPackagePlanResolution(
                    new Uri("https://registry.example/"),
                    Plan([second], [artifact])),
            ],
            new Dictionary<string, InstalledPackageRecord>(),
            CancellationToken.None);

        Assert.Single(calls);
        Assert.Equal(1, retentions.ActiveCount);
        Assert.Equal("https://registry.example/", calls[0].Origin.AbsoluteUri);
        Assert.Equal(["first.package", "second.package"], result.Bindings.Keys.Order(StringComparer.Ordinal));
        var binding = Assert.Single(result.Bindings["first.package"]);
        Assert.Equal(artifact.Identity.RuntimeId, binding.RuntimeId);
        Assert.Equal(artifact.Identity.Version, binding.RuntimeVersion);
        Assert.Equal(artifact.Identity.PackagingRevision, binding.RuntimePackagingRevision);
        Assert.Equal(artifact.ManifestSha256, binding.RuntimeManifestSha256);
        Assert.Equal(artifact.ArtifactSha256, binding.RuntimeArtifactSha256);
        Assert.Equal(artifact.ArtifactSize, binding.RuntimeArtifactSize);
        Assert.Equal("https://registry.example/", binding.RegistryOrigin);
        Assert.Equal(RuntimeExecutionRuntimeAcquisitionSources.Registry, binding.AcquisitionSource);
        Assert.Equal(artifact.SigningKeyId, binding.SigningKeyId);
        Assert.Equal(7, binding.TrustStoreGeneration);
        Assert.Equal(42, binding.RuntimePolicyRevision);
        Assert.Equal(ExecutionRuntimeLifecycleStates.Supported, binding.RuntimeLifecycleState);
        Assert.Equal(ObservedAtUtc, binding.PolicyObservedAtUtc);
        Assert.Equal(ObservedAtUtc.AddMinutes(1), binding.ResolvedAtUtc);
        await result.DisposeAsync();
        Assert.Equal(0, retentions.ActiveCount);
    }

    [Fact]
    public async Task PrepareAsync_EmitsCorrelatedPlanAndBindPhasesWithExactIdentity()
    {
        var logs = new RecordingLogSink();
        var telemetry = new ExecutionRuntimePhaseTelemetry(
            logs.CreateLogger<ExecutionRuntimePhaseTelemetry>());
        var clock = new ManualTimeProvider(ObservedAtUtc);
        var calls = new List<(Uri Origin, RegistryResolvedExecutionRuntimeArtifact Artifact)>();
        var artifact = RuntimeArtifact();
        var preparer = CreatePreparer(clock, calls, telemetry: telemetry);

        await using var result = await preparer.PrepareAsync(
            [new RegistryPackagePlanResolution(
                new Uri("https://registry.example/"),
                Plan(
                    [InstallItem("telemetry.package", RuntimeBinding(artifact.Identity))],
                    [artifact]))],
            new Dictionary<string, InstalledPackageRecord>(),
            CancellationToken.None);

        var identity = new ExecutionRuntimeIdentity(
            artifact.Identity.RuntimeId,
            artifact.Identity.Version,
            artifact.Identity.PackagingRevision,
            artifact.Identity.Rid,
            artifact.Identity.ModuleAbi);
        var plan = RecordedRuntimePhaseAssertions.Single(logs, ExecutionRuntimePhaseNames.Plan);
        var bind = RecordedRuntimePhaseAssertions.Single(logs, ExecutionRuntimePhaseNames.Bind);
        var correlationId = RecordedRuntimePhaseAssertions.Completed(
            plan,
            ExecutionRuntimePhaseNames.Plan);
        Assert.Equal(correlationId, RecordedRuntimePhaseAssertions.Completed(
            bind,
            ExecutionRuntimePhaseNames.Bind));
        RecordedRuntimePhaseAssertions.Identity(plan, identity);
        RecordedRuntimePhaseAssertions.Identity(bind, identity);
        Assert.Null(plan.Properties["Bytes"]);
        Assert.Null(plan.Properties["CacheHit"]);
        Assert.Null(bind.Properties["Bytes"]);
        Assert.Null(bind.Properties["CacheHit"]);
    }

    [Fact]
    public async Task PrepareAsync_PersistsExactLifecyclePolicyWithEnsuredReceipt()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            nameof(RegistryExecutionRuntimeBindingPreparerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new ManualTimeProvider(ObservedAtUtc);
            var calls = new List<(Uri Origin, RegistryResolvedExecutionRuntimeArtifact Artifact)>();
            var artifact = RuntimeArtifact();
            var cache = new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(Path.Combine(root, "state")));
            await using var result = await CreatePreparer(
                    clock,
                    calls,
                    lifecyclePolicies: cache)
                .PrepareAsync(
                    [new RegistryPackagePlanResolution(
                        new Uri("https://registry.example/"),
                        Plan(
                            [InstallItem("test.package", RuntimeBinding(artifact.Identity))],
                            [artifact]))],
                    new Dictionary<string, InstalledPackageRecord>(),
                    CancellationToken.None);
            await cache.InitializeAsync();
            Assert.Empty(cache.CaptureSnapshot().Entries);
            await result.ObserveLifecyclePoliciesAsync(CancellationToken.None);

            var policy = Assert.Single(cache.CaptureSnapshot().Entries);
            Assert.Equal(artifact.Identity.RuntimeId, policy.Identity.RuntimeId);
            Assert.Equal(artifact.ArtifactSha256, policy.ArtifactSha256);
            Assert.Equal(artifact.ObservedRuntimePolicyRevision, policy.RuntimePolicyRevision);
            Assert.Equal(ExecutionRuntimeLifecycleStates.Supported, policy.Lifecycle.State);
            Assert.False(cache.CaptureSnapshot().AuthorizesCollection(
                CreateReceipt(new Uri("https://registry.example/"), artifact)));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task PrepareAsync_AcceptsAndCachesAuthoritativeDevelopmentReceipt()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            nameof(RegistryExecutionRuntimeBindingPreparerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new ManualTimeProvider(ObservedAtUtc);
            var calls = new List<(Uri Origin, RegistryResolvedExecutionRuntimeArtifact Artifact)>();
            var artifact = RuntimeArtifact();
            var origin = new Uri("https://registry.example/");
            var cache = new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(Path.Combine(root, "state")));

            await using var result = await CreatePreparer(
                    clock,
                    calls,
                    lifecyclePolicies: cache,
                    acquisitionSource: RuntimeExecutionRuntimeAcquisitionSources.Development)
                .PrepareAsync(
                    [new RegistryPackagePlanResolution(
                        origin,
                        Plan(
                            [InstallItem("development.package", RuntimeBinding(artifact.Identity))],
                            [artifact]))],
                    new Dictionary<string, InstalledPackageRecord>(),
                    CancellationToken.None);
            await result.ObserveLifecyclePoliciesAsync(CancellationToken.None);

            var binding = Assert.Single(result.Bindings["development.package"]);
            Assert.Equal(RuntimeExecutionRuntimeAcquisitionSources.Development, binding.AcquisitionSource);
            Assert.Equal(origin.AbsoluteUri, binding.RegistryOrigin);
            var policy = Assert.Single(cache.CaptureSnapshot().Entries);
            Assert.True(policy.MatchesReceipt(CreateReceipt(
                origin,
                artifact,
                RuntimeExecutionRuntimeAcquisitionSources.Development)));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task PrepareAsync_AssociatesExactLocalImportWithoutChangingItsProvenance()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            nameof(RegistryExecutionRuntimeBindingPreparerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new ManualTimeProvider(ObservedAtUtc);
            var artifact = RuntimeArtifact();
            var origin = new Uri("https://registry.example/");
            var localReceipt = CreateReceipt(
                origin,
                artifact,
                RuntimeExecutionRuntimeAcquisitionSources.LocalImport);
            var cache = new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(Path.Combine(root, "state")));
            var preparer = new RegistryExecutionRuntimeBindingPreparer(
                (_, _, _, _) =>
                {
                    clock.Advance(TimeSpan.FromMinutes(1));
                    return Task.FromResult(new ExecutionRuntimeEnsureResult(localReceipt, AlreadyPresent: true));
                },
                new TrackingRetentionManager(),
                clock,
                cache);

            await using var result = await preparer.PrepareAsync(
                [new RegistryPackagePlanResolution(
                    origin,
                    Plan(
                        [InstallItem("test.package", RuntimeBinding(artifact.Identity))],
                        [artifact]))],
                new Dictionary<string, InstalledPackageRecord>(),
                CancellationToken.None);
            await result.ObserveLifecyclePoliciesAsync(CancellationToken.None);

            var binding = Assert.Single(result.Bindings["test.package"]);
            Assert.Equal(RuntimeExecutionRuntimeAcquisitionSources.LocalImport, binding.AcquisitionSource);
            Assert.Equal(origin.AbsoluteUri, binding.RegistryOrigin);
            Assert.Equal(RuntimeExecutionRuntimeAcquisitionSources.LocalImport, localReceipt.AcquisitionSource);
            Assert.Null(localReceipt.RegistryOrigin);
            var policy = Assert.Single(cache.CaptureSnapshot().Entries);
            Assert.Equal(origin.AbsoluteUri, policy.RegistryOrigin);
            Assert.True(policy.MatchesReceipt(localReceipt));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Theory]
    [InlineData("unsuccessful")]
    [InlineData("policy")]
    [InlineData("fingerprint")]
    [InlineData("expired")]
    [InlineData("runtime-conflict")]
    public async Task PrepareAsync_RejectsInvalidPlanHeaderBeforeEnsuring(string mismatch)
    {
        var clock = new ManualTimeProvider(ObservedAtUtc);
        var calls = new List<(Uri, RegistryResolvedExecutionRuntimeArtifact)>();
        var artifact = RuntimeArtifact();
        var plan = Plan(
            [InstallItem("test.package", RuntimeBinding(artifact.Identity))],
            [artifact],
            runtimeConflicts: mismatch == "runtime-conflict" ? [RuntimeConflict()] : []);
        plan = mismatch switch
        {
            "unsuccessful" => plan with { Success = false },
            "policy" => plan with { RuntimePolicyRevision = 0 },
            "fingerprint" => plan with { ResolutionFingerprint = " " },
            "expired" => plan with { ExpiresAtUtc = ObservedAtUtc },
            _ => plan,
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => CreatePreparer(clock, calls).PrepareAsync(
            [new RegistryPackagePlanResolution(new Uri("https://registry.example/"), plan)],
            new Dictionary<string, InstalledPackageRecord>(),
            CancellationToken.None));

        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("policy-revision")]
    [InlineData("missing")]
    [InlineData("unreferenced")]
    [InlineData("conflicting-duplicate")]
    public async Task PrepareAsync_RejectsInvalidArtifactInventoryBeforeEnsuring(string mismatch)
    {
        var clock = new ManualTimeProvider(ObservedAtUtc);
        var calls = new List<(Uri, RegistryResolvedExecutionRuntimeArtifact)>();
        var artifact = RuntimeArtifact();
        var bindings = mismatch == "unreferenced"
            ? Array.Empty<RegistryPackageTargetRuntimeBinding>()
            : [RuntimeBinding(artifact.Identity)];
        IReadOnlyList<RegistryResolvedExecutionRuntimeArtifact> artifacts = mismatch switch
        {
            "policy-revision" => [artifact with { ObservedRuntimePolicyRevision = 43 }],
            "missing" => [],
            "conflicting-duplicate" =>
            [
                artifact,
                artifact with { ArtifactSha256 = new string('f', 64) },
            ],
            _ => [artifact],
        };
        var plan = Plan([InstallItem("test.package", bindings)], artifacts);

        await Assert.ThrowsAsync<InvalidDataException>(() => CreatePreparer(clock, calls).PrepareAsync(
            [new RegistryPackagePlanResolution(new Uri("https://registry.example/"), plan)],
            new Dictionary<string, InstalledPackageRecord>(),
            CancellationToken.None));

        Assert.Empty(calls);
    }

    [Fact]
    public async Task PrepareAsync_RejectsOneExactIdentityFromDifferentRegistryOrigins()
    {
        var clock = new ManualTimeProvider(ObservedAtUtc);
        var calls = new List<(Uri, RegistryResolvedExecutionRuntimeArtifact)>();
        var artifact = RuntimeArtifact();

        await Assert.ThrowsAsync<InvalidDataException>(() => CreatePreparer(clock, calls).PrepareAsync(
            [
                new RegistryPackagePlanResolution(
                    new Uri("https://first.registry.example/"),
                    Plan([InstallItem("first.package", RuntimeBinding(artifact.Identity))], [artifact])),
                new RegistryPackagePlanResolution(
                    new Uri("https://second.registry.example/"),
                    Plan([InstallItem("second.package", RuntimeBinding(artifact.Identity))], [artifact])),
            ],
            new Dictionary<string, InstalledPackageRecord>(),
            CancellationToken.None));

        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("target-requirement")]
    [InlineData("range")]
    [InlineData("rid")]
    [InlineData("duplicate")]
    public async Task PrepareAsync_RejectsInvalidBindingBeforeEnsuring(string mismatch)
    {
        var clock = new ManualTimeProvider(ObservedAtUtc);
        var calls = new List<(Uri, RegistryResolvedExecutionRuntimeArtifact)>();
        var artifact = RuntimeArtifact();
        var requirement = RuntimeRequirement();
        var binding = RuntimeBinding(artifact.Identity);
        RegistryPackageInstallPlanItem item;
        IReadOnlyList<RegistryResolvedExecutionRuntimeArtifact> artifacts = [artifact];
        switch (mismatch)
        {
            case "target-requirement":
                item = InstallItem(
                    "test.package",
                    [binding with { Requirement = requirement with { VersionRange = ">=9.0.0 <10.0.0" } }]);
                break;
            case "range":
                var unsatisfied = requirement with { VersionRange = ">=11.0.0 <12.0.0" };
                item = InstallItem(
                    "test.package",
                    [binding with { Requirement = unsatisfied }],
                    targetRequirement: unsatisfied);
                break;
            case "rid":
                var alternateRid = SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers.First(candidate =>
                    !string.Equals(candidate, CurrentRid, StringComparison.Ordinal));
                var alternateIdentity = artifact.Identity with { Rid = alternateRid };
                var alternateArtifact = artifact with { Identity = alternateIdentity };
                item = InstallItem(
                    "test.package",
                    [RuntimeBinding(alternateIdentity)],
                    targetRid: alternateRid);
                artifacts = [alternateArtifact];
                break;
            default:
                item = InstallItem("test.package", [binding, binding]);
                break;
        }
        var plan = Plan([item], artifacts);

        await Assert.ThrowsAsync<InvalidDataException>(() => CreatePreparer(clock, calls).PrepareAsync(
            [new RegistryPackagePlanResolution(new Uri("https://registry.example/"), plan)],
            new Dictionary<string, InstalledPackageRecord>(),
            CancellationToken.None));

        Assert.Empty(calls);
    }

    [Fact]
    public async Task PrepareAsync_KeepRequiresExactInstalledManifestProjection()
    {
        using var installed = InstalledFixture.Create();
        var clock = new ManualTimeProvider(ObservedAtUtc);
        var calls = new List<(Uri, RegistryResolvedExecutionRuntimeArtifact)>();
        var projected = installed.ProjectedBinding;
        var artifact = RuntimeArtifact(projected.Runtime);
        var keep = KeepItem(installed.Package, projected);

        await using var result = await CreatePreparer(clock, calls).PrepareAsync(
            [new RegistryPackagePlanResolution(
                new Uri("https://registry.example/"),
                Plan([keep], [artifact]))],
            installed.Catalog,
            CancellationToken.None);

        Assert.Single(calls);
        Assert.Equal(
            projected.Runtime.PackagingRevision,
            Assert.Single(result.Bindings[installed.Package.PackageId]).RuntimePackagingRevision);

        calls.Clear();
        var mismatched = KeepItem(
            installed.Package,
            projected with
            {
                Runtime = projected.Runtime with { PackagingRevision = projected.Runtime.PackagingRevision + 1 },
            });
        var mismatchArtifact = RuntimeArtifact(Assert.Single(mismatched.RuntimeBindings).Runtime);

        await Assert.ThrowsAsync<InvalidDataException>(() => CreatePreparer(clock, calls).PrepareAsync(
            [new RegistryPackagePlanResolution(
                new Uri("https://registry.example/"),
                Plan([mismatched], [mismatchArtifact]))],
            installed.Catalog,
            CancellationToken.None));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task PrepareAsync_RuntimeOnlyRebindRequiresExactTransitionAndReturnsNewReceiptBinding()
    {
        using var installed = InstalledFixture.Create();
        var clock = new ManualTimeProvider(ObservedAtUtc);
        var calls = new List<(Uri, RegistryResolvedExecutionRuntimeArtifact)>();
        var previous = installed.ProjectedBinding;
        var next = previous with
        {
            Runtime = previous.Runtime with { PackagingRevision = previous.Runtime.PackagingRevision + 1 },
        };
        var artifact = RuntimeArtifact(next.Runtime);
        var rebind = RebindItem(installed.Package, previous, next);

        await using var result = await CreatePreparer(clock, calls).PrepareAsync(
            [new RegistryPackagePlanResolution(
                new Uri("https://registry.example/"),
                Plan([rebind], [artifact]))],
            installed.Catalog,
            CancellationToken.None);

        Assert.Single(calls);
        Assert.Equal(
            next.Runtime.PackagingRevision,
            Assert.Single(result.Bindings[installed.Package.PackageId]).RuntimePackagingRevision);

        calls.Clear();
        var invalidDetails = rebind.RuntimeOnlyRebind! with
        {
            PackageContentIdentity = new string('0', 64),
        };
        var invalid = new RegistryPackageInstallPlanItem(
            rebind.PackageId,
            rebind.CurrentVersion,
            rebind.Version,
            rebind.PackageContentIdentity,
            rebind.ActionKind,
            rebind.DeprecatedMessage,
            rebind.DependsOn,
            rebind.Targets,
            rebind.Artifacts,
            rebind.RuntimeBindings,
            invalidDetails);

        await Assert.ThrowsAsync<InvalidDataException>(() => CreatePreparer(clock, calls).PrepareAsync(
            [new RegistryPackagePlanResolution(
                new Uri("https://registry.example/"),
                Plan([invalid], [artifact]))],
            installed.Catalog,
            CancellationToken.None));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task PrepareAsync_RejectsNonAcquisitionActionShapeBeforeEnsuring()
    {
        using var installed = InstalledFixture.Create();
        var clock = new ManualTimeProvider(ObservedAtUtc);
        var calls = new List<(Uri, RegistryResolvedExecutionRuntimeArtifact)>();
        var projected = installed.ProjectedBinding;
        var artifact = RuntimeArtifact(projected.Runtime);
        var invalidUpdate = new RegistryPackageInstallPlanItem(
            installed.Package.PackageId,
            installed.Package.Version,
            "2.0.0",
            new string('f', 64),
            RegistryPackageActionKinds.PackageUpdate,
            null,
            [],
            [RuntimeTarget(projected.Requirement)],
            [],
            [projected]);

        await Assert.ThrowsAsync<InvalidDataException>(() => CreatePreparer(clock, calls).PrepareAsync(
            [new RegistryPackagePlanResolution(
                new Uri("https://registry.example/"),
                Plan([invalidUpdate], [artifact]))],
            installed.Catalog,
            CancellationToken.None));

        Assert.Empty(calls);
    }

    private static RegistryExecutionRuntimeBindingPreparer CreatePreparer(
        ManualTimeProvider clock,
        ICollection<(Uri Origin, RegistryResolvedExecutionRuntimeArtifact Artifact)> calls,
        IExecutionRuntimeRetentionManager? retentionManager = null,
        ExecutionRuntimeLifecyclePolicyCache? lifecyclePolicies = null,
        ExecutionRuntimePhaseTelemetry? telemetry = null,
        string acquisitionSource = RuntimeExecutionRuntimeAcquisitionSources.Registry)
        => new(
            (origin, artifact, _, _) =>
            {
                calls.Add((origin, artifact));
                clock.Advance(TimeSpan.FromMinutes(1));
                return Task.FromResult(new ExecutionRuntimeEnsureResult(
                    CreateReceipt(origin, artifact, acquisitionSource),
                    AlreadyPresent: false));
            },
            retentionManager ?? new TrackingRetentionManager(),
            clock,
            lifecyclePolicies,
            telemetry);

    private sealed class TrackingRetentionManager : IExecutionRuntimeRetentionManager
    {
        private int _activeCount;

        public int ActiveCount => Volatile.Read(ref _activeCount);

        public ValueTask<IAsyncDisposable> RetainAsync(
            ExecutionRuntimeRetention retention,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(retention);
            Interlocked.Increment(ref _activeCount);
            return ValueTask.FromResult<IAsyncDisposable>(new Lease(this));
        }

        private sealed class Lease(TrackingRetentionManager owner) : IAsyncDisposable
        {
            private TrackingRetentionManager? _owner = owner;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _owner, null) is { } current)
                {
                    Interlocked.Decrement(ref current._activeCount);
                }
                return ValueTask.CompletedTask;
            }
        }
    }

    private static RegistryResolveInstallPlanResponse Plan(
        IReadOnlyList<RegistryPackageInstallPlanItem> items,
        IReadOnlyList<RegistryResolvedExecutionRuntimeArtifact> artifacts,
        IReadOnlyList<RegistryExecutionRuntimeResolutionConflict>? runtimeConflicts = null)
        => new(
            true,
            items,
            [],
            [],
            [],
            [],
            artifacts,
            runtimeConflicts ?? [],
            "resolution-fingerprint",
            42,
            ObservedAtUtc.AddMinutes(10));

    private static RegistryPackageInstallPlanItem InstallItem(
        string packageId,
        RegistryPackageTargetRuntimeBinding binding)
        => InstallItem(packageId, [binding]);

    private static RegistryPackageInstallPlanItem InstallItem(
        string packageId,
        IReadOnlyList<RegistryPackageTargetRuntimeBinding> bindings,
        RegistryExecutionRuntimeRequirement? targetRequirement = null,
        string? targetRid = null)
        => new(
            packageId,
            null,
            "1.0.0",
            new string('f', 64),
            RegistryPackageActionKinds.PackageInstall,
            null,
            [],
            [RuntimeTarget(targetRequirement ?? RuntimeRequirement(), targetRid)],
            [PackageArtifact()],
            bindings);

    private static RegistryPackageInstallPlanItem KeepItem(
        InstalledPackageRecord package,
        RegistryPackageTargetRuntimeBinding binding)
        => new(
            package.PackageId,
            package.Version,
            package.Version,
            package.Provenance.SourceIdentity!,
            RegistryPackageActionKinds.Keep,
            null,
            [],
            [RuntimeTarget(binding.Requirement)],
            [],
            [binding]);

    private static RegistryPackageInstallPlanItem RebindItem(
        InstalledPackageRecord package,
        RegistryPackageTargetRuntimeBinding previous,
        RegistryPackageTargetRuntimeBinding next)
        => new(
            package.PackageId,
            package.Version,
            package.Version,
            package.Provenance.SourceIdentity!,
            RegistryPackageActionKinds.RuntimeOnlyRebind,
            null,
            [],
            [RuntimeTarget(next.Requirement)],
            [],
            [next],
            new RegistryRuntimeOnlyRebindDetails(
                package.Version,
                package.Provenance.SourceIdentity!,
                [previous],
                [next]));

    private static RegistryPackageTarget RuntimeTarget(
        RegistryExecutionRuntimeRequirement requirement,
        string? rid = null)
        => new(
            SunderPackageFormat.RuntimeHostRole,
            rid ?? CurrentRid,
            SunderPackageFormat.WorkerTargetKind,
            "lib/Test.Worker.dll",
            "net10.0",
            "1.1.0",
            [],
            ExecutionRuntime: requirement);

    private static RegistryPackageTargetRuntimeBinding RuntimeBinding(
        RegistryExecutionRuntimeIdentity identity)
        => new(
            SunderPackageFormat.RuntimeHostRole,
            identity.Rid,
            RuntimeRequirement(),
            identity);

    private static RegistryExecutionRuntimeRequirement RuntimeRequirement()
        => new(
            SunderExecutionRuntimeFormat.DotNetRuntimeId,
            ">=10.0.0 <11.0.0",
            SunderExecutionRuntimeFormat.DotNetModuleAbi);

    private static RegistryResolvedExecutionRuntimeArtifact RuntimeArtifact(
        RegistryExecutionRuntimeIdentity? identity = null)
        => new(
            identity ?? new RegistryExecutionRuntimeIdentity(
                SunderExecutionRuntimeFormat.DotNetRuntimeId,
                "10.0.1",
                2,
                CurrentRid,
                SunderExecutionRuntimeFormat.DotNetModuleAbi),
            new string('a', 64),
            new string('b', 64),
            4096,
            "test.release.v1",
            2,
            new string('c', 64),
            "test.attestation.v1",
            new RegistryExecutionRuntimeLifecycle(
                ExecutionRuntimeLifecycleStates.Supported,
                ObservedAtUtc.AddDays(-2),
                ObservedAtUtc.AddDays(-1),
                DeprecatedAtUtc: null,
                InstallBlockedAtUtc: null,
                ActivationBlockedAtUtc: null,
                RevokedAtUtc: null,
                Message: null),
            42,
            "https://registry.example/artifacts/runtime.sunderruntime");

    private static RegistryPackageProjectionArtifact PackageArtifact()
        => new(
            SunderPackageProjectionFormat.SharedKind,
            null,
            new string('d', 64),
            1024,
            "/artifacts/package.sunderpkg",
            new string('e', 64),
            new string('f', 64),
            new string('1', 64),
            SunderPackageProjectionFormat.CurrentProjectionFormatVersion);

    private static ExecutionRuntimeReceiptV1 CreateReceipt(
        Uri origin,
        RegistryResolvedExecutionRuntimeArtifact artifact,
        string acquisitionSource = RuntimeExecutionRuntimeAcquisitionSources.Registry)
    {
        const string launcherPath = "payload/bin/runtime";
        var launcherSha256 = new string('d', 64);
        return new ExecutionRuntimeReceiptV1(
            ExecutionRuntimeReceiptV1.CurrentSchemaVersion,
            new ExecutionRuntimeIdentity(
                artifact.Identity.RuntimeId,
                artifact.Identity.Version,
                artifact.Identity.PackagingRevision,
                artifact.Identity.Rid,
                artifact.Identity.ModuleAbi),
            SunderExecutionRuntimeFormat.WorkerProtocol,
            artifact.ArtifactSha256,
            artifact.ArtifactSize,
            artifact.ManifestSha256,
            new string('e', 64),
            launcherPath,
            launcherSha256,
            1,
            [SunderExecutionRuntimeFormat.DotNetExecArgument, SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder],
            [new ExecutionRuntimeReceiptContentV1(launcherPath, launcherSha256, 1)],
            artifact.NativeAttestationSha256,
            artifact.NativeAttestationKeyId,
            artifact.SigningKeyId,
            7,
            acquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.Development
                ? ExecutionRuntimeTrustEnvironment.Development
                : ExecutionRuntimeTrustEnvironment.Production,
            acquisitionSource,
            acquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.LocalImport
                ? null
                : origin.AbsoluteUri,
            ObservedAtUtc);
    }

    private static RegistryExecutionRuntimeResolutionConflict RuntimeConflict()
        => new(
            "execution-runtime.test-conflict",
            "test conflict",
            RuntimeRequirement(),
            CurrentRid);

    private static string CurrentRid => PackageTargetSelection.GetCurrentRuntimeIdentifier();

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;
    }

    private sealed class InstalledFixture : IDisposable
    {
        private InstalledFixture(
            string rootPath,
            InstalledPackageRecord package,
            RegistryPackageTargetRuntimeBinding projectedBinding)
        {
            RootPath = rootPath;
            Package = package;
            ProjectedBinding = projectedBinding;
            Catalog = new Dictionary<string, InstalledPackageRecord>(StringComparer.OrdinalIgnoreCase)
            {
                [package.PackageId] = package,
            };
        }

        public string RootPath { get; }
        public InstalledPackageRecord Package { get; }
        public RegistryPackageTargetRuntimeBinding ProjectedBinding { get; }
        public IReadOnlyDictionary<string, InstalledPackageRecord> Catalog { get; }

        public static InstalledFixture Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "sunder-runtime-binding-preparer-tests",
                Guid.NewGuid().ToString("N"));
            var packageRoot = Path.Combine(root, "installed", "installed.package", "1.0.0");
            CanonicalPackageTestBuilder.WriteExplodedPackage(
                packageRoot,
                "installed.package",
                "1.0.0",
                typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
                roles: [SunderPackageFormat.RuntimeHostRole],
                runtimeIdentifiers: [CurrentRid]);
             var package = CanonicalPackageTestBuilder.CreateInstalledRecord(
                 packageRoot,
                 "installed.package",
                 "1.0.0") with
             {
                 Provenance = new InstalledPackageProvenanceRecord(
                     InstalledPackageSourceKind.Registry,
                     InstalledPackageVersionPolicy.FollowTag,
                     "https://registry.example/",
                     "installed.package",
                     RequestedTag: "latest",
                     SourceIdentity: new string('e', 64)),
             };
            var validation = SunderPackageArchiveInspector.ValidateExtractedPackageAsync(packageRoot)
                .GetAwaiter()
                .GetResult();
            var projected = Assert.Single(
                RegistryPackagePlanResolver.ProjectRuntimeBindings(package, validation.Manifest!));
            return new InstalledFixture(root, package, projected);
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
    }
}
