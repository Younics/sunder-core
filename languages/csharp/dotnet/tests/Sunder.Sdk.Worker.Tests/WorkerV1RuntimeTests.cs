using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Storage;
using Sunder.Sdk.Worker.Internal;
using Sunder.Sdk.Worker.Tests.TestSupport;

namespace Sunder.Sdk.Worker.Tests;

public sealed class WorkerV1RuntimeTests
{
    [Fact]
    public async Task WorkerV1_ComposesThenStartsCommitsActivatesInvokesAndShutsDown()
    {
        var lifecycle = new List<string>();
        var handler = new TestRpcHandler
        {
            Unary = static (_, _, _, request, _) => ValueTask.FromResult(request.Clone()),
        };
        SunderWorkerContext? configuredContext = null;
        SunderWorkerGenerationContext? committedGeneration = null;
        await using var worker = new WorkerHarness(context =>
        {
            configuredContext = context;
            AssertWorkspaceUnavailable(context);
            lifecycle.Add("configured");
            return new SunderWorkerOptions([WorkerHarness.Registration(handler)])
            {
                OnCandidateStarted = _ =>
                {
                    AssertWorkspaceUnavailable(context);
                    lifecycle.Add("candidate-started");
                    return ValueTask.CompletedTask;
                },
                OnGenerationCommitted = (generation, _) =>
                {
                    AssertWorkspaceUnavailable(context);
                    committedGeneration = generation;
                    lifecycle.Add("generation-committed");
                    return ValueTask.CompletedTask;
                },
                OnBeforeStorageMigrations = (_, _) =>
                {
                    AssertWorkspaceUnavailable(context);
                    lifecycle.Add("storage-migrations-prepared");
                    return ValueTask.CompletedTask;
                },
                OnActivated = (activation, _) =>
                {
                    Assert.Same(context, activation);
                    Assert.True(Path.IsPathFullyQualified(context.RoleLocalWorkspace.WorkspaceRootPath));
                    lifecycle.Add("activated");
                    return ValueTask.CompletedTask;
                },
                OnShutdown = (_, _) =>
                {
                    lifecycle.Add("shutdown");
                    return ValueTask.CompletedTask;
                },
            };
        });

        await worker.HandshakeAndActivateAsync();

        Assert.NotNull(configuredContext);
        Assert.Equal(
            ["configured", "candidate-started", "generation-committed", "storage-migrations-prepared", "activated"],
            lifecycle);
        Assert.Equal(Guid.ParseExact(WorkerHarness.ActivationId, "N"), committedGeneration?.ActivationId);
        Assert.Equal(7, committedGeneration?.SessionGeneration);

        await worker.SendInvocationAsync("unary1", "unary", "echo");
        using (var result = await worker.ReadAsync())
        {
            Assert.Equal("worker.result", result.RootElement.GetProperty("type").GetString());
            Assert.Equal("hello", result.RootElement.GetProperty("value").GetProperty("message").GetString());
        }

        await worker.SendInvocationAsync("stream1", "server-stream", "watch");
        using (var complete = await worker.ReadAsync())
        {
            Assert.Equal("worker.complete", complete.RootElement.GetProperty("type").GetString());
        }

        await worker.ShutdownAsync("activation-retired");
        Assert.Equal("shutdown", lifecycle[^1]);
        Assert.Equal(string.Empty, worker.Diagnostics.ToString());
    }

    [Fact]
    public async Task WorkerV1_ContextExposesHostStampedIdentityAndDistinctOwnedRoots()
    {
        SunderWorkerContext? context = null;
        await using var worker = new WorkerHarness(candidate =>
        {
            context = candidate;
            return new SunderWorkerOptions([WorkerHarness.Registration(new TestRpcHandler())]);
        });

        await worker.HandshakeAndActivateAsync();

        Assert.NotNull(context);
        Assert.Equal(WorkerHarness.PackageId, context.PackageId);
        Assert.Equal(WorkerHarness.PackageVersion, context.Version);
        Assert.Equal(worker.ContentPath, context.ContentRootPath);
        Assert.Equal(worker.DataPath, context.TemporaryRootPath);
        Assert.Equal(worker.WorkspacePath, context.RoleLocalWorkspace.WorkspaceRootPath);
        Assert.Same(context.Storage.State, context.State);
        Assert.Same(context.Storage.Files, context.Files);
        Assert.Same(context.Storage.RoleLocalWorkspace, context.RoleLocalWorkspace);
        Assert.IsAssignableFrom<IPackageStorageKeyMigrator>(context.State);
        Assert.IsAssignableFrom<IPackageStorageKeyMigrator>(context.Secrets);
        Assert.Equal(
            Path.Combine(worker.WorkspacePath, "models", "index.bin"),
            context.RoleLocalWorkspace.GetLocalPath("models/index.bin"));
        var exception = Assert.Throws<ArgumentException>(() =>
            context.RoleLocalWorkspace.GetLocalPath("../escape"));
        Assert.Equal("relativePath", exception.ParamName);
        Assert.StartsWith(
            "Package workspace paths must be portable, non-empty, relative, and free of traversal.",
            exception.Message,
            StringComparison.Ordinal);

        await worker.ShutdownAsync();
    }

