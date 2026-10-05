using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;
using Xunit;
using SdkPackageAuthStatusKind = Sunder.Sdk.Authentication.PackageAuthStatusKind;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed class ProcessRuntimeWorkerTests
{
    [Fact]
    public void WorkerGenerationActivation_DefaultDeadlineIsLifecycleOwned()
    {
        var processPolicy = new RuntimeProcessPolicyOptions();
        var lifecyclePolicy = new RuntimeLifecyclePolicyOptions();

        Assert.Equal(TimeSpan.FromSeconds(10), processPolicy.StartupTimeout);
        Assert.Equal(
            TimeSpan.FromMinutes(2),
            lifecyclePolicy.PackageRuntimeGenerationActivationTimeout);
    }

    [Fact]
    public void WorkerWire_ErrorUsesExactProtocolShape()
    {
        var error = new SunderRpcError(
            SunderRpcErrorKind.Unavailable,
            "rpc.provider.unavailable",
            "The provider is unavailable.",
            SunderRpcDispatchState.PossiblyDispatched,
            SunderRpcRetrySafety.Unsafe);
        using var current = JsonDocument.Parse(JsonSerializer.Serialize(
            WorkerWire.Error(error),
            WorkerJson.Options));

        Assert.Equal(
            ["kind", "code", "message", "dispatchState", "retrySafety"],
            current.RootElement.EnumerateObject().Select(static property => property.Name));
        Assert.Equal("possibly-dispatched", current.RootElement.GetProperty("dispatchState").GetString());
        Assert.Equal("unsafe", current.RootElement.GetProperty("retrySafety").GetString());
    }

    [Fact]
    public void WorkerProtocolIntegerReader_ExecutesSharedSafeIntegerTokenCorpus()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "WorkerV1Conformance", "corpus.json")));
        foreach (var fixture in corpus.RootElement.GetProperty("safeIntegerTokens").EnumerateArray())
        {
            var token = fixture.GetProperty("token").GetString()!;
            using var document = JsonDocument.Parse($"{{\"value\":{token}}}");
            var exception = Record.Exception(() =>
                SunderWorkerProtocol.RequiredNonNegativeInt64(document.RootElement, "value"));
            Assert.Equal(fixture.GetProperty("valid").GetBoolean(), exception is null);
            if (exception is not null)
            {
                Assert.IsType<SunderWorkerProtocolException>(exception);
            }
        }

        using var signed = JsonDocument.Parse("{\"value\":-2147483648}");
        Assert.Equal(int.MinValue, SunderWorkerProtocol.RequiredInt32(signed.RootElement, "value"));
        foreach (var token in new[] { "-0", "1.0", "1e0", "2147483648", "-2147483649" })
        {
            using var document = JsonDocument.Parse($"{{\"value\":{token}}}");
            Assert.Throws<SunderWorkerProtocolException>(() =>
                SunderWorkerProtocol.RequiredInt32(document.RootElement, "value"));
        }
    }

    [Fact]
    public async Task WorkerProtocolReader_UsesAsciiOnlyCollisionFoldingForNestedRpcValues()
    {
        var body = "{\"type\":\"worker.result\",\"id\":\"unicode1\",\"value\":{\"k\":true,\"\u212a\":false,\"\u00e9\":1,\"\u00c9\":2}}"u8.ToArray();
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        using var stream = new MemoryStream(header.Concat(body).ToArray());

        using var document = await SunderWorkerProtocol.ReadFrameAsync(
            stream,
            new RuntimeProcessPolicyOptions(),
            CancellationToken.None);

        var value = document.RootElement.GetProperty("value");
        Assert.Equal(4, value.EnumerateObject().Count());
        Assert.True(value.GetProperty("k").GetBoolean());
        Assert.False(value.GetProperty("\u212a").GetBoolean());
    }

    [Fact]
    public void WorkerEventQueueValueClone_CompactsLexicalPaddingBeforeByteAccounting()
    {
        var padding = new string(' ', 4096);
        using var source = JsonDocument.Parse($"{{{padding}\"value\"{padding}:{padding}1{padding}}}");
        var limits = WorkerTransportLimits.Create(RuntimeResourcePolicies.Standard, maximumHeaderBytes: 8192);

        var queued = SunderWorkerProtocol.CloneValueForQueue(
            source.RootElement,
            limits,
            out var serializedBytes);

        Assert.True(Encoding.UTF8.GetByteCount(source.RootElement.GetRawText()) > serializedBytes);
        Assert.Equal(serializedBytes, Encoding.UTF8.GetByteCount(queued.GetRawText()));
        Assert.Equal(1, queued.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task WorkerTransport_EnforcesNegotiatedValueNodeAndObjectMemberLimits()
    {
        var limits = WorkerTransportLimits.Create(RuntimeResourcePolicies.CustomMinimums, maximumHeaderBytes: 8192);
        var excessiveNodes = $"{{\"type\":\"worker.result\",\"value\":[{string.Join(',', Enumerable.Repeat("null", 16_384))}]}}";
        var excessiveMembers = $"{{\"type\":\"worker.result\",\"value\":{{{string.Join(',', Enumerable.Range(0, 257).Select(static index => $"\"p{index}\":null"))}}}}}";

        await AssertWorkerFrameRejectedAsync(excessiveNodes, limits);
        await AssertWorkerFrameRejectedAsync(excessiveMembers, limits);
        Assert.Throws<WorkerTransportResourceException>(() =>
            SunderWorkerProtocol.SerializeFrameBody(
                new { type = "host.result", value = Enumerable.Repeat<object?>(null, 16_384).ToArray() },
                limits));
        Assert.Throws<WorkerTransportResourceException>(() =>
            SunderWorkerProtocol.SerializeFrameBody(
                new
                {
                    type = "host.result",
                    value = Enumerable.Range(0, 257).ToDictionary(static index => $"p{index}", static _ => (object?)null),
                },
                limits));
    }

    private static async Task AssertWorkerFrameRejectedAsync(string json, WorkerTransportLimits limits)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        using var stream = new MemoryStream(header.Concat(body).ToArray());
        await Assert.ThrowsAsync<WorkerTransportResourceException>(async () =>
            await SunderWorkerProtocol.ReadFrameAsync(stream, limits, CancellationToken.None));
    }

    [Fact]
    public async Task WorkerSdkPackagedExecutable_AcceptsMaximumVersionAndCompletesLifecycle()
    {
        await using var temporary = TemporaryDirectoryScope.Create("sunder-worker-sdk-host-tests");
        var root = temporary.RootPath;
        var catalog = new RuntimeRpcCatalog();
        RuntimeRpcPermissionStore? permissions = null;
        RuntimeContentTransferStore? transfers = null;
        try
        {
            var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
            var errors = new List<string>();
            var prepared = await new PackageSessionPreparer(
                rid,
                ephemeralRuntimeBindingResolver: TestExecutionRuntimeLaunchResolver.Instance).PrepareDevPackageAsync(
                0,
                GetWorkerSdkFixtureOutput(rid),
                Path.Combine(root, "session"),
                errors,
                CancellationToken.None);
            Assert.NotNull(prepared);
            Assert.Empty(errors);
            prepared = prepared with { Version = GetMaximumSemanticVersion() };
            Assert.Equal(256, prepared.Version.Length);
            Assert.Equal(SunderPackageFormat.WorkerTargetKind, prepared.SelectedTarget?.Kind);
            Assert.Contains(
                SunderWorkerProtocol.V1Capability,
                prepared.SelectedTarget?.RequiredHostCapabilities ?? []);

            var paths = new RuntimePackagePaths(Path.Combine(root, "runtime-data"));
            var owner = new RuntimeSessionOwner(
                NullLogger<RuntimeSessionOwner>.Instance,
                new RuntimeEventStreamService(),
                rpcCatalog: catalog);
            permissions = new RuntimeRpcPermissionStore(paths);
            transfers = new RuntimeContentTransferStore(paths);
            var broker = new RuntimeRpcBroker(
                catalog,
                permissions,
                owner.State,
                owner,
                hostStopping: CancellationToken.None,
                policy: null,
                timeProvider: null,
                contentStore: transfers,
                transportPolicy: new RuntimeTransportPolicyOptions());
            var activationId = Guid.NewGuid();
            var context = new RuntimePackageDataResources(
                prepared.PackageId,
                prepared.Version,
                paths.PackageDataRootPath);
            await context.Storage.State.SetValueAsync("legacy.state.v1", "state-value");
            await context.Secrets.SetSecretAsync("legacy.secret.v1", "secret-value");
            var launch = await TestExecutionRuntimeLaunchResolver.Instance.ResolveAsync(
                prepared,
                activationId,
                CancellationToken.None);
            await using var worker = new ProcessRuntimeWorker(
                NullLogger.Instance,
                prepared,
                context,
                activationId,
                broker,
                launch);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await worker.ComposeAsync().WaitAsync(timeout.Token);
            await worker.StartAsync().WaitAsync(timeout.Token);
            var generation = new RuntimePackageGeneration(activationId, 1);
            await worker.CommitGenerationAsync(generation).WaitAsync(timeout.Token);
            await worker.PrepareStorageMigrationsAsync(generation).WaitAsync(timeout.Token);
            await worker.ActivateGenerationAsync().WaitAsync(timeout.Token);
            Assert.Null(await context.Storage.State.GetValueAsync("legacy.state.v1"));
            Assert.Equal("state-value", await context.Storage.State.GetValueAsync("fixture.state.v1"));
            Assert.Null(await context.Secrets.GetSecretAsync("legacy.secret.v1"));
            Assert.Equal("secret-value", await context.Secrets.GetSecretAsync("fixture.secret.v1"));

            var callback = worker.CallbackHandlers["fixture.callback"];
            var callbackStart = await callback.StartCallbackAsync(new PackageCallbackStartContext(
                "callback1",
                new Uri("http://127.0.0.1/callback"),
                parameters: new Dictionary<string, string> { ["message"] = "Start fixture." }));
            Assert.Equal("callback1", callbackStart?.CallbackSessionId);
            Assert.Equal("Start fixture.", callbackStart?.Message);
            var callbackCompletion = await callback.CompleteCallbackAsync(
                new PackageCallbackCompletionContext(
                    "callback1",
                    new Dictionary<string, string?> { ["code"] = "accepted" }));
            Assert.Equal(PackageCallbackCompletionState.Completed, callbackCompletion.State);
            Assert.Equal("accepted", callbackCompletion.Message);

            var auth = Assert.IsAssignableFrom<Sunder.Sdk.Abstractions.IPackageAuthHandler>(worker.AuthHandler);
            Assert.Equal(SdkPackageAuthStatusKind.NotConnected, (await auth.GetStatusAsync()).Status);
            var authStart = await auth.StartAuthorizationAsync(new PackageAuthSessionStartContext(
                "auth1",
                new Uri("http://127.0.0.1/auth/callback")));
            Assert.Equal("auth1", authStart?.AuthSessionId);
            var connected = await auth.CompleteAuthorizationAsync(
                new PackageAuthSessionCompletionContext(
                    "auth1",
                    new Dictionary<string, string?> { ["code"] = "accepted" }));
            Assert.Equal(SdkPackageAuthStatusKind.Connected, connected.Status);
            Assert.Equal(SdkPackageAuthStatusKind.NotConnected, (await auth.DisconnectAsync()).Status);
            await worker.StopAsync().WaitAsync(timeout.Token);
            await worker.GenerationCompletion.WaitAsync(timeout.Token);
        }
        finally
        {
            catalog.Dispose();
            permissions?.Dispose();
            transfers?.Dispose();
        }
    }

    [ExternalArtifactFact("SUNDER_REQUIRE_DOTNET_WORKER_ARTIFACT", "SUNDER_DOTNET_WORKER_ARCHIVE_PATH")]
    [Trait("Category", "ExternalArtifact")]
    public async Task WorkerSdkPackagedExecutable_ExactArchiveInstallsThroughRuntimeHostAndRecordsReleaseEvidence()
    {
        var archivePath = Environment.GetEnvironmentVariable("SUNDER_DOTNET_WORKER_ARCHIVE_PATH");
        Assert.False(string.IsNullOrWhiteSpace(archivePath), "SUNDER_DOTNET_WORKER_ARCHIVE_PATH is required by the selected artifact lane.");
        archivePath = Path.GetFullPath(archivePath!);
        Assert.True(File.Exists(archivePath), $"Native .NET worker archive does not exist: {archivePath}");
        await using var temporary = TemporaryDirectoryScope.Create("sunder-dotnet-worker-release-tests");
        var root = temporary.RootPath;
        var paths = new RuntimePackagePaths(Path.Combine(root, "runtime"));
        var host = new RuntimePackageSessionTestHost(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
            new InstalledPackageStore(paths),
            new SunderPackageArchiveInstaller(paths),
            lifecyclePolicy: new RuntimeLifecyclePolicyOptions
            {
                PackageWorkerStartupTimeout = TimeSpan.FromSeconds(30),
                PackageRuntimeGenerationActivationTimeout = TimeSpan.FromSeconds(30),
            });
        int? workerProcessId = null;
        string? workerTemporaryPath = null;
        try
        {
            await host.InitializeAsync();
            var startup = Stopwatch.StartNew();
            var install = await host.InstallPackageFromRuntimePathAsync(archivePath);
            startup.Stop();
            Assert.True(install.Success, string.Join(Environment.NewLine, install.Errors));
            var installed = Assert.Single(await host.GetInstalledPackagesAsync());
            Assert.Equal("test.worker.v1.executable", installed.PackageId);
            var worker = Assert.IsType<ProcessRuntimeWorker>(
                host.ActiveSession.LoadedPackageMap[installed.PackageId].Worker);
            var process = WorkerReleaseMetrics.GetProcess(worker);
            workerProcessId = process.Id;
            workerTemporaryPath = worker.WorkerTemporaryPath;
            await WorkerReleaseMetrics.RecordAsync(
                "dotnetWorker",
                PackageTargetSelection.GetCurrentRuntimeIdentifier(),
                archivePath,
                WorkerReleaseMetrics.DirectoryBytes(paths.GetInstalledPackagePath(installed.PackageId, installed.Version)),
                startup.Elapsed,
                workerProcessCount: 1,
                process,
                Environment.GetEnvironmentVariable("SUNDER_WORKER_RELEASE_METRICS_PATH"));
        }
        finally
        {
            await host.ShutdownAsync();
            if (workerProcessId is { } processId)
            {
                await Eventually.ObserveAsync(
                    _ => ValueTask.FromResult(IsProcessAlive(processId)),
                    static alive => !alive,
                    TimeSpan.FromSeconds(10),
                    describe: (alive, error) => $"workerPid={processId}; alive={alive}; error={error?.Message}");
                Assert.False(IsProcessAlive(processId), $"Worker process {processId} survived Runtime Host shutdown.");
            }
            if (workerTemporaryPath is not null)
            {
                Assert.False(
                    Directory.Exists(workerTemporaryPath),
                    $"Worker temporary directory survived Runtime Host shutdown: {workerTemporaryPath}");
            }
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public async Task WorkerV1_CannotAcknowledgeBeforeDeclaredStorageMigrationsComplete()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v1-skip-declared-migration");

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.StartAndActivateAsync());
        await Assert.ThrowsAsync<SunderWorkerProtocolException>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WorkerV1_MigrationPreparationAdmitsOnlyHostMediatedStateReads()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v1-migration-preparation-state");
        await fixture.Context.Storage.State.SetValueAsync("legacy.value", "stored");
        await fixture.Worker.StartAsync();
        var generation = new RuntimePackageGeneration(fixture.ActivationId, 1);
        await fixture.Worker.CommitGenerationAsync(generation);

        await fixture.Worker.PrepareStorageMigrationsAsync(generation);

        using var status = await fixture.WaitForWorkerStatusAsync("v1-migration-preparation-state.json");
        Assert.Equal("stored", status.RootElement.GetProperty("stateValue").GetString());
        Assert.Equal(
            ["legacy.value"],
            status.RootElement.GetProperty("keys").EnumerateArray().Select(static item => item.GetString()));
        Assert.Equal("stored", await fixture.Context.Storage.State.GetValueAsync("legacy.value"));
        await fixture.Worker.ActivateGenerationAsync();
    }

    [Fact]
    public async Task WorkerV1_RecoveryCandidateUsesPackageDataUntilExactAuthorityIsRevoked()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v1-recovery-candidate-package-data",
            enforcePackageSessionLease: true);
        await fixture.Context.Storage.State.SetValueAsync("legacy.value", "candidate-value");
        var (request, candidate) = await fixture.BeginRecoveryCandidateAsync();
        var generation = new RuntimePackageGeneration(
            candidate.RuntimeActivationId,
            request.ActivationGeneration);

        await fixture.Worker.StartAsync();
        await fixture.Worker.CommitGenerationAsync(generation);
        await fixture.Worker.PrepareStorageMigrationsAsync(generation);

        using (var candidateStatus = await fixture.WaitForWorkerStatusAsync(
                   "v1-migration-preparation-state.json"))
        {
            Assert.Equal(
                "candidate-value",
                candidateStatus.RootElement.GetProperty("stateValue").GetString());
            Assert.Equal(
                ["legacy.value"],
                candidateStatus.RootElement.GetProperty("keys").EnumerateArray()
                    .Select(static item => item.GetString()));
        }

        await fixture.RevokeRecoveryCandidateAsync(request, candidate);
        await fixture.Worker.ActivateGenerationAsync();

        using var staleStatus = await fixture.WaitForWorkerStatusAsync("v1-stale-package-data.json");
        Assert.Equal("unavailable", staleStatus.RootElement.GetProperty("kind").GetString());
        Assert.Equal(
            "worker.activation.unavailable",
            staleStatus.RootElement.GetProperty("code").GetString());
        Assert.True(staleStatus.RootElement.GetProperty("uploadConsumed").GetBoolean());
        Assert.Equal(0, fixture.WorkerPayloadHandleCount());
    }

    [Fact]
    public async Task WorkerV1_MigrationPreparationRejectsStateMutation()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v1-migration-preparation-mutation");
        await fixture.Worker.StartAsync();
        var generation = new RuntimePackageGeneration(fixture.ActivationId, 1);
        await fixture.Worker.CommitGenerationAsync(generation);

        var preparationFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Worker.PrepareStorageMigrationsAsync(generation));
        Assert.IsType<SunderWorkerProtocolException>(preparationFailure.InnerException);
        await Assert.ThrowsAsync<SunderWorkerProtocolException>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(await fixture.Context.Storage.State.GetValueAsync("legacy.value"));
    }

    [Fact]
    public async Task WorkerV1_MigrationPreparationRequiresExactCommittedGeneration()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        await fixture.Worker.StartAsync();
        var generation = new RuntimePackageGeneration(fixture.ActivationId, 1);
        await fixture.Worker.CommitGenerationAsync(generation);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Worker.PrepareStorageMigrationsAsync(
                new RuntimePackageGeneration(fixture.ActivationId, 2)));

        await fixture.Worker.PrepareStorageMigrationsAsync(generation);
        await fixture.Worker.ActivateGenerationAsync();
    }

    [Fact]
    public async Task WorkerV1_FailedActivationRollsBackHostMediatedStateSecretsAndJournal()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v1-transaction-migration-fail",
            enforcePackageSessionLease: true);
        await fixture.Context.Storage.State.SetValueAsync("legacy.state.v1", "state-value");
        await fixture.Context.Secrets.SetSecretAsync("legacy.secret.v1", "secret-value");

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.PublishCandidateSessionAsync());

        Assert.Equal(0, fixture.Owner.Generation);
        Assert.Equal("state-value", await fixture.Context.Storage.State.GetValueAsync("legacy.state.v1"));
        Assert.Null(await fixture.Context.Storage.State.GetValueAsync("state.current.v1"));
        Assert.Equal("secret-value", await fixture.Context.Secrets.GetSecretAsync("legacy.secret.v1"));
        Assert.Null(await fixture.Context.Secrets.GetSecretAsync("secret.current.v1"));
        foreach (var plan in fixture.Worker.StorageMigrationPlans)
        {
            Assert.False(await fixture.Context.MigrationJournal.IsCompletedAsync(plan));
        }
    }

    [Fact]
    public async Task WorkerV1_SuccessfulActivationCommitsCanonicalMigrationJournal()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v1-transaction-migration",
            enforcePackageSessionLease: true);
        await fixture.Context.Storage.State.SetValueAsync("legacy.state.v1", "state-value");
        await fixture.Context.Secrets.SetSecretAsync("legacy.secret.v1", "secret-value");

        await fixture.PublishCandidateSessionAsync();

        Assert.Equal(1, fixture.Owner.Generation);
        Assert.Null(await fixture.Context.Storage.State.GetValueAsync("legacy.state.v1"));
        Assert.Equal("state-value", await fixture.Context.Storage.State.GetValueAsync("state.current.v1"));
        Assert.Null(await fixture.Context.Secrets.GetSecretAsync("legacy.secret.v1"));
        Assert.Equal("secret-value", await fixture.Context.Secrets.GetSecretAsync("secret.current.v1"));
        var plans = fixture.Worker.StorageMigrationPlans;
        Assert.Equal(["secrets:1.0.0", "state:1.0.0"], plans.Select(static plan => plan.MigrationId));
        Assert.All(plans, static plan => Assert.Matches("^[0-9a-f]{64}$", plan.PlanSha256));
        foreach (var plan in plans)
        {
            Assert.True(await fixture.Context.MigrationJournal.IsCompletedAsync(plan));
        }
    }

    [Fact]
    public async Task WorkerV1_MigrationJournalSkipsCompletedVersionAndRunsAfterVersionJump()
    {
        var sharedDataRoot = Path.Combine(
            Path.GetTempPath(),
            "sunder-worker-migration-journal-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            await using (var first = await WorkerFixture.CreateAsync(
                             "v1-transaction-migration",
                             enforcePackageSessionLease: true,
                             packageDataRoot: sharedDataRoot))
            {
                await first.Context.Storage.State.SetValueAsync("legacy.state.v1", "first-state");
                await first.Context.Secrets.SetSecretAsync("legacy.secret.v1", "first-secret");
                await first.PublishCandidateSessionAsync();
            }

            await using (var sameVersion = await WorkerFixture.CreateAsync(
                             "v1-transaction-migration",
                             enforcePackageSessionLease: true,
                             packageDataRoot: sharedDataRoot))
            {
                await sameVersion.Context.Storage.State.SetValueAsync("legacy.state.v1", "same-version-state");
                await sameVersion.Context.Secrets.SetSecretAsync("legacy.secret.v1", "same-version-secret");
                await sameVersion.PublishCandidateSessionAsync();

                Assert.Equal(
                    "same-version-state",
                    await sameVersion.Context.Storage.State.GetValueAsync("legacy.state.v1"));
                Assert.Equal(
                    "first-state",
                    await sameVersion.Context.Storage.State.GetValueAsync("state.current.v1"));
                Assert.Equal(
                    "same-version-secret",
                    await sameVersion.Context.Secrets.GetSecretAsync("legacy.secret.v1"));
                Assert.Equal(
                    "first-secret",
                    await sameVersion.Context.Secrets.GetSecretAsync("secret.current.v1"));
            }

            await using (var jumpedVersion = await WorkerFixture.CreateAsync(
                             "v1-transaction-migration",
                             enforcePackageSessionLease: true,
                             packageVersion: "3.0.0",
                             packageDataRoot: sharedDataRoot))
            {
                await jumpedVersion.Context.Storage.State.SetValueAsync("legacy.state.v1", "first-state");
                await jumpedVersion.Context.Secrets.SetSecretAsync("legacy.secret.v1", "first-secret");
                await jumpedVersion.PublishCandidateSessionAsync();

                Assert.Null(await jumpedVersion.Context.Storage.State.GetValueAsync("legacy.state.v1"));
                Assert.Null(await jumpedVersion.Context.Secrets.GetSecretAsync("legacy.secret.v1"));
                Assert.Contains(
                    jumpedVersion.Worker.StorageMigrationPlans,
                    static plan => plan.MigrationId == "state:3.0.0");
                Assert.Contains(
                    jumpedVersion.Worker.StorageMigrationPlans,
                    static plan => plan.MigrationId == "secrets:3.0.0");
            }
        }
        finally
        {
            if (Directory.Exists(sharedDataRoot)) Directory.Delete(sharedDataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Worker_HandshakeInvokeStreamCancellationAndShutdown()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        await fixture.StartAndActivateAsync();

        var result = await fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" }),
            CancellationToken.None);
        Assert.True(result.GetProperty("accepted").GetBoolean());

        var events = new List<JsonElement>();
        await foreach (var item in fixture.Worker.InvokeServerStreamAsync(
                           "example.provider",
                           fixture.InvocationContext(),
                           "messages",
                           "watch",
                           JsonSerializer.SerializeToElement(new { message = "hello" }),
                           CancellationToken.None))
        {
            events.Add(item);
        }
        Assert.Equal([1, 2], events.Select(item => item.GetProperty("sequence").GetInt32()));

        using var cancellation = new CancellationTokenSource();
        var call = fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "wait",
            JsonSerializer.SerializeToElement(new { message = "wait" }),
            cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);

        await Task.WhenAll(fixture.Worker.StopAsync(), fixture.Worker.StopAsync());
        await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Worker_FailedExecutionRootReleaseFailsWaitersAndCanBeRetried()
    {
        var lease = new FailOnceExecutionRuntimeProcessLease(
            new TestExecutionRuntimeProcessLease(TestExecutionRuntimeLaunchResolver.Instance.RuntimeRootPath));
        await using var fixture = await WorkerFixture.CreateAsync(
            "normal",
            executionRuntimeLease: lease);
        await fixture.StartAndActivateAsync();

        var firstStop = fixture.Worker.StopAsync();
        var waitingStop = fixture.Worker.StopAsync();

        await Assert.ThrowsAsync<IOException>(() => firstStop);
        await Assert.ThrowsAsync<IOException>(() => waitingStop);
        Assert.Equal(1, lease.ReleaseAttempts);

        await fixture.Worker.StopAsync();

        Assert.Equal(2, lease.ReleaseAttempts);
        Assert.True(lease.Released);
    }

    [Theory]
    [InlineData("nested-call-context")]
    [InlineData("nested-call-null-parent")]
    [InlineData("nested-call-concurrent-frames")]
    public async Task Worker_NestedCallInheritsHostOwnedDeadlineAndDepthAcrossProcessBoundary(string mode)
    {
        await using var fixture = await WorkerFixture.CreateAsync(mode);
        var activation = await fixture.PublishBrokerSessionAsync();
        var requestedDeadline = DateTimeOffset.UtcNow.AddMinutes(1);

        var result = await activation.Client.InvokeAsync(
            activation.Endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "nested" }),
            new SunderRpcCallOptions(requestedDeadline));
        using var status = await fixture.WaitForWorkerStatusAsync("nested-call-context.json");

        Assert.True(result.GetProperty("accepted").GetBoolean());
        Assert.Equal(1, status.RootElement.GetProperty("parentCallDepth").GetInt32());
        Assert.Equal(2, status.RootElement.GetProperty("nestedCallDepth").GetInt32());
        Assert.Equal("1", status.RootElement.GetProperty("extendedDeadlineCapability").GetString());
        if (mode == "nested-call-concurrent-frames")
        {
            Assert.Equal(3, status.RootElement.GetProperty("concurrentCallDepth").GetInt32());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("concurrentCallDepth").ValueKind);
        }
        Assert.Equal(
            status.RootElement.GetProperty("parentDeadlineUtc").GetDateTimeOffset(),
            status.RootElement.GetProperty("nestedDeadlineUtc").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("nested-call-missing-parent")]
    [InlineData("nested-call-forged-parent")]
    [InlineData("nested-call-completed-parent")]
    public async Task Worker_OptedInLineageRejectsUnknownOrCompletedParentId(string mode)
    {
        await using var fixture = await WorkerFixture.CreateAsync(mode);
        var activation = await fixture.PublishBrokerSessionAsync();

        try
        {
            _ = await activation.Client.InvokeAsync(
                activation.Endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "invalid-parent" }));
        }
        catch (SunderRpcException)
        {
            // The completed-parent case may return before the following protocol violation is read.
        }

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Worker_NonOptedTargetRejectsLineageProtocolField()
    {
        await using var fixture = await WorkerFixture.CreateAsync("nonopt-lineage-field");
        var activation = await fixture.PublishBrokerSessionAsync();

        _ = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await activation.Client.InvokeAsync(
                activation.Endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "undeclared-lineage" })));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Worker_StreamCountOverflowIsHostAuthenticatedAndCallLocal()
    {
        await using var fixture = await WorkerFixture.CreateAsync("stream-queue-overflow");
        var activation = await fixture.PublishBrokerSessionAsync();
        await fixture.SetSubscribePermissionAsync(RuntimeRpcPermissionState.Granted);
        await using var stream = activation.Client.SubscribeAsync(
                activation.Endpoint,
                "messages",
                "watch",
                JsonSerializer.SerializeToElement(new { message = "slow-consumer" }))
            .GetAsyncEnumerator();

        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(stream.Current.GetProperty("accepted").GetBoolean());
        using (var cancelled = await fixture.WaitForWorkerStatusAsync(
                   "stream-queue-overflow-cancelled.json"))
        {
            Assert.False(string.IsNullOrWhiteSpace(cancelled.RootElement.GetProperty("id").GetString()));
        }

        var exhausted = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await stream.MoveNextAsync().AsTask());
        Assert.True(exhausted.IsHostAuthenticated);
        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, exhausted.Error.Kind);
        Assert.Equal("rpc.worker.stream-queue-limit", exhausted.Error.Code);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, exhausted.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, exhausted.Error.RetrySafety);
        Assert.Null(exhausted.Error.Details);
        Assert.StartsWith(
            "The process worker stream queue exceeded its count or byte bound.",
            exhausted.Error.Message,
            StringComparison.Ordinal);
        Assert.Contains("limit=count", exhausted.Error.Message, StringComparison.Ordinal);
        Assert.Contains("queuedMessages=32/32", exhausted.Error.Message, StringComparison.Ordinal);
        Assert.Contains("provider=example.provider", exhausted.Error.Message, StringComparison.Ordinal);
        Assert.Contains("service=messages", exhausted.Error.Message, StringComparison.Ordinal);
        Assert.Contains("method=watch", exhausted.Error.Message, StringComparison.Ordinal);
        Assert.False(RuntimeRpcFailureContextStore.TryGet(exhausted, out _));
        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);

        var sibling = await activation.Client.InvokeAsync(
            activation.Endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "still-active" }));
        Assert.True(sibling.GetProperty("accepted").GetBoolean());
        Assert.Single(fixture.Catalog.GetSnapshot().Providers);
        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Broker_ProcessResponseLimitIsHostAuthenticatedAndCallLocal(bool stream)
    {
        await using var fixture = await WorkerFixture.CreateAsync("response-limit");
        var activation = await fixture.PublishBrokerSessionAsync();
        if (stream)
        {
            await fixture.SetSubscribePermissionAsync(RuntimeRpcPermissionState.Granted);
        }
        var request = JsonSerializer.SerializeToElement(new { message = "oversized" });

        SunderRpcException exhausted;
        if (stream)
        {
            await using var events = activation.Client.SubscribeAsync(
                    activation.Endpoint,
                    "messages",
                    "watch",
                    request)
                .GetAsyncEnumerator();
            exhausted = await Assert.ThrowsAsync<SunderRpcException>(async () =>
                await events.MoveNextAsync().AsTask());
        }
        else
        {
            exhausted = await Assert.ThrowsAsync<SunderRpcException>(async () =>
                await activation.Client.InvokeAsync(
                    activation.Endpoint,
                    "messages",
                    "send",
                    request));
        }

        Assert.True(exhausted.IsHostAuthenticated);
        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, exhausted.Error.Kind);
        Assert.Equal("rpc.worker.response-limit", exhausted.Error.Code);
        Assert.Equal(
            "The RPC response exceeds the negotiated worker transport limits.",
            exhausted.Error.Message);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, exhausted.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, exhausted.Error.RetrySafety);
        Assert.DoesNotContain("worker-secret", exhausted.ToString(), StringComparison.Ordinal);
        Assert.False(RuntimeRpcFailureContextStore.TryGet(exhausted, out _));
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage("process.test")!.Readiness);
        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);

        var valid = await activation.Client.InvokeAsync(
            activation.Endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "still-active" }));
        Assert.True(valid.GetProperty("accepted").GetBoolean());
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);
    }

    [Fact]
    public async Task Worker_PreCancelledInvocationIsOrderedBeforeCancellation()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        await fixture.StartAndActivateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Worker.InvokeUnaryAsync(
                "example.provider",
                fixture.InvocationContext(),
                "messages",
                "wait",
                JsonSerializer.SerializeToElement(new { message = "cancelled" }),
                cancellation.Token));

        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);
        await fixture.Worker.StopAsync();
    }

    [Fact]
    public async Task WorkerV1_ComposesStartsCommitsActivatesInvokesAndShutsDown()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        await fixture.StartAndActivateAsync();

        var result = await fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" }),
            CancellationToken.None);

        Assert.True(result.GetProperty("accepted").GetBoolean());
        await fixture.Worker.StopAsync();
        await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WorkerV1_ProviderFaultPersistsOnlySafeIdentityAndCorrelatesBrokerFailure()
    {
        const string secret = "provider-secret=must-never-persist";
        const string exceptionType = "SecretBearingProviderException";
        var logger = new CapturingLogger();
        await using var fixture = await WorkerFixture.CreateAsync(
            "v1-provider-fault",
            logger: logger);
        var activation = await fixture.PublishBrokerSessionAsync();

        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => activation.Client.InvokeAsync(
            activation.Endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" })).AsTask());

        var exceptionFingerprint = RuntimeRpcFailureContextStore.CreateExceptionFingerprint(exceptionType);
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
        Assert.Equal("rpc.provider.handler-fault", failure.Error.Code);
        Assert.Equal("The provider handler violated the RPC invocation contract.", failure.Error.Message);
        Assert.True(RuntimeRpcFailureContextStore.TryGet(failure, out var context));
        Assert.Equal("caller.package", context.CallerPackageId);
        Assert.Equal("process.test", context.ProviderPackageId);
        Assert.Equal("1.0.0", context.ProviderPackageVersion);
        Assert.Equal("example.provider", context.ProviderId);
        Assert.Equal("example.rpc", context.ContractId);
        Assert.Equal(fixture.ActivationId, context.ProviderActivationId);
        Assert.Equal("messages", context.ServiceId);
        Assert.Equal("send", context.MethodId);
        Assert.Equal(exceptionType, context.ExceptionType);
        Assert.Equal(exceptionFingerprint, context.ExceptionFingerprint);
        await fixture.Worker.StopAsync();

        var logFiles = Directory.GetFiles(fixture.Context.Storage.LogsRootPath, "*.log");
        var persisted = string.Join(
            Environment.NewLine,
            await Task.WhenAll(logFiles.Select(static path => File.ReadAllTextAsync(path))));
        Assert.Contains("event=runtime.rpc.provider-fault", persisted, StringComparison.Ordinal);
        Assert.Contains("msg=\"A process RPC provider handler faulted.\"", persisted, StringComparison.Ordinal);
        Assert.Contains("rpc_caller_package_id=caller.package", persisted, StringComparison.Ordinal);
        Assert.Contains("rpc_caller_package_version=1.0.0", persisted, StringComparison.Ordinal);
        Assert.Contains("rpc_provider_package_id=process.test", persisted, StringComparison.Ordinal);
        Assert.Contains("rpc_provider_package_version=1.0.0", persisted, StringComparison.Ordinal);
        Assert.Contains("rpc_provider_id=example.provider", persisted, StringComparison.Ordinal);
        Assert.Contains("rpc_contract_id=example.rpc", persisted, StringComparison.Ordinal);
        Assert.Contains(
            $"rpc_provider_activation_id={fixture.ActivationId:D}",
            persisted,
            StringComparison.Ordinal);
        Assert.Contains("rpc_service_id=messages", persisted, StringComparison.Ordinal);
        Assert.Contains("rpc_method_id=send", persisted, StringComparison.Ordinal);
        Assert.Contains($"rpc_exception_type={exceptionType}", persisted, StringComparison.Ordinal);
        Assert.Contains(
            $"rpc_exception_fingerprint={exceptionFingerprint}",
            persisted,
            StringComparison.Ordinal);
        Assert.Matches("rpc_invocation_id=[0-9a-f]{32}", persisted);
        Assert.DoesNotContain("exception_message", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("exception_stacktrace", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Messages, message => message.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(
            fixture.Owner.GetSnapshot().Errors,
            error => error.Contains(secret, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("protocol-mismatch")]
    [InlineData("protocol-v3-mismatch")]
    public async Task WorkerV1_WhenReadyBindsNonV1_FailsWithoutFallback(string mode)
    {
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await WorkerFixture.CreateAsync(mode));
    }

    [Fact]
    public async Task WorkerV1_LoggingIsAdmittedAtomicallyWithReady()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v1-ready-logging");

        using var status = await fixture.WaitForWorkerStatusAsync("v1-ready-logging.json");
        Assert.True(status.RootElement.GetProperty("accepted").GetBoolean());
        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);
    }

    [Fact]
    public async Task WorkerV1_SettingsStateSchemaAndPayloadsUseHostAuthority()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v1-package-data",
            enforcePackageSessionLease: true);

        Assert.NotNull(fixture.Worker.SettingsSchema);
        var schema = fixture.Worker.SettingsSchema!;
        var section = Assert.Single(schema.Sections);
        Assert.Equal("general", section.SectionId);
        Assert.Equal(["enabled", "token"], section.Fields.Select(static field => field.Key));

        await fixture.PublishBrokerSessionAsync();
        await fixture.WaitForActivationAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v1-package-data.json");
        var root = status.RootElement;
        Assert.Equal("true", root.GetProperty("effective").GetString());
        Assert.True(root.GetProperty("storedWasNull").GetBoolean());
        Assert.Equal(string.Empty, root.GetProperty("emptyValue").GetString());
        Assert.True(root.GetProperty("contains").GetBoolean());
        Assert.Equal(["empty.value"], root.GetProperty("keys").EnumerateArray()
            .Select(static item => item.GetString()));
        Assert.Equal("validation", root.GetProperty("validationKind").GetString());
        Assert.True(root.GetProperty("largeRoundTripped").GetBoolean());
        Assert.True(root.GetProperty("uploadConsumed").GetBoolean());
        Assert.True(root.GetProperty("downloadReleased").GetBoolean());
        Assert.Equal(fixture.Worker.WorkerTemporaryPath, root.GetProperty("dataPath").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("statePath").ValueKind);
        Assert.Equal("false", await fixture.Context.Settings.GetStoredValueAsync("enabled"));
        Assert.Equal(1024 * 1024, (await fixture.Context.Storage.State.GetValueAsync("large.value"))?.Length);
        Assert.Equal(0, fixture.WorkerPayloadHandleCount());

        await fixture.Worker.StopAsync();
    }

    [Fact]
    public async Task WorkerV1_FilesAndSecretsUseHostAuthority()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v1-files-secrets",
            enforcePackageSessionLease: true);

        await fixture.PublishBrokerSessionAsync();
        await fixture.WaitForActivationAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v1-files-secrets.json");
        Assert.True(status.RootElement.GetProperty("secretWasMissing").GetBoolean());
        Assert.True(status.RootElement.GetProperty("secretRoundTripped").GetBoolean());
        Assert.True(status.RootElement.GetProperty("fileRoundTripped").GetBoolean());
        Assert.True(status.RootElement.GetProperty("downloadReleased").GetBoolean());
        Assert.Null(await fixture.Context.Secrets.GetSecretAsync("provider.token"));
        Assert.Null(await fixture.Context.Storage.Files.ReadAsync("cache/model.bin"));
        Assert.Equal(0, fixture.WorkerPayloadHandleCount());

        await fixture.Worker.StopAsync();
    }

    [Fact]
    public async Task WorkerV1_LoggingUsesHostSinksAndDrainsShutdown()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v1-logging");

        await fixture.PublishBrokerSessionAsync();
        await fixture.WaitForActivationAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v1-logging.json");
        Assert.True(status.RootElement.GetProperty("accepted").GetBoolean());

        var eventsPath = Assert.Single(Directory.GetFiles(
            fixture.Context.Storage.LogsRootPath,
            "events-*.log"));
        var events = await File.ReadAllTextAsync(eventsPath);
        Assert.Contains("event=worker.event", events, StringComparison.Ordinal);
        Assert.Contains("msg=\"Structured message\"", events, StringComparison.Ordinal);
        Assert.Contains("attempt=3", events, StringComparison.Ordinal);
        Assert.Contains("api_key=\"[REDACTED]\"", events, StringComparison.Ordinal);
        Assert.Contains("source=events", events, StringComparison.Ordinal);
        Assert.Contains("attribute_source=forged-source", events, StringComparison.Ordinal);
        Assert.Contains("attribute_category=forged-category", events, StringComparison.Ordinal);
        Assert.Contains("exception_type=System.InvalidOperationException", events, StringComparison.Ordinal);
        Assert.Contains("exception_inner_type=System.ArgumentException", events, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-leak", events, StringComparison.Ordinal);

        var runtimePath = Assert.Single(Directory.GetFiles(
            fixture.Context.Storage.LogsRootPath,
            "runtime-*.log"));
        var runtime = await File.ReadAllTextAsync(runtimePath);
        Assert.Contains("event=worker.ready", runtime, StringComparison.Ordinal);
        Assert.Contains("category=Worker.Test", runtime, StringComparison.Ordinal);
        Assert.Contains("event_id=42", runtime, StringComparison.Ordinal);
        Assert.Contains("attempt=4", runtime, StringComparison.Ordinal);

        await fixture.Worker.StopAsync();

        runtime = await File.ReadAllTextAsync(runtimePath);
        Assert.Contains("category=Worker.Shutdown", runtime, StringComparison.Ordinal);
        Assert.Contains("msg=\"Worker stopping\"", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerV1_MalformedShutdownLoggingFaultsProtocol()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v1-malformed-shutdown-logging");
        await fixture.StartAndActivateAsync();
        await fixture.WaitForActivationAsync();

        await Assert.ThrowsAsync<SunderWorkerProtocolException>(async () =>
            await fixture.Worker.StopAsync());
        await Assert.ThrowsAsync<SunderWorkerProtocolException>(async () =>
            await fixture.Worker.GenerationCompletion);
    }

    [Fact]
    public async Task WorkerV1_InvalidSettingsSchemaFailsComposition()
    {
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await WorkerFixture.CreateAsync("v1-invalid-settings-schema"));
    }

    [Theory]
    [InlineData(false, "candidate-hang")]
    [InlineData(true, "commit-hang")]
    public async Task WorkerV1_StopCompletesInterruptedLifecycleWaiter(bool duringCommit, string mode)
    {
        await using var fixture = await WorkerFixture.CreateAsync(mode);
        if (duringCommit) await fixture.Worker.StartAsync();
        var lifecycle = duringCommit
            ? fixture.Worker.CommitGenerationAsync(new RuntimePackageGeneration(
                fixture.ActivationId,
                1))
            : fixture.Worker.StartAsync();

        await fixture.Worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await lifecycle.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false, "candidate-shutdown-hang")]
    [InlineData(true, "commit-shutdown-hang")]
    public async Task WorkerV1_ForcedStopCompletesInterruptedLifecycleWaiter(bool duringCommit, string mode)
    {
        var policy = new RuntimeProcessPolicyOptions
        {
            ShutdownTimeout = TimeSpan.FromMilliseconds(100),
        };
        await using var fixture = await WorkerFixture.CreateAsync(mode, policy);
        if (duringCommit) await fixture.Worker.StartAsync();
        var lifecycle = duringCommit
            ? fixture.Worker.CommitGenerationAsync(new RuntimePackageGeneration(
                fixture.ActivationId,
                1))
            : fixture.Worker.StartAsync();

        await fixture.Worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await lifecycle.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false, "migration-preparation-hang")]
    [InlineData(true, "migration-preparation-shutdown-hang")]
    public async Task WorkerV1_StopCompletesInterruptedMigrationPreparationWaiter(
        bool forceShutdown,
        string mode)
    {
        var policy = forceShutdown
            ? new RuntimeProcessPolicyOptions { ShutdownTimeout = TimeSpan.FromMilliseconds(100) }
            : null;
        await using var fixture = await WorkerFixture.CreateAsync(mode, policy);
        await fixture.Worker.StartAsync();
        var generation = new RuntimePackageGeneration(fixture.ActivationId, 1);
        await fixture.Worker.CommitGenerationAsync(generation);
        var preparation = fixture.Worker.PrepareStorageMigrationsAsync(generation);

        await fixture.Worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await preparation.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WorkerV1_RejectsOversizedInlinePackageDataValue()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v1-oversized-inline");
        try
        {
            await fixture.StartAndActivateAsync();
        }
        catch (SunderWorkerProtocolException)
        {
            // The protocol fault can race the activation acknowledgement.
        }

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WorkerV1_StaleActivationConsumesSubmittedUploadHandle()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v1-stale-package-data",
            enforcePackageSessionLease: true);
        await fixture.StartAndActivateAsync();
        await fixture.WaitForActivationAsync();

        using var status = await fixture.WaitForWorkerStatusAsync("v1-stale-package-data.json");
        Assert.Equal("unavailable", status.RootElement.GetProperty("kind").GetString());
        Assert.Equal("worker.activation.unavailable", status.RootElement.GetProperty("code").GetString());
        Assert.True(status.RootElement.GetProperty("uploadConsumed").GetBoolean());
        Assert.Equal(0, fixture.WorkerPayloadHandleCount());
    }

    [Fact]
    public async Task WorkerV1_StaleActivationStillValidatesSubmittedUploadIntegrity()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v1-stale-malformed-payload",
            enforcePackageSessionLease: true);
        try
        {
            await fixture.StartAndActivateAsync();
        }
        catch (SunderWorkerProtocolException)
        {
            // The expected integrity fault can race the activation acknowledgement.
        }

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, fixture.WorkerPayloadHandleCount());
    }

    [Fact]
    public async Task WorkerV1_ShutdownAcknowledgementWaitsForWorkerCallCleanup()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v1-shutdown-worker-call");
        await fixture.StartAndActivateAsync();
        await fixture.WaitForActivationAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v1-shutdown-worker-call.json");
        Assert.True(status.RootElement.GetProperty("started").GetBoolean());

        await fixture.Worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("v1-shutdown-buffered-get-provider")]
    [InlineData("v1-shutdown-buffered-discover")]
    [InlineData("v1-shutdown-buffered-scoped-discover")]
    public async Task WorkerV1_CallFrameCrossingActiveShutdownIsCancelledWithoutFault(string mode)
    {
        await using var fixture = await WorkerFixture.CreateAsync(mode);
        _ = await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync($"{mode}.json");
        Assert.True(status.RootElement.GetProperty("started").GetBoolean());

        await fixture.Worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WorkerV1_CallFrameCrossingShutdownStillRequiresValidEnvelope()
    {
        const string mode = "v1-shutdown-buffered-invalid-discover";
        await using var fixture = await WorkerFixture.CreateAsync(mode);
        await fixture.StartAndActivateAsync();
        await fixture.WaitForActivationAsync();
        using var status = await fixture.WaitForWorkerStatusAsync($"{mode}.json");
        Assert.True(status.RootElement.GetProperty("started").GetBoolean());

        await Assert.ThrowsAsync<SunderWorkerProtocolException>(async () =>
            await fixture.Worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<SunderWorkerProtocolException>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WorkerV1_ShutdownFromReadyDoesNotGrantBufferedOrdinaryCallAuthority()
    {
        const string mode = "v1-ready-buffered-discover";
        await using var fixture = await WorkerFixture.CreateAsync(mode);
        using var status = await fixture.WaitForWorkerStatusAsync($"{mode}.json");
        Assert.True(status.RootElement.GetProperty("started").GetBoolean());

        var exception = await Assert.ThrowsAsync<SunderWorkerProtocolException>(async () =>
            await fixture.Worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("before its Runtime generation was active", exception.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<SunderWorkerProtocolException>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [UnixFact]
    public async Task Worker_UnixFifoContentPathIsRejectedWithoutBlocking()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-worker-fifo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fifoPath = Path.Combine(root, "content.fifo");
        try
        {
            Assert.Equal(0, MakeFifo(fifoPath, Convert.ToUInt32("600", 8)));
            var method = typeof(ProcessRuntimeWorker).GetMethod(
                "OpenWorkerOwnedContentFile",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

            var invocation = Task.Run(() => method.Invoke(null, [fifoPath]));
            var exception = await Assert.ThrowsAsync<System.Reflection.TargetInvocationException>(async () =>
                await invocation.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.IsType<SunderWorkerProtocolException>(exception.InnerException);
        }
        finally
        {
            File.Delete(fifoPath);
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task RuntimePackageActivator_ProjectsWorkerSettingsSchemaWithHostIdentity()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v1-package-data");
        var result = await fixture.ActivatePreparedPackageAsync();
        var loaded = Assert.IsType<ActiveLoadedPackage>(result.LoadedPackage);
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                [loaded.Descriptor.PackageId] = loaded,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                [loaded.Descriptor.PackageId] = result.SessionPackage,
            },
            workersStarted: false);
        try
        {
            Assert.True(result.Success);
            Assert.Null(loaded.CanonicalSettingsSchema);
            Assert.Null(loaded.SettingsSchema);

            await session.StartWorkersAsync(
                NullLogger.Instance,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));

            Assert.True(session.TryGetLoadedPackage("process.test", out var started));
            Assert.NotNull(started!.CanonicalSettingsSchema);
            Assert.NotNull(started.SettingsSchema);
            Assert.Equal("process.test", started.SettingsSchema!.PackageId);
            Assert.Equal("Process Test", started.SettingsSchema.PackageDisplayName);
            Assert.Equal("Process worker settings.", started.SettingsSchema.Summary);
            Assert.Equal("true", await started.Settings.GetValueAsync("enabled"));
            await started.FileStore.WriteAsync("activator/context.txt", "context"u8.ToArray());
            Assert.Equal("context"u8.ToArray(), await started.FileStore.ReadAsync("activator/context.txt"));
        }
        finally
        {
            await session.StopWorkersAsync(NullLogger.Instance, TimeSpan.FromSeconds(5));
            await session.DisposeAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RuntimePackageActivator_RecoveryFactoryResolvesFreshRuntimeLease()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        var resolver = new RecordingExecutionRuntimeLaunchResolver();
        var result = await fixture.ActivatePreparedPackageAsync(resolver);
        var loaded = Assert.IsType<ActiveLoadedPackage>(result.LoadedPackage);
        var replacement = loaded.RecoveryFactory!();
        try
        {
            Assert.Equal(2, resolver.LeaseIds.Count);
            Assert.NotEqual(resolver.LeaseIds[0], resolver.LeaseIds[1]);
            Assert.NotEqual(loaded.RuntimeActivationId, replacement.RuntimeActivationId);
            Assert.NotSame(loaded.Worker, replacement.Worker);
        }
        finally
        {
            await loaded.Worker.DisposeAsync();
            await replacement.Worker.DisposeAsync();
        }
    }

    [Fact]
    public async Task Worker_PreActivationProviderCallIsUnavailableWithoutFaultingGeneration()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        await fixture.Worker.StartAsync();
        await fixture.Worker.CommitGenerationAsync(
            new RuntimePackageGeneration(fixture.ActivationId, 1));

        var unavailable = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await fixture.Worker.InvokeUnaryAsync(
                "example.provider",
                fixture.InvocationContext(),
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "early" }),
                CancellationToken.None));

        Assert.Equal(SunderRpcErrorKind.Unavailable, unavailable.Error.Kind);
        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);

        await fixture.PrepareStorageMigrationsAsync();
        await fixture.Worker.ActivateGenerationAsync();
        var result = await fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "ready" }),
            CancellationToken.None);
        Assert.True(result.GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task WorkerV1_RejectsMismatchedEffectiveResourceProfileHash()
    {
        var exception = await Assert.ThrowsAnyAsync<ProcessRuntimeWorkerException>(async () =>
            await WorkerFixture.CreateAsync("resource-profile-mismatch"));

        Assert.Contains("resource profile", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkerV1_OversizedRequestIsNotDispatchedAndDoesNotFaultProcess()
    {
        var policy = new RuntimeProcessPolicyOptions
        {
            EffectiveResourcePolicy = RuntimeResourcePolicies.CustomMinimums,
        };
        await using var fixture = await WorkerFixture.CreateAsync("normal", policy);
        await fixture.StartAndActivateAsync();
        var oversized = JsonSerializer.SerializeToElement(new { text = new string('"', 600_000) });

        var exhausted = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await fixture.Worker.InvokeUnaryAsync(
                "example.provider",
                fixture.InvocationContext(),
                "messages",
                "send",
                oversized,
                CancellationToken.None));

        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, exhausted.Error.Kind);
        Assert.Equal(SunderRpcDispatchState.NotDispatched, exhausted.Error.DispatchState);
        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);
        var valid = await fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "still-active" }),
            CancellationToken.None);
        Assert.True(valid.GetProperty("accepted").GetBoolean());
        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);
    }

    [Fact]
    public async Task WorkerV1_ProviderDispatchWaitsForActivationAcknowledgement()
    {
        await using var fixture = await WorkerFixture.CreateAsync("activation-hang");
        await fixture.Worker.StartAsync();
        await fixture.Worker.CommitGenerationAsync(
            new RuntimePackageGeneration(fixture.ActivationId, 1));
        await fixture.PrepareStorageMigrationsAsync();
        var activation = fixture.Worker.ActivateGenerationAsync();
        using var cancellation = new CancellationTokenSource();

        var invocation = fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "early" }),
            cancellation.Token).AsTask();
        using var activationEntered = await fixture.WaitForWorkerStatusAsync("activation-entered.json");

        Assert.False(invocation.IsCompleted);
        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        await fixture.Worker.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => activation);
    }

    [Fact]
    public async Task WorkerV1_ActivationWaitersRespectOutstandingHostCallLimit()
    {
        var policy = new RuntimeProcessPolicyOptions { MaxOutstandingHostCalls = 1 };
        await using var fixture = await WorkerFixture.CreateAsync(
            "activation-hang",
            policy);
        await fixture.Worker.StartAsync();
        await fixture.Worker.CommitGenerationAsync(
            new RuntimePackageGeneration(fixture.ActivationId, 1));
        await fixture.PrepareStorageMigrationsAsync();
        var activation = fixture.Worker.ActivateGenerationAsync();
        using var cancellation = new CancellationTokenSource();
        var first = fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "first" }),
            cancellation.Token).AsTask();
        using var activationEntered = await fixture.WaitForWorkerStatusAsync("activation-entered.json");

        var exhausted = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await fixture.Worker.InvokeUnaryAsync(
                "example.provider",
                fixture.InvocationContext(),
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "second" }),
                CancellationToken.None));

        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, exhausted.Error.Kind);
        Assert.Equal("rpc.worker.host-call-limit", exhausted.Error.Code);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await fixture.Worker.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => activation);
    }

    [Fact]
    public async Task WorkerV1_PendingProviderDispatchContinuesAfterActivationAcknowledgement()
    {
        var logger = new CapturingLogger();
        await using var fixture = await WorkerFixture.CreateAsync("activation-delay", logger: logger);
        await fixture.Worker.StartAsync();
        await fixture.Worker.CommitGenerationAsync(
            new RuntimePackageGeneration(fixture.ActivationId, 1));
        await fixture.PrepareStorageMigrationsAsync();
        var activation = fixture.Worker.ActivateGenerationAsync();

        var invocation = fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "pending" }),
            CancellationToken.None).AsTask();
        using var activationEntered = await fixture.WaitForWorkerStatusAsync("activation-entered.json");

        try
        {
            Assert.False(invocation.IsCompleted);
        }
        finally
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixture.Worker.WorkerTemporaryPath, "activation-delay.release"),
                "release");
        }
        await activation.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await invocation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.GetProperty("accepted").GetBoolean());
        Assert.Contains(logger.Messages, message => message.Contains("activation dispatched", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("activation acknowledged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkerV1_ReportedGenerationFaultFaultsOnlyItsExactGenerationCompletion()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v1-generation-fault");
        ProcessRuntimeGenerationFaultException? activationFault = null;
        try
        {
            await fixture.StartAndActivateAsync();
        }
        catch (ProcessRuntimeGenerationFaultException exception)
        {
            activationFault = exception;
        }
        catch (InvalidOperationException exception) when (
            exception.InnerException is ProcessRuntimeGenerationFaultException)
        {
            activationFault = (ProcessRuntimeGenerationFaultException)exception.InnerException;
        }

        var fault = await Assert.ThrowsAsync<ProcessRuntimeGenerationFaultException>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));

        if (activationFault is not null)
        {
            Assert.Same(activationFault, fault);
        }
        Assert.Equal("worker.test-fault", fault.Code);
        Assert.Contains("bounded fault", fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerV1_MismatchedCallbackIdentityFaultsExactGeneration()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v1-callback-mismatch");
        await fixture.StartAndActivateAsync();
        var callback = fixture.Worker.CallbackHandlers["fixture.callback"];

        await Assert.ThrowsAsync<SunderWorkerProtocolException>(() => callback.StartCallbackAsync(
            new PackageCallbackStartContext(
                "callback1",
                new Uri("http://127.0.0.1/callback"))));
        await Assert.ThrowsAsync<SunderWorkerProtocolException>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WorkerV1_CanCallHostWhileActivationIsInitializing()
    {
        await using var fixture = await WorkerFixture.CreateAsync("activation-host-call");

        await fixture.PublishCandidateSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("activation-host-call.json");

        Assert.True(
            status.RootElement.GetProperty("responseType").GetString() == "host.result",
            status.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(1, status.RootElement.GetProperty("providerCount").GetInt32());
        Assert.Equal(
            status.RootElement.GetProperty("endpointReference").GetString(),
            Assert.Single(fixture.Catalog.GetSnapshot().Providers).Endpoint.Value);
        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);
        await fixture.Worker.StopAsync();
    }

    [Fact]
    public async Task WorkerV1_InvariantReportRoundTripsHostDecision()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v1-invariant-report");

        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v1-invariant-report.json");

        Assert.Equal("host.result", status.RootElement.GetProperty("responseType").GetString());
        Assert.False(status.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage("process.test")!.Readiness);
        await fixture.Worker.StopAsync();
    }

    [Fact]
    public async Task WorkerV1_CallerScopeRoundTripsRpcAndContentAuthority()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v1-scope-roundtrip");

        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v1-scope-roundtrip.json");

        Assert.Equal("caller-payload-processed", status.RootElement.GetProperty("content").GetString());
        Assert.Equal("process.test", status.RootElement.GetProperty("callerPackageId").GetString());
        Assert.True(status.RootElement.GetProperty("invocationHandleReleased").GetBoolean());
        Assert.True(status.RootElement.GetProperty("scopeHandleReleased").GetBoolean());
        await fixture.Worker.StopAsync();
        await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WorkerV1_ShutdownRevokesLeakedCallerScopeAndContent()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v1-scope-leak");

        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v1-scope-leak.json");
        Assert.False(string.IsNullOrWhiteSpace(status.RootElement.GetProperty("scopeId").GetString()));
        Assert.Equal(1, fixture.WorkerCallScopeCount());
        Assert.NotEmpty(fixture.TransferFiles());

        await fixture.Worker.StopAsync();

        Assert.Equal(0, fixture.WorkerCallScopeCount());
        Assert.Empty(fixture.TransferFiles());
    }

    [Fact]
    public async Task WorkerV1_ShutdownDeadlineDoesNotWaitForBlockingScopeCallback()
    {
        var policy = new RuntimeProcessPolicyOptions
        {
            ShutdownTimeout = TimeSpan.FromMilliseconds(150),
        };
        await using var fixture = await WorkerFixture.CreateAsync(
            "v1-scope-leak",
            policy);
        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v1-scope-leak.json");
        using var releaseCallback = new ManualResetEventSlim();
        using var callbackStarted = new ManualResetEventSlim();
        using var registration = fixture.WorkerCallScopeRevocationToken().Register(() =>
        {
            callbackStarted.Set();
            releaseCallback.Wait();
        });

        var started = Stopwatch.GetTimestamp();
        try
        {
            await fixture.Worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(1)));
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseCallback.Set();
        }
    }

    [Fact]
    public async Task WorkerV1_CallerScopeUsesInstalledPackagePermissions()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v1-scope-permission-denied");

        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v1-scope-permission-denied.json");

        Assert.Equal("permission-denied", status.RootElement.GetProperty("kind").GetString());
        Assert.Equal("rpc.permission.denied", status.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task WorkerV1_BoundsMaterializedCallerScopeHandles()
    {
        var policy = new RuntimeProcessPolicyOptions
        {
            MaxMaterializedWorkerContentHandles = 1,
        };
        await using var fixture = await WorkerFixture.CreateAsync(
            "v1-scope-handle-limit",
            policy);

        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v1-scope-handle-limit.json");

        Assert.Equal("resource-exhausted", status.RootElement.GetProperty("handleLimitKind").GetString());
        Assert.Equal(
            "rpc.worker.content-handle-limit",
            status.RootElement.GetProperty("handleLimitCode").GetString());
        Assert.True(status.RootElement.GetProperty("handleLimitRetrySucceeded").GetBoolean());
    }

    [Fact]
    public async Task WorkerV1_RejectsUnknownCallerScope()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v1-unknown-scope");
        try
        {
            await fixture.StartAndActivateAsync();
        }
        catch
        {
            // The protocol fault may race the activation acknowledgement.
        }

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Worker_StopCompletesWhenForceStopCallbackThrows()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        await fixture.StartAndActivateAsync();
        var forceStop = Assert.IsType<CancellationTokenSource>(typeof(ProcessRuntimeWorker)
            .GetField("_forceStop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(fixture.Worker));
        using var callback = forceStop.Token.Register(
            static () => throw new InvalidOperationException("force-stop callback failed"));

        await fixture.Worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Worker_ContentRegisterOpenAndReleaseRemainBoundToHostInvocation()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        var activation = await fixture.PublishBrokerSessionAsync();
        await using var scope = await activation.Client.CreateCallScopeAsync();
        await using var inputStream = new MemoryStream(Encoding.UTF8.GetBytes("caller-payload"));
        var input = await scope.RegisterContentAsync(
            activation.Endpoint,
            inputStream,
            new SunderRpcContentRegistrationOptions("text/plain", "caller.txt", inputStream.Length));

        var result = await scope.InvokeAsync(
            activation.Endpoint,
            "messages",
            "content-roundtrip",
            JsonSerializer.SerializeToElement(new
            {
                message = "content",
                content = WorkerWire.ToWire(input),
            }));

        Assert.True(result.GetProperty("accepted").GetBoolean());
        Assert.True(result.GetProperty("openedFileReleased").GetBoolean());
        var output = ReadContentReference(result.GetProperty("content"));
        await using var outputStream = await scope.OpenContentAsync(output);
        using var reader = new StreamReader(outputStream, Encoding.UTF8, leaveOpen: false);
        Assert.Equal("caller-payload-processed", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task WorkerV1_ContentReleaseUsesControlLaneWhenRpcLaneIsFull()
    {
        var policy = new RuntimeProcessPolicyOptions
        {
            MaxOutstandingWorkerCalls = 1,
        };
        await using var fixture = await WorkerFixture.CreateAsync(
            "content-control-lane",
            policy);
        var activation = await fixture.PublishBrokerSessionAsync();
        await using var scope = await activation.Client.CreateCallScopeAsync();
        await using var inputStream = new MemoryStream(Encoding.UTF8.GetBytes("caller-payload"));
        var input = await scope.RegisterContentAsync(
            activation.Endpoint,
            inputStream,
            new SunderRpcContentRegistrationOptions("text/plain", "caller.txt", inputStream.Length));

        var result = await scope.InvokeAsync(
            activation.Endpoint,
            "messages",
            "content-roundtrip",
            JsonSerializer.SerializeToElement(new
            {
                message = "content",
                content = WorkerWire.ToWire(input),
            }));

        Assert.True(result.GetProperty("openedFileReleased").GetBoolean());
        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);
    }

    [Fact]
    public async Task Worker_UsesSanitizedExplicitEnvironmentWithoutRuntimeTokens()
    {
        await using var fixture = await WorkerFixture.CreateAsync("environment");
        await fixture.StartAndActivateAsync();

        var result = await fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "environment",
            JsonSerializer.SerializeToElement(new { message = "environment" }),
            CancellationToken.None);

        Assert.Equal(JsonValueKind.Null, result.GetProperty("nodeOptions").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("runtimeToken").ValueKind);
        Assert.Equal("process.test", result.GetProperty("packageId").GetString());
        Assert.Equal("1.0.0", result.GetProperty("packageVersion").GetString());
        Assert.Equal(fixture.ActivationId.ToString("N"), result.GetProperty("activationId").GetString());
        Assert.Equal(fixture.Prepared.SessionId, result.GetProperty("sessionId").GetString());
        Assert.Equal(
            new DirectoryInfo(fixture.Prepared.ShadowFolder).Name,
            new DirectoryInfo(result.GetProperty("workingPath").GetString()!).Name);
        Assert.Equal(Path.GetFullPath(fixture.Prepared.ShadowFolder), result.GetProperty("contentRoot").GetString());
        Assert.Equal(fixture.Worker.WorkerTemporaryPath, result.GetProperty("temporaryRoot").GetString());
        Assert.Equal(
            fixture.Context.Storage.RoleLocalWorkspace.WorkspaceRootPath,
            result.GetProperty("workspaceRoot").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("workspaceEnvironment").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("dataPath").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("statePath").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("path").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("home").ValueKind);
        Assert.Equal(
            ExpectedWorkerEnvironmentNames(),
            result.GetProperty("environmentNames")
                .EnumerateArray()
                .Select(static item => NormalizeEnvironmentName(item.GetString()!))
                .Order(StringComparer.Ordinal));
        Assert.True(Directory.Exists(fixture.Context.Storage.DataRootPath));
        Assert.Equal(fixture.Worker.WorkspaceRootPath, result.GetProperty("workspaceRoot").GetString());
        Assert.EndsWith("Z", result.GetProperty("deadlineUtc").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("extendedDeadlineCapability").ValueKind);
    }

    [Fact]
    public async Task Worker_AcceptsMaximumLengthPackageVersionAcrossEnvironmentAndHandshake()
    {
        var packageVersion = GetMaximumSemanticVersion();
        Assert.Equal(256, packageVersion.Length);
        await using var fixture = await WorkerFixture.CreateAsync("environment", packageVersion: packageVersion);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await fixture.StartAndActivateAsync().WaitAsync(timeout.Token);

        var result = await fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "environment",
            JsonSerializer.SerializeToElement(new { message = "environment" }),
            timeout.Token);

        Assert.Equal(packageVersion, result.GetProperty("packageVersion").GetString());
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("macos")]
    [InlineData("linux")]
    public void WorkerEnvironmentBaseline_ContainsOnlyOsMinimumAndPackageLocalTemp(
        string platformName)
    {
        var platform = platformName switch
        {
            "windows" => WorkerProcessPlatform.Windows,
            "macos" => WorkerProcessPlatform.MacOS,
            "linux" => WorkerProcessPlatform.Linux,
            _ => throw new ArgumentOutOfRangeException(nameof(platformName)),
        };
        var source = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = platform == WorkerProcessPlatform.Windows
                ? @"C:\Windows\System32"
                : "/usr/local/bin:/usr/bin",
            ["HOME"] = "/Users/test",
            ["USERPROFILE"] = @"C:\Users\test",
            ["PATHEXT"] = ".EXE;.CMD",
            ["ComSpec"] = @"C:\Windows\System32\cmd.exe",
            ["SystemRoot"] = @"C:\Windows",
            ["LANG"] = "en_US.UTF-8",
            ["LC_CTYPE"] = "UTF-8",
            ["__CF_USER_TEXT_ENCODING"] = "0x1F5:0x0:0x0",
            ["UNRELATED"] = "must-not-pass",
        };

        var environment = WorkerProcessEnvironment.CreateBaseline(
            source.GetValueOrDefault,
            platform == WorkerProcessPlatform.Windows
                ? @"C:\Temp\sunder-worker"
                : "/tmp/sunder-worker",
            platform,
            allowDockerEnvironment: false);

        Assert.Equal(
            platform == WorkerProcessPlatform.Windows
                ? ["SystemRoot", "TEMP", "TMP", "TMPDIR", "WINDIR"]
                : platform == WorkerProcessPlatform.MacOS
                    ? ["TEMP", "TMP", "TMPDIR", "__CF_USER_TEXT_ENCODING"]
                    : ["TEMP", "TMP", "TMPDIR"],
            environment.Keys.Order(StringComparer.Ordinal));
        if (platform == WorkerProcessPlatform.Windows)
        {
            Assert.Equal(source["SystemRoot"], environment["SystemRoot"]);
            Assert.Equal(source["SystemRoot"], environment["WINDIR"]);
        }
        Assert.Equal(environment["TMPDIR"], environment["TMP"]);
        Assert.Equal(environment["TMPDIR"], environment["TEMP"]);
    }

    [Fact]
    public void WorkerEnvironmentBaseline_DockerCapabilityAllowsOnlyNonSecretDockerConnectionVariables()
    {
        var source = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/test",
            ["DOCKER_HOST"] = "unix:///run/user/1000/docker.sock",
            ["DOCKER_CONTEXT"] = "desktop-linux",
            ["DOCKER_CONFIG"] = "/home/test/.docker-custom",
            ["DOCKER_CERT_PATH"] = "/home/test/.docker/certs",
            ["DOCKER_TLS_VERIFY"] = "1",
            ["DOCKER_AUTH_CONFIG"] = "must-not-pass",
            ["DOCKER_CONTENT_TRUST_REPOSITORY_PASSPHRASE"] = "must-not-pass",
        };

        var denied = WorkerProcessEnvironment.CreateBaseline(
            source.GetValueOrDefault,
            "/tmp/worker",
            WorkerProcessPlatform.Linux,
            allowDockerEnvironment: false);
        var allowed = WorkerProcessEnvironment.CreateBaseline(
            source.GetValueOrDefault,
            "/tmp/worker",
            WorkerProcessPlatform.Linux,
            allowDockerEnvironment: true);

        Assert.DoesNotContain(denied.Keys, static key => key.StartsWith("DOCKER_", StringComparison.Ordinal));
        Assert.Equal(
            ["DOCKER_CERT_PATH", "DOCKER_CONFIG", "DOCKER_CONTEXT", "DOCKER_HOST", "DOCKER_TLS_VERIFY"],
            allowed.Keys.Where(static key => key.StartsWith("DOCKER_", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void WorkerEnvironment_DockerManifestWithoutExactInstalledGrantIsDenied()
    {
        var target = new SunderPackageTargetManifest
        {
            RequiredHostCapabilities = [SunderSdkCapabilities.DockerEnvironmentV1],
        };

        Assert.False(WorkerProcessEnvironment.AllowsDockerEnvironment(target, []));
        Assert.True(WorkerProcessEnvironment.AllowsDockerEnvironment(
            target,
            [SunderSdkCapabilities.DockerEnvironmentV1]));
    }

    [Fact]
    public void WorkerEnvironmentBaseline_DoesNotLeakTokensProfilersCloudOrSunderBootstrap()
    {
        var blocked = new[]
        {
            "PATH",
            "HOME",
            "USERPROFILE",
            "PATHEXT",
            "ComSpec",
            "LANG",
            "LC_ALL",
            "LC_CTYPE",
            "SUNDER_RUNTIME_BEARER_TOKEN",
            "SUNDER_HOST_STATE_ROOT",
            "GITHUB_TOKEN",
            "OPENAI_API_KEY",
            "AWS_ACCESS_KEY_ID",
            "AZURE_CLIENT_SECRET",
            "GOOGLE_APPLICATION_CREDENTIALS",
            "CORECLR_ENABLE_PROFILING",
            "CORECLR_PROFILER",
            "COR_ENABLE_PROFILING",
            "DOTNET_STARTUP_HOOKS",
            "DOTNET_ROOT",
            "DOTNET_HOST_PATH",
            "DOTNET_ADDITIONAL_DEPS",
            "DOTNET_SHARED_STORE",
            "DOTNET_MULTILEVEL_LOOKUP",
            "NODE_OPTIONS",
            "NODE_PATH",
            "NVM_BIN",
            "NVM_DIR",
        };
        var source = blocked.ToDictionary(static key => key, static _ => (string?)"must-not-pass", StringComparer.Ordinal);

        var environment = WorkerProcessEnvironment.CreateBaseline(
            source.GetValueOrDefault,
            "/tmp/worker",
            WorkerProcessPlatform.Linux,
            allowDockerEnvironment: true);

        Assert.All(blocked, key => Assert.DoesNotContain(key, environment.Keys));
    }

    [Fact]
    public async Task WorkerLaunch_UsesExplicitGenericRecipeWithShellAndAmbientRuntimeLookupDisabled()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");

        var startInfo = fixture.Worker.CreateStartInfo();

        Assert.False(startInfo.UseShellExecute);
        Assert.True(Path.IsPathFullyQualified(startInfo.FileName));
        Assert.NotEqual(Path.GetFullPath(fixture.Prepared.EntryAssemblyPath!), startInfo.FileName);
        Assert.Equal(
            [SunderExecutionRuntimeFormat.DotNetExecArgument, Path.GetFullPath(fixture.Prepared.EntryAssemblyPath!)],
            startInfo.ArgumentList);
        Assert.DoesNotContain("PATH", startInfo.Environment.Keys);
        Assert.DoesNotContain("DOTNET_ROOT", startInfo.Environment.Keys);
        Assert.DoesNotContain("DOTNET_HOST_PATH", startInfo.Environment.Keys);
        Assert.DoesNotContain("NVM_BIN", startInfo.Environment.Keys);
        Assert.DoesNotContain("NVM_DIR", startInfo.Environment.Keys);
        Assert.Equal(
            ExpectedWorkerEnvironmentNames(),
            startInfo.Environment.Keys
                .Select(NormalizeEnvironmentName)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WorkerV1_ReceivesDistinctCanonicalContentTemporaryAndWorkspaceRoots()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v1-environment");
        await fixture.StartAndActivateAsync();

        using var status = await fixture.WaitForWorkerStatusAsync("v1-environment.json");
        var root = status.RootElement;
        Assert.Equal(Path.GetFullPath(fixture.Prepared.ShadowFolder), root.GetProperty("contentRoot").GetString());
        Assert.Equal(fixture.Worker.WorkerTemporaryPath, root.GetProperty("temporaryRoot").GetString());
        Assert.Equal(
            fixture.Context.Storage.RoleLocalWorkspace.WorkspaceRootPath,
            root.GetProperty("workspaceRoot").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("workspaceEnvironment").ValueKind);
        Assert.NotEqual(root.GetProperty("contentRoot").GetString(), root.GetProperty("temporaryRoot").GetString());
        Assert.NotEqual(root.GetProperty("contentRoot").GetString(), root.GetProperty("workspaceRoot").GetString());
        Assert.NotEqual(root.GetProperty("temporaryRoot").GetString(), root.GetProperty("workspaceRoot").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("legacyContentPath").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("legacyDataPath").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("legacyWorkingPath").ValueKind);
    }

    private static string[] ExpectedWorkerEnvironmentNames()
    {
        var names = new List<string>
        {
            "SUNDER_ACTIVATION_ID",
            "SUNDER_PACKAGE_CONTENT_ROOT",
            "SUNDER_PACKAGE_ID",
            "SUNDER_PACKAGE_VERSION",
            "SUNDER_SESSION_ID",
            "SUNDER_WORKER_PROTOCOL",
            "SUNDER_WORKER_TEMP_ROOT",
            "TEMP",
            "TMP",
            "TMPDIR",
        };
        if (OperatingSystem.IsWindows())
        {
            names.Add("SystemRoot");
            names.Add("WINDIR");
        }
        else if (OperatingSystem.IsMacOS())
        {
            names.Add("__CF_USER_TEXT_ENCODING");
        }
        return names
            .Select(NormalizeEnvironmentName)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeEnvironmentName(string name)
        => OperatingSystem.IsWindows() ? name.ToUpperInvariant() : name;

    [Theory]
    [InlineData("malformed-frame")]
    [InlineData("crash")]
    [InlineData("caller-spoof")]
    [InlineData("content-spoof")]
    public async Task Worker_ProtocolViolationCrashAndCallerSpoofFaultExactGeneration(string mode)
    {
        await using var fixture = await WorkerFixture.CreateAsync(mode);
        try
        {
            await fixture.StartAndActivateAsync();
        }
        catch
        {
            // A protocol fault can race the activation acknowledgement.
        }

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Worker_RejectsDuplicateTerminalFrames()
    {
        await using var fixture = await WorkerFixture.CreateAsync("duplicate-terminal");
        await fixture.StartAndActivateAsync();

        var result = await fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" }),
            CancellationToken.None);
        Assert.True(result.GetProperty("accepted").GetBoolean());
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Worker_IgnoresCancellationThatCrossesACompletedHostResponse()
    {
        await using var fixture = await WorkerFixture.CreateAsync("late-worker-cancel");
        await fixture.StartAndActivateAsync();

        var result = await fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" }),
            CancellationToken.None);

        Assert.True(result.GetProperty("accepted").GetBoolean());
        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);
    }

    [Fact]
    public async Task Worker_BoundsStartupShutdownAndStderr()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => WorkerFixture.CreateAsync(
            "startup-hang",
            new RuntimeProcessPolicyOptions
            {
                StartupTimeout = TimeSpan.FromMilliseconds(150),
            }));

        await using (var shutdown = await WorkerFixture.CreateAsync("shutdown-hang", new RuntimeProcessPolicyOptions
                     {
                         ShutdownTimeout = TimeSpan.FromMilliseconds(150),
                     }))
        {
            await shutdown.StartAndActivateAsync();
            var started = Stopwatch.GetTimestamp();
            await shutdown.Worker.StopAsync();
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        }

        await using (var activation = await WorkerFixture.CreateAsync("activation-hang"))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await activation.Worker.StartAsync(cancellation.Token);
            var generation = new RuntimePackageGeneration(activation.ActivationId, 1);
            await activation.Worker.CommitGenerationAsync(generation, cancellation.Token);
            await activation.Worker.PrepareStorageMigrationsAsync(generation, cancellation.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                activation.Worker.ActivateGenerationAsync(cancellation.Token));
            await activation.Worker.StopAsync();
        }

        var logger = new CapturingLogger();
        await using (var stderr = await WorkerFixture.CreateAsync("stderr", new RuntimeProcessPolicyOptions
                     {
                         MaxStderrBytes = 1024,
                         MaxStderrLineCharacters = 128,
                     }, logger))
        {
            await stderr.StartAndActivateAsync();
            await stderr.Worker.StopAsync();
        }
        Assert.Contains(logger.Messages, message => message.Contains("exceeded 1024 bytes", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains(new string('x', 256), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("invalid-output")]
    [InlineData("handler-fault")]
    public async Task Broker_InvalidFakeWorkerOutputFaultsExactProcessActivation(string mode)
    {
        await using var fixture = await WorkerFixture.CreateAsync(mode);
        var activation = await fixture.PublishBrokerSessionAsync();

        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await activation.Client.InvokeAsync(
                activation.Endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "hello" })));

        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
        if (mode == "invalid-output")
        {
            Assert.Equal("rpc.provider.invalid-output", failure.Error.Code);
            Assert.Equal("The provider returned output that violates its contract.", failure.Error.Message);
            Assert.True(RuntimeRpcFailureContextStore.TryGet(failure, out var diagnostic));
            Assert.Equal("caller.package", diagnostic.CallerPackageId);
            Assert.Equal("1.0.0", diagnostic.CallerPackageVersion);
            Assert.NotNull(diagnostic.CallerActivationId);
            Assert.Equal("process.test", diagnostic.ProviderPackageId);
            Assert.Equal("1.0.0", diagnostic.ProviderPackageVersion);
            Assert.Equal(fixture.ActivationId, diagnostic.ProviderActivationId);
            Assert.Equal("example.rpc", diagnostic.ContractId);
            Assert.Equal("messages", diagnostic.ServiceId);
            Assert.Equal("send", diagnostic.MethodId);
            Assert.Equal("unary", diagnostic.CallKind);
            Assert.Equal("type-mismatch", diagnostic.ValidationCode);
            Assert.Equal("$.*", diagnostic.ValidationPath);
            Assert.True(diagnostic.ValueUtf8Bytes > 0);
            Assert.True(diagnostic.ValueMaximumDepth > 0);
            Assert.True(diagnostic.ValueTotalNodes > 0);
            Assert.True(diagnostic.ValueMaximumObjectMembers > 0);
            Assert.Equal(0, diagnostic.ValueInlineDecodedBytes);

            var logFiles = Directory.GetFiles(fixture.Context.Storage.LogsRootPath, "*.log");
            var persisted = string.Join(
                Environment.NewLine,
                await Task.WhenAll(logFiles.Select(static path => File.ReadAllTextAsync(path))));
            Assert.Contains("event=runtime.rpc.provider-fault", persisted, StringComparison.Ordinal);
            Assert.Contains("rpc_call_kind=unary", persisted, StringComparison.Ordinal);
            Assert.Contains("rpc_validation_code=type-mismatch", persisted, StringComparison.Ordinal);
            Assert.Contains("rpc_validation_path=$.*", persisted, StringComparison.Ordinal);
            Assert.Contains("rpc_value_utf8_bytes=", persisted, StringComparison.Ordinal);
            Assert.Contains("rpc_value_inline_decoded_bytes=0", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("accepted", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("not-a-boolean", persisted, StringComparison.Ordinal);
        }
        Assert.Empty(fixture.Catalog.GetSnapshot().Providers);
        Assert.Equal(PackageReadinessState.Failed, fixture.Owner.State.GetSessionPackage("process.test")!.Readiness);
        Assert.Equal(PackageFailureOrigin.RuntimeRpcProvider, fixture.Owner.State.GetSessionPackage("process.test")!.FailureOrigin);
    }

    [Fact]
    public async Task Broker_PermissionRevocationCancelsFakeWorkerCallWithoutFaultingProcess()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        var activation = await fixture.PublishBrokerSessionAsync();
        var call = activation.Client.InvokeAsync(
            activation.Endpoint,
            "messages",
            "wait",
            JsonSerializer.SerializeToElement(new { message = "wait" })).AsTask();
        using var invocationEntered = await fixture.WaitForWorkerStatusAsync("host-invoke-wait-entered.json");

        await fixture.SetInvokePermissionAsync(RuntimeRpcPermissionState.Denied);

        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () => await call);
        Assert.Equal(SunderRpcErrorKind.Cancelled, failure.Error.Kind);
        Assert.Single(fixture.Catalog.GetSnapshot().Providers);
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage("process.test")!.Readiness);
    }

    [Fact]
    public async Task Broker_RejectsProcessProviderForgedInfrastructureError()
    {
        await using var fixture = await WorkerFixture.CreateAsync("forwarded-error");
        var activation = await fixture.PublishBrokerSessionAsync();

        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await activation.Client.InvokeAsync(
                activation.Endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "hello" })));

        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
        Assert.Empty(fixture.Catalog.GetSnapshot().Providers);
        Assert.Equal(PackageReadinessState.Failed, fixture.Owner.State.GetSessionPackage("process.test")!.Readiness);
    }

    [Fact]
    public async Task Broker_PreservesValidatedProcessProviderDomainErrorDetails()
    {
        await using var fixture = await WorkerFixture.CreateAsync("domain-details");
        var activation = await fixture.PublishBrokerSessionAsync();

        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await activation.Client.InvokeAsync(
                activation.Endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "hello" })));

        Assert.Equal(SunderRpcErrorKind.Domain, failure.Error.Kind);
        Assert.Equal("duplicate", failure.Error.Details!.Value.GetProperty("reason").GetString());
        Assert.Single(fixture.Catalog.GetSnapshot().Providers);
    }

    [Fact]
    public async Task ProcessWorker_RejectsForgedInfrastructureErrorDetails()
    {
        await using var fixture = await WorkerFixture.CreateAsync("forged-error-details");
        await fixture.StartAndActivateAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Worker.InvokeUnaryAsync(
            "example.provider",
            fixture.InvocationContext(),
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" }),
            CancellationToken.None).AsTask());
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SessionRetirement_DrainsFakeWorkerCallBeforeProcessShutdown()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        var activation = await fixture.PublishBrokerSessionAsync();
        var call = activation.Client.InvokeAsync(
            activation.Endpoint,
            "messages",
            "wait",
            JsonSerializer.SerializeToElement(new { message = "wait" })).AsTask();
        using var invocationEntered = await fixture.WaitForWorkerStatusAsync("host-invoke-wait-entered.json");

        var retirement = fixture.Owner.State.ClearActiveSessionAsync();
        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () => await call);
        await retirement;

        Assert.Equal(SunderRpcErrorKind.Cancelled, failure.Error.Kind);
        await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HostStop_ShutsDownProcessActivationWithoutRestart()
    {
        using var hostStopping = new CancellationTokenSource();
        await using var fixture = await WorkerFixture.CreateAsync(
            "normal",
            hostStopping: hostStopping.Token);
        await fixture.StartAndActivateAsync();

        hostStopping.Cancel();

        await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class RecordingExecutionRuntimeLaunchResolver : IExecutionRuntimeLaunchResolver
    {
        public List<string> LeaseIds { get; } = [];

        public async Task<ResolvedExecutionRuntimeLaunch> ResolveAsync(
            PreparedRuntimePackage package,
            Guid activationId,
            CancellationToken cancellationToken)
        {
            var launch = await TestExecutionRuntimeLaunchResolver.Instance.ResolveAsync(
                package,
                activationId,
                cancellationToken);
            LeaseIds.Add(launch.LeaseId);
            return launch;
        }
    }

    private sealed class FailOnceExecutionRuntimeProcessLease(TestExecutionRuntimeProcessLease inner)
        : IExecutionRuntimeProcessLease
    {
        public string LeaseId => inner.LeaseId;

        public string RuntimeRootPath => inner.RuntimeRootPath;

        public int ReleaseAttempts { get; private set; }

        public bool Released => inner.Released;

        public Task<IExecutionRuntimeContainedProcess> LaunchAsync(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken = default,
            Sunder.Package.Projections.PackageProjectionAcquisition? packageProjection = null)
            => inner.LaunchAsync(startInfo, cancellationToken, packageProjection);

        public Task ReleaseAfterProcessTreeTerminationAsync(CancellationToken cancellationToken = default)
        {
            ReleaseAttempts++;
            if (ReleaseAttempts == 1)
            {
                throw new IOException("Synthetic execution-root release failure.");
            }
            return inner.ReleaseAfterProcessTreeTerminationAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _mode;
        private readonly RuntimeRpcCatalog _catalog;
        private readonly RuntimePackageRecoveryQueue _recoveryQueue;
        private readonly RuntimeRpcPermissionStore _permissions;
        private readonly RuntimeSessionOwner _owner;
        private readonly RuntimePackageDataResources _context;
        private readonly RuntimeContentTransferStore _transfers;
        private readonly RuntimeRpcBroker _broker;

        private WorkerFixture(
            string root,
            string mode,
            PreparedRuntimePackage prepared,
            Guid activationId,
            ProcessRuntimeWorker worker,
            RuntimeRpcCatalog catalog,
            RuntimePackageRecoveryQueue recoveryQueue,
            RuntimeRpcPermissionStore permissions,
            RuntimeSessionOwner owner,
            RuntimePackageDataResources context,
            RuntimeContentTransferStore transfers,
            RuntimeRpcBroker broker)
        {
            _root = root;
            _mode = mode;
            Prepared = prepared;
            ActivationId = activationId;
            Worker = worker;
            _catalog = catalog;
            _recoveryQueue = recoveryQueue;
            _permissions = permissions;
            _owner = owner;
            _context = context;
            _transfers = transfers;
            _broker = broker;
        }

        public PreparedRuntimePackage Prepared { get; }
        public Guid ActivationId { get; }
        public ProcessRuntimeWorker Worker { get; }
        public RuntimeRpcCatalog Catalog => _catalog;
        public RuntimeSessionOwner Owner => _owner;
        public RuntimeContentTransferStore Transfers => _transfers;
        public RuntimePackageDataResources Context => _context;

        public static async Task<WorkerFixture> CreateAsync(
            string mode,
            RuntimeProcessPolicyOptions? policy = null,
            ILogger? logger = null,
            CancellationToken hostStopping = default,
            bool enforcePackageSessionLease = false,
            string packageVersion = "1.0.0",
            string? packageDataRoot = null,
            IExecutionRuntimeProcessLease? executionRuntimeLease = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "sunder-worker-tests", Guid.NewGuid().ToString("N"));
            var shadow = Path.Combine(root, "projection");
            TestWorkerFixture.CopyPayload(shadow);
            await File.WriteAllTextAsync(Path.Combine(shadow, "mode.txt"), mode);
            var entryPoint = Path.Combine(shadow, "Sunder.Runtime.Host.TestWorker.dll");
            var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
            var extendedDeadline = mode.StartsWith("nested-call-", StringComparison.Ordinal);
            var target = new SunderPackageTargetManifest
            {
                Role = SunderPackageFormat.RuntimeHostRole,
                Rid = rid,
                Kind = SunderPackageFormat.WorkerTargetKind,
                EntryPoint = Path.GetFileName(entryPoint),
                TargetFramework = "net10.0",
                SdkVersion = "1.1.0",
                RequiredHostCapabilities = extendedDeadline
                    ?
                    [
                        "core.v1",
                        "execution-runtime.v1",
                        "rpc.extended-deadline.v1",
                        "rpc.v1",
                        "sdk-baseline-1-1.v1",
                        "worker-protocol.v1",
                    ]
                    : ["core.v1", "execution-runtime.v1", "rpc.v1", "sdk-baseline-1-1.v1", "worker-protocol.v1"],
                ExecutionRuntime = new SunderPackageExecutionRuntimeManifest
                {
                    Id = SunderExecutionRuntimeFormat.DotNetRuntimeId,
                    VersionRange = ">=10.0.0 <11.0.0",
                    ModuleAbi = SunderExecutionRuntimeFormat.DotNetModuleAbi,
                },
            };
            var manifest = new SunderPackageManifest
            {
                ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion,
                ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
                Id = "process.test",
                Name = "Process Test",
                Version = packageVersion,
                Targets = [target],
                ContractBundles =
                [
                    new SunderPackageContractBundleManifest
                    {
                        Source = "owned",
                        AuthorityKind = "package",
                        AuthorityId = "process.test",
                        ContractId = Contract.ContractId,
                        Version = Contract.Version,
                        DescriptorPath = "contracts/example.rpc.json",
                        DescriptorSha256 = Contract.Sha256,
                        AbiSha256 = Contract.AbiSha256,
                        Exposure = SunderPackageFormat.PublicContractExposure,
                    },
                ],
                UsesContracts =
                [
                    new SunderPackageContractUseManifest
                    {
                        Role = SunderPackageFormat.RuntimeHostRole,
                        ContractId = Contract.ContractId,
                        ContractVersion = Contract.Version,
                        AbiSha256 = Contract.AbiSha256,
                        Required = true,
                        Actions = ["discover", "invoke", "subscribe"],
                    },
                ],
                Provides = [ProviderManifest()],
            };
            var key = new SunderPackageTargetKey(SunderPackageFormat.RuntimeHostRole, rid);
            var source = new RuntimePackageSource(
                "process.test",
                PackageSourceKind.Installed,
                shadow,
                Manifest: manifest,
                SelectedTargetKey: key,
                SelectedTarget: target);
            var prepared = new PreparedRuntimePackage(
                shadow,
                source,
                shadow,
                "process.test",
                packageVersion,
                PackageHostRoles.Runtime,
                new RuntimePackageActivationState("process.test", "Process Test", packageVersion, PackageHostRoles.Runtime, null, key, target),
                entryPoint,
                key,
                target,
                [],
                new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal)
                {
                    [PackageSessionPreparer.ContractKey(Contract.ContractId, Contract.Version)] = Contract,
                },
                new string('a', 64))
            {
                SessionId = Guid.NewGuid().ToString("N"),
            };
            var paths = new RuntimePackagePaths(Path.Combine(root, "runtime-data"));
            var catalog = new RuntimeRpcCatalog();
            var recoveryQueue = new RuntimePackageRecoveryQueue();
            var owner = new RuntimeSessionOwner(
                NullLogger<RuntimeSessionOwner>.Instance,
                new RuntimeEventStreamService(),
                rpcCatalog: catalog,
                recoveryQueue: recoveryQueue);
            var permissions = new RuntimeRpcPermissionStore(paths);
            var transfers = new RuntimeContentTransferStore(paths);
            var broker = new RuntimeRpcBroker(
                catalog,
                permissions,
                owner.State,
                owner,
                hostStopping: CancellationToken.None,
                policy: null,
                timeProvider: null,
                contentStore: transfers,
                transportPolicy: new RuntimeTransportPolicyOptions());
            var activationId = Guid.NewGuid();
            var context = new RuntimePackageDataResources(
                "process.test",
                packageVersion,
                packageDataRoot ?? paths.PackageDataRootPath);
            var launch = executionRuntimeLease is null
                ? await TestExecutionRuntimeLaunchResolver.Instance.ResolveAsync(
                    prepared,
                    activationId,
                    CancellationToken.None)
                : new ResolvedExecutionRuntimeLaunch(
                    TestExecutionRuntimeLaunchResolver.Instance.RuntimeRootPath,
                    TestExecutionRuntimeLaunchResolver.Instance.LauncherPath,
                    [SunderExecutionRuntimeFormat.DotNetExecArgument, Path.GetFullPath(prepared.EntryAssemblyPath!)],
                    executionRuntimeLease,
                    prepared.ExecutionRuntimeBinding?.Identity);
            var worker = new ProcessRuntimeWorker(
                logger ?? NullLogger.Instance,
                prepared,
                context,
                activationId,
                broker,
                launch,
                policy,
                hostStopping,
                enforcePackageSessionLease ? owner.State : null);
            var fixture = new WorkerFixture(
                root,
                mode,
                prepared,
                activationId,
                worker,
                catalog,
                recoveryQueue,
                permissions,
                owner,
                context,
                transfers,
                broker);
            try
            {
                await worker.ComposeAsync();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task StartAndActivateAsync()
        {
            await Worker.StartAsync();
            var generation = new RuntimePackageGeneration(ActivationId, 1);
            await Worker.CommitGenerationAsync(generation);
            await Worker.PrepareStorageMigrationsAsync(generation);
            await Worker.ActivateGenerationAsync();
        }

        public Task PrepareStorageMigrationsAsync()
            => Worker.PrepareStorageMigrationsAsync(
                new RuntimePackageGeneration(ActivationId, 1));

        public async Task<PackageActivationResult> ActivatePreparedPackageAsync(
            IExecutionRuntimeLaunchResolver? launchResolver = null)
        {
            var warnings = new List<string>();
            var errors = new List<string>();
            return await new RuntimePackageActivator(
                    NullLogger.Instance,
                    new RuntimePackagePaths(Path.Combine(_root, "runtime-data")),
                    _broker,
                    packageSessionState: _owner.State,
                    executionRuntimeLaunchResolver: launchResolver
                                                    ?? TestExecutionRuntimeLaunchResolver.Instance)
                .ActivateAsync(
                    Prepared,
                    warnings,
                    errors,
                    CancellationToken.None);
        }

        public async Task WaitForActivationAsync()
            => await Worker.ActivationCompletion.WaitAsync(TimeSpan.FromSeconds(5));

        public async Task<(RuntimePackageRecoveryRequest Request, ActiveLoadedPackage Candidate)>
            BeginRecoveryCandidateAsync()
        {
            var candidate = CreateProcessLoadedPackage();
            var failedActivationId = Guid.NewGuid();
            var failedPackage = candidate with
            {
                Worker = new TestRuntimePackageWorker(),
                RuntimeActivationId = failedActivationId,
                RpcProviders = new Dictionary<string, RuntimeRpcProviderRegistration>(StringComparer.Ordinal),
                RecoveryFactory = () => candidate,
            };
            var session = new ActivePackageSession(
                sessionFolder: null,
                new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
                {
                    [failedPackage.Descriptor.PackageId] = failedPackage,
                },
                new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
                {
                    [failedPackage.Descriptor.PackageId] = new SessionPackageDescriptor(
                        failedPackage.Descriptor.PackageId,
                        failedPackage.Descriptor.DisplayName,
                        failedPackage.Descriptor.Version,
                        PackageHostRoles.Runtime,
                        null,
                        true,
                        PackageReadinessState.Ready,
                        [],
                        null,
                        null,
                        null,
                        0),
                },
                workersStarted: false,
                readySources: [Prepared.Source]);
            var publisher = new PackageSessionPublisher(
                _owner,
                NullLogger<PackageSessionPublisher>.Instance,
                rpcCatalog: _catalog);
            await publisher.PublishAsync(
                session,
                _owner.Sources.Snapshot(),
                [],
                [],
                _owner.Generation,
                CancellationToken.None);
            var activationGeneration = _owner.Generation;

            _owner.HandleRuntimeGenerationFault(
                failedPackage.Descriptor.PackageId,
                new RuntimePackageGeneration(failedActivationId, activationGeneration),
                new InvalidOperationException("old activation failed"));
            var request = await _recoveryQueue.Reader.ReadAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await request.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                RuntimePackageRecoveryValidation.Ready,
                _owner.State.BeginPackageRecoveryActivation(request, candidate));
            _catalog.PreparePackageRecovery(
                candidate,
                _owner.Generation,
                request.ActivationGeneration);
            return (request, candidate);
        }

        public async Task RevokeRecoveryCandidateAsync(
            RuntimePackageRecoveryRequest request,
            ActiveLoadedPackage candidate)
        {
            var packageDrain = _owner.State.AbandonPackageRecoveryActivation(
                request,
                candidate.RuntimeActivationId);
            var rpcDrain = _catalog.DiscardPackageRecovery(
                request.PackageId,
                candidate.RuntimeActivationId);
            await Task.WhenAll(packageDrain, rpcDrain).WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task PublishCandidateSessionAsync()
        {
            await _permissions.SetAsync(
                PermissionKey("process.test", SunderRpcProtocol.DiscoverAction),
                RuntimeRpcPermissionState.Granted,
                CancellationToken.None);
            var processPackage = CreateProcessLoadedPackage();
            var session = new ActivePackageSession(
                sessionFolder: null,
                new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
                {
                    [processPackage.Descriptor.PackageId] = processPackage,
                },
                new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
                {
                    [processPackage.Descriptor.PackageId] = new SessionPackageDescriptor(
                        processPackage.Descriptor.PackageId,
                        processPackage.Descriptor.DisplayName,
                        processPackage.Descriptor.Version,
                        PackageHostRoles.Runtime,
                        null,
                        true,
                        PackageReadinessState.Ready,
                        [],
                        null,
                        null,
                        null,
                        0),
                },
                workersStarted: false);
            var publisher = new PackageSessionPublisher(
                _owner,
                NullLogger<PackageSessionPublisher>.Instance,
                rpcCatalog: _catalog);
            await publisher.PublishAsync(
                session,
                _owner.Sources.Snapshot(),
                [],
                [],
                _owner.Generation,
                CancellationToken.None);
        }

        public async Task<BrokerActivation> PublishBrokerSessionAsync()
        {
            await Worker.StartAsync();
            var processPackage = CreateProcessLoadedPackage();

            // This caller is a synthetic owned-contract fixture, not an imported authority.
            var callerManifest = new SunderPackageManifest
            {
                Id = "caller.package",
                Name = "Caller Package",
                Version = "1.0.0",
                ContractBundles =
                [
                    new SunderPackageContractBundleManifest
                    {
                        Source = "owned",
                        AuthorityKind = "package",
                        AuthorityId = "caller.package",
                        ContractId = Contract.ContractId,
                        Version = Contract.Version,
                        DescriptorPath = "contracts/example.rpc.json",
                        DescriptorSha256 = Contract.Sha256,
                        AbiSha256 = Contract.AbiSha256,
                        Exposure = SunderPackageFormat.PublicContractExposure,
                    },
                ],
                UsesContracts =
                [
                    new SunderPackageContractUseManifest
                    {
                        Role = SunderPackageFormat.RuntimeHostRole,
                        ContractId = Contract.ContractId,
                        ContractVersion = Contract.Version,
                        AbiSha256 = Contract.AbiSha256,
                        Required = true,
                        Actions = ["discover", "invoke", "subscribe"],
                    },
                ],
                Provides = [],
            };
            var callerContext = new RuntimePackageDataResources(
                "caller.package",
                "1.0.0",
                Path.Combine(_root, "caller-data"));
            var caller = new ActiveLoadedPackage(
                new ActivePackageDescriptor(
                    "caller.package",
                    "Caller Package",
                    "1.0.0",
                    PackageHostRoles.Runtime,
                    null,
                    true,
                    PackageReadinessState.Ready,
                    []),
                new RuntimePackageSource("caller.package", PackageSourceKind.Installed, Prepared.ShadowFolder, Manifest: callerManifest),
                SettingsSchema: null,
                callerContext.Storage.State,
                callerContext.Secrets,
                callerContext.Storage.Files,
                callerContext.Settings,
                new TestRuntimePackageWorker(disposeAsync: () =>
                {
                    callerContext.Dispose();
                    return ValueTask.CompletedTask;
                }))
            {
                RuntimeActivationId = Guid.NewGuid(),
                RpcContracts = Prepared.RpcContracts!,
                RpcContractUses = callerManifest.UsesContracts!.Select(static use => use!).ToArray(),
                RpcManifestSha256 = new string('a', 64),
            };
            var packages = new[] { caller, processPackage };
            var session = new ActivePackageSession(
                sessionFolder: null,
                packages.ToDictionary(static package => package.Descriptor.PackageId, StringComparer.OrdinalIgnoreCase),
                packages.ToDictionary(
                    static package => package.Descriptor.PackageId,
                    static package => new SessionPackageDescriptor(
                        package.Descriptor.PackageId,
                        package.Descriptor.PackageId,
                        package.Descriptor.Version,
                        PackageHostRoles.Runtime,
                        null,
                        true,
                        PackageReadinessState.Ready,
                        [],
                        null,
                        null,
                        null,
                        0),
                    StringComparer.OrdinalIgnoreCase),
                workersStarted: true);
            await _owner.PublishAsync(
                session,
                _owner.Sources.Snapshot(),
                [],
                [],
                _owner.Generation);
            if (!string.Equals(_mode, "v1-scope-permission-denied", StringComparison.Ordinal))
            {
                foreach (var action in new[]
                         {
                             SunderRpcProtocol.DiscoverAction,
                             SunderRpcProtocol.InvokeAction,
                             SunderRpcProtocol.SubscribeAction,
                         })
                {
                    await _permissions.SetAsync(
                        PermissionKey("process.test", action),
                        RuntimeRpcPermissionState.Granted,
                        CancellationToken.None);
                }
            }
            await session.CommitRuntimeGenerationAsync(_owner.Generation);
            _catalog.PrepareCandidateSession(session, _owner.Generation);
            await session.ActivateCommittedRuntimeGenerationAsync();
            _catalog.ActivateSession(session, _owner.Generation);
            await _permissions.SetAsync(
                PermissionKey("caller.package", SunderRpcProtocol.DiscoverAction),
                RuntimeRpcPermissionState.Granted,
                CancellationToken.None);
            await SetInvokePermissionAsync(RuntimeRpcPermissionState.Granted);
            var client = new RuntimeRpcClient(
                new RuntimeRpcBroker(
                    _catalog,
                    _permissions,
                    _owner.State,
                    _owner,
                    hostStopping: CancellationToken.None,
                    policy: null,
                    timeProvider: null,
                    contentStore: _transfers,
                    transportPolicy: new RuntimeTransportPolicyOptions()),
                new RuntimeRpcCallerStamp("caller.package", caller.RuntimeActivationId));
            var endpoint = Assert.Single((await client.DiscoverAsync(Contract.ContractId)).Providers).Endpoint;
            return new BrokerActivation(client, endpoint);
        }

        private ActiveLoadedPackage CreateProcessLoadedPackage()
        {
            var processManifest = Prepared.Source.Manifest!;
            var declaration = Assert.Single(processManifest.Provides!)!;
            return new ActiveLoadedPackage(
                new ActivePackageDescriptor(
                    "process.test",
                    "Process Test",
                    Prepared.Version,
                    PackageHostRoles.Runtime,
                    null,
                    true,
                    PackageReadinessState.Ready,
                    []),
                Prepared.Source,
                SettingsSchema: null,
                _context.Storage.State,
                _context.Secrets,
                _context.Storage.Files,
                _context.Settings,
                Worker)
            {
                RuntimeActivationId = ActivationId,
                RpcContracts = Prepared.RpcContracts!,
                RpcContractUses = processManifest.UsesContracts!.Select(static use => use!).ToArray(),
                RpcProviders = new Dictionary<string, RuntimeRpcProviderRegistration>(StringComparer.Ordinal)
                {
                    [declaration.ProviderId!] = new RuntimeRpcProviderRegistration(
                        declaration.ProviderId!,
                        declaration.ContractId!,
                        declaration.ContractVersion!,
                        declaration.DescriptorSha256!,
                        Contract,
                        new ProcessRpcProviderHandler(Worker, declaration.ProviderId!),
                        declaration),
                },
                RpcManifestSha256 = new string('a', 64),
            };
        }

        public Task SetInvokePermissionAsync(RuntimeRpcPermissionState state)
            => _permissions.SetAsync(
                PermissionKey("caller.package", SunderRpcProtocol.InvokeAction),
                state,
                CancellationToken.None);

        public Task SetSubscribePermissionAsync(RuntimeRpcPermissionState state)
            => _permissions.SetAsync(
                PermissionKey("caller.package", SunderRpcProtocol.SubscribeAction),
                state,
                CancellationToken.None);

        private static RuntimeRpcPermissionKey PermissionKey(string packageId, string action)
            => new(
                packageId,
                "1.0.0",
                new string('a', 64),
                SunderPackageFormat.RuntimeHostRole,
                Contract.ContractId,
                Contract.Version,
                Contract.AbiSha256!,
                action);

        public SunderRpcInvocationContext InvocationContext()
            => new(
                "caller.package",
                "1.0.0",
                new SunderRpcProviderSnapshot(
                    "process.test",
                    Prepared.Version,
                    "example.provider",
                    "example.rpc",
                    "1.0.0",
                    new string('a', 64),
                    Contract.AbiSha256,
                    ActivationId,
                    1,
                    1,
                    new SunderRpcEndpointReference("rpc1_process"),
                    1,
                    SunderRpcProviderState.Active),
                DateTimeOffset.UtcNow.AddMinutes(1),
                1,
                CancellationToken.None);

        public async Task<JsonDocument> WaitForWorkerStatusAsync(string fileName)
        {
            var path = Path.Combine(
                Worker.WorkerTemporaryPath,
                fileName);
            var signal = FileSignal.WaitForAsync(
                path,
                TimeSpan.FromSeconds(5),
                () => $"generationCompleted={Worker.GenerationCompletion.IsCompleted}; temporaryRoot={Worker.WorkerTemporaryPath}");
            var completed = await Task.WhenAny(signal, Worker.GenerationCompletion);
            if (completed == Worker.GenerationCompletion && !File.Exists(path))
            {
                await Worker.GenerationCompletion;
                throw new InvalidOperationException("The worker stopped before writing its status file.");
            }
            await signal;
            return JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        }

        public int WorkerCallScopeCount()
        {
            var scopes = Assert.IsAssignableFrom<System.Collections.IDictionary>(typeof(ProcessRuntimeWorker)
                .GetField("_workerCallScopes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(Worker));
            return scopes.Count;
        }

        public int WorkerPayloadHandleCount()
        {
            var handles = Assert.IsAssignableFrom<System.Collections.IDictionary>(typeof(ProcessRuntimeWorker)
                .GetField("_workerPayloadHandles", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(Worker));
            return handles.Count;
        }

        public CancellationToken WorkerCallScopeRevocationToken()
        {
            var scopes = Assert.IsAssignableFrom<System.Collections.IDictionary>(typeof(ProcessRuntimeWorker)
                .GetField("_workerCallScopes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(Worker));
            var workerScope = Assert.Single(scopes.Values.Cast<object>());
            var scope = Assert.IsType<RuntimeRpcCallScope>(workerScope.GetType()
                .GetProperty("Scope")!
                .GetValue(workerScope));
            return scope.RevocationToken;
        }

        public string[] TransferFiles()
        {
            var path = new RuntimePackagePaths(Path.Combine(_root, "runtime-data")).TransferRootPath;
            return Directory.Exists(path) ? Directory.GetFiles(path) : [];
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Worker.DisposeAsync();
            }
            catch
            {
            }
            _catalog.Dispose();
            _permissions.Dispose();
            _transfers.Dispose();
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }

        internal static string ResolveConfiguration()
            => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent?.Name
               ?? "Debug";

        internal static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate the Sunder Core repository root.");
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo(string path, uint mode);

    private static string GetWorkerSdkFixtureOutput(string rid)
        => Path.Combine(
            WorkerFixture.FindRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "tests",
            "Sunder.Package.Build.TestFixtures",
            "WorkerV1Executable",
            "bin",
            WorkerFixture.ResolveConfiguration(),
            "net10.0",
            rid,
            "sunder-dev");

    private static string GetMaximumSemanticVersion()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "WorkerV1Conformance", "corpus.json")));
        var boundary = corpus.RootElement.GetProperty("semanticVersionBoundary");
        var prefix = boundary.GetProperty("prefix").GetString()!;
        var suffixCharacter = Assert.Single(boundary.GetProperty("suffixCharacter").GetString()!);
        var version = prefix + new string(
            suffixCharacter,
            boundary.GetProperty("acceptedSuffixLength").GetInt32());
        Assert.Equal(boundary.GetProperty("maximumLength").GetInt32(), version.Length);
        return version;
    }

    private static SunderPackageProviderManifest ProviderManifest()
        => new()
        {
            ProviderId = "example.provider",
            ContractId = Contract.ContractId,
            ContractVersion = Contract.Version,
            DescriptorSha256 = Contract.Sha256,
            AbiSha256 = Contract.AbiSha256,
            Role = SunderPackageFormat.RuntimeHostRole,
            Audience = SunderPackageFormat.PublicProviderAudience,
        };

    private static SunderRpcContractDescriptor Contract { get; } = SunderRpcContractDescriptor.Parse(
        System.Text.Encoding.UTF8.GetBytes("""
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "descriptorVersion":1,
          "schemaProfile":"sunder.rpc.values.v1",
          "contractId":"example.rpc",
          "version":"1.0.0",
          "errors":{"messages.rejected":{"detailsSchema":{"$ref":"#/$defs/ErrorDetails"}}},
          "services":{"messages":{"methods":{
            "send":{"kind":"unary","idempotency":"none","errors":["messages.rejected"],"requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Response"}},
            "wait":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Response"}},
            "content-roundtrip":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/ContentRequest"},"responseSchema":{"$ref":"#/$defs/ContentResponse"}},
            "watch":{"kind":"server-stream","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Request"},"eventSchema":{"$ref":"#/$defs/Response"}}
          }}},
          "$defs":{
            "Request":{"type":"object","properties":{"message":{"type":"string","minLength":1,"maxLength":128}},"required":["message"],"additionalProperties":false},
            "Response":{"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false},
            "ErrorDetails":{"type":"object","properties":{"reason":{"type":"string","minLength":1,"maxLength":128}},"required":["reason"],"additionalProperties":false},
            "ContentReference":{"type":"object","properties":{"id":{"type":"string","minLength":1,"maxLength":128},"length":{"type":"integer","minimum":0,"maximum":67108864},"sha256":{"type":"string","minLength":64,"maxLength":64},"mediaType":{"type":"string","minLength":1,"maxLength":128},"fileName":{"type":"string","minLength":1,"maxLength":255},"expiresAtUtc":{"type":"string","minLength":1,"maxLength":64},"repeatability":{"type":"string","minLength":1,"maxLength":32,"enum":["single-use","repeatable"]}},"required":["id","length","sha256","mediaType","fileName","expiresAtUtc","repeatability"],"additionalProperties":false},
            "ContentRequest":{"type":"object","properties":{"message":{"type":"string","minLength":1,"maxLength":128},"content":{"$ref":"#/$defs/ContentReference"}},"required":["message","content"],"additionalProperties":false},
            "ContentResponse":{"type":"object","properties":{"accepted":{"type":"boolean"},"content":{"$ref":"#/$defs/ContentReference"},"openedFileReleased":{"type":"boolean"}},"required":["accepted","content","openedFileReleased"],"additionalProperties":false}
          }
        }
        """));

    private static SunderRpcContentReference ReadContentReference(JsonElement value)
        => new(
            value.GetProperty("id").GetString()!,
            value.GetProperty("length").GetInt64(),
            value.GetProperty("sha256").GetString()!,
            value.GetProperty("mediaType").GetString()!,
            value.GetProperty("fileName").GetString()!,
            value.GetProperty("expiresAtUtc").GetDateTimeOffset(),
            value.GetProperty("repeatability").GetString() == "single-use"
                ? SunderRpcContentRepeatability.SingleUse
                : SunderRpcContentRepeatability.Repeatable);

    private sealed record BrokerActivation(RuntimeRpcClient Client, SunderRpcEndpointReference Endpoint);

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
