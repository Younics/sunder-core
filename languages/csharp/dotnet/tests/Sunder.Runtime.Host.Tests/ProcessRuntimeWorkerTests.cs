using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ProcessRuntimeWorkerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerSdkPackagedExecutable_HandshakesActivatesAndShutsDown(bool useV2)
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
                GetWorkerSdkFixtureOutput(rid, useV2),
                Path.Combine(root, "session"),
                errors,
                CancellationToken.None);
            Assert.NotNull(prepared);
            Assert.Empty(errors);
            Assert.Equal(SunderPackageFormat.WorkerTargetKind, prepared.SelectedTarget?.Kind);
            Assert.Equal(
                useV2,
                prepared.SelectedTarget?.RequiredHostCapabilities?.Contains(
                    SunderWorkerProtocol.V2Capability,
                    StringComparer.Ordinal) == true);

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
            var context = new RuntimePackageContext(
                prepared.PackageId,
                prepared.Version,
                prepared.ShadowFolder,
                paths.PackageDataRootPath);
            await using var worker = new ProcessRuntimeWorker(
                NullLogger.Instance,
                prepared,
                context,
                activationId,
                broker,
                protocol: useV2 ? SunderWorkerProtocol.V2 : SunderWorkerProtocol.V1);

            if (useV2) await worker.ComposeAsync();
            await worker.StartAsync();
            await worker.CommitGenerationAsync(new Sunder.Sdk.Abstractions.PackageRuntimeGeneration(activationId, 1));
            worker.ActivateGeneration();
            await worker.ActivationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
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
    public async Task WorkerV2_ComposesStartsCommitsActivatesInvokesAndShutsDown()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal", useV2: true);
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
    public async Task WorkerV2_ProviderFaultPersistsOnlySafeIdentityAndCorrelatesBrokerFailure()
    {
        const string secret = "provider-secret=must-never-persist";
        const string exceptionType = "SecretBearingProviderException";
        var logger = new CapturingLogger();
        await using var fixture = await WorkerFixture.CreateAsync(
            "v2-provider-fault",
            logger: logger,
            useV2: true);
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

        var logFiles = Directory.GetFiles(fixture.Context.LocalStorage.LogsRootPath, "*.log");
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
    public async Task WorkerV2_WhenReadyBindsV1_FailsWithoutFallback()
    {
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await WorkerFixture.CreateAsync("protocol-mismatch", useV2: true));
    }

    [Fact]
    public async Task WorkerV2_LoggingIsAdmittedAtomicallyWithReady()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v2-ready-logging", useV2: true);

        using var status = await fixture.WaitForWorkerStatusAsync("v2-ready-logging.json");
        Assert.True(status.RootElement.GetProperty("accepted").GetBoolean());
        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);
    }

    [Fact]
    public async Task WorkerV2_SettingsStateSchemaAndPayloadsUseHostAuthority()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v2-package-data",
            useV2: true,
            enforcePackageSessionLease: true);

        Assert.NotNull(fixture.Worker.SettingsSchema);
        var schema = fixture.Worker.SettingsSchema!;
        var section = Assert.Single(schema.Sections);
        Assert.Equal("general", section.SectionId);
        Assert.Equal(["enabled", "token"], section.Fields.Select(static field => field.Key));

        await fixture.PublishBrokerSessionAsync();
        await fixture.WaitForActivationAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v2-package-data.json");
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
    public async Task WorkerV2_FilesAndSecretsUseHostAuthority()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v2-files-secrets",
            useV2: true,
            enforcePackageSessionLease: true);

        await fixture.PublishBrokerSessionAsync();
        await fixture.WaitForActivationAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v2-files-secrets.json");
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
    public async Task WorkerV2_LoggingUsesHostSinksAndDrainsShutdown()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v2-logging",
            useV2: true);

        await fixture.PublishBrokerSessionAsync();
        await fixture.WaitForActivationAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v2-logging.json");
        Assert.True(status.RootElement.GetProperty("accepted").GetBoolean());

        var eventsPath = Assert.Single(Directory.GetFiles(
            fixture.Context.LocalStorage.LogsRootPath,
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
            fixture.Context.LocalStorage.LogsRootPath,
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
    public async Task WorkerV2_MalformedShutdownLoggingFaultsProtocol()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v2-malformed-shutdown-logging",
            useV2: true);
        await fixture.StartAndActivateAsync();
        await fixture.WaitForActivationAsync();

        await Assert.ThrowsAsync<SunderWorkerProtocolException>(async () =>
            await fixture.Worker.StopAsync());
        await Assert.ThrowsAsync<SunderWorkerProtocolException>(async () =>
            await fixture.Worker.GenerationCompletion);
    }

    [Fact]
    public async Task WorkerV2_InvalidSettingsSchemaFailsComposition()
    {
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await WorkerFixture.CreateAsync("v2-invalid-settings-schema", useV2: true));
    }

    [Theory]
    [InlineData(false, "candidate-hang")]
    [InlineData(true, "commit-hang")]
    public async Task WorkerV2_StopCompletesInterruptedLifecycleWaiter(bool duringCommit, string mode)
    {
        await using var fixture = await WorkerFixture.CreateAsync(mode, useV2: true);
        if (duringCommit) await fixture.Worker.StartAsync();
        var lifecycle = duringCommit
            ? fixture.Worker.CommitGenerationAsync(new Sunder.Sdk.Abstractions.PackageRuntimeGeneration(
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
    public async Task WorkerV2_ForcedStopCompletesInterruptedLifecycleWaiter(bool duringCommit, string mode)
    {
        var policy = new RuntimeProcessPolicyOptions
        {
            ShutdownTimeout = TimeSpan.FromMilliseconds(100),
        };
        await using var fixture = await WorkerFixture.CreateAsync(mode, policy, useV2: true);
        if (duringCommit) await fixture.Worker.StartAsync();
        var lifecycle = duringCommit
            ? fixture.Worker.CommitGenerationAsync(new Sunder.Sdk.Abstractions.PackageRuntimeGeneration(
                fixture.ActivationId,
                1))
            : fixture.Worker.StartAsync();

        await fixture.Worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await lifecycle.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WorkerV2_RejectsOversizedInlinePackageDataValue()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v2-oversized-inline", useV2: true);
        await fixture.StartAndActivateAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WorkerV2_StaleActivationConsumesSubmittedUploadHandle()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v2-stale-package-data",
            useV2: true,
            enforcePackageSessionLease: true);
        await fixture.StartAndActivateAsync();
        await fixture.WaitForActivationAsync();

        using var status = await fixture.WaitForWorkerStatusAsync("v2-stale-package-data.json");
        Assert.Equal("unavailable", status.RootElement.GetProperty("kind").GetString());
        Assert.Equal("worker.activation.unavailable", status.RootElement.GetProperty("code").GetString());
        Assert.True(status.RootElement.GetProperty("uploadConsumed").GetBoolean());
        Assert.Equal(0, fixture.WorkerPayloadHandleCount());
    }

    [Fact]
    public async Task WorkerV2_StaleActivationStillValidatesSubmittedUploadIntegrity()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v2-stale-malformed-payload",
            useV2: true,
            enforcePackageSessionLease: true);
        await fixture.StartAndActivateAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, fixture.WorkerPayloadHandleCount());
    }

    [Fact]
    public async Task WorkerV2_ShutdownAcknowledgementWaitsForWorkerCallCleanup()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v2-shutdown-worker-call", useV2: true);
        await fixture.StartAndActivateAsync();
        await fixture.WaitForActivationAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v2-shutdown-worker-call.json");
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
        await using var fixture = await WorkerFixture.CreateAsync("v2-package-data", useV2: true);
        var result = await fixture.ActivatePreparedPackageAsync();
        var loaded = Assert.IsType<ActiveLoadedPackage>(result.LoadedPackage);
        try
        {
            Assert.True(result.Success);
            Assert.NotNull(loaded.CanonicalSettingsSchema);
            Assert.NotNull(loaded.SettingsSchema);
            Assert.Equal("process.test", loaded.SettingsSchema!.PackageId);
            Assert.Equal("Process Test", loaded.SettingsSchema.PackageDisplayName);
            Assert.Equal("Process worker settings.", loaded.SettingsSchema.Summary);
            Assert.Equal("true", await loaded.Settings.GetValueAsync("enabled"));
            var packageContext = loaded.ServiceProvider
                .GetRequiredService<Sunder.Sdk.Abstractions.IPackageContext>();
            await packageContext.Storage.Files.WriteAsync("activator/context.txt", "context"u8.ToArray());
            Assert.Equal("context"u8.ToArray(), await packageContext.Storage.Files.ReadAsync("activator/context.txt"));
            Assert.NotNull(packageContext.Logging);
        }
        finally
        {
            await ((IAsyncDisposable)loaded.ServiceProvider).DisposeAsync();
        }
    }

    [Fact]
    public async Task Worker_PreActivationProviderCallIsUnavailableWithoutFaultingGeneration()
    {
        await using var fixture = await WorkerFixture.CreateAsync("normal");
        await fixture.Worker.StartAsync();
        await fixture.Worker.CommitGenerationAsync(
            new Sunder.Sdk.Abstractions.PackageRuntimeGeneration(fixture.ActivationId, 1));

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

        fixture.Worker.ActivateGeneration();
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
    public async Task WorkerV2_ProviderDispatchWaitsForActivationAcknowledgement()
    {
        await using var fixture = await WorkerFixture.CreateAsync("activation-hang", useV2: true);
        await fixture.Worker.StartAsync();
        await fixture.Worker.CommitGenerationAsync(
            new Sunder.Sdk.Abstractions.PackageRuntimeGeneration(fixture.ActivationId, 1));
        fixture.Worker.ActivateGeneration();
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
    }

    [Fact]
    public async Task WorkerV2_ActivationWaitersRespectOutstandingHostCallLimit()
    {
        var policy = new RuntimeProcessPolicyOptions { MaxOutstandingHostCalls = 1 };
        await using var fixture = await WorkerFixture.CreateAsync(
            "activation-hang",
            policy,
            useV2: true);
        await fixture.Worker.StartAsync();
        await fixture.Worker.CommitGenerationAsync(
            new Sunder.Sdk.Abstractions.PackageRuntimeGeneration(fixture.ActivationId, 1));
        fixture.Worker.ActivateGeneration();
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
    }

    [Fact]
    public async Task WorkerV2_PendingProviderDispatchContinuesAfterActivationAcknowledgement()
    {
        await using var fixture = await WorkerFixture.CreateAsync("activation-delay", useV2: true);
        await fixture.Worker.StartAsync();
        await fixture.Worker.CommitGenerationAsync(
            new Sunder.Sdk.Abstractions.PackageRuntimeGeneration(fixture.ActivationId, 1));
        fixture.Worker.ActivateGeneration();

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
        Assert.True(result.GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task WorkerV2_CanCallHostWhileActivationIsInitializing()
    {
        await using var fixture = await WorkerFixture.CreateAsync("activation-host-call", useV2: true);

        await fixture.StartAndActivateAsync();
        await fixture.WaitForActivationAsync();

        Assert.False(fixture.Worker.GenerationCompletion.IsCompleted);
        await fixture.Worker.StopAsync();
    }

    [Fact]
    public async Task WorkerV2_InvariantReportRoundTripsHostDecision()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v2-invariant-report", useV2: true);

        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v2-invariant-report.json");

        Assert.Equal("host.result", status.RootElement.GetProperty("responseType").GetString());
        Assert.False(status.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage("process.test")!.Readiness);
        await fixture.Worker.StopAsync();
    }

    [Fact]
    public async Task WorkerV2_CallerScopeRoundTripsRpcAndContentAuthority()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v2-scope-roundtrip", useV2: true);

        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v2-scope-roundtrip.json");

        Assert.Equal("caller-payload-processed", status.RootElement.GetProperty("content").GetString());
        Assert.Equal("process.test", status.RootElement.GetProperty("callerPackageId").GetString());
        Assert.True(status.RootElement.GetProperty("invocationHandleReleased").GetBoolean());
        Assert.True(status.RootElement.GetProperty("scopeHandleReleased").GetBoolean());
        await fixture.Worker.StopAsync();
        await fixture.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WorkerV2_ShutdownRevokesLeakedCallerScopeAndContent()
    {
        await using var fixture = await WorkerFixture.CreateAsync("v2-scope-leak", useV2: true);

        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v2-scope-leak.json");
        Assert.False(string.IsNullOrWhiteSpace(status.RootElement.GetProperty("scopeId").GetString()));
        Assert.Equal(1, fixture.WorkerCallScopeCount());
        Assert.NotEmpty(fixture.TransferFiles());

        await fixture.Worker.StopAsync();

        Assert.Equal(0, fixture.WorkerCallScopeCount());
        Assert.Empty(fixture.TransferFiles());
    }

    [Fact]
    public async Task WorkerV2_ShutdownDeadlineDoesNotWaitForBlockingScopeCallback()
    {
        var policy = new RuntimeProcessPolicyOptions
        {
            ShutdownTimeout = TimeSpan.FromMilliseconds(150),
        };
        await using var fixture = await WorkerFixture.CreateAsync(
            "v2-scope-leak",
            policy,
            useV2: true);
        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v2-scope-leak.json");
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
    public async Task WorkerV2_CallerScopeUsesInstalledPackagePermissions()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            "v2-scope-permission-denied",
            useV2: true);

        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v2-scope-permission-denied.json");

        Assert.Equal("permission-denied", status.RootElement.GetProperty("kind").GetString());
        Assert.Equal("rpc.permission.denied", status.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task WorkerV2_BoundsMaterializedCallerScopeHandles()
    {
        var policy = new RuntimeProcessPolicyOptions
        {
            MaxMaterializedWorkerContentHandles = 1,
        };
        await using var fixture = await WorkerFixture.CreateAsync(
            "v2-scope-handle-limit",
            policy,
            useV2: true);

        await fixture.PublishBrokerSessionAsync();
        using var status = await fixture.WaitForWorkerStatusAsync("v2-scope-handle-limit.json");

        Assert.Equal("resource-exhausted", status.RootElement.GetProperty("handleLimitKind").GetString());
        Assert.Equal(
            "rpc.worker.content-handle-limit",
            status.RootElement.GetProperty("handleLimitCode").GetString());
        Assert.True(status.RootElement.GetProperty("handleLimitRetrySucceeded").GetBoolean());
    }

    [Theory]
    [InlineData(false, "v1-scope-open")]
    [InlineData(false, "v1-package-data")]
    [InlineData(false, "v1-logging")]
    [InlineData(true, "v2-unknown-scope")]
    public async Task Worker_RejectsWrongProtocolOrUnknownCallerScope(bool useV2, string mode)
    {
        await using var fixture = await WorkerFixture.CreateAsync(mode, useV2: useV2);
        await fixture.StartAndActivateAsync();

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

    [Theory]
    [InlineData("normal")]
    [InlineData("v1-temp-content")]
    public async Task Worker_ContentRegisterOpenAndDiscardRemainBoundToHostInvocation(string mode)
    {
        await using var fixture = await WorkerFixture.CreateAsync(mode);
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
        Assert.True(result.GetProperty("openedFileDiscarded").GetBoolean());
        var output = ReadContentReference(result.GetProperty("content"));
        await using var outputStream = await scope.OpenContentAsync(output);
        using var reader = new StreamReader(outputStream, Encoding.UTF8, leaveOpen: false);
        Assert.Equal("caller-payload-processed", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task WorkerV1_ContentDiscardUsesControlLaneWhenRpcLaneIsFull()
    {
        var policy = new RuntimeProcessPolicyOptions
        {
            MaxOutstandingWorkerCalls = 1,
        };
        await using var fixture = await WorkerFixture.CreateAsync(
            "v1-content-control-lane",
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

        Assert.True(result.GetProperty("openedFileDiscarded").GetBoolean());
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
        Assert.True(Path.IsPathFullyQualified(result.GetProperty("dataPath").GetString()!));
        Assert.EndsWith(Path.Combine("data", "state.json"), result.GetProperty("statePath").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("Z", result.GetProperty("deadlineUtc").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("malformed-frame")]
    [InlineData("crash")]
    [InlineData("caller-spoof")]
    [InlineData("content-spoof")]
    public async Task Worker_ProtocolViolationCrashAndCallerSpoofFaultExactGeneration(string mode)
    {
        await using var fixture = await WorkerFixture.CreateAsync(mode);
        await fixture.StartAndActivateAsync();

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
        await using (var startup = await WorkerFixture.CreateAsync("startup-hang", new RuntimeProcessPolicyOptions
                     {
                         StartupTimeout = TimeSpan.FromMilliseconds(150),
                     }))
        {
            await Assert.ThrowsAsync<TimeoutException>(() => startup.Worker.StartAsync());
        }

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

        await using (var activation = await WorkerFixture.CreateAsync("activation-hang", new RuntimeProcessPolicyOptions
                     {
                         ActivationTimeout = TimeSpan.FromMilliseconds(150),
                     }))
        {
            await activation.StartAndActivateAsync();
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await activation.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
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
        private readonly RuntimePackageContext _context;
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
            RuntimePackageContext context,
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
        public RuntimePackageContext Context => _context;

        public static async Task<WorkerFixture> CreateAsync(
            string mode,
            RuntimeProcessPolicyOptions? policy = null,
            ILogger? logger = null,
            CancellationToken hostStopping = default,
            bool useV2 = false,
            bool enforcePackageSessionLease = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "sunder-process-worker-tests", Guid.NewGuid().ToString("N"));
            var shadow = Path.Combine(root, "projection");
            Directory.CreateDirectory(shadow);
            foreach (var fixtureFile in Directory.EnumerateFiles(GetTestWorkerOutput()))
            {
                File.Copy(fixtureFile, Path.Combine(shadow, Path.GetFileName(fixtureFile)));
            }
            await File.WriteAllTextAsync(Path.Combine(shadow, "mode.txt"), mode);
            var entryPoint = Path.Combine(shadow, "Sunder.Runtime.Host.TestWorker.dll");
            var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
            var target = new SunderPackageTargetManifest
            {
                Role = SunderPackageFormat.RuntimeHostRole,
                Rid = rid,
                Kind = SunderPackageFormat.ProcessTargetKind,
                EntryPoint = Path.GetFileName(entryPoint),
                TargetFramework = "net10.0",
                SdkVersion = "1.1.0",
                RequiredHostCapabilities = useV2
                    ? ["rpc.v1", "sdk-baseline-1-1.v1", "worker-protocol.v2"]
                    : ["rpc.v1", "sdk-baseline-1-1.v1"],
            };
            var manifest = new SunderPackageManifest
            {
                Id = "process.test",
                Name = "Process Test",
                Version = "1.0.0",
                Targets = [target],
                ContractBundles =
                [
                    new SunderPackageContractBundleManifest
                    {
                        ContractId = Contract.ContractId,
                        Version = Contract.Version,
                        DescriptorPath = "contracts/example.rpc.json",
                        Sha256 = Contract.Sha256,
                    },
                ],
                UsesContracts =
                [
                    new SunderPackageContractUseManifest
                    {
                        ContractId = Contract.ContractId,
                        VersionRange = Contract.Version,
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
                shadow,
                "process.test",
                "1.0.0",
                PackageHostRoles.Runtime,
                new RuntimePackageActivationState("process.test", "Process Test", "1.0.0", PackageHostRoles.Runtime, null, key, target),
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
                DevProcessLaunch = new DevProcessLaunchMetadata(
                    1,
                    DevProcessLaunchMetadata.NodeKind,
                    "process.test",
                    "1.0.0",
                    rid,
                    target.EntryPoint,
                    ResolveDotnetHost(),
                    "24.0.0",
                    new string('b', 64)),
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
            var context = new RuntimePackageContext("process.test", "1.0.0", shadow, paths.PackageDataRootPath);
            var worker = new ProcessRuntimeWorker(
                logger ?? NullLogger.Instance,
                prepared,
                context,
                activationId,
                broker,
                policy,
                hostStopping,
                useV2 ? SunderWorkerProtocol.V2 : SunderWorkerProtocol.V1,
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
            if (!useV2)
            {
                return fixture;
            }
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
            await Worker.CommitGenerationAsync(new Sunder.Sdk.Abstractions.PackageRuntimeGeneration(ActivationId, 1));
            Worker.ActivateGeneration();
        }

        public async Task<PackageActivationResult> ActivatePreparedPackageAsync()
        {
            using var sharedAssemblies = new RuntimeSharedAssemblyRegistry([]);
            var warnings = new List<string>();
            var errors = new List<string>();
            return await new RuntimePackageActivator(
                    NullLogger.Instance,
                    new RuntimePackagePaths(Path.Combine(_root, "runtime-data")),
                    _broker,
                    packageSessionState: _owner.State)
                .ActivateAsync(
                    Prepared,
                    sharedAssemblies,
                    warnings,
                    errors,
                    CancellationToken.None);
        }

        public async Task WaitForActivationAsync()
            => await Worker.ActivationCompletion.WaitAsync(TimeSpan.FromSeconds(5));

        public async Task<BrokerActivation> PublishBrokerSessionAsync()
        {
            await Worker.StartAsync();
            var processManifest = Prepared.Source.Manifest!;
            var processContext = _context;
            var declaration = Assert.Single(processManifest.Provides!)!;
            var processPackage = new ActiveLoadedPackage(
                new ActivePackageDescriptor(
                    "process.test",
                    "Process Test",
                    "1.0.0",
                    PackageHostRoles.Runtime,
                    null,
                    true,
                    PackageReadinessState.Ready,
                    []),
                Prepared.Source,
                SettingsSchema: null,
                processContext.Storage.State,
                processContext.SecretsStore,
                AuthHandler: null,
                CallbackHandlers: new Dictionary<string, Sunder.Sdk.Abstractions.IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase),
                BackgroundServices: [Worker],
                new ServiceCollection().BuildServiceProvider(),
                LoadContext: null,
                processContext.Settings)
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
                        declaration.ContractSha256!,
                        Contract,
                        new ProcessRpcProviderHandler(Worker, declaration.ProviderId!),
                        declaration),
                },
                RpcManifestSha256 = new string('a', 64),
            };

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
                        ContractId = Contract.ContractId,
                        VersionRange = Contract.Version,
                        Required = true,
                        Actions = ["discover", "invoke", "subscribe"],
                    },
                ],
                Provides = [],
            };
            var callerContext = new RuntimePackageContext(
                "caller.package",
                "1.0.0",
                Prepared.ShadowFolder,
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
                callerContext.SecretsStore,
                AuthHandler: null,
                CallbackHandlers: new Dictionary<string, Sunder.Sdk.Abstractions.IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase),
                BackgroundServices: [],
                new ServiceCollection().BuildServiceProvider(),
                LoadContext: null,
                callerContext.Settings)
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
                backgroundServicesStarted: true);
            await _owner.PublishAsync(
                session,
                _owner.Sources.Snapshot(),
                [],
                [],
                _owner.Generation);
            if (!string.Equals(_mode, "v2-scope-permission-denied", StringComparison.Ordinal))
            {
                foreach (var action in new[]
                         {
                             SunderRpcProtocol.DiscoverAction,
                             SunderRpcProtocol.InvokeAction,
                             SunderRpcProtocol.SubscribeAction,
                         })
                {
                    await _permissions.SetAsync(
                        "process.test",
                        "1.0.0",
                        new string('a', 64),
                        Contract.ContractId,
                        action,
                        RuntimeRpcPermissionState.Granted,
                        CancellationToken.None);
                }
            }
            await Worker.CommitGenerationAsync(new Sunder.Sdk.Abstractions.PackageRuntimeGeneration(
                ActivationId,
                _owner.Generation));
            _catalog.ActivateSession(session, _owner.Generation);
            Worker.ActivateGeneration();
            await _permissions.SetAsync(
                "caller.package",
                "1.0.0",
                new string('a', 64),
                Contract.ContractId,
                SunderRpcProtocol.DiscoverAction,
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

        public Task SetInvokePermissionAsync(RuntimeRpcPermissionState state)
            => _permissions.SetAsync(
                "caller.package",
                "1.0.0",
                new string('a', 64),
                Contract.ContractId,
                SunderRpcProtocol.InvokeAction,
                state,
                CancellationToken.None);

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

        private static string ResolveDotnetHost()
        {
            var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
            if (Environment.ProcessPath is { } processPath
                && string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return processPath;
            }
            foreach (var candidate in new[]
                     {
                         "/usr/local/share/dotnet/dotnet",
                         "/usr/share/dotnet/dotnet",
                         @"C:\Program Files\dotnet\dotnet.exe",
                     })
            {
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("Could not resolve the dotnet Host for the fake process worker.");
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

    private static string GetWorkerSdkFixtureOutput(string rid, bool useV2)
        => Path.Combine(
            WorkerFixture.FindRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "tests",
            "Sunder.Package.Build.TestFixtures",
            useV2 ? "WorkerV2Executable" : "WorkerExecutable",
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
            ContractSha256 = Contract.Sha256,
            Role = SunderPackageFormat.RuntimeHostRole,
        };

    private static SunderRpcContractDescriptor Contract { get; } = SunderRpcContractDescriptor.Parse(
        System.Text.Encoding.UTF8.GetBytes("""
        {
          "descriptorVersion":1,
          "contractId":"example.rpc",
          "version":"1.0.0",
          "services":[{"serviceId":"messages","methods":[
            {"methodId":"send","kind":"unary","requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Response"}},
            {"methodId":"wait","kind":"unary","requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Response"}},
            {"methodId":"content-roundtrip","kind":"unary","requestSchema":{"$ref":"#/$defs/ContentRequest"},"responseSchema":{"$ref":"#/$defs/ContentResponse"}},
            {"methodId":"watch","kind":"server-stream","requestSchema":{"$ref":"#/$defs/Request"},"eventSchema":{"$ref":"#/$defs/Response"}}
          ]}],
          "$defs":{
            "Request":{"type":"object","properties":{"message":{"type":"string","minLength":1,"maxLength":128}},"required":["message"],"additionalProperties":false},
            "Response":{"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false},
            "ContentReference":{"type":"object","properties":{"id":{"type":"string","minLength":1,"maxLength":128},"length":{"type":"integer","minimum":0,"maximum":67108864},"sha256":{"type":"string","minLength":64,"maxLength":64},"mediaType":{"type":"string","minLength":1,"maxLength":128},"fileName":{"type":"string","minLength":1,"maxLength":255},"expiresAtUtc":{"type":"string","minLength":1,"maxLength":64},"repeatability":{"type":"string","minLength":1,"maxLength":32,"enum":["single-use","repeatable"]}},"required":["id","length","sha256","mediaType","fileName","expiresAtUtc","repeatability"],"additionalProperties":false},
            "ContentRequest":{"type":"object","properties":{"message":{"type":"string","minLength":1,"maxLength":128},"content":{"$ref":"#/$defs/ContentReference"}},"required":["message","content"],"additionalProperties":false},
            "ContentResponse":{"type":"object","properties":{"accepted":{"type":"boolean"},"content":{"$ref":"#/$defs/ContentReference"},"openedFileDiscarded":{"type":"boolean"}},"required":["accepted","content","openedFileDiscarded"],"additionalProperties":false}
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
