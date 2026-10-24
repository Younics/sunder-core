using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Rpc;
using Xunit;
using Xunit.Abstractions;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class DevPackageOwnerLeaseTests
{
    private readonly ITestOutputHelper _output;

    public DevPackageOwnerLeaseTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task WatchAppPayloadEditsRetainWorkerBirthAndRpcWhilePublishingNewAppSnapshotsAndRejectingInvalidBytes()
    {
        var events = new RuntimeEventStreamService();
        var lifecyclePolicy = new RuntimeLifecyclePolicyOptions();
        await using var operationGate = new RuntimeOperationGate(events, lifecyclePolicy);
        var recoveryQueue = new RuntimePackageRecoveryQueue();
        var launches = new RecordingRoleLaunchResolver();
        var logs = new RecordingLogSink();
        using var policyContext = new ActivePolicyTestContext();
        var packagePolicy = new PackagePolicyAdmission(policyContext.Cache);
        var fixture = CreateFixture(lifecyclePolicy, logger: logs.CreateLogger<RuntimePackageSessionTestHost>(), events: events, operationGate: operationGate,
            recoveryQueue: recoveryQueue, executionRuntimeLaunchResolver: launches, packagePolicy: packagePolicy);
        var recovery = new RuntimePackageRecoveryCoordinator(recoveryQueue, fixture.Host.SessionOwner, operationGate,
            fixture.Host.RpcCatalog, events, new(), lifecyclePolicy, TimeProvider.System,
            NullLogger<RuntimePackageRecoveryCoordinator>.Instance);
        const string packageId = "startup.counted.package";
        const string ownerToken = "owner-token-role-retention-retention";
        var folder = CreateRolePackage(fixture.Paths.RootPath, packageId, workerMode: "v1-retained-recovery");
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync("roles", Mutation(fixture.Host.RuntimeInstanceId, ownerToken, "load", 1, folder, true));
            var original = fixture.Host.ActiveSession.LoadedPackageMap[packageId];
            var originalLaunch = launches.Packages[original.RuntimeActivationId];
            var birth = new RuntimePackageGeneration(original.RuntimeActivationId, original.ActivationSessionGeneration!.Value);
            var workerFolder = original.Source.EffectiveSnapshotFolder;
            Assert.NotNull(original.Source.SnapshotDirectory);
            var sessionFolders = new HashSet<string> { fixture.Host.ActiveSession.SessionFolder! };
            var counters = Path.Combine(original.Worker.WorkspaceRootPath!, "activation-counters.txt");
            var activations = File.ReadAllText(counters);
            var originalProvider = Assert.Single(fixture.Host.RpcCatalog.GetSnapshot().Providers);
            var rpc = new RuntimeRpcClient(fixture.Host.RpcBroker, new(packageId, birth.ActivationId));
            await using var bindings = new SunderRpcServiceBindingLifetime(rpc);
            await bindings.DiscoverAsync(originalProvider.ContractId);
            var issued = bindings.CreateProvider(originalProvider, static (_, _, _) => new object());
            var previous = Assert.Single(fixture.Host.GetActivePackageUiSnapshots());
            var previousSource = original.Source;
            await AssertRoleActivationStateAsync(fixture.Host, birth);

            for (var revision = 1; revision <= 3; revision++)
            {
                await PublishAppEditAsync(revision.ToString(), valid: true);
                var current = fixture.Host.ActiveSession.LoadedPackageMap[packageId];
                Assert.Same(original.Worker, current.Worker);
                Assert.Equal(birth.ActivationId, current.RuntimeActivationId);
                Assert.Equal(birth.SessionGeneration, current.ActivationSessionGeneration);
                Assert.False(current.Worker.GenerationCompletion.IsCompleted);
                Assert.Same(original.RecoveryFactory, current.RecoveryFactory);
                Assert.Equal(activations, File.ReadAllText(counters));
                Assert.True(Directory.Exists(workerFolder));
                Assert.Equal("0", File.ReadAllText(Path.Combine(workerFolder, AppAssetPath)));
                Assert.Same(current.Source, Assert.Single(fixture.Host.GetActiveRuntimePackageSources()));
                Assert.NotEqual(previousSource.ContentIdentity, current.Source.ContentIdentity);
                Assert.Equal(original.Source.DevelopmentWorkerContentIdentity, current.Source.DevelopmentWorkerContentIdentity);
                Assert.Same(original.Source.SnapshotDirectory, current.Source.WorkerSnapshotDirectory);
                Assert.Equal(File.ReadAllBytes(Path.Combine(workerFolder, SunderPackageFormat.ManifestPath)),
                    File.ReadAllBytes(Path.Combine(current.Source.EffectiveSnapshotFolder, SunderPackageFormat.ManifestPath)));
                Assert.NotEqual(previousSource.EffectiveSnapshotFolder, current.Source.EffectiveSnapshotFolder);
                sessionFolders.Add(fixture.Host.ActiveSession.SessionFolder!);
                if (revision > 1) Assert.False(Directory.Exists(previousSource.EffectiveSnapshotFolder));
                using (var lease = fixture.Host.SessionOwner.State.AcquirePackageActivationLease(packageId, birth))
                    Assert.True(lease.Generation > birth.SessionGeneration);
                Assert.Throws<RuntimeUnavailableException>(() => fixture.Host.SessionOwner.State.AcquirePackageActivationLease(packageId,
                    new(birth.ActivationId, fixture.Host.SessionGeneration)));

                var snapshot = Assert.Single(fixture.Host.GetActivePackageUiSnapshots());
                Assert.NotEqual(previous.ContentHash, snapshot.ContentHash);
                Assert.NotEqual(previous.ActivationFingerprint, snapshot.ActivationFingerprint);
                Assert.Equal(current.Source.ContentIdentity, snapshot.PolicySubject!.PackageContentIdentity);
                Assert.Equal(revision.ToString(), ReadAppAsset(fixture.Host, snapshot));
                var provider = Assert.Single(fixture.Host.RpcCatalog.GetSnapshot().Providers);
                Assert.Equal(originalProvider.ActivationId, provider.ActivationId);
                Assert.Equal(originalProvider.ActivationEpoch, provider.ActivationEpoch);
                Assert.Equal(originalProvider.Endpoint, provider.Endpoint);
                Assert.Equal(originalProvider.SessionGeneration, provider.SessionGeneration);
                Assert.False(issued.RetirementToken.IsCancellationRequested);
                await issued.EnsureCurrentAsync();
                await AssertRoleRpcAsync(fixture.Host);
                await AssertRoleCallbackAsync(fixture.Host, birth);
                previous = snapshot;
                previousSource = current.Source;

                if (revision == 2)
                {
                    var generation = fixture.Host.SessionGeneration;
                    var sessionFolder = fixture.Host.ActiveSession.SessionFolder;
                    await PublishAppEditAsync("unindexed", valid: false);
                    Assert.Equal(generation, fixture.Host.SessionGeneration);
                    Assert.Same(current.Worker, fixture.Host.ActiveSession.LoadedPackageMap[packageId].Worker);
                    Assert.Equal(previous.ContentHash, Assert.Single(fixture.Host.GetActivePackageUiSnapshots()).ContentHash);
                    Assert.Equal(sessionFolder, fixture.Host.ActiveSession.SessionFolder);
                    Assert.True(Directory.Exists(previousSource.EffectiveSnapshotFolder));
                    await AssertRoleRpcAsync(fixture.Host);
                }
            }

            // The transferred monitor must fault the current session using the old
            // Worker's birth, even after multiple App publications. Inspect the
            // real queued request before letting the coordinator recover it.
            var latestGeneration = fixture.Host.SessionGeneration;
            var latestSession = fixture.Host.ActiveSession;
            var originalClient = new RuntimeRpcClient(fixture.Host.RpcBroker, new(packageId, birth.ActivationId));
            await using var recoveryEvents = events.Subscribe(events.GetSnapshot().SequenceId);
            WorkerReleaseMetrics.GetProcess(original.Worker).Kill(entireProcessTree: true);
            using var recoveryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var request = await recoveryQueue.Reader.ReadAsync(recoveryDeadline.Token);
            Assert.Same(latestSession, request.Session);
            Assert.Same(previousSource, request.Source);
            Assert.Equal(birth.ActivationId, request.FailedActivationId);
            Assert.Equal(latestGeneration, request.ActivationGeneration);
            Assert.True(request.ActivationGeneration > birth.SessionGeneration);
            // EOF and the process exit monitor race to report an externally killed
            // process. Both must retain the same exact recovery authority.
            Assert.True(request.Failure is EndOfStreamException or ProcessRuntimeWorkerException, request.Failure.ToString());
            _output.WriteLine("Worker termination: {0} ({1})", request.FailureOrigin, request.Failure.GetType().Name);
            await request.CleanupCompletion.WaitAsync(recoveryDeadline.Token);
            Assert.True(Directory.Exists(workerFolder));
            Assert.True(File.Exists(originalLaunch.EntryAssemblyPath));
            Assert.True(Directory.Exists(previousSource.EffectiveSnapshotFolder));
            packagePolicy.Require(originalLaunch.Source);
            packagePolicy.Require(request.Source);
            Assert.Equal(JsonSerializer.Serialize(packagePolicy.Subjects(originalLaunch.Source)),
                JsonSerializer.Serialize(packagePolicy.Subjects(request.Source)));
            recoveryQueue.Enqueue(request);
            await recovery.StartAsync(recoveryDeadline.Token);
            var recoveredSuccessfully = false;
            await foreach (var item in recoveryEvents.Reader.ReadAllAsync(recoveryDeadline.Token))
            {
                if (item.Kind != RuntimeEventKind.PackageRecoverySucceeded || !item.PackageIds.Contains(packageId)) continue;
                Assert.True(item.Success);
                recoveredSuccessfully = true;
                break;
            }
            Assert.True(recoveredSuccessfully);

            var recovered = fixture.Host.ActiveSession.LoadedPackageMap[packageId];
            var recoveryBirth = new RuntimePackageGeneration(recovered.RuntimeActivationId, request.ActivationGeneration);
            Assert.NotSame(original.Worker, recovered.Worker);
            Assert.NotEqual(birth.ActivationId, recoveryBirth.ActivationId);
            Assert.Equal(recoveryBirth.SessionGeneration, recovered.ActivationSessionGeneration);
            Assert.True(fixture.Host.SessionGeneration > recoveryBirth.SessionGeneration);
            Assert.Same(previousSource, recovered.Source);
            Assert.Same(previousSource, Assert.Single(fixture.Host.GetActiveRuntimePackageSources()));
            Assert.Same(original.Source.SnapshotDirectory, recovered.Source.WorkerSnapshotDirectory);
            Assert.Equal(original.RpcManifestSha256, recovered.RpcManifestSha256);
            Assert.Equal(RuntimeRpcPackageMetadata.GetManifestSha256(recovered.Source.ContentIndex!), recovered.RpcManifestSha256);

            // The fresh process still launches the exact original validated bytes;
            // only the published source belongs to the latest App snapshot.
            var recoveredLaunch = launches.Packages[recovered.RuntimeActivationId];
            Assert.Same(originalLaunch.Source, recoveredLaunch.Source);
            Assert.Equal(originalLaunch.Source.ContentIdentity, recoveredLaunch.Source.ContentIdentity);
            Assert.NotEqual(recovered.Source.ContentIdentity, recoveredLaunch.Source.ContentIdentity);
            Assert.Equal(originalLaunch.ManifestSha256, recoveredLaunch.ManifestSha256);
            Assert.Equal(originalLaunch.EntryAssemblyPath, recoveredLaunch.EntryAssemblyPath);
            Assert.Equal(originalLaunch.ShadowFolder, recoveredLaunch.ShadowFolder);
            Assert.Equal(originalLaunch.SessionId, recoveredLaunch.SessionId);
            var validated = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(workerFolder, recoveryDeadline.Token);
            Assert.True(validated.Success, string.Join(" | ", validated.Errors));
            Assert.Equal(activations + activations, File.ReadAllText(counters));
            await AssertRoleActivationStateAsync(fixture.Host, recoveryBirth);
            await AssertRoleCallbackAsync(fixture.Host, recoveryBirth);
            await AssertRoleRpcAsync(fixture.Host);

            var recoveredApp = Assert.Single(fixture.Host.GetActivePackageUiSnapshots());
            Assert.Equal(previous.ContentHash, recoveredApp.ContentHash);
            Assert.Equal(previous.PolicySubject, recoveredApp.PolicySubject);
            Assert.Equal(recovered.Source.ContentIdentity, recoveredApp.PolicySubject!.PackageContentIdentity);
            Assert.Equal("3", ReadAppAsset(fixture.Host, recoveredApp));
            using (var lease = fixture.Host.SessionOwner.State.AcquirePackageActivationLease(packageId, recoveryBirth))
                Assert.Equal(fixture.Host.SessionGeneration, lease.Generation);
            Assert.Throws<RuntimeUnavailableException>(() => fixture.Host.SessionOwner.State.AcquirePackageActivationLease(packageId, birth));
            Assert.Throws<RuntimeUnavailableException>(() => fixture.Host.SessionOwner.State.AcquirePackageActivationLease(packageId,
                new(recoveryBirth.ActivationId, fixture.Host.SessionGeneration)));
            var staleCaller = await Assert.ThrowsAsync<SunderRpcException>(() => originalClient.DiscoverAsync("example.rpc").AsTask());
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, staleCaller.Error.Kind);
            var recoveredClient = new RuntimeRpcClient(fixture.Host.RpcBroker, new(packageId, recoveryBirth.ActivationId));
            var staleEndpoint = await Assert.ThrowsAsync<SunderRpcException>(() => recoveredClient.InvokeAsync(originalProvider.Endpoint,
                "messages", "send", JsonSerializer.SerializeToElement(new { message = "stale endpoint" })).AsTask());
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, staleEndpoint.Error.Kind);

            var generationAfterRecovery = fixture.Host.SessionGeneration;
            foreach (var staleBirth in new[] { birth, new RuntimePackageGeneration(recoveryBirth.ActivationId, birth.SessionGeneration),
                         new RuntimePackageGeneration(recoveryBirth.ActivationId, generationAfterRecovery) })
                fixture.Host.SessionOwner.HandleRuntimeGenerationFault(packageId, staleBirth, new InvalidOperationException("stale fault callback"));
            Assert.Equal(generationAfterRecovery, fixture.Host.SessionGeneration);
            Assert.Same(recovered.Worker, fixture.Host.ActiveSession.LoadedPackageMap[packageId].Worker);

            // Transfer the recovered process once more: the original directory
            // reference must survive even though its loaded Source is now newer.
            await PublishAppEditAsync("4", valid: true);
            sessionFolders.Add(fixture.Host.ActiveSession.SessionFolder!);
            var afterRecoveryEdit = fixture.Host.ActiveSession.LoadedPackageMap[packageId];
            Assert.Same(recovered.Worker, afterRecoveryEdit.Worker);
            Assert.Equal(recoveryBirth.SessionGeneration, afterRecoveryEdit.ActivationSessionGeneration);
            Assert.Same(original.Source.SnapshotDirectory, afterRecoveryEdit.Source.WorkerSnapshotDirectory);
            Assert.False(Directory.Exists(previousSource.EffectiveSnapshotFolder));
            Assert.True(File.Exists(recoveredLaunch.EntryAssemblyPath));
            Assert.Equal("4", ReadAppAsset(fixture.Host, Assert.Single(fixture.Host.GetActivePackageUiSnapshots())));
            await AssertRoleRpcAsync(fixture.Host);
            await AssertRoleCallbackAsync(fixture.Host, recoveryBirth);
            await AssertRoleActivationStateAsync(fixture.Host, recoveryBirth);

            await fixture.Host.ReleaseDevPackageOwnerAsync("roles", new(fixture.Host.RuntimeInstanceId, ownerToken));
            Assert.True(original.Worker.GenerationCompletion.IsCompleted);
            Assert.True(recovered.Worker.GenerationCompletion.IsCompleted);
            Assert.False(Directory.Exists(workerFolder));
            Assert.False(File.Exists(recoveredLaunch.EntryAssemblyPath));
            Assert.All(sessionFolders, path => Assert.False(Directory.Exists(path), $"Leaked session snapshot: {path}"));

            async Task PublishAppEditAsync(string value, bool valid)
            {
                await using var subscription = events.Subscribe(events.GetSnapshot().SequenceId);
                var journal = folder + ".sunder-output-transaction.json";
                File.WriteAllText(journal, "publication in progress");
                File.WriteAllText(Path.Combine(folder, AppAssetPath), value);
                if (valid) CanonicalPackageTestBuilder.WriteContentIndex(folder);
                var started = Stopwatch.GetTimestamp();
                File.Delete(journal);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await foreach (var item in subscription.Reader.ReadAllAsync(deadline.Token))
                {
                    if (item.Kind != RuntimeEventKind.DevReloadCompleted) continue;
                    _output.WriteLine("App publication {0} (valid={1}): {2:F1} ms from journal removal to reload result",
                        value, valid, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    Assert.Equal(valid, item.Success);
                    return;
                }
                Assert.Fail("The watcher did not publish a reload result.");
            }
        }
        finally
        {
            try { await recovery.DisposeAsync(); }
            finally
            {
                await fixture.DisposeAsync();
                foreach (var entry in logs.Entries.TakeLast(150))
                    _output.WriteLine("{0}: {1}{2}", entry.Level, entry.Message, entry.Exception is null ? "" : " | " + entry.Exception);
            }
        }
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("shared")]
    [InlineData("metadata")]
    [InlineData("dependency")]
    public async Task WatchInvalidatesAffectedWorkerClosureAndRetainsUnrelatedWorkers(string change)
    {
        var fixture = CreateFixture();
        const string packageId = "startup.counted.package";
        var dependency = CreatePackageLayout(fixture.Paths.RootPath, "dependency", "dependency.package", "1.0.0").InstallPath;
        var unrelated = CreatePackageLayout(fixture.Paths.RootPath, "unrelated", "unrelated.package", "1.0.0").InstallPath;
        var folder = CreateRolePackage(fixture.Paths.RootPath, packageId, [new("dependency.package", ">=1.0.0 <2.0.0")]);
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync("roles", new(fixture.Host.RuntimeInstanceId,
                "owner-token-role-changes-changes", "load", 1, [new(folder, false), new(dependency, false), new(unrelated, false)]));
            var original = fixture.Host.ActiveSession.LoadedPackageMap[packageId];
            var dependencyWorker = fixture.Host.ActiveSession.LoadedPackageMap["dependency.package"].Worker;
            var unrelatedWorker = fixture.Host.ActiveSession.LoadedPackageMap["unrelated.package"].Worker;
            var app = fixture.Host.GetActivePackageUiSnapshots().Single(value => value.PackageId == packageId);
            var changedFolder = change == "dependency" ? dependency : folder;
            if (change == "metadata")
            {
                var path = Path.Combine(folder, SunderPackageFormat.ManifestPath);
                var manifest = JsonNode.Parse(File.ReadAllText(path))!;
                manifest["summary"] = "Changed package metadata";
                File.WriteAllText(path, manifest.ToJsonString());
            }
            else
            {
                var path = Path.Combine(changedFolder, change == "shared" ? "payload/shared/assets/shared.txt" : RuntimeAssetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "changed effective Worker bytes");
            }
            CanonicalPackageTestBuilder.WriteContentIndex(changedFolder);
            var stage = await fixture.Host.StageWatchedPackageLifecycleAsync(new([change == "dependency" ? "dependency.package" : packageId]));
            Assert.NotNull(stage.StageId);
            var result = await fixture.Host.CommitPackageLifecycleStageAsync(stage.StageId);
            Assert.True(result.Success, string.Join(" | ", result.Errors));
            var current = fixture.Host.ActiveSession.LoadedPackageMap[packageId];
            Assert.NotSame(original.Worker, current.Worker);
            Assert.True(original.Worker.GenerationCompletion.IsCompleted);
            Assert.NotEqual(original.RuntimeActivationId, current.RuntimeActivationId);
            Assert.Same(unrelatedWorker, fixture.Host.ActiveSession.LoadedPackageMap["unrelated.package"].Worker);
            Assert.Equal(change != "dependency", ReferenceEquals(dependencyWorker, fixture.Host.ActiveSession.LoadedPackageMap["dependency.package"].Worker));
            Assert.Throws<RuntimeUnavailableException>(() => fixture.Host.SessionOwner.State.AcquirePackageActivationLease(packageId,
                new(original.RuntimeActivationId, original.ActivationSessionGeneration!.Value)));
            if (change == "worker")
            {
                var currentApp = fixture.Host.GetActivePackageUiSnapshots().Single(value => value.PackageId == packageId);
                Assert.Equal(app.ContentHash, currentApp.ContentHash);
                // App permit/source authority remains full-package exact. The App
                // owner must separately qualify role-local activation continuation.
                Assert.NotEqual(app.PolicySubject, currentApp.PolicySubject);
            }
            await AssertRoleRpcAsync(fixture.Host);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task CanceledWatchedCandidateAfterWorkerCommitRetainsLastGoodAppAndBorrowedWorkerAndCleansSnapshots()
    {
        var fixture = CreateFixture(new RuntimeLifecyclePolicyOptions { PackageRuntimeGenerationActivationAttempts = 1 });
        var folder = CreateRolePackage(fixture.Paths.RootPath, "startup.counted.package");
        var trigger = Path.Combine(fixture.Paths.RootPath, "cancel-version.txt");
        var started = Path.Combine(fixture.Paths.RootPath, "commit-started");
        var second = CreatePackageLayout(fixture.Paths.RootPath, "cancel", "generation.cancelling.package", "1.0.0", trigger, started).InstallPath;
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync("roles", new(fixture.Host.RuntimeInstanceId,
                "owner-token-role-rollback-rollback", "load", 1, [new(folder, false), new(second, false)]));
            var original = fixture.Host.ActiveSession.LoadedPackageMap["startup.counted.package"];
            var previousApp = fixture.Host.GetActivePackageUiSnapshots().Single(value => value.PackageId == original.Source.PackageId);
            File.WriteAllText(Path.Combine(folder, AppAssetPath), "rejected App bytes");
            CanonicalPackageTestBuilder.WriteContentIndex(folder);
            var runtimeAsset = Path.Combine(second, RuntimeAssetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(runtimeAsset)!);
            File.WriteAllText(runtimeAsset, "replacement Worker");
            CanonicalPackageTestBuilder.WriteContentIndex(second);
            var stage = await fixture.Host.StageWatchedPackageLifecycleAsync(new(["startup.counted.package", "generation.cancelling.package"]));
            Assert.NotNull(stage.StageId);
            var candidateFolder = fixture.Host.GetStagedLifecycleSession(stage.StageId)!.SessionFolder!;
            File.WriteAllText(trigger, "1.0.0");
            using var cancellation = new CancellationTokenSource();
            var committed = fixture.Host.CommitPackageLifecycleStageAsync(stage.StageId, cancellation.Token);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (!File.Exists(started) && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
            Assert.True(File.Exists(started), "The candidate did not reach generation commit.");
            // Recovery of the last-good second Worker may now commit normally.
            File.Delete(trigger);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => committed);
            var current = fixture.Host.ActiveSession.LoadedPackageMap[original.Source.PackageId];
            Assert.Same(original.Worker, current.Worker);
            Assert.Equal(original.ActivationSessionGeneration, current.ActivationSessionGeneration);
            Assert.False(current.Worker.GenerationCompletion.IsCompleted);
            Assert.True(Directory.Exists(original.Source.EffectiveSnapshotFolder));
            Assert.False(Directory.Exists(candidateFolder));
            Assert.Equal(previousApp.ContentHash, fixture.Host.GetActivePackageUiSnapshots().Single(value => value.PackageId == original.Source.PackageId).ContentHash);
            Assert.Same(current.Source, fixture.Host.GetActiveRuntimePackageSources().Single(value => value.PackageId == current.Source.PackageId));
            await AssertRoleRpcAsync(fixture.Host);
        }
        finally { await fixture.DisposeAsync(); }
    }

    private static readonly string AppAssetPath = "payload/app/shared/assets/app.txt";
    private static string RuntimeAssetPath => $"payload/runtime/{PackageTargetSelection.GetCurrentRuntimeIdentifier()}/assets/worker.txt";

    private static string CreateRolePackage(string root, string packageId, IReadOnlyList<InstalledPackageDependencyRecord>? dependencies = null,
        string? workerMode = null)
    {
        var folder = Path.Combine(root, "roles", packageId);
        var contract = SunderRpcContractDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes("""
            {"$schema":"https://json-schema.org/draft/2020-12/schema","descriptorVersion":1,"schemaProfile":"sunder.rpc.values.v1",
             "contractId":"example.rpc","version":"1.0.0","errors":{},
             "services":{"messages":{"methods":{"send":{"kind":"unary","idempotency":"none","errors":[],
               "requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Response"}}}}},
             "$defs":{"Request":{"type":"object","properties":{"message":{"type":"string","minLength":1,"maxLength":128}},"required":["message"],"additionalProperties":false},
               "Response":{"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false}}}
            """));
        Directory.CreateDirectory(Path.Combine(folder, "payload/shared/contracts"));
        File.WriteAllBytes(Path.Combine(folder, "payload/shared/contracts/example.rpc.json"), contract.GetCanonicalUtf8());
        CanonicalPackageTestBuilder.WriteExplodedPackage(folder, packageId, "1.0.0", typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            dependencies: dependencies, usesContracts: [new()
            {
                Role = "runtime", ContractId = contract.ContractId, ContractVersion = contract.Version,
                AbiSha256 = contract.AbiSha256, Required = false, Actions = ["discover", "invoke"],
            }], contractBundles: [new()
            {
                Source = "owned", AuthorityKind = "package", AuthorityId = packageId, ContractId = contract.ContractId,
                Version = contract.Version, DescriptorPath = "contracts/example.rpc.json", DescriptorSha256 = contract.Sha256,
                AbiSha256 = contract.AbiSha256, Exposure = "public",
            }]);
        var manifestPath = Path.Combine(folder, SunderPackageFormat.ManifestPath);
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["provides"] = JsonSerializer.SerializeToNode(new[] { new SunderPackageProviderManifest
        {
            Role = "runtime", ProviderId = "watched.provider", ContractId = contract.ContractId,
            ContractVersion = contract.Version, DescriptorSha256 = contract.Sha256, AbiSha256 = contract.AbiSha256, Audience = "public",
        } });
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, AppAssetPath))!);
        File.WriteAllText(Path.Combine(folder, AppAssetPath), "0");
        if (workerMode is not null) File.WriteAllText(Path.Combine(folder, "payload/shared/mode.txt"), workerMode);
        CanonicalPackageTestBuilder.WriteContentIndex(folder);
        return folder;
    }

    private static string ReadAppAsset(RuntimePackageSessionTestHost host, PackageUiSnapshotDescriptor snapshot)
    {
        using var lease = host.AcquireCurrentUiSnapshot(snapshot.SnapshotId);
        Assert.NotNull(lease);
        using var archive = new ZipArchive(lease.Stream, ZipArchiveMode.Read, leaveOpen: true);
        using var reader = new StreamReader(archive.GetEntry("assets/app.txt")!.Open());
        return reader.ReadToEnd();
    }

    private static async Task AssertRoleRpcAsync(RuntimePackageSessionTestHost host)
    {
        var provider = Assert.Single(host.RpcCatalog.GetSnapshot().Providers);
        var package = host.ActiveSession.LoadedPackageMap[provider.PackageId];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = new RuntimeRpcClient(host.RpcBroker, new(provider.PackageId, package.RuntimeActivationId));
        Assert.Equal(provider.Endpoint, Assert.Single((await client.DiscoverAsync("example.rpc", deadline.Token)).Providers).Endpoint);
        var result = await client.InvokeAsync(provider.Endpoint, "messages", "send",
            JsonSerializer.SerializeToElement(new { message = "retained Worker RPC" }), cancellationToken: deadline.Token);
        Assert.True(result.GetProperty("accepted").GetBoolean());
    }

    private static async Task AssertRoleActivationStateAsync(RuntimePackageSessionTestHost host, RuntimePackageGeneration birth)
    {
        var state = await host.GetPackageStateAsync("startup.counted.package", "retention.activation");
        Assert.NotNull(state);
        Assert.NotNull(state.Value);
        using var value = JsonDocument.Parse(state.Value);
        Assert.Equal(birth.ActivationId.ToString("N"), value.RootElement.GetProperty("activationId").GetString());
        Assert.Equal(birth.SessionGeneration, value.RootElement.GetProperty("sessionGeneration").GetInt64());
    }

    private static async Task AssertRoleCallbackAsync(RuntimePackageSessionTestHost host, RuntimePackageGeneration birth)
    {
        const string packageId = "startup.counted.package";
        using var lease = host.SessionOwner.State.AcquirePackageActivationLease(packageId, birth);
        var handler = host.ActiveSession.LoadedPackageMap[packageId].GetCallbackHandler("fixture.callback");
        Assert.NotNull(handler);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sessionId = Guid.NewGuid().ToString("N");
        var started = await handler.StartCallbackAsync(new(sessionId, new Uri("https://example.invalid/callback"), "fixture.callback",
            new Dictionary<string, string>()), deadline.Token);
        Assert.NotNull(started);
        Assert.Equal(packageId, started.PackageId);
        Assert.Equal(sessionId, started.CallbackSessionId);
        var completed = await handler.CompleteCallbackAsync(new(sessionId, new Dictionary<string, string?>()), deadline.Token);
        Assert.Equal(packageId, completed.PackageId);
        Assert.Equal(sessionId, completed.CallbackSessionId);
        Assert.Equal(PackageCallbackCompletionState.Completed, completed.State);
    }

    private sealed class RecordingRoleLaunchResolver : IExecutionRuntimeLaunchResolver, IEphemeralExecutionRuntimeBindingResolver
    {
        internal ConcurrentDictionary<Guid, PreparedRuntimePackage> Packages { get; } = new();

        public Task<ResolvedExecutionRuntimeLaunch> ResolveAsync(PreparedRuntimePackage package, Guid activationId, CancellationToken token)
        {
            Packages[activationId] = package;
            return TestExecutionRuntimeLaunchResolver.Instance.ResolveAsync(package, activationId, token);
        }

        public Task<ResolvedEphemeralExecutionRuntimeBinding> ResolveEphemeralBindingAsync(SunderPackageTargetKey key,
            SunderPackageTargetManifest target, CancellationToken token)
            => TestExecutionRuntimeLaunchResolver.Instance.ResolveEphemeralBindingAsync(key, target, token);
    }
}
