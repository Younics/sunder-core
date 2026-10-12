using System.IO.Compression;
using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Sunder.Package.Format;
using Sunder.Package.Projections;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class ExecutionRuntimeManagementServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-29T12:00:00Z");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnsureAsync_FinalMetadataConfirmationPinsExactRuntimeAndHoldsOperationGate(bool retain)
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var confirming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.BeforeThirdMetadataAsync = async token =>
        {
            confirming.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ensure = fixture.Management.EnsureAsync(fixture.EnsureRequest with { RetainUntilUninstall = retain }, timeout.Token);
        try
        {
            await confirming.Task.WaitAsync(timeout.Token);
            Assert.Empty((await fixture.ManagementState.ReadAsync(timeout.Token)).ManualInstalls);
            using var waiting = new CancellationTokenSource();
            var enter = fixture.Provider.GetRequiredService<RuntimeOperationGate>().EnterAsync(waiting.Token);
            var gateWasHeld = !enter.IsCompleted;
            await waiting.CancelAsync();
            try { await using var unexpected = await enter; }
            catch (OperationCanceledException) { }
            Assert.True(gateWasHeld, "GC must not capture a pre-handoff manual-retention snapshot.");

            // The fixture authority deliberately does not take the operation gate: these
            // sweeps separately exercise the outer exact store pin during final HTTP work.
            for (var pass = 0; pass < 2; pass++)
            {
                var collected = await fixture.Store.CollectGarbageAsync(TimeSpan.Zero, timeout.Token);
                Assert.Equal(0, collected.RootsRemoved);
                Assert.Equal(0, collected.ArchivesRemoved);
            }
            var identity = ExecutionRuntimeIdentity.FromContract(fixture.Artifact.Identity);
            Assert.True((await fixture.Store.VerifyAsync(identity, false, timeout.Token)).Healthy);
            release.TrySetResult();
            var result = await ensure;
            Assert.True(result.Success, fixture.Failure(result));
            Assert.Equal(RuntimeExecutionRuntimeStatuses.Ready, result.Runtime!.Health.Status);
            Assert.Equal(retain, result.Runtime.RetainedUntilUninstall);
            Assert.Equal(retain ? 1 : 0, (await fixture.ManagementState.ReadAsync(timeout.Token)).ManualInstalls.Count);
            Assert.NotNull(await fixture.Store.GetReceiptAsync(identity, timeout.Token));
            var collector = new ExecutionRuntimeStore(fixture.Store.Layout,
                fixture.Provider.GetRequiredService<PackageExecutionRuntimeGarbageCollectionAuthority>(),
                fixture.Provider.GetRequiredService<TimeProvider>());
            await collector.CollectGarbageAsync(TimeSpan.Zero, timeout.Token);
            await collector.CollectGarbageAsync(TimeSpan.Zero, timeout.Token);
            Assert.Equal(retain, await collector.GetReceiptAsync(identity, timeout.Token) is not null);
        }
        finally
        {
            release.TrySetResult();
            await ensure.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task EnsureAsync_UsesOfficialMetadataReconfirmsBeforeAndAfterDownloadAndPersistsManualRetention()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var events = fixture.Provider.GetRequiredService<RuntimeEventStreamService>();
        var sequence = events.GetSnapshot().SequenceId;

        var result = await fixture.Management.EnsureAsync(fixture.EnsureRequest, CancellationToken.None);

        Assert.True(result.Success, fixture.Failure(result));
        Assert.False(result.AlreadyPresent);
        Assert.Equal(["metadata", "metadata", "download", "metadata"], fixture.Handler.Operations);
        Assert.Equal(fixture.Artifact.Identity, result.Runtime!.Identity);
        Assert.Equal(fixture.Artifact.Distribution.MetadataSha256, result.Runtime.InstalledArtifact.Provenance.Distribution.MetadataSha256);
        Assert.Equal(fixture.Artifact.Distribution.MaterializationProfile, result.Runtime.InstalledArtifact.Provenance.Distribution.MaterializationProfile);
        Assert.Equal(fixture.ArchiveSize, result.Runtime.InstalledArtifact.ArchiveSize);
        Assert.True(result.Runtime.RetainedUntilUninstall);

        var state = await fixture.ManagementState.ReadAsync(CancellationToken.None);
        var retained = Assert.Single(state.ManualInstalls);
        Assert.Equal(fixture.Artifact.Identity, retained.Runtime);
        Assert.Equal(fixture.EnsureRequest.Requirement, retained.Requirement);
        Assert.Contains(events.GetSnapshot(sequence).Events, item => item.Kind == RuntimeEventKind.ExecutionRuntimesChanged);
    }

    [Theory]
    [InlineData("metadata-hash")]
    [InlineData("observed-at")]
    public async Task EnsureAsync_AcceptsSameSubjectMetadataAndTimestampChurn(string churn)
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        if (churn == "metadata-hash")
        {
            fixture.Handler.MetadataSuffix = "\n  ";
        }
        else
        {
            fixture.Handler.SupportPhase = "maintenance";
        }

        var result = await fixture.Management.EnsureAsync(fixture.EnsureRequest, CancellationToken.None);

        Assert.True(result.Success, fixture.Failure(result));
        Assert.Equal(fixture.Artifact.Identity, result.Runtime!.Identity);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("digest")]
    public async Task EnsureAsync_RejectsMaterialSelectionDriftBeforeDownload(string drift)
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        fixture.Handler.BeforeSecondMetadata = () =>
        {
            if (drift == "version")
            {
                fixture.Handler.Version = "10.0.2";
                fixture.Handler.ArchiveBytes = fixture.CreateArchiveBytes("10.0.2", 0x43);
            }
            else
            {
                fixture.Handler.ArchiveBytes = fixture.CreateArchiveBytes("10.0.1", 0x44);
            }
        };

        var result = await fixture.Management.EnsureAsync(fixture.EnsureRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.PlanStale, result.ErrorCode);
        Assert.DoesNotContain("download", fixture.Handler.Operations);
        Assert.Empty((await fixture.ManagementState.ReadAsync(CancellationToken.None)).ManualInstalls);
    }

    [Fact]
    public async Task EnsureAsync_RejectsMaterialSelectionDriftAfterDownloadAndLeavesPreparedBytesUnbound()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        fixture.Handler.BeforeThirdMetadata = () =>
        {
            fixture.Handler.Version = "10.0.2";
            fixture.Handler.ArchiveBytes = fixture.CreateArchiveBytes("10.0.2", 0x43);
        };

        var result = await fixture.Management.EnsureAsync(fixture.EnsureRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.PlanStale, result.ErrorCode);
        Assert.Equal(["metadata", "metadata", "download", "metadata"], fixture.Handler.Operations);
        Assert.Empty((await fixture.ManagementState.ReadAsync(CancellationToken.None)).ManualInstalls);
        var prepared = Assert.Single(await fixture.Management.ListAsync(CancellationToken.None));
        Assert.False(prepared.RetainedUntilUninstall);
        Assert.Equal(0, prepared.PackageReferenceCount);
        Assert.Empty(await fixture.InstalledPackages.ListAsync());
    }

    [Fact]
    public async Task EnsureAsync_OfflineFailureNamesRequirementAndRidWithoutImportRecoveryText()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        fixture.Handler.Failure = new HttpRequestException("official metadata offline");

        var result = await fixture.Management.EnsureAsync(fixture.EnsureRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.DownloadFailed, result.ErrorCode);
        Assert.Contains(fixture.EnsureRequest.Requirement.RuntimeId, result.Message, StringComparison.Ordinal);
        Assert.Contains(fixture.EnsureRequest.Requirement.VersionRange, result.Message, StringComparison.Ordinal);
        Assert.Contains(PackageTargetSelection.GetCurrentRuntimeIdentifier(), result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime import", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureAsync_CancellationDuringDownloadDoesNotRetainManualInstall()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        fixture.Handler.BlockNextDownload();
        using var cancellation = new CancellationTokenSource();

        var task = fixture.Management.EnsureAsync(fixture.EnsureRequest, cancellation.Token);
        await fixture.Handler.DownloadEntered.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        Assert.Empty((await fixture.ManagementState.ReadAsync(CancellationToken.None)).ManualInstalls);
        Assert.Empty(await fixture.Management.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAsync_UsesSafeHealthMessageForCorruptInstalledRuntime()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAsync();
        fixture.CorruptInstalledContent(receipt);

        var result = await fixture.Management.VerifyAsync(receipt.Identity.ToContract(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.IntegrityFailed, result.ErrorCode);
        Assert.Equal("Execution runtime failed deep verification and requires repair.", result.Message);
        Assert.Equal(RuntimeExecutionRuntimeStatuses.Unhealthy, result.Runtime!.Health.Status);
        Assert.Equal(SafeMessage(ExecutionRuntimeErrorCodes.IntegrityFailed), result.Runtime.Health.Message);
    }

    [Fact]
    public async Task RepairAsync_UsesCachedOfficialArchiveOfflineAndPreservesPackageBindingProvenance()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackageAsync("repair.package");
        fixture.CorruptInstalledContent(receipt);
        fixture.Handler.Failure = new HttpRequestException("metadata offline");
        var before = Assert.Single(await fixture.Management.GetPackageBindingsAsync("repair.package", CancellationToken.None));
        var impact = await fixture.Management.GetImpactAsync(receipt.Identity.ToContract(), CancellationToken.None);

        var result = await fixture.Management.RepairAsync(receipt.Identity.ToContract(), new(impact.Fingerprint), CancellationToken.None);

        Assert.True(result.Success, fixture.Failure(result));
        Assert.Equal("Execution runtime repaired and dependent packages reloaded.", result.Message);
        var after = Assert.Single(await fixture.Management.GetPackageBindingsAsync("repair.package", CancellationToken.None));
        Assert.Equal(before.Runtime, after.Runtime);
        Assert.Equal(before.InstalledArtifact, after.InstalledArtifact);
    }

    [Fact]
    public async Task RepairAsync_RejectsStaleImpactBeforeMutation()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var receipt = await fixture.EnsureAndBindPackageAsync("repair.package");
        fixture.CorruptInstalledContent(receipt);
        var impact = await fixture.Management.GetImpactAsync(receipt.Identity.ToContract(), CancellationToken.None);
        await fixture.AddBoundPackageAsync("late.package", receipt);

        var result = await fixture.Management.RepairAsync(receipt.Identity.ToContract(), new(impact.Fingerprint), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ExecutionRuntimeErrorCodes.PlanStale, result.ErrorCode);
    }

    [Fact]
    public async Task CollectGarbageAsync_RunsRuntimeAndProjectionCleanupTogether()
    {
        await using var fixture = await ManagementFixture.CreateAsync();

        var result = await fixture.Management.CollectGarbageAsync(CancellationToken.None);

        Assert.True(result.Success, fixture.Failure(result));
        Assert.Contains("Execution runtime GC removed", result.Message, StringComparison.Ordinal);
        Assert.Contains("Package projection GC", result.Message, StringComparison.Ordinal);
    }

    private static string SafeMessage(string code)
        => ExecutionRuntimeErrorCatalog.All.Single(value => value.Code == code).SafeMessage;

    internal sealed class ManagementFixture : IAsyncDisposable
    {
        private ManagementFixture(string root, RuntimePackagePaths paths, ServiceProvider provider, OfficialRuntimeHandler handler,
            RuntimeExecutionRuntimeVendorArtifact artifact, long archiveSize, byte payloadByte)
        {
            Root = root;
            Paths = paths;
            Provider = provider;
            Handler = handler;
            Artifact = artifact;
            ArchiveSize = archiveSize;
            PayloadByte = payloadByte;
            Management = provider.GetRequiredService<ExecutionRuntimeManagementService>();
            Installation = provider.GetRequiredService<ExecutionRuntimeInstallationService>();
            Updates = provider.GetRequiredService<ExecutionRuntimeUpdateService>();
            Prerequisites = provider.GetRequiredService<PackageRuntimePrerequisiteService>();
            ManagementState = provider.GetRequiredService<ExecutionRuntimeManagementState>();
            Store = provider.GetRequiredService<ExecutionRuntimeStore>();
            InstalledPackages = provider.GetRequiredService<InstalledPackageStore>();
            PackageLifecycle = provider.GetRequiredService<InstalledPackageLifecycleService>();
            Projections = provider.GetRequiredService<PackageProjectionCache>();
            Logger = (CapturingLogger<ExecutionRuntimeManagementService>)provider.GetRequiredService<ILogger<ExecutionRuntimeManagementService>>();
            EnsureRequest = new(new RuntimeExecutionRuntimeRequirement(artifact.Identity.RuntimeId, ">=10.0.0 <11.0.0", artifact.Identity.ModuleAbi));
        }

        internal string Root { get; }
        internal RuntimePackagePaths Paths { get; }
        internal ServiceProvider Provider { get; }
        internal OfficialRuntimeHandler Handler { get; }
        internal RuntimeExecutionRuntimeVendorArtifact Artifact { get; }
        internal long ArchiveSize { get; }
        internal byte PayloadByte { get; }
        internal ExecutionRuntimeManagementService Management { get; }
        internal ExecutionRuntimeInstallationService Installation { get; }
        internal ExecutionRuntimeUpdateService Updates { get; }
        internal PackageRuntimePrerequisiteService Prerequisites { get; }
        internal ExecutionRuntimeManagementState ManagementState { get; }
        internal ExecutionRuntimeStore Store { get; }
        internal InstalledPackageStore InstalledPackages { get; }
        internal InstalledPackageLifecycleService PackageLifecycle { get; }
        internal PackageProjectionCache Projections { get; }
        internal CapturingLogger<ExecutionRuntimeManagementService> Logger { get; }
        internal RuntimeExecutionRuntimeEnsureRequest EnsureRequest { get; }

        internal static async Task<ManagementFixture> CreateAsync(string version = "10.0.1", byte payloadByte = 0x42,
            IPackageStoreFaultInjector? packageFault = null, TimeProvider? timeProvider = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "SunderExecutionRuntimeManagementTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
                var archiveBytes = ExecutionRuntimeManagementServiceTests.CreateArchiveBytes(version, rid, payloadByte);
                var artifact = CreateArtifact(version, rid, archiveBytes);
                var handler = new OfficialRuntimeHandler(rid, version, archiveBytes);
                var paths = new RuntimePackagePaths(Path.Combine(root, "state"));
                RuntimeLocalState.EnsureInitialized(paths.RootPath);
                var logger = new CapturingLogger<ExecutionRuntimeManagementService>();
                var services = new ServiceCollection();
                services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("management-test-token"));
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(timeProvider ?? new FixedTimeProvider(Now));
                services.RemoveAll<IPolicyContinuousClock>();
                services.AddSingleton<IPolicyContinuousClock>(new MutablePolicyClock());
                if (packageFault is not null) services.AddSingleton(packageFault);
                services.RemoveAll<ILogger<ExecutionRuntimeManagementService>>();
                services.AddSingleton<ILogger<ExecutionRuntimeManagementService>>(logger);
                services.RemoveAll<IOfficialRuntimePlatformProbe>();
                services.AddSingleton<IOfficialRuntimePlatformProbe, NoopOfficialRuntimePlatformProbe>();
                services.RemoveAll<IExecutionRuntimeGarbageCollectionAuthority>();
                services.AddSingleton<IExecutionRuntimeGarbageCollectionAuthority>(new TestExecutionRuntimeGarbageCollectionAuthority());
                services.RemoveAll<PackageProjectionCache>();
                services.AddSingleton(provider => new PackageProjectionCache(paths.RootPath,
                    new PackageProjectionCacheOptions { GarbageCollectionGracePeriod = TimeSpan.Zero },
                    timeProvider: provider.GetRequiredService<TimeProvider>(),
                    executionRuntimeLeaseRootPath: provider.GetRequiredService<ExecutionRuntimeStoreLayout>().LeaseRootPath));
                services.AddHttpClient(OfficialRuntimeHttpClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
                services.AddSingleton<PackagePolicyHandler>();
                services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<PackagePolicyHandler>());
                var provider = services.BuildServiceProvider();
                await provider.GetRequiredService<ExecutionRuntimeStore>().InitializeAsync();
                return new(root, paths, provider, handler, artifact, archiveBytes.LongLength, payloadByte);
            }
            catch
            {
                _ = ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(root);
                throw;
            }
        }

        internal async Task<ExecutionRuntimeReceiptV1> EnsureAsync()
        {
            var result = await Management.EnsureAsync(EnsureRequest, CancellationToken.None);
            Assert.True(result.Success, Failure(result));
            return await Store.GetReceiptAsync(ExecutionRuntimeIdentity.FromContract(result.Runtime!.Identity), CancellationToken.None)
                   ?? throw new InvalidOperationException("The runtime receipt was not installed.");
        }

        internal async Task<ExecutionRuntimeReceiptV1> EnsureAndBindPackageAsync(string packageId)
        {
            var receipt = await EnsureAsync();
            await AddBoundPackageAsync(packageId, receipt);
            return receipt;
        }

        internal async Task AddBoundPackageAsync(string packageId, ExecutionRuntimeReceiptV1 receipt, bool enabled = false)
        {
            var installPath = Paths.GetInstalledPackagePath(packageId, "1.0.0");
            CanonicalPackageTestBuilder.WriteExplodedPackage(installPath, packageId, "1.0.0",
                typeof(ExecutionRuntimeManagementServiceTests).Assembly.Location,
                roles: [SunderPackageFormat.RuntimeHostRole], runtimeIdentifiers: [receipt.Identity.Rid]);
            var binding = new InstalledPackageExecutionRuntimeBindingRecord(
                SunderPackageFormat.RuntimeHostRole,
                receipt.Identity.Rid,
                EnsureRequest.Requirement,
                receipt.Identity.ToContract(),
                receipt.InstalledArtifact,
                Now);
            var package = CanonicalPackageTestBuilder.CreateInstalledRecord(installPath, packageId, "1.0.0",
                isEnabled: enabled, executionRuntimeBindings: [binding]);
            var packages = (await InstalledPackages.ListAsync(CancellationToken.None)).Append(package).ToArray();
            await InstalledPackages.WriteAsync(packages, CancellationToken.None);
        }

        internal async Task<string> CreatePackageArchiveAsync(string packageId)
        {
            var source = Path.Combine(Root, "package-sources", packageId);
            CanonicalPackageTestBuilder.WriteExplodedPackage(source, packageId, "1.0.0",
                typeof(ExecutionRuntimeManagementServiceTests).Assembly.Location,
                roles: [SunderPackageFormat.RuntimeHostRole], runtimeIdentifiers: [PackageTargetSelection.GetCurrentRuntimeIdentifier()]);
            return await CanonicalPackageTestBuilder.CreateArchiveAsync(source);
        }

        internal async Task UpdatePackageBindingAsync(string packageId, InstalledPackageExecutionRuntimeBindingRecord binding)
        {
            var packages = (await InstalledPackages.ListAsync(CancellationToken.None)).Select(package =>
                string.Equals(package.PackageId, packageId, StringComparison.Ordinal)
                    ? package with { ExecutionRuntimeBindings = [binding] }
                    : package).ToArray();
            await InstalledPackages.WriteAsync(packages, CancellationToken.None);
        }

        internal async Task RefreshPackagePolicyAsync()
        {
            var subjects = (await InstalledPackages.ListAsync()).Select(InstalledPackagePolicyRecord.Subject).ToArray();
            await Provider.GetRequiredService<RegistryPackagePolicySynchronizer>().RefreshAsync(
                [new(ActivePolicyTestContext.Origin, subjects, [])], default);
        }

        internal void CorruptInstalledContent(ExecutionRuntimeReceiptV1 receipt)
        {
            var contentRoot = Store.Layout.GetContentRootPath(receipt.ContentIdentity);
            var file = Directory.EnumerateFiles(contentRoot, "*", SearchOption.AllDirectories)
                .First(path => !Path.GetFileName(path).Equals("receipt.json", StringComparison.OrdinalIgnoreCase));
            ExecutionRuntimeRootFileSystem.MakeOwnedTreeWritable(contentRoot);
            File.WriteAllText(file, "corrupt");
        }

        internal byte[] CreateArchiveBytes(string version, byte payloadByte)
            => ExecutionRuntimeManagementServiceTests.CreateArchiveBytes(version, PackageTargetSelection.GetCurrentRuntimeIdentifier(), payloadByte);

        internal static RuntimeExecutionRuntimeVendorArtifact CreateArtifact(string version, string rid, byte[] archiveBytes)
        {
            var vendorDigest = Convert.ToHexStringLower(SHA512.HashData(archiveBytes));
            var archiveName = OfficialRuntimeSourcePolicy.ArchiveName(ConfiguredOfficialSourceId.DotNet, version, rid);
            var metadata = OfficialRuntimeHandler.CreateDotNetMetadata(rid, version, vendorDigest, "active", "");
            return new(
                new(ConfiguredOfficialSourceId.DotNet, SunderPackageFormat.DotNetExecutionRuntimeId, version, rid,
                    SunderPackageFormat.DotNetWorkerModuleAbi, RuntimeExecutionRuntimeChecksumAlgorithm.Sha512, vendorDigest),
                new($"https://builds.dotnet.microsoft.com/dotnet/Runtime/{version}/{archiveName}",
                    OfficialRuntimeSourcePolicy.MetadataUri(ConfiguredOfficialSourceId.DotNet).AbsoluteUri,
                    Sha256(metadata), null, null,
                    rid.StartsWith("win-", StringComparison.Ordinal) ? RuntimeExecutionRuntimeArchiveFormat.Zip : RuntimeExecutionRuntimeArchiveFormat.TarGzip,
                    OfficialRuntimeSourcePolicy.Profile(ConfiguredOfficialSourceId.DotNet), null),
                new(RuntimeExecutionRuntimeSupportState.Supported, "10.0 LTS", Now, DateTimeOffset.Parse("2028-11-10T00:00:00Z"), null));
        }

        internal string Failure(RuntimeExecutionRuntimeOperationResult result)
            => $"{result.ErrorCode}: {result.Message}. Operations: {string.Join(", ", Handler.Operations)}. Logged: {Logger.LastException}";

        internal string Failure(RuntimeExecutionRuntimeUpdateResult result)
            => $"{result.ErrorCode}: {result.Message}. Operations: {string.Join(", ", Handler.Operations)}.";

        internal string Failure(RuntimeExecutionRuntimeInstallPlan result)
            => $"{result.ErrorCode}: {result.Message}. Operations: {string.Join(", ", Handler.Operations)}.";

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            _ = ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(Root);
        }
    }

    internal sealed class PackagePolicyHandler(TimeProvider clock) : HttpMessageHandler
    {
        internal bool Deny { get; set; }
        internal Sunder.Registry.Contracts.RegistryResolveInstallPlanResponse? Plan { get; set; }
        internal Dictionary<string, RegistryResolveInstallPlanResponse> PlansByOrigin { get; } = new(StringComparer.Ordinal);
        internal IReadOnlyDictionary<string, byte[]> Artifacts { get; set; } = new Dictionary<string, byte[]>();
        internal List<Sunder.Registry.Contracts.RegistryResolvePackageChangesRequest> PlansRequested { get; } = [];
        internal int Downloads { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var origin = request.RequestUri!.GetLeftPart(UriPartial.Authority);
            Assert.True(request.RequestUri.Host == "registry.example" || PlansByOrigin.ContainsKey(origin));
            if (request.Method == HttpMethod.Get && Artifacts.TryGetValue(request.RequestUri.AbsoluteUri, out var bytes))
            {
                Downloads++;
                return new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
            }
            if (request.RequestUri.AbsolutePath == Sunder.Registry.Contracts.RegistryApiRoutes.PackageResolvePackageChanges)
            {
                PlansRequested.Add((await request.Content!.ReadFromJsonAsync<Sunder.Registry.Contracts.RegistryResolvePackageChangesRequest>(token))!);
                return new(HttpStatusCode.OK) { RequestMessage = request, Content = JsonContent.Create(PlansByOrigin.GetValueOrDefault(origin) ?? Plan!) };
            }
            Assert.EndsWith("/policy/synchronize", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            var subjects = await request.Content!.ReadFromJsonAsync<Sunder.Registry.Contracts.RegistryPolicySynchronizationRequest>(token);
            var observedAt = clock.GetUtcNow();
            var response = ActivePolicyTestContext.Allowed(subjects!, Deny ? 2 : 1) with
            { RegistryOrigin = origin, ObservedAtUtc = observedAt, ValidUntilUtc = observedAt.AddHours(24) };
            if (Deny) response = response with { Packages = subjects!.Packages.Select(package =>
                new Sunder.Registry.Contracts.RegistryPackagePolicyDecision(package, "SecurityBlocked", "Listed", "Denied",
                    Sunder.Registry.Contracts.PackagePolicyErrorCodes.SecurityBlocked, "Blocked", [])).ToArray() };
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = JsonContent.Create(response) };
        }
    }

    internal sealed class OfficialRuntimeHandler(string rid, string version, byte[] archiveBytes) : HttpMessageHandler
    {
        private readonly object _sync = new();
        private int _metadataCount;
        private TaskCompletionSource _downloadEntered = CompletedSource();
        private TaskCompletionSource? _downloadBlock;

        internal string Rid { get; } = rid;
        internal string Version { get; set; } = version;
        internal byte[] ArchiveBytes { get; set; } = archiveBytes;
        internal string SupportPhase { get; set; } = "active";
        internal string MetadataSuffix { get; set; } = "";
        internal Exception? Failure { get; set; }
        internal Action? BeforeSecondMetadata { get; set; }
        internal Action? BeforeThirdMetadata { get; set; }
        internal Func<CancellationToken, Task>? BeforeThirdMetadataAsync { get; set; }
        internal List<string> Operations { get; } = [];

        internal Task DownloadEntered
        {
            get
            {
                lock (_sync) return _downloadEntered.Task;
            }
        }

        internal TaskCompletionSource BlockNextDownload()
        {
            lock (_sync)
            {
                _downloadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _downloadBlock = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return _downloadBlock;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            if (request.RequestUri!.AbsoluteUri == OfficialRuntimeSourcePolicy.MetadataUri(ConfiguredOfficialSourceId.DotNet).AbsoluteUri)
            {
                Action? callback = null;
                Func<CancellationToken, Task>? asyncCallback = null;
                lock (_sync)
                {
                    _metadataCount++;
                    if (_metadataCount == 2) callback = BeforeSecondMetadata;
                    if (_metadataCount == 3) callback = BeforeThirdMetadata;
                    if (_metadataCount == 3) asyncCallback = BeforeThirdMetadataAsync;
                }
                callback?.Invoke();
                if (asyncCallback is not null) await asyncCallback(cancellationToken);
                string metadata;
                lock (_sync)
                {
                    Operations.Add("metadata");
                    metadata = CreateDotNetMetadata(Rid, Version, Convert.ToHexStringLower(SHA512.HashData(ArchiveBytes)), SupportPhase, MetadataSuffix);
                }
                return new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(Encoding.UTF8.GetBytes(metadata)) };
            }

            TaskCompletionSource? downloadBlock;
            lock (_sync)
            {
                Operations.Add("download");
                _downloadEntered.TrySetResult();
                downloadBlock = _downloadBlock;
                _downloadBlock = null;
            }
            if (downloadBlock is not null) await downloadBlock.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(ArchiveBytes)
                {
                    Headers = { ContentLength = ArchiveBytes.LongLength },
                },
            };
        }

        internal static string CreateDotNetMetadata(string rid, string version, string sha512, string supportPhase, string suffix)
        {
            var archiveName = OfficialRuntimeSourcePolicy.ArchiveName(ConfiguredOfficialSourceId.DotNet, version, rid);
            var metadataName = $"dotnet-runtime-{rid}" + (rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz");
            var url = $"https://builds.dotnet.microsoft.com/dotnet/Runtime/{version}/{archiveName}";
            return $$"""
                {
                  "channel-version": "10.0",
                  "release-type": "lts",
                  "support-phase": "{{supportPhase}}",
                  "eol-date": "2028-11-10",
                  "releases": [
                    {
                      "runtime": {
                        "version": "{{version}}",
                        "files": [
                          {
                            "rid": "{{rid}}",
                            "name": "{{metadataName}}",
                            "hash": "{{sha512}}",
                            "url": "{{url}}"
                          }
                        ]
                      }
                    }
                  ]
                }
                """ + suffix;
        }

        private static TaskCompletionSource CompletedSource()
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }

    internal static byte[] CreateArchiveBytes(string version, string rid, byte payloadByte)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [rid.StartsWith("win-", StringComparison.Ordinal) ? "dotnet.exe" : "dotnet"] = [0x7f, 0x45, 0x4c, 0x46, payloadByte],
            [$"shared/Microsoft.NETCore.App/{version}/System.Private.CoreLib.dll"] = Enumerable.Repeat(payloadByte, 64).ToArray(),
            ["LICENSE.txt"] = Encoding.UTF8.GetBytes("Synthetic official .NET runtime fixture."),
        };
        using var output = new MemoryStream();
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, bytes) in files)
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                    using var stream = entry.Open();
                    stream.Write(bytes);
                }
            }
        }
        else
        {
            using (var gzip = new GZipStream(output, CompressionLevel.NoCompression, leaveOpen: true))
            using (var tar = new TarWriter(gzip, leaveOpen: true))
            {
                foreach (var (name, bytes) in files)
                {
                    using var stream = new MemoryStream(bytes);
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = stream });
                }
            }
        }
        return output.ToArray();
    }

    internal static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));
    internal static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class NoopOfficialRuntimePlatformProbe : IOfficialRuntimePlatformProbe
    {
        public Task VerifyAsync(RuntimeExecutionRuntimeVendorArtifact artifact, string rootPath, string launcherRelativePath, CancellationToken token)
        {
            _ = artifact;
            token.ThrowIfCancellationRequested();
            var launcher = Path.Combine([rootPath, .. launcherRelativePath.Split('/')]);
            if (!File.Exists(launcher)) throw new FileNotFoundException("Official runtime launcher was not materialized.", launcher);
            return Task.CompletedTask;
        }
    }

    internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public Exception? LastException { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => LastException = exception;
    }
}
