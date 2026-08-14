using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;
using Xunit;
using SdkPackageAuthStatusKind = Sunder.Sdk.Authentication.PackageAuthStatusKind;

namespace Sunder.Runtime.Host.Tests;

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
    public async Task WorkerSdkPackagedExecutable_HandshakesActivatesAndShutsDown()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-worker-sdk-host-tests", Guid.NewGuid().ToString("N"));
        var catalog = new RuntimeRpcCatalog();
        RuntimeRpcPermissionStore? permissions = null;
        RuntimeContentTransferStore? transfers = null;
        try
        {
            var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
            var errors = new List<string>();
            var prepared = await new PackageSessionPreparer(rid).PrepareDevPackageAsync(
                0,
                GetWorkerSdkFixtureOutput(rid),
                Path.Combine(root, "session"),
                errors,
                CancellationToken.None);
            Assert.NotNull(prepared);
            Assert.Empty(errors);
            Assert.Equal(SunderPackageFormat.WorkerTargetKind, prepared.SelectedTarget?.Kind);
            Assert.Contains(
                SunderWorkerProtocol.V3Capability,
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
            await using var worker = new ProcessRuntimeWorker(
                NullLogger.Instance,
                prepared,
                context,
                activationId,
                broker);

            await worker.ComposeAsync();
            await worker.StartAsync();
            var generation = new RuntimePackageGeneration(activationId, 1);
            await worker.CommitGenerationAsync(generation);
            await worker.PrepareStorageMigrationsAsync(generation);
            await worker.ActivateGenerationAsync().WaitAsync(TimeSpan.FromSeconds(5));
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
            await worker.StopAsync();
            await worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            catalog.Dispose();
            permissions?.Dispose();
            transfers?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkerSdkPackagedExecutable_ExactArchiveInstallsThroughRuntimeHostAndRecordsReleaseEvidence()
    {
        var archivePath = Environment.GetEnvironmentVariable("SUNDER_DOTNET_WORKER_ARCHIVE_PATH");
        if (string.IsNullOrWhiteSpace(archivePath)) return;
        archivePath = Path.GetFullPath(archivePath);
        Assert.True(File.Exists(archivePath), $"Native .NET worker archive does not exist: {archivePath}");
        var root = Path.Combine(Path.GetTempPath(), "sunder-dotnet-worker-release-tests", Guid.NewGuid().ToString("N"));
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
            Assert.Equal("test.worker.v3.executable", installed.PackageId);
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
                var timeout = Stopwatch.StartNew();
                while (IsProcessAlive(processId) && timeout.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await Task.Delay(50);
                }
                Assert.False(IsProcessAlive(processId), $"Worker process {processId} survived Runtime Host shutdown.");
            }
            if (workerTemporaryPath is not null)
            {
                Assert.False(
                    Directory.Exists(workerTemporaryPath),
                    $"Worker temporary directory survived Runtime Host shutdown: {workerTemporaryPath}");
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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
    public async Task WorkerV3_CannotAcknowledgeBeforeDeclaredStorageMigrationsComplete()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v3-skip-declared-migration");

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.StartAndActivateAsync());
        await Assert.ThrowsAsync<SunderWorkerProtocolException>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WorkerV3_MigrationPreparationAdmitsOnlyHostMediatedStateReads()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v3-migration-preparation-state");
        await fixture.Context.Storage.State.SetValueAsync("legacy.value", "stored");
        await fixture.Worker.StartAsync();
        var generation = new RuntimePackageGeneration(fixture.ActivationId, 1);
        await fixture.Worker.CommitGenerationAsync(generation);

        await fixture.Worker.PrepareStorageMigrationsAsync(generation);

        using var status = await fixture.WaitForWorkerStatusAsync("v3-migration-preparation-state.json");
        Assert.Equal("stored", status.RootElement.GetProperty("stateValue").GetString());
        Assert.Equal(
            ["legacy.value"],
            status.RootElement.GetProperty("keys").EnumerateArray().Select(static item => item.GetString()));
        Assert.Equal("stored", await fixture.Context.Storage.State.GetValueAsync("legacy.value"));
        await fixture.Worker.ActivateGenerationAsync();
    }

    [Fact]
    public async Task WorkerV3_MigrationPreparationRejectsStateMutation()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v3-migration-preparation-mutation");
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
    public async Task WorkerV3_MigrationPreparationRequiresExactCommittedGeneration()
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
    public async Task WorkerV3_FailedActivationRollsBackHostMediatedStateSecretsAndJournal()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v3-transaction-migration-fail",
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
    public async Task WorkerV3_SuccessfulActivationCommitsCanonicalMigrationJournal()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v3-transaction-migration",
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
    public async Task WorkerV3_MigrationJournalSkipsCompletedVersionAndRunsAfterVersionJump()
    {
        var sharedDataRoot = Path.Combine(
            Path.GetTempPath(),
            "sunder-worker-migration-journal-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            await using (var first = await WorkerFixture.CreateAsync(
                             "v3-transaction-migration",
                             enforcePackageSessionLease: true,
                             packageDataRoot: sharedDataRoot))
            {
                await first.Context.Storage.State.SetValueAsync("legacy.state.v1", "first-state");
                await first.Context.Secrets.SetSecretAsync("legacy.secret.v1", "first-secret");
                await first.PublishCandidateSessionAsync();
            }

            await using (var sameVersion = await WorkerFixture.CreateAsync(
                             "v3-transaction-migration",
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
                             "v3-transaction-migration",
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
    public async Task WorkerV3_ComposesStartsCommitsActivatesInvokesAndShutsDown()
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
    public async Task WorkerV3_ProviderFaultPersistsOnlySafeIdentityAndCorrelatesBrokerFailure()
    {
        const string secret = "provider-secret=must-never-persist";
        const string exceptionType = "SecretBearingProviderException";
        var logger = new CapturingLogger();
        await using var fixture = await WorkerFixture.CreateAsync(
            "v3-provider-fault",
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

    [Fact]
    public async Task WorkerV3_WhenReadyBindsV1_FailsWithoutFallback()
    {
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await WorkerFixture.CreateAsync("protocol-mismatch"));
    }

    [Fact]
    public async Task WorkerV3_LoggingIsAdmittedAtomicallyWithReady()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v3-ready-logging");

        using var status = await fixture.WaitForWorkerStatusAsync("v3-ready-logging.json");
        Assert.True(status.RootElement.GetProperty("accepted").GetBoolean());
        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);
    }

    [Fact]
    public async Task WorkerV3_SettingsStateSchemaAndPayloadsUseHostAuthority()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v3-package-data",
            enforcePackageSessionLease: true);

        Assert.NotNull(fixture.Worker.SettingsSchema);
        var schema = fixture.Worker.SettingsSchema!;
        var section = Assert.Single(schema.Sections);
        Assert.Equal("general", section.SectionId);
        Assert.Equal(["enabled", "token"], section.Fields.Select(static field => field.Key));

        await fixture.PublishBrokerSessionAsync();
        await fixture.WaitForActivationAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v3-package-data.json");
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
    public async Task WorkerV3_FilesAndSecretsUseHostAuthority()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v3-files-secrets",
            enforcePackageSessionLease: true);

        await fixture.PublishBrokerSessionAsync();
        await fixture.WaitForActivationAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v3-files-secrets.json");
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
    public async Task WorkerV3_LoggingUsesHostSinksAndDrainsShutdown()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v3-logging");

        await fixture.PublishBrokerSessionAsync();
        await fixture.WaitForActivationAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v3-logging.json");
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
    public async Task WorkerV3_MalformedShutdownLoggingFaultsProtocol()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v3-malformed-shutdown-logging");
        await fixture.StartAndActivateAsync();
        await fixture.WaitForActivationAsync();

        await Assert.ThrowsAsync<SunderWorkerProtocolException>(async () =>
            await fixture.Worker.StopAsync());
        await Assert.ThrowsAsync<SunderWorkerProtocolException>(async () =>
            await fixture.Worker.GenerationCompletion);
    }

    [Fact]
    public async Task WorkerV3_InvalidSettingsSchemaFailsComposition()
    {
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await WorkerFixture.CreateAsync("v3-invalid-settings-schema"));
    }

    [Theory]
    [InlineData(false, "candidate-hang")]
    [InlineData(true, "commit-hang")]
    public async Task WorkerV3_StopCompletesInterruptedLifecycleWaiter(bool duringCommit, string mode)
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
    public async Task WorkerV3_ForcedStopCompletesInterruptedLifecycleWaiter(bool duringCommit, string mode)
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
    public async Task WorkerV3_StopCompletesInterruptedMigrationPreparationWaiter(
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

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await preparation.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WorkerV3_RejectsOversizedInlinePackageDataValue()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v3-oversized-inline");
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
    public async Task WorkerV3_StaleActivationConsumesSubmittedUploadHandle()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v3-stale-package-data",
            enforcePackageSessionLease: true);
        await fixture.StartAndActivateAsync();
        await fixture.WaitForActivationAsync();

        using var status = await fixture.WaitForWorkerStatusAsync("v3-stale-package-data.json");
        Assert.Equal("unavailable", status.RootElement.GetProperty("kind").GetString());
        Assert.Equal("worker.activation.unavailable", status.RootElement.GetProperty("code").GetString());
        Assert.True(status.RootElement.GetProperty("uploadConsumed").GetBoolean());
        Assert.Equal(0, fixture.WorkerPayloadHandleCount());
    }

    [Fact]
    public async Task WorkerV3_StaleActivationStillValidatesSubmittedUploadIntegrity()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v3-stale-malformed-payload",
            enforcePackageSessionLease: true);
        await fixture.StartAndActivateAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, fixture.WorkerPayloadHandleCount());
    }

    [Fact]
    public async Task WorkerV3_ShutdownAcknowledgementWaitsForWorkerCallCleanup()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v3-shutdown-worker-call");
        await fixture.StartAndActivateAsync();
        await fixture.WaitForActivationAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v3-shutdown-worker-call.json");
        Assert.True(status.RootElement.GetProperty("started").GetBoolean());

        await fixture.Worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Worker_UnixFifoContentPathIsRejectedWithoutBlocking()
    {
        if (OperatingSystem.IsWindows()) return;
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
        await using var fixture = await WorkerFixture.CreateAsync("v3-package-data");
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
    public async Task WorkerV3_ProviderDispatchWaitsForActivationAcknowledgement()
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
        await Task.Delay(100);

        Assert.False(invocation.IsCompleted);
        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        await fixture.Worker.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => activation);
    }

    [Fact]
    public async Task WorkerV3_ActivationWaitersRespectOutstandingHostCallLimit()
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
        await Task.Delay(100);

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
    public async Task WorkerV3_PendingProviderDispatchContinuesAfterActivationAcknowledgement()
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
        await Task.Delay(50);

        Assert.False(invocation.IsCompleted);
        var result = await invocation.WaitAsync(TimeSpan.FromSeconds(5));
        await activation;
        Assert.True(result.GetProperty("accepted").GetBoolean());
        Assert.Contains(logger.Messages, message => message.Contains("activation dispatched", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("activation acknowledged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkerV3_ReportedGenerationFaultFaultsOnlyItsExactGenerationCompletion()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v3-generation-fault");
        await fixture.StartAndActivateAsync();

        var fault = await Assert.ThrowsAsync<ProcessRuntimeGenerationFaultException>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("worker.test-fault", fault.Code);
        Assert.Contains("bounded fault", fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerV3_MismatchedCallbackIdentityFaultsExactGeneration()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v3-callback-mismatch");
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
    public async Task WorkerV3_CanCallHostWhileActivationIsInitializing()
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
    public async Task WorkerV3_InvariantReportRoundTripsHostDecision()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v3-invariant-report");

        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v3-invariant-report.json");

        Assert.Equal("host.result", status.RootElement.GetProperty("responseType").GetString());
        Assert.False(status.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage("process.test")!.Readiness);
        await fixture.Worker.StopAsync();
    }

    [Fact]
    public async Task WorkerV3_CallerScopeRoundTripsRpcAndContentAuthority()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v3-scope-roundtrip");

        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v3-scope-roundtrip.json");

        Assert.Equal("caller-payload-processed", status.RootElement.GetProperty("content").GetString());
        Assert.Equal("process.test", status.RootElement.GetProperty("callerPackageId").GetString());
        Assert.True(status.RootElement.GetProperty("invocationHandleReleased").GetBoolean());
        Assert.True(status.RootElement.GetProperty("scopeHandleReleased").GetBoolean());
        await fixture.Worker.StopAsync();
        await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WorkerV3_ShutdownRevokesLeakedCallerScopeAndContent()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v3-scope-leak");

        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v3-scope-leak.json");
        Assert.False(string.IsNullOrWhiteSpace(status.RootElement.GetProperty("scopeId").GetString()));
        Assert.Equal(1, fixture.WorkerCallScopeCount());
        Assert.NotEmpty(fixture.TransferFiles());

        await fixture.Worker.StopAsync();

        Assert.Equal(0, fixture.WorkerCallScopeCount());
        Assert.Empty(fixture.TransferFiles());
    }

    [Fact]
    public async Task WorkerV3_ShutdownDeadlineDoesNotWaitForBlockingScopeCallback()
    {
        var policy = new RuntimeProcessPolicyOptions
        {
            ShutdownTimeout = TimeSpan.FromMilliseconds(150),
        };
        await using var fixture = await WorkerFixture.CreateAsync(
            "v3-scope-leak",
            policy);
        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v3-scope-leak.json");
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
    public async Task WorkerV3_CallerScopeUsesInstalledPackagePermissions()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v3-scope-permission-denied");

        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v3-scope-permission-denied.json");

        Assert.Equal("permission-denied", status.RootElement.GetProperty("kind").GetString());
        Assert.Equal("rpc.permission.denied", status.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task WorkerV3_BoundsMaterializedCallerScopeHandles()
    {
        var policy = new RuntimeProcessPolicyOptions
        {
            MaxMaterializedWorkerContentHandles = 1,
        };
        await using var fixture = await WorkerFixture.CreateAsync(
            "v3-scope-handle-limit",
            policy);

        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v3-scope-handle-limit.json");

        Assert.Equal("resource-exhausted", status.RootElement.GetProperty("handleLimitKind").GetString());
        Assert.Equal(
            "rpc.worker.content-handle-limit",
            status.RootElement.GetProperty("handleLimitCode").GetString());
        Assert.True(status.RootElement.GetProperty("handleLimitRetrySucceeded").GetBoolean());
    }

    [Fact]
    public async Task WorkerV3_RejectsUnknownCallerScope()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v3-unknown-scope");
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
    public async Task WorkerV3_ContentReleaseUsesControlLaneWhenRpcLaneIsFull()
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
        Assert.Equal(JsonValueKind.Null, result.GetProperty("dataPath").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("statePath").ValueKind);
        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), result.GetProperty("path").GetString());
        Assert.Equal(
            Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME"),
            result.GetProperty("home").GetString());
        Assert.EndsWith("Z", result.GetProperty("deadlineUtc").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkerEnvironmentBaseline_ContainsOnlyOsMinimumAndPackageLocalTemp(bool isWindows)
    {
        var source = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = isWindows ? @"C:\Windows\System32" : "/usr/local/bin:/usr/bin",
            ["HOME"] = "/Users/test",
            ["USERPROFILE"] = @"C:\Users\test",
            ["PATHEXT"] = ".EXE;.CMD",
            ["ComSpec"] = @"C:\Windows\System32\cmd.exe",
            ["SystemRoot"] = @"C:\Windows",
            ["LANG"] = "en_US.UTF-8",
            ["LC_CTYPE"] = "UTF-8",
            ["UNRELATED"] = "must-not-pass",
        };

        var environment = WorkerProcessEnvironment.CreateBaseline(
            source.GetValueOrDefault,
            isWindows ? @"C:\Temp\sunder-worker" : "/tmp/sunder-worker",
            isWindows,
            allowDockerEnvironment: false);

        Assert.Equal(source["PATH"], environment["PATH"]);
        Assert.Equal(isWindows ? source["USERPROFILE"] : source["HOME"], environment[isWindows ? "USERPROFILE" : "HOME"]);
        Assert.Equal(isWindows, environment.ContainsKey("PATHEXT"));
        Assert.Equal(isWindows, environment.ContainsKey("ComSpec"));
        Assert.Equal(!isWindows, environment.ContainsKey("LANG"));
        Assert.Equal(environment["TMPDIR"], environment["TMP"]);
        Assert.Equal(environment["TMPDIR"], environment["TEMP"]);
        Assert.DoesNotContain("UNRELATED", environment.Keys);
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
            isWindows: false,
            allowDockerEnvironment: false);
        var allowed = WorkerProcessEnvironment.CreateBaseline(
            source.GetValueOrDefault,
            "/tmp/worker",
            isWindows: false,
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
            "NODE_OPTIONS",
        };
        var source = blocked.ToDictionary(static key => key, static _ => (string?)"must-not-pass", StringComparer.Ordinal);
        source["PATH"] = "/usr/bin";
        source["HOME"] = "/home/test";

        var environment = WorkerProcessEnvironment.CreateBaseline(
            source.GetValueOrDefault,
            "/tmp/worker",
            isWindows: false,
            allowDockerEnvironment: true);

        Assert.All(blocked, key => Assert.DoesNotContain(key, environment.Keys));
    }

    [Fact]
    public async Task WorkerV3_ReceivesDistinctCanonicalContentTemporaryAndWorkspaceRoots()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v3-environment");
        await fixture.StartAndActivateAsync();

        using var status = await fixture.WaitForWorkerStatusAsync("v3-environment.json");
        var root = status.RootElement;
        Assert.Equal(Path.GetFullPath(fixture.Prepared.ShadowFolder), root.GetProperty("contentRoot").GetString());
        Assert.Equal(fixture.Worker.WorkerTemporaryPath, root.GetProperty("temporaryRoot").GetString());
        Assert.Equal(
            fixture.Context.Storage.RoleLocalWorkspace.WorkspaceRootPath,
            root.GetProperty("workspaceRoot").GetString());
        Assert.NotEqual(root.GetProperty("contentRoot").GetString(), root.GetProperty("temporaryRoot").GetString());
        Assert.NotEqual(root.GetProperty("contentRoot").GetString(), root.GetProperty("workspaceRoot").GetString());
        Assert.NotEqual(root.GetProperty("temporaryRoot").GetString(), root.GetProperty("workspaceRoot").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("legacyContentPath").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("legacyDataPath").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("legacyWorkingPath").ValueKind);
    }

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
        await Task.Delay(100);

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
    public async Task SessionRetirement_DrainsFakeWorkerCallBeforeProcessShutdown()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        var activation = await fixture.PublishBrokerSessionAsync();
        var call = activation.Client.InvokeAsync(
            activation.Endpoint,
            "messages",
            "wait",
            JsonSerializer.SerializeToElement(new { message = "wait" })).AsTask();
        await Task.Delay(100);

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

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _mode;
        private readonly RuntimeRpcCatalog _catalog;
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
            string? packageDataRoot = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "sunder-worker-tests", Guid.NewGuid().ToString("N"));
            var shadow = Path.Combine(root, "projection");
            Directory.CreateDirectory(shadow);
            foreach (var fixtureFile in Directory.EnumerateFiles(GetTestWorkerOutput()))
            {
                File.Copy(fixtureFile, Path.Combine(shadow, Path.GetFileName(fixtureFile)));
            }
            await File.WriteAllTextAsync(Path.Combine(shadow, "mode.txt"), mode);
            var entryPoint = Path.Combine(
                shadow,
                OperatingSystem.IsWindows()
                    ? "Sunder.Runtime.Host.TestWorker.exe"
                    : "Sunder.Runtime.Host.TestWorker");
            if (!OperatingSystem.IsWindows())
            {
                var modeBits = File.GetUnixFileMode(entryPoint);
                File.SetUnixFileMode(entryPoint, modeBits | UnixFileMode.UserExecute);
            }
            var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
            var target = new SunderPackageTargetManifest
            {
                Role = SunderPackageFormat.RuntimeHostRole,
                Rid = rid,
                Kind = SunderPackageFormat.WorkerTargetKind,
                EntryPoint = Path.GetFileName(entryPoint),
                TargetFramework = "net10.0",
                SdkVersion = "1.1.0",
                RequiredHostCapabilities = ["core.v1", "rpc.v2", "sdk-baseline-1-1.v1", "worker-protocol.v3"],
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
            var owner = new RuntimeSessionOwner(
                NullLogger<RuntimeSessionOwner>.Instance,
                new RuntimeEventStreamService(),
                rpcCatalog: catalog);
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
            var worker = new ProcessRuntimeWorker(
                logger ?? NullLogger.Instance,
                prepared,
                context,
                activationId,
                broker,
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

        public async Task<PackageActivationResult> ActivatePreparedPackageAsync()
        {
            var warnings = new List<string>();
            var errors = new List<string>();
            return await new RuntimePackageActivator(
                    NullLogger.Instance,
                    new RuntimePackagePaths(Path.Combine(_root, "runtime-data")),
                    _broker,
                    packageSessionState: _owner.State)
                .ActivateAsync(
                    Prepared,
                    warnings,
                    errors,
                    CancellationToken.None);
        }

        public async Task WaitForActivationAsync()
            => await Worker.ActivationCompletion.WaitAsync(TimeSpan.FromSeconds(5));

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
            var processManifest = Prepared.Source.Manifest!;
            var processPackage = CreateProcessLoadedPackage();

            var callerManifest = new SunderPackageManifest
            {
                Id = "caller.package",
                Name = "Caller Package",
                Version = "1.0.0",
                ContractBundles = processManifest.ContractBundles,
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
            if (!string.Equals(_mode, "v3-scope-permission-denied", StringComparison.Ordinal))
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
                    "1.0.0",
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(path))
            {
                if (Worker.GenerationCompletion.IsCompleted)
                {
                    await Worker.GenerationCompletion;
                    throw new InvalidOperationException("The worker stopped before writing its status file.");
                }
                await Task.Delay(25, timeout.Token);
            }
            return JsonDocument.Parse(await File.ReadAllBytesAsync(path, timeout.Token));
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

        private static string GetTestWorkerOutput()
            => Path.Combine(
                FindRepositoryRoot(),
                "languages",
                "csharp",
                "dotnet",
                "tests",
                "Sunder.Runtime.Host.TestWorker",
                "bin",
                ResolveConfiguration(),
                "net10.0");

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
            "WorkerV3Executable",
            "bin",
            WorkerFixture.ResolveConfiguration(),
            "net10.0",
            rid,
            "sunder-dev");

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
          "descriptorVersion":2,
          "contractId":"example.rpc",
          "version":"1.0.0",
          "errors":{},
          "services":{"messages":{"methods":{
            "send":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Response"}},
            "wait":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Response"}},
            "content-roundtrip":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/ContentRequest"},"responseSchema":{"$ref":"#/$defs/ContentResponse"}},
            "watch":{"kind":"server-stream","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Request"},"eventSchema":{"$ref":"#/$defs/Response"}}
          }}},
          "$defs":{
            "Request":{"type":"object","properties":{"message":{"type":"string","minLength":1,"maxLength":128}},"required":["message"],"additionalProperties":false},
            "Response":{"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false},
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