    [Theory]
    [InlineData("missing-property")]
    [InlineData("relative")]
    [InlineData("missing-directory")]
    [InlineData("content-root")]
    [InlineData("temporary-root")]
    [InlineData("file")]
    [InlineData("link")]
    public async Task WorkerV1_ActivateRejectsInvalidWorkspaceBinding(string scenario)
    {
        await using var worker = new WorkerHarness(
            _ => new SunderWorkerOptions([WorkerHarness.Registration(new TestRpcHandler())]));
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();

        if (scenario == "missing-property")
        {
            await worker.SendAsync(new { type = "host.activate", sessionGeneration = 7 });
        }
        else
        {
            var workspacePath = scenario switch
            {
                "relative" => "relative/workspace",
                "missing-directory" => Path.Combine(worker.WorkspacePath, "missing"),
                "content-root" => worker.ContentPath,
                "temporary-root" => worker.DataPath,
                "file" => CreateWorkspaceFile(worker.WorkspacePath),
                "link" when !OperatingSystem.IsWindows() => CreateWorkspaceLink(worker.WorkspacePath),
                "link" => worker.ContentPath,
                _ => throw new InvalidOperationException($"Unknown workspace test scenario '{scenario}'."),
            };
            await worker.ActivateAsync(workspaceRootPath: workspacePath);
        }

        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WorkerV1_PreparesExactGenerationWithReadOnlyStateBeforeActivation()
    {
        var preparationCompleted = false;
        await using var worker = new WorkerHarness(context => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnBeforeStorageMigrations = async (preparation, cancellationToken) =>
            {
                Assert.Equal(Guid.ParseExact(WorkerHarness.ActivationId, "N"), preparation.Generation.ActivationId);
                Assert.Equal(7, preparation.Generation.SessionGeneration);
                var rejected = await Assert.ThrowsAsync<SunderRpcException>(() =>
                    context.State.GetValueAsync("legacy.value", cancellationToken));
                Assert.Equal(SunderRpcErrorKind.Unavailable, rejected.Error.Kind);
                Assert.Equal("stored", await preparation.GetStateValueAsync("legacy.value", cancellationToken));
                Assert.True(await preparation.ContainsStateKeyAsync("legacy.value", cancellationToken));
                Assert.Equal(
                    ["legacy.value"],
                    await preparation.ListStateKeysAsync("legacy", cancellationToken));
                preparationCompleted = true;
            },
            OnActivated = (_, _) =>
            {
                Assert.True(preparationCompleted);
                return ValueTask.CompletedTask;
            },
        });

        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.SendAsync(new
        {
            type = "host.prepare-storage-migrations",
            activationId = WorkerHarness.ActivationId,
            sessionGeneration = 7,
        });
        using (var get = await worker.ReadAsync())
        {
            Assert.Equal("worker.state-get", get.RootElement.GetProperty("type").GetString());
            Assert.Equal("value", get.RootElement.GetProperty("mode").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = get.RootElement.GetProperty("id").GetString(),
                value = new { kind = "inline", value = "stored" },
            });
        }
        using (var contains = await worker.ReadAsync())
        {
            Assert.Equal("worker.state-get", contains.RootElement.GetProperty("type").GetString());
            Assert.Equal("contains", contains.RootElement.GetProperty("mode").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = contains.RootElement.GetProperty("id").GetString(),
                value = true,
            });
        }
        using (var list = await worker.ReadAsync())
        {
            Assert.Equal("worker.state-list", list.RootElement.GetProperty("type").GetString());
            Assert.Equal("legacy", list.RootElement.GetProperty("prefix").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = list.RootElement.GetProperty("id").GetString(),
                value = new { kind = "inline", keys = new[] { "legacy.value" } },
            });
        }
        using (var prepared = await worker.ReadAsync())
        {
            Assert.Equal("worker.storage-migrations-prepared", prepared.RootElement.GetProperty("type").GetString());
            Assert.Equal(WorkerHarness.ActivationId, prepared.RootElement.GetProperty("activationId").GetString());
            Assert.Equal(7, prepared.RootElement.GetProperty("sessionGeneration").GetInt64());
        }

        await worker.ActivateAsync();
        using (var activated = await worker.ReadAsync())
        {
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        }
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV1_DeclaresAndCompletesStorageMigrationsBeforeActivationCallback()
    {
        var activated = false;
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            StateMigrations =
            [
                PackageStorageKeyMigration.Exact("legacy.state.v1", "state.current.v1"),
            ],
            SecretMigrations =
            [
                PackageStorageKeyMigration.DynamicCleanup(key => key == "legacy.secret.v1"
                    ? PackageStorageKeyMigrationAction.Rewrite("secret.current.v1", precedence: 2)
                    : PackageStorageKeyMigrationAction.NoMatch),
            ],
            OnActivated = (_, _) =>
            {
                activated = true;
                return ValueTask.CompletedTask;
            },
        });

        await worker.HandshakeAsync();
        var plans = worker.ReadyContributions!.Value.GetProperty("storageMigrations");
        Assert.Collection(
            plans.EnumerateArray(),
            state =>
            {
                Assert.Equal("state", state.GetProperty("store").GetString());
                var rule = Assert.Single(state.GetProperty("rules").EnumerateArray());
                Assert.Equal("exact", rule.GetProperty("kind").GetString());
                Assert.Equal("legacy.state.v1", rule.GetProperty("legacyKey").GetString());
                Assert.Equal("state.current.v1", rule.GetProperty("destinationKey").GetString());
            },
            secrets =>
            {
                Assert.Equal("secrets", secrets.GetProperty("store").GetString());
                var rule = Assert.Single(secrets.GetProperty("rules").EnumerateArray());
                Assert.Equal(["kind"], rule.EnumerateObject().Select(static property => property.Name));
                Assert.Equal("dynamic-cleanup", rule.GetProperty("kind").GetString());
            });
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();
        await worker.ActivateAsync();

        using (var stateApply = await worker.ReadAsync())
        {
            var root = stateApply.RootElement;
            Assert.Equal("worker.migration-apply", root.GetProperty("type").GetString());
            Assert.Equal("state", root.GetProperty("store").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = root.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var secretKeys = await worker.ReadAsync())
        {
            var root = secretKeys.RootElement;
            Assert.Equal("worker.migration-keys", root.GetProperty("type").GetString());
            Assert.Equal("secrets", root.GetProperty("store").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = root.GetProperty("id").GetString(),
                value = new { keys = new[] { "legacy.secret.v1" } },
            });
        }
        using (var secretApply = await worker.ReadAsync())
        {
            var root = secretApply.RootElement;
            Assert.Equal("worker.migration-apply", root.GetProperty("type").GetString());
            Assert.Equal("secrets", root.GetProperty("store").GetString());
            var rule = Assert.Single(root.GetProperty("rules").EnumerateArray());
            var action = Assert.Single(rule.GetProperty("actions").EnumerateArray());
            Assert.Equal("legacy.secret.v1", action.GetProperty("key").GetString());
            Assert.Equal("rewrite", action.GetProperty("kind").GetString());
            Assert.Equal("secret.current.v1", action.GetProperty("destinationKey").GetString());
            Assert.Equal(2, action.GetProperty("precedence").GetInt32());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = root.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var acknowledgement = await worker.ReadAsync())
        {
            Assert.Equal("worker.activated", acknowledgement.RootElement.GetProperty("type").GetString());
        }
        Assert.True(activated);

        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV1_CompositionPreparesCommitsActivatesShutsDownAndDisposesInOrder()
    {
        var events = new List<string>();
        var services = new RecordingServiceProvider(events);
        var first = new RecordingWorkerLifecycleService("first", events);
        var second = new RecordingMonitoredLifecycleService("second", events);
        var composition = new SunderWorkerComposition(services, [first, second]);
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            Composition = composition,
            OnCandidateStarted = _ =>
            {
                events.Add("custom-candidate");
                return ValueTask.CompletedTask;
            },
            OnGenerationCommitted = (_, _) =>
            {
                events.Add("custom-commit");
                return ValueTask.CompletedTask;
            },
            OnActivated = (_, _) =>
            {
                events.Add("custom-activate");
                return ValueTask.CompletedTask;
            },
            OnShutdown = (_, _) =>
            {
                events.Add("custom-shutdown");
                return ValueTask.CompletedTask;
            },
        });

        await worker.HandshakeAndActivateAsync();

        Assert.Same(services, composition.Services);
        Assert.Equal(
            [
                "prepare:first",
                "prepare:second",
                "custom-candidate",
                "commit:first:7",
                "commit:second:7",
                "custom-commit",
                "activate:first",
                "activate:second",
                "custom-activate",
            ],
            events);
        await worker.ShutdownAsync();
        Assert.Equal(
            [
                "prepare:first",
                "prepare:second",
                "custom-candidate",
                "commit:first:7",
                "commit:second:7",
                "custom-commit",
                "activate:first",
                "activate:second",
                "custom-activate",
                "custom-shutdown",
                "shutdown:second",
                "shutdown:first",
                "dispose:services",
            ],
            events);
    }

    [Fact]
    public async Task WorkerV1_CompositionShutsDownPreparedDiscardedCandidateWithoutCommitOrActivation()
    {
        var events = new List<string>();
        var composition = new SunderWorkerComposition(
            new RecordingServiceProvider(events),
            [new RecordingWorkerLifecycleService("candidate", events)]);
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            Composition = composition,
        });

        await worker.HandshakeAsync();
        await worker.SendAsync(new { type = "host.start-candidate" });
        using (var started = await worker.ReadAsync())
        {
            Assert.Equal("worker.candidate-started", started.RootElement.GetProperty("type").GetString());
        }
        await worker.ShutdownAsync("candidate-discarded");

        Assert.Equal(
            ["prepare:candidate", "shutdown:candidate", "dispose:services"],
            events);
    }

    [Fact]
    public async Task WorkerV1_CompositionActivationCompletesBeforeAcknowledgement()
    {
        var events = new List<string>();
        var lifecycle = new BlockingActivationLifecycleService("blocking", events);
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            Composition = new SunderWorkerComposition(
                new RecordingServiceProvider(events),
                [lifecycle]),
        });

        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();
        await worker.ActivateAsync();
        await lifecycle.ActivationStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var acknowledgement = worker.ReadAsync().AsTask();
        await Task.Delay(100);
        Assert.False(acknowledgement.IsCompleted);

        lifecycle.ReleaseActivation();
        using (var activated = await acknowledgement)
        {
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        }
        await worker.ShutdownAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerV1_CompositionGenerationFaultFaultsSupervisedWorker(bool hostShutdownFirst)
    {
        var events = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestRpcHandler
        {
            Unary = async (_, _, _, request, token) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return request.Clone();
                }
                finally
                {
                    cancelled.TrySetResult();
                    await releaseCleanup.Task;
                    events.Add("handler-drained");
                    handlerFinished.TrySetResult();
                }
            },
        };
        var lifecycle = new RecordingMonitoredLifecycleService("faulting", events);
        var composition = new SunderWorkerComposition(
            new RecordingServiceProvider(events),
            [lifecycle]);
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(handler)])
        {
            Composition = composition,
        });
        await worker.HandshakeAndActivateAsync();
        await worker.SendInvocationAsync("fault-active-call", "unary", "echo");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            if (hostShutdownFirst)
            {
                await worker.SendAsync(new { type = "host.shutdown", shutdownId = "fault-stop", reason = "test" });
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            lifecycle.Fail(new InvalidOperationException("lifecycle failed"));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using (var report = await worker.ReadAsync())
            {
                Assert.Equal("worker.generation-fault", report.RootElement.GetProperty("type").GetString());
                Assert.Equal(WorkerHarness.ActivationId, report.RootElement.GetProperty("activationId").GetString());
                var diagnostic = report.RootElement.GetProperty("message").GetString()!;
                Assert.Contains(nameof(InvalidOperationException), diagnostic, StringComparison.Ordinal);
                Assert.DoesNotContain("lifecycle failed", diagnostic, StringComparison.Ordinal);
            }
            // A request written before the Host saw the fault may cross the report.
            // It must never enter the package or abort its ongoing cleanup.
            await worker.SendInvocationAsync("crossed-fault-call", "unary", "echo");
            await worker.SendAsync(new { type = "host.cancel", id = "crossed-fault-call" });
            if (!hostShutdownFirst)
                await worker.SendAsync(new { type = "host.shutdown", shutdownId = "fault-stop", reason = "test" });
            Assert.DoesNotContain("shutdown:faulting", events);
            Assert.DoesNotContain("dispose:services", events);
            Assert.False(worker.RunTask.IsCompleted);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await handlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(failure.Message.Contains("lifecycle service faulted", StringComparison.OrdinalIgnoreCase), failure.ToString());
        await composition.DisposeAsync();
        Assert.Equal(
            ["prepare:faulting", "commit:faulting:7", "activate:faulting", "handler-drained", "shutdown:faulting", "dispose:services"],
            events);
    }

    [Theory]
    [InlineData("candidate", true)]
    [InlineData("commit", true)]
    [InlineData("migration", true)]
    [InlineData("activation", true)]
    [InlineData("activation", false)]
    public async Task WorkerV1_StartupFailureRunsTheShutdownOwnerExactlyOnce(string stage, bool withComposition)
    {
        var events = new List<string>();
        var shutdownCount = 0;
        var composition = new SunderWorkerComposition(new RecordingServiceProvider(events),
            [new RecordingWorkerLifecycleService("startup", events)]);
        ValueTask Step(string current) => current == stage
            ? ValueTask.FromException(new InvalidOperationException("startup failure: " + stage))
            : ValueTask.CompletedTask;
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            Composition = withComposition ? composition : null,
            OnCandidateStarted = _ => Step("candidate"),
            OnGenerationCommitted = (_, _) => Step("commit"),
            OnBeforeStorageMigrations = (_, _) => Step("migration"),
            OnActivated = (_, _) => Step("activation"),
            OnShutdown = (_, _) =>
            {
                Interlocked.Increment(ref shutdownCount);
                events.Add("shutdown-owner");
                return ValueTask.CompletedTask;
            },
        });
        await worker.HandshakeAsync();
        await worker.SendAsync(new { type = "host.start-candidate" });
        if (stage != "candidate")
        {
            using var started = await worker.ReadAsync();
            Assert.Equal("worker.candidate-started", started.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new { type = "host.commit-generation", activationId = WorkerHarness.ActivationId, sessionGeneration = 7 });
            if (stage != "commit")
            {
                using var committed = await worker.ReadAsync();
                Assert.Equal("worker.generation-committed", committed.RootElement.GetProperty("type").GetString());
                await worker.SendAsync(new { type = "host.prepare-storage-migrations", activationId = WorkerHarness.ActivationId, sessionGeneration = 7 });
                if (stage != "migration")
                {
                    using var prepared = await worker.ReadAsync();
                    Assert.Equal("worker.storage-migrations-prepared", prepared.RootElement.GetProperty("type").GetString());
                    await worker.ActivateAsync();
                }
            }
        }
        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("startup failure: " + stage, failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, shutdownCount);
        if (withComposition)
        {
            await composition.DisposeAsync();
            Assert.Equal(["shutdown-owner", "shutdown:startup", "dispose:services"], events.TakeLast(3));
            Assert.Single(events, value => value == "dispose:services");
        }
    }

    [Fact]
    public async Task WorkerV1_FaultDrainTimeoutDoesNotDisposeDependenciesUnderNoncooperativeWork()
    {
        var events = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle = new RecordingMonitoredLifecycleService("faulting", events);
        var composition = new SunderWorkerComposition(new RecordingServiceProvider(events), [lifecycle]);
        var handler = new TestRpcHandler
        {
            Unary = async (_, _, _, request, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                finished.TrySetResult();
                return request;
            },
        };
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions([WorkerHarness.Registration(handler)])
        {
            Composition = composition,
        }, WorkerLimits.Default with { ShutdownDrainTimeout = TimeSpan.FromMilliseconds(150) });
        await worker.HandshakeAndActivateAsync();
        await worker.SendInvocationAsync("noncooperative", "unary", "echo");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            lifecycle.Fail(new InvalidOperationException("original lifecycle failure"));
            var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("original lifecycle failure", failure.ToString(), StringComparison.Ordinal);
            Assert.Contains("deadline", failure.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("shutdown:faulting", events);
            Assert.DoesNotContain("dispose:services", events);
        }
        finally
        {
            release.TrySetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await composition.DisposeAsync();
        }
    }

    [Fact]
    public async Task WorkerV1_LifecycleAndShutdownFailuresRetainBothCausesAndDisposeOnce()
    {
        var events = new List<string>();
        var lifecycle = new RecordingMonitoredLifecycleService("faulting", events);
        var composition = new SunderWorkerComposition(new RecordingServiceProvider(events), [lifecycle]);
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            Composition = composition,
            OnShutdown = (_, _) => ValueTask.FromException(new InvalidOperationException("shutdown failure")),
        });
        await worker.HandshakeAndActivateAsync();
        lifecycle.Fail(new InvalidOperationException("original lifecycle failure"));
        using (var report = await worker.ReadAsync())
            Assert.Equal("worker.generation-fault", report.RootElement.GetProperty("type").GetString());
        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("original lifecycle failure", failure.ToString(), StringComparison.Ordinal);
        Assert.Contains("shutdown failure", failure.ToString(), StringComparison.Ordinal);
        await composition.DisposeAsync();
        Assert.Single(events, value => value == "dispose:services");
    }

    [Fact]
    public async Task WorkerV1_CallerCancellationDoesNotEraseShutdownFailure()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnShutdown = async (_, token) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { throw new IOException("Cleanup failed after cancellation."); }
            },
        });
        await worker.HandshakeAndActivateAsync();
        await worker.SendAsync(new { type = "host.shutdown", shutdownId = "cancel-failure", reason = "test" });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        worker.Cancel();
        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("Cleanup failed after cancellation.", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerV1_SynchronousActivationPrefixDoesNotBlockShutdownOrHideItsDrain()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var shutdown = false;
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = (_, token) =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(5));
                token.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            },
            OnShutdown = (_, _) => { shutdown = true; return ValueTask.CompletedTask; },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();
        await worker.ActivateAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await worker.SendAsync(new { type = "host.shutdown", shutdownId = "sync-stop", reason = "test" });
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(shutdown);
        }
        finally { release.Set(); }
        using var acknowledgement = await worker.ReadAsync();
        Assert.Equal("worker.shutdown-ack", acknowledgement.RootElement.GetProperty("type").GetString());
        await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(shutdown);
    }

    [Fact]
    public async Task WorkerV1_CallerAbortRejectsShutdownLoggingWithoutStrandingAnOutboundCall()
    {
        SunderWorkerContext? context = null;
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new WorkerHarness(candidate =>
        {
            context = candidate;
            return new SunderWorkerOptions([WorkerHarness.Registration(new TestRpcHandler())])
            {
                OnShutdown = async (_, _) =>
                {
                    var failure = await Assert.ThrowsAsync<SunderRpcException>(() =>
                        context.Logging.Events.InformationAsync("worker.stopping", "Stopping").AsTask());
                    Assert.Equal("rpc.worker.not-active", failure.Error.Code);
                    shutdown.TrySetResult();
                },
            };
        });
        await worker.HandshakeAndActivateAsync();
        worker.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(shutdown.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WorkerV1_CompositionPublishesDisposalBeforeReentrantServiceCleanup()
    {
        var events = new List<string>();
        Task? reentrant = null;
        SunderWorkerComposition? composition = null;
        var attempts = 0;
        var services = new RecordingServiceProvider(events)
        {
            OnDispose = () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    reentrant = composition!.DisposeAsync().AsTask();
            },
        };
        composition = new SunderWorkerComposition(services);
        await composition.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await reentrant!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, attempts);
        Assert.Equal(["dispose:services"], events);
    }

    [Fact]
    public async Task WorkerV1_ReportsOnlyOneBoundedFaultForItsActiveGeneration()
    {
        SunderWorkerContext? context = null;
        await using var worker = new WorkerHarness(candidate =>
        {
            context = candidate;
            return new SunderWorkerOptions([WorkerHarness.Registration(new TestRpcHandler())]);
        });
        await worker.HandshakeAndActivateAsync(sessionGeneration: 17);

        await context!.ReportGenerationFaultAsync(
            "worker.model-corrupt",
            "The local model index is corrupt.\r\n");

        using (var report = await worker.ReadAsync())
        {
            var root = report.RootElement;
            Assert.Equal(
                ["type", "activationId", "sessionGeneration", "code", "message"],
                root.EnumerateObject().Select(static property => property.Name));
            Assert.Equal("worker.generation-fault", root.GetProperty("type").GetString());
            Assert.Equal(WorkerHarness.ActivationId, root.GetProperty("activationId").GetString());
            Assert.Equal(17, root.GetProperty("sessionGeneration").GetInt64());
            Assert.Equal("worker.model-corrupt", root.GetProperty("code").GetString());
            Assert.Equal("The local model index is corrupt.", root.GetProperty("message").GetString());
        }
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await context.ReportGenerationFaultAsync("worker.second-fault", "duplicate"));

        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV1_RejectsControlGenerationOutsideJavaScriptSafeIntegerRange()
    {
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())]));
        await worker.HandshakeAsync();
        await worker.SendAsync(new { type = "host.start-candidate" });
        using (var started = await worker.ReadAsync())
        {
            Assert.Equal("worker.candidate-started", started.RootElement.GetProperty("type").GetString());
        }

        await worker.SendAsync(new
        {
            type = "host.commit-generation",
            activationId = WorkerHarness.ActivationId,
            sessionGeneration = 9_007_199_254_740_992L,
        });

        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("cancel-after-churn")]
    [InlineData("invoke-with-live-callback-id")]
    public async Task WorkerV1_CallbackCatalogAndCancellationAreExactGenerationBound(string scenario)
    {
        var handler = new BlockingCallbackHandler();
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler { Unary = static (_, _, _, request, _) => ValueTask.FromResult(request.Clone()) })])
        {
            CallbackHandlers = [handler],
        }, WorkerLimits.Default with { MaximumRememberedHostIds = 2 });
        await worker.HandshakeAndActivateAsync(sessionGeneration: 23);

        var contributions = worker.ReadyContributions!.Value;
        Assert.Equal(
            [
                "settingsSchema",
                "storageMigrations",
                "callbackHandlers",
                "authHandler",
                "rpcProviders",
                "candidateLifecycle",
                "generationLifecycle",
                "migrationPreparationLifecycle",
                "operationalOutcomesV1",
            ],
            contributions.EnumerateObject().Select(static property => property.Name));
        Assert.Equal(
            [handler.CallbackHandlerId],
            contributions.GetProperty("callbackHandlers").EnumerateArray()
                .Select(static item => item.GetString()));
        Assert.False(contributions.GetProperty("authHandler").GetBoolean());

        await worker.SendAsync(new
        {
            type = "host.callback-start",
            id = "callback-call-1",
            activationId = WorkerHarness.ActivationId,
            sessionGeneration = 23,
            value = new
            {
                handlerId = handler.CallbackHandlerId,
                callbackSessionId = "callback-session-1",
                callbackUri = "http://127.0.0.1/callback",
                purpose = (string?)null,
                parameters = new { },
            },
        });
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));
        if (scenario == "invoke-with-live-callback-id")
        {
            await worker.SendInvocationAsync("callback-call-1", "unary", "echo", sessionGeneration: 23);
            await Assert.ThrowsAsync<WorkerProtocolException>(() => worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
            return;
        }
        if (scenario == "cancel-after-churn")
        {
            for (var index = 0; index < 3; index++)
            {
                var id = "churn" + index;
                await worker.SendInvocationAsync(id, "unary", "echo", sessionGeneration: 23);
                using var response = await worker.ReadAsync();
                Assert.Equal(id, response.RootElement.GetProperty("id").GetString());
            }
        }
        await worker.SendAsync(new
        {
            type = "host.contribution-cancel",
            id = "callback-call-1",
            activationId = WorkerHarness.ActivationId,
            sessionGeneration = 23,
        });

        using (var response = await worker.ReadAsync())
        {
            var root = response.RootElement;
            Assert.Equal("worker.contribution-error", root.GetProperty("type").GetString());
            Assert.Equal("callback-call-1", root.GetProperty("id").GetString());
            Assert.Equal(WorkerHarness.ActivationId, root.GetProperty("activationId").GetString());
            Assert.Equal(23, root.GetProperty("sessionGeneration").GetInt64());
            Assert.Equal(
                "worker.contribution-cancelled",
                root.GetProperty("error").GetProperty("code").GetString());
        }
        await handler.Cancelled.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.SendAsync(new
        {
            type = "host.contribution-cancel", id = "callback-call-1",
            activationId = WorkerHarness.ActivationId, sessionGeneration = 23,
        });
        await worker.ShutdownAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkerV1_ProviderFaultEmitsOnlySafeDiagnosticBeforeGenericError(
        bool diagnosticAccepted)
    {
        const string secret = "provider-secret=must-never-persist";
        var handler = new TestRpcHandler
        {
            Unary = static (_, _, _, _, _) => throw new SecretBearingProviderException(secret),
        };
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(handler)]));
        await worker.HandshakeAndActivateAsync();

        await worker.SendInvocationAsync("providerFault1", "unary", "send");
        string diagnosticId;
        using (var diagnostic = await worker.ReadAsync())
        {
            var root = diagnostic.RootElement;
            Assert.Equal(
                [
                    "type",
                    "id",
                    "invocationId",
                    "providerId",
                    "serviceId",
                    "methodId",
                    "exceptionType",
                    "exceptionFingerprint",
                ],
                root.EnumerateObject().Select(static property => property.Name));
            Assert.Equal("worker.provider-fault-diagnostic", root.GetProperty("type").GetString());
            diagnosticId = root.GetProperty("id").GetString()!;
            Assert.Equal("providerFault1", root.GetProperty("invocationId").GetString());
            Assert.Equal(WorkerHarness.ProviderId, root.GetProperty("providerId").GetString());
            Assert.Equal("messages", root.GetProperty("serviceId").GetString());
            Assert.Equal("send", root.GetProperty("methodId").GetString());
            var exceptionType = typeof(SecretBearingProviderException).FullName!;
            Assert.Equal(exceptionType, root.GetProperty("exceptionType").GetString());
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exceptionType))).ToLowerInvariant(),
                root.GetProperty("exceptionFingerprint").GetString());
            Assert.DoesNotContain(secret, root.GetRawText(), StringComparison.Ordinal);
        }

        await worker.SendAsync(diagnosticAccepted
            ? new
            {
                type = "host.result",
                id = diagnosticId,
                value = (object?)null,
            }
            : new
            {
                type = "host.error",
                id = diagnosticId,
                error = (object)new
                {
                    kind = "unavailable",
                    code = "rpc.logging.unavailable",
                    message = "Host logging is unavailable.",
                    dispatchState = "not-dispatched",
                    retrySafety = "safe",
                },
            });

        using (var response = await worker.ReadAsync())
        {
            var root = response.RootElement;
            Assert.Equal("worker.error", root.GetProperty("type").GetString());
            Assert.Equal("providerFault1", root.GetProperty("id").GetString());
            var error = root.GetProperty("error");
            Assert.Equal("provider-fault", error.GetProperty("kind").GetString());
            Assert.Equal("rpc.provider.handler-fault", error.GetProperty("code").GetString());
            Assert.Equal("The process provider handler failed.", error.GetProperty("message").GetString());
            Assert.DoesNotContain(secret, root.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(SecretBearingProviderException), root.GetRawText(), StringComparison.Ordinal);
        }

        await worker.ShutdownAsync();
    }

    [Theory]
    [InlineData("sunder.worker.unknown", 99)]
    [InlineData("sunder.worker.v3", 3)]
    public async Task WorkerV1_RejectsNonV1HelloWithoutFallback(string protocol, int protocolVersion)
    {
        var configured = false;
        await using var worker = new WorkerHarness(_ =>
        {
            configured = true;
            return new SunderWorkerOptions([WorkerHarness.Registration(new TestRpcHandler())]);
        });

        await worker.SendAsync(new
        {
            type = "host.hello",
            protocol,
            protocolVersion,
            challenge = "challenge",
            packageId = WorkerHarness.PackageId,
            packageVersion = WorkerHarness.PackageVersion,
            activationId = WorkerHarness.ActivationId,
            sessionId = WorkerHarness.SessionId,
            providers = new[]
            {
                new
                {
                    providerId = WorkerHarness.ProviderId,
                    contractId = WorkerHarness.ContractId,
                    contractVersion = WorkerHarness.ContractVersion,
                    contractSha256 = WorkerHarness.DescriptorSha256,
                },
            },
        });

        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(configured);
    }

    [Fact]
    public async Task WorkerV1_AcceptsHigherSessionGenerationForSameSurvivingActivation()
    {
        var handler = new TestRpcHandler
        {
            Unary = static (_, _, _, request, _) => ValueTask.FromResult(request.Clone()),
        };
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(handler)]));
        await worker.HandshakeAndActivateAsync(sessionGeneration: 7);

        await worker.SendInvocationAsync(
            "restamped1",
            "unary",
            "echo",
            sessionGeneration: 8);
        using (var result = await worker.ReadAsync())
        {
            Assert.Equal("worker.result", result.RootElement.GetProperty("type").GetString());
            Assert.Equal("hello", result.RootElement.GetProperty("value").GetProperty("message").GetString());
        }
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV1_RejectsProviderGenerationBelowInitialCommitBeforeEnteringHandler()
    {
        var handlerEntryCount = 0;
        var handler = new TestRpcHandler
        {
            Unary = (_, _, _, request, _) =>
            {
                Interlocked.Increment(ref handlerEntryCount);
                return ValueTask.FromResult(request.Clone());
            },
        };
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(handler)]));
        await worker.HandshakeAndActivateAsync(sessionGeneration: 7);

        await worker.SendInvocationAsync(
            "stale1",
            "unary",
            "echo",
            sessionGeneration: 6);

        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("exact registered provider activation", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref handlerEntryCount));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerV1_ActivationCanCallHostBeforeAcknowledgingReadiness(bool rpcExtendedDeadlineV1)
    {
        SunderRpcCatalogSnapshot? discovered = null;
        await using var worker = new WorkerHarness(context => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                discovered = await context.Rpc.DiscoverAsync(WorkerHarness.ContractId, cancellationToken);
                await context.Rpc.InvokeAsync(new SunderRpcEndpointReference("rpc1_example"),
                    "messages", "send", JsonSerializer.SerializeToElement(new { message = "activation" }),
                    cancellationToken: cancellationToken);
            },
        }, rpcExtendedDeadlineV1: rpcExtendedDeadlineV1);
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();

        await worker.ActivateAsync();
        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.discover", request.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    revision = 3,
                    sequence = 4,
                    providers = Array.Empty<object>(),
                    resetRequired = false,
                },
            });
        }
        using (var invocation = await worker.ReadAsync())
        {
            Assert.Equal("worker.invoke", invocation.RootElement.GetProperty("type").GetString());
            if (rpcExtendedDeadlineV1)
                Assert.Equal(JsonValueKind.Null, invocation.RootElement.GetProperty("parentInvocationId").ValueKind);
            else
                Assert.False(invocation.RootElement.TryGetProperty("parentInvocationId", out _));
            await worker.SendAsync(new { type = "host.result", id = invocation.RootElement.GetProperty("id").GetString(), value = new { accepted = true } });
        }
        using (var activated = await worker.ReadAsync())
        {
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        }

        Assert.Equal(3, discovered?.Revision);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV1_ProviderSynchronousPrefixCannotBlockNestedHostResponse()
    {
        SunderWorkerContext? context = null;
        var handler = new TestRpcHandler
        {
            Unary = (_, _, _, request, cancellationToken) =>
            {
                _ = context!.Rpc.DiscoverAsync(WorkerHarness.ContractId, cancellationToken)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                return ValueTask.FromResult(request.Clone());
            },
        };
        await using var worker = new WorkerHarness(workerContext =>
        {
            context = workerContext;
            return new SunderWorkerOptions([WorkerHarness.Registration(handler)]);
        });
        await worker.HandshakeAndActivateAsync();

        await worker.SendInvocationAsync("nested1", "unary", "nested");
        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.discover", request.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    revision = 1,
                    sequence = 1,
                    providers = Array.Empty<object>(),
                    resetRequired = false,
                },
            });
        }
        using (var result = await worker.ReadAsync())
        {
            Assert.Equal("worker.result", result.RootElement.GetProperty("type").GetString());
            Assert.Equal("hello", result.RootElement.GetProperty("value").GetProperty("message").GetString());
        }

        await worker.ShutdownAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerV1_NestedInvocationEmitsLineageOnlyWhenCapabilityIsDeclared(
        bool rpcExtendedDeadlineV1)
    {
        SunderWorkerContext? context = null;
        var handler = new TestRpcHandler
        {
            Unary = async (_, _, _, request, cancellationToken) =>
            {
                _ = await context!.Rpc.InvokeAsync(
                    new SunderRpcEndpointReference("rpc1_example"),
                    "messages",
                    "send",
                    request,
                    cancellationToken: cancellationToken);
                return request.Clone();
            },
        };
        await using var worker = new WorkerHarness(workerContext =>
        {
            context = workerContext;
            return new SunderWorkerOptions([WorkerHarness.Registration(handler)]);
        }, rpcExtendedDeadlineV1: rpcExtendedDeadlineV1);
        await worker.HandshakeAndActivateAsync();

        await worker.SendInvocationAsync("parent1", "unary", "nested");
        using (var nested = await worker.ReadAsync())
        {
            Assert.Equal("worker.invoke", nested.RootElement.GetProperty("type").GetString());
            if (rpcExtendedDeadlineV1)
            {
                Assert.Equal("parent1", nested.RootElement.GetProperty("parentInvocationId").GetString());
            }
            else
            {
                Assert.False(nested.RootElement.TryGetProperty("parentInvocationId", out _));
            }
            await worker.SendAsync(new
            {
                type = "host.result",
                id = nested.RootElement.GetProperty("id").GetString(),
                value = new { accepted = true },
            });
        }
        using (var result = await worker.ReadAsync())
        {
            Assert.Equal("worker.result", result.RootElement.GetProperty("type").GetString());
        }

        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV1_OptedInNestedScopeAndSubscribeCarryParentInvocationId()
    {
        SunderWorkerContext? context = null;
        var handler = new TestRpcHandler
        {
            Unary = async (_, _, _, request, cancellationToken) =>
            {
                await using var scope = await context!.Rpc.CreateCallScopeAsync(
                    cancellationToken: cancellationToken);
                await foreach (var _ in scope.SubscribeAsync(
                                   new SunderRpcEndpointReference("rpc1_example"),
                                   "messages",
                                   "watch",
                                   request,
                                   cancellationToken: cancellationToken))
                {
                }
                return request.Clone();
            },
        };
        await using var worker = new WorkerHarness(workerContext =>
        {
            context = workerContext;
            return new SunderWorkerOptions([WorkerHarness.Registration(handler)]);
        }, rpcExtendedDeadlineV1: true);
        await worker.HandshakeAndActivateAsync();

        await worker.SendInvocationAsync("scope-parent", "unary", "nested-scope");
        const string scopeId = "nested-scope-id";
        using (var open = await worker.ReadAsync())
        {
            Assert.Equal("worker.scope-open", open.RootElement.GetProperty("type").GetString());
            Assert.Equal("scope-parent", open.RootElement.GetProperty("parentInvocationId").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = open.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    scopeId,
                    deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
                },
            });
        }
        using (var subscribe = await worker.ReadAsync())
        {
            Assert.Equal("worker.subscribe", subscribe.RootElement.GetProperty("type").GetString());
            Assert.Equal(scopeId, subscribe.RootElement.GetProperty("scopeId").GetString());
            Assert.Equal("scope-parent", subscribe.RootElement.GetProperty("parentInvocationId").GetString());
            await worker.SendAsync(new
            {
                type = "host.complete",
                id = subscribe.RootElement.GetProperty("id").GetString(),
            });
        }
        using (var close = await worker.ReadAsync())
        {
            Assert.Equal("worker.scope-close", close.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = close.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var result = await worker.ReadAsync())
        {
            Assert.Equal("worker.result", result.RootElement.GetProperty("type").GetString());
        }

        await worker.ShutdownAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerV1_ShutdownInterruptsPendingCandidateOrCommitCallback(bool duringCommit)
    {
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnCandidateStarted = duringCommit
                ? null
                : async cancellationToken =>
                {
                    callbackStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                },
            OnGenerationCommitted = duringCommit
                ? async (_, cancellationToken) =>
                {
                    callbackStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                : null,
        });
        await worker.HandshakeAsync();
        await worker.SendAsync(new { type = "host.start-candidate" });
        if (duringCommit)
        {
            using var started = await worker.ReadAsync();
            Assert.Equal("worker.candidate-started", started.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.commit-generation",
                activationId = WorkerHarness.ActivationId,
                sessionGeneration = 7,
            });
        }
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await worker.SendAsync(new
        {
            type = "host.shutdown",
            shutdownId = "shutdown1",
            reason = "candidate-discarded",
        });
        using (var acknowledgement = await worker.ReadAsync())
        {
            Assert.Equal("worker.shutdown-ack", acknowledgement.RootElement.GetProperty("type").GetString());
        }
        await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WorkerV1_ExactCommitRetryIsIdempotentWhileCallbackRuns()
    {
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnGenerationCommitted = async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref callbackCount);
                callbackStarted.TrySetResult();
                await releaseCallback.Task.WaitAsync(cancellationToken);
            },
        });
        await worker.HandshakeAsync();
        await worker.SendAsync(new { type = "host.start-candidate" });
        using (var started = await worker.ReadAsync())
        {
            Assert.Equal("worker.candidate-started", started.RootElement.GetProperty("type").GetString());
        }
        var commit = new
        {
            type = "host.commit-generation",
            activationId = WorkerHarness.ActivationId,
            sessionGeneration = 7,
        };
        await worker.SendAsync(commit);
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.SendAsync(commit);
        await Task.Delay(100);

        Assert.False(worker.RunTask.IsCompleted);
        releaseCallback.TrySetResult();
        using (var committed = await worker.ReadAsync())
        {
            Assert.Equal("worker.generation-committed", committed.RootElement.GetProperty("type").GetString());
        }
        var nextOutput = worker.ReadAsync().AsTask();
        await Task.Delay(100);
        Assert.False(nextOutput.IsCompleted);
        await worker.SendAsync(new
        {
            type = "host.shutdown",
            shutdownId = "shutdown1",
            reason = "candidate-discarded",
        });
        using (var acknowledgement = await nextOutput)
        {
            Assert.Equal("worker.shutdown-ack", acknowledgement.RootElement.GetProperty("type").GetString());
        }
        Assert.Equal(1, callbackCount);
        await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WorkerV1_CallScopeRoutesRpcAndClosesOnce()
    {
        var requestedDeadline = DateTimeOffset.UtcNow.AddMinutes(2);
        var clampedDeadline = requestedDeadline.AddSeconds(-5);
        SunderRpcProviderSnapshot? provider = null;
        SunderRpcCatalogSnapshot? catalog = null;
        JsonElement invocation = default;
        var invariantReportAccepted = false;
        var catalogEvents = new List<SunderRpcCatalogEvent>();
        var streamEvents = new List<JsonElement>();
        await using var worker = new WorkerHarness(context => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                var scope = await context.Rpc.CreateCallScopeAsync(
                    new SunderRpcCallOptions(requestedDeadline),
                    cancellationToken);
                Assert.Equal(clampedDeadline, scope.DeadlineUtc);
                provider = await scope.GetProviderAsync(
                    new SunderRpcEndpointReference("rpc1_example"),
                    cancellationToken);
                invariantReportAccepted = await scope.TryReportInvariantViolationAsync(
                    new SunderRpcEndpointReference("rpc1_example"),
                    new InvalidOperationException("line one\r\nline two " + new string('x', 600)),
                    cancellationToken);
                catalog = await scope.DiscoverAsync(WorkerHarness.ContractId, cancellationToken);
                invocation = await scope.InvokeAsync(
                    new SunderRpcEndpointReference("rpc1_example"),
                    "messages",
                    "send",
                    JsonSerializer.SerializeToElement(new { message = "hello" }),
                    cancellationToken: cancellationToken);
                await foreach (var item in scope.WatchAsync(3, 4, cancellationToken))
                {
                    catalogEvents.Add(item);
                }
                await foreach (var item in scope.SubscribeAsync(
                                   new SunderRpcEndpointReference("rpc1_example"),
                                   "messages",
                                   "watch",
                                   JsonSerializer.SerializeToElement(new { message = "hello" }),
                                   cancellationToken: cancellationToken))
                {
                    streamEvents.Add(item);
                }
                await scope.DisposeAsync();
                await scope.DisposeAsync();
            },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();
        await worker.ActivateAsync();

        const string scopeId = "worker-scope-test";
        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.scope-open", request.RootElement.GetProperty("type").GetString());
            Assert.Equal(requestedDeadline, request.RootElement.GetProperty("deadlineUtc").GetDateTimeOffset());
            Assert.False(request.RootElement.TryGetProperty("parentInvocationId", out _));
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    scopeId,
                    deadlineUtc = clampedDeadline.UtcDateTime.ToString("O"),
                },
            });
        }
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.get-provider", scopeId);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = WorkerHarness.ProviderSnapshot(),
            });
        }
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.report-invariant-violation", scopeId);
            Assert.Equal("rpc1_example", request.RootElement.GetProperty("endpointReference").GetString());
            var message = request.RootElement.GetProperty("exceptionMessage").GetString();
            Assert.NotNull(message);
            Assert.Equal(512, message.Length);
            Assert.DoesNotContain('\r', message);
            Assert.DoesNotContain('\n', message);
            Assert.DoesNotContain(nameof(InvalidOperationException), message, StringComparison.Ordinal);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = true,
            });
        }
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.discover", scopeId);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    revision = 3,
                    sequence = 4,
                    providers = new[] { WorkerHarness.ProviderSnapshot() },
                    resetRequired = false,
                },
            });
        }
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.invoke", scopeId);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new { accepted = true },
            });
        }
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.watch", scopeId);
            var id = request.RootElement.GetProperty("id").GetString();
            await worker.SendAsync(new
            {
                type = "host.event",
                id,
                value = new
                {
                    revision = 4,
                    sequence = 5,
                    kind = "added",
                    provider = WorkerHarness.ProviderSnapshot(),
                },
            });
            await worker.SendAsync(new { type = "host.complete", id });
        }
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.subscribe", scopeId);
            var id = request.RootElement.GetProperty("id").GetString();
            await worker.SendAsync(new { type = "host.event", id, value = new { sequence = 1 } });
            await worker.SendAsync(new { type = "host.complete", id });
        }
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.scope-close", scopeId);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var activated = await worker.ReadAsync())
        {
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        }

        Assert.Equal("example.provider", provider?.ProviderId);
        Assert.Equal(WorkerHarness.DescriptorSha256, provider?.DescriptorSha256);
        Assert.Equal(WorkerHarness.AbiSha256, provider?.AbiSha256);
        Assert.True(invariantReportAccepted);
        Assert.Equal(3, catalog?.Revision);
        Assert.True(invocation.GetProperty("accepted").GetBoolean());
        Assert.Single(catalogEvents);
        Assert.Single(streamEvents);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV1_CallScopeRegistersOpensAndReleasesContent()
    {
        const string content = "scope-content";
        string? opened = null;
        await using var worker = new WorkerHarness(context => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                await using var scope = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                await using var source = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
                var reference = await scope.RegisterContentAsync(
                    new SunderRpcEndpointReference("rpc1_example"),
                    source,
                    new SunderRpcContentRegistrationOptions("text/plain", "request.txt", source.Length),
                    cancellationToken);
                await using var stream = await scope.OpenContentAsync(reference, cancellationToken);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                opened = await reader.ReadToEndAsync(cancellationToken);
            },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();
        await worker.ActivateAsync();

        const string scopeId = "worker-scope-content";
        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.scope-open", request.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    scopeId,
                    deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
                },
            });
        }

        string stagingPath;
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.content-register", scopeId);
            Assert.Equal("rpc1_example", request.RootElement.GetProperty("endpointReference").GetString());
            stagingPath = request.RootElement.GetProperty("filePath").GetString()!;
            Assert.StartsWith(worker.DataPath, stagingPath, StringComparison.Ordinal);
            Assert.Equal(content, await File.ReadAllTextAsync(stagingPath));
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = WorkerHarness.ContentReference("rpc-content-scope", content.Length),
            });
        }
        using (var request = await worker.ReadAsync())
        {
            Assert.False(File.Exists(stagingPath));
            AssertScopedRequest(request.RootElement, "worker.content-open", scopeId);
            var hostPath = Path.Combine(worker.DataPath, "opened-scope-content.txt");
            await File.WriteAllTextAsync(hostPath, content);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new { handleId = "scope-handle", filePath = hostPath },
            });
        }
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.content-release", scopeId);
            Assert.Equal("scope-handle", request.RootElement.GetProperty("handleId").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.scope-close", scopeId);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var activated = await worker.ReadAsync())
        {
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        }

        Assert.Equal(content, opened);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV1_CallScopeRejectsDuplicateHostScopeId()
    {
        await using var worker = new WorkerHarness(context => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                await using var first = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
            },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();
        await worker.ActivateAsync();

        for (var index = 0; index < 2; index++)
        {
            using var request = await worker.ReadAsync();
            Assert.Equal("worker.scope-open", request.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    scopeId = "duplicate-scope",
                    deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
                },
            });
        }

        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("reused RPC call-scope id", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerV1_CancelledScopeOpenReleasesCrossedHostResult()
    {
        using var scopeCancellation = new CancellationTokenSource();
        var cancelled = false;
        SunderWorkerContext? configuredContext = null;
        var limits = WorkerLimits.Default with { MaximumCallScopes = 1 };
        await using var worker = new WorkerHarness(context =>
        {
            configuredContext = context;
            return new SunderWorkerOptions([WorkerHarness.Registration(new TestRpcHandler())])
            {
                OnActivated = async (_, _) =>
                {
                    try
                    {
                        await context.Rpc.CreateCallScopeAsync(cancellationToken: scopeCancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }
                },
            };
        }, limits);
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();
        await worker.ActivateAsync();

        string openId;
        using (var open = await worker.ReadAsync())
        {
            Assert.Equal("worker.scope-open", open.RootElement.GetProperty("type").GetString());
            openId = open.RootElement.GetProperty("id").GetString()!;
        }
        scopeCancellation.Cancel();
        var activated = false;
        while (true)
        {
            using var output = await worker.ReadAsync();
            var type = output.RootElement.GetProperty("type").GetString();
            if (type == "worker.activated")
            {
                activated = true;
                continue;
            }
            Assert.Equal("worker.cancel", type);
            Assert.Equal(openId, output.RootElement.GetProperty("id").GetString());
            break;
        }
        await worker.SendAsync(new
        {
            type = "host.result",
            id = openId,
            value = new
            {
                scopeId = "crossed-scope",
                deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
            },
        });

        var closed = false;
        while (!activated || !closed)
        {
            using var output = await worker.ReadAsync();
            var type = output.RootElement.GetProperty("type").GetString();
            if (type == "worker.activated")
            {
                activated = true;
                continue;
            }
            Assert.Equal("worker.scope-close", type);
            Assert.Equal("crossed-scope", output.RootElement.GetProperty("scopeId").GetString());
            closed = true;
            var exhausted = await Assert.ThrowsAsync<SunderRpcException>(async () =>
                await configuredContext!.Rpc.CreateCallScopeAsync());
            Assert.Equal(SunderRpcErrorKind.ResourceExhausted, exhausted.Error.Kind);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = output.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }

        Assert.True(cancelled);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV1_CancelledContentOpenReleasesCrossedHostHandle()
    {
        using var openCancellation = new CancellationTokenSource();
        var allowScopeClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        await using var worker = new WorkerHarness(context => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                await using var scope = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                try
                {
                    await scope.OpenContentAsync(
                        new SunderRpcContentReference(
                            "crossed-content",
                            1,
                            new string('d', 64),
                            "text/plain",
                            "crossed.txt",
                            DateTimeOffset.UtcNow.AddMinutes(1),
                            SunderRpcContentRepeatability.SingleUse),
                        openCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    await allowScopeClose.Task.WaitAsync(cancellationToken);
                }
            },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();
        await worker.ActivateAsync();

        const string scopeId = "crossed-content-scope";
        using (var openScope = await worker.ReadAsync())
        {
            Assert.Equal("worker.scope-open", openScope.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = openScope.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    scopeId,
                    deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
                },
            });
        }
        string contentOpenId;
        using (var contentOpen = await worker.ReadAsync())
        {
            AssertScopedRequest(contentOpen.RootElement, "worker.content-open", scopeId);
            contentOpenId = contentOpen.RootElement.GetProperty("id").GetString()!;
        }
        openCancellation.Cancel();
        using (var cancellation = await worker.ReadAsync())
        {
            Assert.Equal("worker.cancel", cancellation.RootElement.GetProperty("type").GetString());
            Assert.Equal(contentOpenId, cancellation.RootElement.GetProperty("id").GetString());
        }
        var crossedContentPath = Path.Combine(worker.DataPath, "crossed-content.txt");
        await File.WriteAllBytesAsync(crossedContentPath, [42]);
        await worker.SendAsync(new
        {
            type = "host.result",
            id = contentOpenId,
            value = new
            {
                handleId = "crossed-handle",
                filePath = crossedContentPath,
            },
        });
        using (var release = await worker.ReadAsync())
        {
            AssertScopedRequest(release.RootElement, "worker.content-release", scopeId);
            Assert.Equal("crossed-handle", release.RootElement.GetProperty("handleId").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = release.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }

        allowScopeClose.TrySetResult();
        using (var close = await worker.ReadAsync())
        {
            AssertScopedRequest(close.RootElement, "worker.scope-close", scopeId);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = close.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var activated = await worker.ReadAsync())
        {
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        }

        Assert.True(cancelled);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV1_CrossedScopeOpenRetainsCapacityUntilScopeCloseTerminates()
    {
        var attemptSecondOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondOpenAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exhausted = false;
        var limits = WorkerLimits.Default with { MaximumContentHandles = 1 };
        await using var worker = new WorkerHarness(context => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                var first = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                var second = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                var firstOpen = first.OpenContentAsync(
                    new SunderRpcContentReference(
                        "crossed-capacity-content",
                        1,
                        new string('d', 64),
                        "text/plain",
                        "crossed-capacity.txt",
                        DateTimeOffset.UtcNow.AddMinutes(1),
                        SunderRpcContentRepeatability.SingleUse),
                    cancellationToken).AsTask();
                var firstDispose = first.DisposeAsync().AsTask();
                await attemptSecondOpen.Task.WaitAsync(cancellationToken);
                try
                {
                    await second.OpenContentAsync(
                        new SunderRpcContentReference(
                            "second-capacity-content",
                            1,
                            new string('e', 64),
                            "text/plain",
                            "second-capacity.txt",
                            DateTimeOffset.UtcNow.AddMinutes(1),
                            SunderRpcContentRepeatability.SingleUse),
                        cancellationToken);
                }
                catch (SunderRpcException exception) when (
                    exception.Error.Kind == SunderRpcErrorKind.ResourceExhausted)
                {
                    exhausted = true;
                }
                secondOpenAttempted.TrySetResult();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await firstOpen);
                await firstDispose;
                await second.DisposeAsync();
            },
        }, limits);
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();
        await worker.ActivateAsync();

        var scopeIds = new List<string>();
        for (var index = 0; index < 2; index++)
        {
            using var open = await worker.ReadAsync();
            Assert.Equal("worker.scope-open", open.RootElement.GetProperty("type").GetString());
            var scopeId = "capacity-scope-" + index;
            scopeIds.Add(scopeId);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = open.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    scopeId,
                    deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
                },
            });
        }

        string? contentOpenId = null;
        string? firstCloseId = null;
        for (var index = 0; index < 3; index++)
        {
            using var output = await worker.ReadAsync();
            switch (output.RootElement.GetProperty("type").GetString())
            {
                case "worker.content-open":
                    contentOpenId = output.RootElement.GetProperty("id").GetString();
                    Assert.Equal(scopeIds[0], output.RootElement.GetProperty("scopeId").GetString());
                    break;
                case "worker.cancel":
                    break;
                case "worker.scope-close":
                    firstCloseId = output.RootElement.GetProperty("id").GetString();
                    Assert.Equal(scopeIds[0], output.RootElement.GetProperty("scopeId").GetString());
                    break;
                default:
                    throw new Xunit.Sdk.XunitException("Unexpected worker output while closing crossed content open.");
            }
        }
        Assert.NotNull(contentOpenId);
        Assert.NotNull(firstCloseId);
        var contentPath = Path.Combine(worker.DataPath, "crossed-capacity.txt");
        await File.WriteAllBytesAsync(contentPath, [42]);
        await worker.SendAsync(new
        {
            type = "host.result",
            id = contentOpenId,
            value = new { handleId = "crossed-capacity-handle", filePath = contentPath },
        });

        var nextOutput = worker.ReadAsync().AsTask();
        attemptSecondOpen.TrySetResult();
        await secondOpenAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(exhausted);
        Assert.False(nextOutput.IsCompleted);

        await worker.SendAsync(new { type = "host.result", id = firstCloseId, value = (object?)null });
        using (var secondClose = await nextOutput)
        {
            Assert.Equal("worker.scope-close", secondClose.RootElement.GetProperty("type").GetString());
            Assert.Equal(scopeIds[1], secondClose.RootElement.GetProperty("scopeId").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = secondClose.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var activated = await worker.ReadAsync())
        {
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        }
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV1_ScopeCloseWaitsForCrossedHandleRelease()
    {
        using var openCancellation = new CancellationTokenSource();
        await using var worker = new WorkerHarness(context => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                await using var scope = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                try
                {
                    await scope.OpenContentAsync(
                        new SunderRpcContentReference(
                            "release-before-close",
                            1,
                            new string('d', 64),
                            "text/plain",
                            "release-before-close.txt",
                            DateTimeOffset.UtcNow.AddMinutes(1),
                            SunderRpcContentRepeatability.SingleUse),
                        openCancellation.Token);
                }
                catch (OperationCanceledException) when (openCancellation.IsCancellationRequested)
                {
                }
            },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();
        await worker.ActivateAsync();

        const string scopeId = "release-before-close-scope";
        using (var openScope = await worker.ReadAsync())
        {
            await worker.SendAsync(new
            {
                type = "host.result",
                id = openScope.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    scopeId,
                    deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
                },
            });
        }
        string contentOpenId;
        using (var contentOpen = await worker.ReadAsync())
        {
            contentOpenId = contentOpen.RootElement.GetProperty("id").GetString()!;
        }
        openCancellation.Cancel();
        using (var cancellation = await worker.ReadAsync())
        {
            Assert.Equal("worker.cancel", cancellation.RootElement.GetProperty("type").GetString());
        }
        var contentPath = Path.Combine(worker.DataPath, "release-before-close.txt");
        await File.WriteAllBytesAsync(contentPath, [42]);
        await worker.SendAsync(new
        {
            type = "host.result",
            id = contentOpenId,
            value = new { handleId = "release-before-close-handle", filePath = contentPath },
        });

        string releaseId;
        using (var release = await worker.ReadAsync())
        {
            AssertScopedRequest(release.RootElement, "worker.content-release", scopeId);
            releaseId = release.RootElement.GetProperty("id").GetString()!;
        }
        var close = worker.ReadAsync().AsTask();
        await Task.Delay(100);
        Assert.False(close.IsCompleted);
        await worker.SendAsync(new
        {
            type = "host.error",
            id = releaseId,
            error = new
            {
                kind = "unavailable",
                code = "rpc.scope.unavailable",
                message = "The RPC call scope is stale or unavailable.",
                dispatchState = "not-dispatched",
                retrySafety = "safe",
            },
        });
        using (var closeOutput = await close)
        {
            AssertScopedRequest(closeOutput.RootElement, "worker.scope-close", scopeId);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = closeOutput.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var activated = await worker.ReadAsync())
        {
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        }
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV1_ScopeCloseUsesControlLaneWhenRpcLaneIsFull()
    {
        var limits = WorkerLimits.Default with { MaximumOutboundCalls = 1 };
        var discoverCancelled = false;
        await using var worker = new WorkerHarness(context => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                var scope = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                var discovery = scope.DiscoverAsync(WorkerHarness.ContractId, cancellationToken).AsTask();
                await scope.DisposeAsync();
                try
                {
                    await discovery;
                }
                catch (OperationCanceledException)
                {
                    discoverCancelled = true;
                }
            },
        }, limits);
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();
        await worker.ActivateAsync();

        const string scopeId = "saturated-scope";
        using (var open = await worker.ReadAsync())
        {
            Assert.Equal("worker.scope-open", open.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = open.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    scopeId,
                    deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
                },
            });
        }
        string discoverId;
        using (var discover = await worker.ReadAsync())
        {
            AssertScopedRequest(discover.RootElement, "worker.discover", scopeId);
            discoverId = discover.RootElement.GetProperty("id").GetString()!;
        }

        string? closeId = null;
        for (var index = 0; index < 2; index++)
        {
            using var output = await worker.ReadAsync();
            var type = output.RootElement.GetProperty("type").GetString();
            if (type == "worker.cancel")
            {
                Assert.Equal(discoverId, output.RootElement.GetProperty("id").GetString());
                await worker.SendAsync(new
                {
                    type = "host.error",
                    id = discoverId,
                    error = new
                    {
                        kind = "cancelled",
                        code = "rpc.call.cancelled",
                        message = "cancelled",
                        dispatchState = "possibly-dispatched",
                        retrySafety = "unsafe",
                    },
                });
            }
            else
            {
                AssertScopedRequest(output.RootElement, "worker.scope-close", scopeId);
                closeId = output.RootElement.GetProperty("id").GetString();
            }
        }
        Assert.NotNull(closeId);
        await worker.SendAsync(new { type = "host.result", id = closeId, value = (object?)null });
        using (var activated = await worker.ReadAsync())
        {
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        }

        Assert.True(discoverCancelled);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV1_ScopeDeadlineRevokesOpenContentStream()
    {
        var streamReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActivation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Stream? openedStream = null;
        await using var worker = new WorkerHarness(context => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                await using var scope = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                await using var stream = await scope.OpenContentAsync(
                    new SunderRpcContentReference(
                        "deadline-content",
                        1,
                        new string('d', 64),
                        "text/plain",
                        "deadline.txt",
                        DateTimeOffset.UtcNow.AddMinutes(1),
                        SunderRpcContentRepeatability.SingleUse),
                    cancellationToken);
                openedStream = stream;
                streamReady.TrySetResult();
                await releaseActivation.Task.WaitAsync(cancellationToken);
            },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();
        await worker.ActivateAsync();

        const string scopeId = "deadline-scope";
        using (var open = await worker.ReadAsync())
        {
            Assert.Equal("worker.scope-open", open.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = open.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    scopeId,
                    deadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(300).UtcDateTime.ToString("O"),
                },
            });
        }
        using (var openContent = await worker.ReadAsync())
        {
            AssertScopedRequest(openContent.RootElement, "worker.content-open", scopeId);
            var path = Path.Combine(worker.DataPath, "deadline-content.txt");
            await File.WriteAllBytesAsync(path, [42]);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = openContent.RootElement.GetProperty("id").GetString(),
                value = new { handleId = "deadline-handle", filePath = path },
            });
        }
        await streamReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(500);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await openedStream!.ReadExactlyAsync(new byte[1]));

        releaseActivation.TrySetResult();
        using (var close = await worker.ReadAsync())
        {
            AssertScopedRequest(close.RootElement, "worker.scope-close", scopeId);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = close.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var activated = await worker.ReadAsync())
        {
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        }
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV1_MalformedHostContentHandleFaultsRuntime()
    {
        await using var worker = new WorkerHarness(context => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                await using var scope = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                await scope.OpenContentAsync(
                    new SunderRpcContentReference(
                        "escaped-content",
                        1,
                        new string('d', 64),
                        "text/plain",
                        "escaped.txt",
                        DateTimeOffset.UtcNow.AddMinutes(1),
                        SunderRpcContentRepeatability.SingleUse),
                    cancellationToken);
            },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();
        await worker.ActivateAsync();

        const string scopeId = "malformed-content-scope";
        using (var open = await worker.ReadAsync())
        {
            Assert.Equal("worker.scope-open", open.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = open.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    scopeId,
                    deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
                },
            });
        }
        using (var openContent = await worker.ReadAsync())
        {
            AssertScopedRequest(openContent.RootElement, "worker.content-open", scopeId);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = openContent.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    handleId = "escaped-handle",
                    filePath = Path.Combine(Path.GetTempPath(), "outside-worker-data.content"),
                },
            });
        }
        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("escaped", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertWorkspaceUnavailable(SunderWorkerContext context)
    {
        var rootException = Assert.Throws<InvalidOperationException>(
            () => context.RoleLocalWorkspace.WorkspaceRootPath);
        Assert.Contains("until the Host activates", rootException.Message, StringComparison.Ordinal);
        var pathException = Assert.Throws<InvalidOperationException>(
            () => context.RoleLocalWorkspace.GetLocalPath("data.json"));
        Assert.Contains("until the Host activates", pathException.Message, StringComparison.Ordinal);
    }

    private static string CreateWorkspaceFile(string workspacePath)
    {
        var path = Path.Combine(workspacePath, "not-a-directory");
        File.WriteAllText(path, "file");
        return path;
    }

    private static string CreateWorkspaceLink(string workspacePath)
    {
        var path = workspacePath + "-link";
        Directory.CreateSymbolicLink(path, workspacePath);
        return path;
    }

    private static void AssertScopedRequest(JsonElement request, string type, string scopeId)
    {
        Assert.Equal(type, request.GetProperty("type").GetString());
        Assert.Equal(scopeId, request.GetProperty("scopeId").GetString());
    }

    private sealed class SecretBearingProviderException(string message) : Exception(message);

    private sealed class RecordingServiceProvider(List<string> events) : IServiceProvider, IAsyncDisposable
    {
        public Action? OnDispose { get; init; }
        public object? GetService(Type serviceType) => null;

        public ValueTask DisposeAsync()
        {
            events.Add("dispose:services");
            OnDispose?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private class RecordingWorkerLifecycleService : ISunderWorkerLifecycleService
    {
        public RecordingWorkerLifecycleService(string name, List<string> events)
        {
            Name = name;
            Events = events;
        }

        protected string Name { get; }
        protected List<string> Events { get; }

        public virtual Task Completion => Task.CompletedTask;

        public virtual ValueTask PrepareCandidateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("prepare:" + Name);
            return ValueTask.CompletedTask;
        }

        public virtual ValueTask CommitGenerationAsync(
            SunderWorkerGenerationContext generation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add($"commit:{Name}:{generation.SessionGeneration}");
            return ValueTask.CompletedTask;
        }

        public virtual ValueTask ActivateAsync(
            SunderWorkerContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("activate:" + Name);
            return ValueTask.CompletedTask;
        }

        public virtual ValueTask ShutdownAsync(
            SunderWorkerShutdownContext context,
            CancellationToken cancellationToken = default)
        {
            Events.Add("shutdown:" + Name);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingMonitoredLifecycleService : RecordingWorkerLifecycleService
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingMonitoredLifecycleService(string name, List<string> events)
            : base(name, events)
        {
        }

        public override Task Completion => _completion.Task;

        public override async ValueTask ShutdownAsync(
            SunderWorkerShutdownContext context,
            CancellationToken cancellationToken = default)
        {
            await base.ShutdownAsync(context, cancellationToken);
            _completion.TrySetResult();
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }

    private sealed class BlockingActivationLifecycleService(
        string name,
        List<string> events) : RecordingWorkerLifecycleService(name, events)
    {
        private readonly TaskCompletionSource _activationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseActivation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ActivationStarted => _activationStarted.Task;

        public override async ValueTask ActivateAsync(
            SunderWorkerContext context,
            CancellationToken cancellationToken = default)
        {
            await base.ActivateAsync(context, cancellationToken);
            _activationStarted.TrySetResult();
            await _releaseActivation.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseActivation() => _releaseActivation.TrySetResult();
    }

    private sealed class BlockingCallbackHandler : IPackageCallbackHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string CallbackHandlerId => "fixture.callback";

        public Task Started => _started.Task;

        public Task Cancelled => _cancelled.Task;

        public async Task<PackageCallbackStartResult?> StartCallbackAsync(
            PackageCallbackStartContext context,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            }
            catch (OperationCanceledException)
            {
                _cancelled.TrySetResult();
                throw;
            }
        }

        public Task<PackageCallbackCompletionResult> CompleteCallbackAsync(
            PackageCallbackCompletionContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
