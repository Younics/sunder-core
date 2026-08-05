using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Storage;
using Sunder.Sdk.Worker;
using Sunder.Sdk.Worker.Internal;
using Sunder.Sdk.Worker.Tests.TestSupport;

namespace Sunder.Sdk.Worker.Tests;

public sealed class WorkerV2PackageDataTests
{
    [Fact]
    public async Task WorkerV2_AdvertisesCanonicalSettingsSchema()
    {
        var schema = CreateSettingsSchema();
        await using var worker = CreateWorker(_ => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            SettingsSchema = schema,
        });

        await worker.HandshakeAsync();

        var wire = worker.ReadyContributions!.Value.GetProperty("settingsSchema");
        Assert.Equal("Worker settings.", wire.GetProperty("summary").GetString());
        var section = Assert.Single(wire.GetProperty("sections").EnumerateArray());
        Assert.Equal("general", section.GetProperty("sectionId").GetString());
        var fields = section.GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(["text", "secret", "boolean", "select"],
            fields.Select(field => field.GetProperty("kind").GetString()));
        Assert.Equal("safe", fields[3].GetProperty("defaultValue").GetString());
        Assert.Equal(["safe", "fast"],
            fields[3].GetProperty("options").EnumerateArray()
                .Select(option => option.GetProperty("value").GetString()));
    }

    [Fact]
    public async Task WorkerV2_SettingsAndStateUseStrictInlineEnvelopes()
    {
        string? effective = null;
        string? stored = "sentinel";
        string? state = null;
        bool contains = false;
        IReadOnlyList<string>? keys = null;
        await using var worker = CreateWorker(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            SettingsSchema = CreateSettingsSchema(),
            OnActivated = async (_, cancellationToken) =>
            {
                effective = await context.Settings.GetValueAsync("enabled", cancellationToken);
                stored = await context.Settings.GetStoredValueAsync("enabled", cancellationToken);
                await context.Settings.SetValueAsync("enabled", "false", cancellationToken);
                await context.Settings.DeleteValueAsync("enabled", cancellationToken);
                state = await context.State.GetValueAsync("sync.cursor", cancellationToken);
                contains = await context.State.ContainsKeyAsync("sync.cursor", cancellationToken);
                await context.State.SetValueAsync("sync.cursor", string.Empty, cancellationToken);
                keys = await context.State.ListKeysAsync("sync.", cancellationToken);
                await context.State.DeleteValueAsync("sync.cursor", cancellationToken);
            },
        });
        await BeginActivationAsync(worker);

        await RespondToStringGetAsync(worker, "worker.settings-get", "effective", "true");
        await RespondToStringGetAsync(worker, "worker.settings-get", "stored", null);
        await RespondToInlineSetAsync(worker, "worker.settings-set", "false");
        await RespondToNullMutationAsync(worker, "worker.settings-delete");
        await RespondToStringGetAsync(worker, "worker.state-get", "value", "cursor");
        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.state-get", request.RootElement.GetProperty("type").GetString());
            Assert.Equal("contains", request.RootElement.GetProperty("mode").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = true,
            });
        }
        await RespondToInlineSetAsync(worker, "worker.state-set", string.Empty);
        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.state-list", request.RootElement.GetProperty("type").GetString());
            Assert.Equal("sync.", request.RootElement.GetProperty("prefix").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new { kind = "inline", keys = new[] { "sync.cursor", "sync.last-run" } },
            });
        }
        await RespondToNullMutationAsync(worker, "worker.state-delete");
        await FinishActivationAsync(worker);

        Assert.Equal("true", effective);
        Assert.Null(stored);
        Assert.Equal("cursor", state);
        Assert.True(contains);
        Assert.Equal(["sync.cursor", "sync.last-run"], keys);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV2_SecretsUseStrictNonEnumerableEnvelopes()
    {
        string? missing = "sentinel";
        await using var worker = CreateWorker(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                missing = await context.Secrets.GetSecretAsync("provider.token", cancellationToken);
                await context.Secrets.SetSecretAsync("provider.token", "secret-value", cancellationToken);
                await context.Secrets.DeleteSecretAsync("provider.token", cancellationToken);
            },
        });
        await BeginActivationAsync(worker);

        using (var get = await worker.ReadAsync())
        {
            Assert.Equal("worker.secrets-get", get.RootElement.GetProperty("type").GetString());
            Assert.Equal(["type", "id", "key"], get.RootElement.EnumerateObject().Select(static item => item.Name));
            await worker.SendAsync(new
            {
                type = "host.result",
                id = get.RootElement.GetProperty("id").GetString(),
                value = new { kind = "inline", value = (string?)null },
            });
        }
        await RespondToInlineSetAsync(worker, "worker.secrets-set", "secret-value");
        await RespondToNullMutationAsync(worker, "worker.secrets-delete");
        await FinishActivationAsync(worker);

        Assert.Null(missing);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV2_FilesUsePayloadsBeyondDataValueLimit()
    {
        var expected = Enumerable.Range(0, 1024 * 1024 + 1)
            .Select(static index => (byte)(index % 251))
            .ToArray();
        byte[]? returned = null;
        await using var worker = CreateWorker(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                await context.Files.WriteAsync("cache/model.bin", expected, cancellationToken);
                returned = await context.Files.ReadAsync("cache/model.bin", cancellationToken);
                await context.Files.DeleteAsync("cache/model.bin", cancellationToken);
            },
        });
        await BeginActivationAsync(worker);

        const string uploadHandle = "worker-file-upload";
        var payloadDirectory = Path.Combine(worker.DataPath, "payload");
        Directory.CreateDirectory(payloadDirectory);
        var uploadPath = Path.Combine(payloadDirectory, uploadHandle + ".data");
        await File.WriteAllBytesAsync(uploadPath, []);
        using (var allocation = await worker.ReadAsync())
        {
            Assert.Equal("worker.payload-allocate", allocation.RootElement.GetProperty("type").GetString());
            Assert.Equal(expected.Length, allocation.RootElement.GetProperty("length").GetInt64());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = allocation.RootElement.GetProperty("id").GetString(),
                value = new { handleId = uploadHandle, filePath = uploadPath, length = expected.Length },
            });
        }
        using (var write = await worker.ReadAsync())
        {
            Assert.Equal("worker.files-write", write.RootElement.GetProperty("type").GetString());
            Assert.Equal("cache/model.bin", write.RootElement.GetProperty("relativePath").GetString());
            var contents = write.RootElement.GetProperty("contents");
            Assert.Equal(uploadHandle, contents.GetProperty("handleId").GetString());
            Assert.Equal(expected, await File.ReadAllBytesAsync(uploadPath));
            await worker.SendAsync(new
            {
                type = "host.result",
                id = write.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var read = await worker.ReadAsync())
        {
            Assert.Equal("worker.files-read", read.RootElement.GetProperty("type").GetString());
            var downloadPath = Path.Combine(payloadDirectory, "worker-file-download.data");
            await File.WriteAllBytesAsync(downloadPath, expected);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = read.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    kind = "payload",
                    handleId = "worker-file-download",
                    filePath = downloadPath,
                    length = expected.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(),
                },
            });
        }
        using (var release = await worker.ReadAsync())
        {
            Assert.Equal("worker.payload-release", release.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = release.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        await RespondToNullMutationAsync(worker, "worker.files-delete");
        await FinishActivationAsync(worker);

        Assert.Equal(expected, returned);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV2_LoggingUsesIndependentBoundedCallsAndDrainsShutdownLogs()
    {
        SunderRpcException? loggingLimit = null;
        await using var worker = CreateWorker(
            context => new SunderWorkerV2Options(
                [WorkerHarness.Registration(new TestRpcHandler())])
            {
                OnActivated = async (_, cancellationToken) =>
                {
                    var state = context.State.GetValueAsync("pending.value", cancellationToken);
                    var eventWrite = context.Logging.Events.WriteAsync(
                        PackageLogLevel.Warning,
                        "worker.event",
                        "Structured message",
                        new Dictionary<string, object?>
                        {
                            ["attempt"] = 3,
                            ["provider"] = "test",
                            ["Provider"] = "duplicate",
                        },
                        new InvalidOperationException("outer", new ArgumentException("inner")),
                        cancellationToken).AsTask();
                    try
                    {
                        await context.Logging.Events.WriteAsync(
                            PackageLogLevel.Information,
                            "worker.second",
                            "Second message",
                            cancellationToken: cancellationToken);
                    }
                    catch (SunderRpcException exception)
                    {
                        loggingLimit = exception;
                    }
                    await eventWrite;
                    context.Logging.LoggerFactory
                        .CreateLogger("Worker.Test")
                        .LogInformation(new EventId(42, "worker.ready"), "Attempt {Attempt}", 3);
                    await state;
                },
                OnShutdown = (_, _) =>
                {
                    context.Logging.LoggerFactory
                        .CreateLogger("Worker.Shutdown")
                        .LogWarning("Worker stopping");
                    return ValueTask.CompletedTask;
                },
            },
            WorkerLimits.Default with
            {
                MaximumOutboundCalls = 1,
                MaximumLoggingCalls = 1,
            });
        await BeginActivationAsync(worker);

        using var stateRequest = await worker.ReadAsync();
        Assert.Equal("worker.state-get", stateRequest.RootElement.GetProperty("type").GetString());
        using (var structured = await worker.ReadAsync())
        {
            Assert.Equal("worker.logging-write", structured.RootElement.GetProperty("type").GetString());
            Assert.Equal("event", structured.RootElement.GetProperty("channel").GetString());
            Assert.Equal((int)PackageLogLevel.Warning, structured.RootElement.GetProperty("level").GetInt32());
            Assert.Equal("worker.event", structured.RootElement.GetProperty("eventName").GetString());
            Assert.Equal("Structured message", structured.RootElement.GetProperty("message").GetString());
            Assert.Equal(3, structured.RootElement.GetProperty("attributes").GetProperty("attempt").GetInt32());
            Assert.Equal(2, structured.RootElement.GetProperty("attributes").EnumerateObject().Count());
            Assert.False(structured.RootElement.GetProperty("attributes").TryGetProperty("Provider", out _));
            Assert.Equal(
                [typeof(InvalidOperationException).FullName, typeof(ArgumentException).FullName],
                structured.RootElement.GetProperty("exceptions").EnumerateArray()
                    .Select(static item => item.GetProperty("type").GetString()));
            await worker.SendAsync(new
            {
                type = "host.result",
                id = structured.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var conventional = await worker.ReadAsync())
        {
            Assert.Equal("worker.logging-write", conventional.RootElement.GetProperty("type").GetString());
            Assert.Equal("logger", conventional.RootElement.GetProperty("channel").GetString());
            Assert.Equal("Worker.Test", conventional.RootElement.GetProperty("category").GetString());
            Assert.Equal(42, conventional.RootElement.GetProperty("eventId").GetInt32());
            Assert.Equal("worker.ready", conventional.RootElement.GetProperty("eventName").GetString());
            Assert.Equal(3, conventional.RootElement.GetProperty("attributes").GetProperty("Attempt").GetInt32());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = conventional.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        await worker.SendAsync(new
        {
            type = "host.result",
            id = stateRequest.RootElement.GetProperty("id").GetString(),
            value = new { kind = "inline", value = (string?)null },
        });
        await FinishActivationAsync(worker);
        Assert.NotNull(loggingLimit);
        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, loggingLimit.Error.Kind);
        Assert.Equal("rpc.worker.logging-call-limit", loggingLimit.Error.Code);

        await worker.SendAsync(new
        {
            type = "host.shutdown",
            shutdownId = "shutdown1",
            reason = "activation-retired",
        });
        using var shutdownLog = await worker.ReadAsync();
        Assert.Equal("worker.logging-write", shutdownLog.RootElement.GetProperty("type").GetString());
        Assert.Equal("Worker.Shutdown", shutdownLog.RootElement.GetProperty("category").GetString());
        var acknowledgement = worker.ReadAsync().AsTask();
        await Task.Delay(100);
        Assert.False(acknowledgement.IsCompleted);
        await worker.SendAsync(new
        {
            type = "host.result",
            id = shutdownLog.RootElement.GetProperty("id").GetString(),
            value = (object?)null,
        });
        using (var output = await acknowledgement)
        {
            Assert.Equal("worker.shutdown-ack", output.RootElement.GetProperty("type").GetString());
        }
        await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WorkerV2_NonNullLoggingResultFaultsProtocol()
    {
        await using var worker = CreateWorker(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
                await context.Logging.Events.WriteAsync(
                    PackageLogLevel.Information,
                    "worker.event",
                    "message",
                    cancellationToken: cancellationToken),
        });
        await BeginActivationAsync(worker);

        using var request = await worker.ReadAsync();
        Assert.Equal("worker.logging-write", request.RootElement.GetProperty("type").GetString());
        await worker.SendAsync(new
        {
            type = "host.result",
            id = request.RootElement.GetProperty("id").GetString(),
            value = new { unexpected = true },
        });

        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WorkerV2_LargeStateValueUsesBoundedPayloadHandles()
    {
        var expected = new string('x', 70 * 1024);
        string? returned = null;
        await using var worker = CreateWorker(
            context => new SunderWorkerV2Options(
                [WorkerHarness.Registration(new TestRpcHandler())])
            {
                OnActivated = async (_, cancellationToken) =>
                {
                    await context.State.SetValueAsync("large.value", expected, cancellationToken);
                    returned = await context.State.GetValueAsync("large.value", cancellationToken);
                },
            },
            WorkerLimits.Default with { MaximumOutboundCalls = 1 });
        await BeginActivationAsync(worker);

        string uploadHandle;
        string uploadPath;
        using (var allocation = await worker.ReadAsync())
        {
            Assert.Equal("worker.payload-allocate", allocation.RootElement.GetProperty("type").GetString());
            Assert.Equal(Encoding.UTF8.GetByteCount(expected), allocation.RootElement.GetProperty("length").GetInt64());
            uploadHandle = "worker-payload-upload";
            var payloadPath = Path.Combine(worker.DataPath, "payload");
            Directory.CreateDirectory(payloadPath);
            uploadPath = Path.Combine(payloadPath, uploadHandle + ".data");
            await File.WriteAllBytesAsync(uploadPath, []);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = allocation.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    handleId = uploadHandle,
                    filePath = uploadPath,
                    length = Encoding.UTF8.GetByteCount(expected),
                },
            });
        }
        using (var set = await worker.ReadAsync())
        {
            Assert.Equal("worker.state-set", set.RootElement.GetProperty("type").GetString());
            var payload = set.RootElement.GetProperty("value");
            Assert.Equal("payload", payload.GetProperty("kind").GetString());
            Assert.Equal(uploadHandle, payload.GetProperty("handleId").GetString());
            var bytes = await File.ReadAllBytesAsync(uploadPath);
            Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                payload.GetProperty("sha256").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = set.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var get = await worker.ReadAsync())
        {
            Assert.Equal("worker.state-get", get.RootElement.GetProperty("type").GetString());
            var bytes = Encoding.UTF8.GetBytes(expected);
            var downloadPath = Path.Combine(worker.DataPath, "payload", "worker-payload-download.data");
            await File.WriteAllBytesAsync(downloadPath, bytes);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = get.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    kind = "payload",
                    handleId = "worker-payload-download",
                    filePath = downloadPath,
                    length = bytes.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                },
            });
        }
        using (var release = await worker.ReadAsync())
        {
            Assert.Equal("worker.payload-release", release.RootElement.GetProperty("type").GetString());
            Assert.Equal("worker-payload-download", release.RootElement.GetProperty("handleId").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = release.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        await FinishActivationAsync(worker);

        Assert.Equal(expected, returned);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV2_CommittedMutationWinsCancellationRace()
    {
        using var cancellation = new CancellationTokenSource();
        var completed = false;
        await using var worker = CreateWorker(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, _) =>
            {
                await context.State.SetValueAsync("race.value", "committed", cancellation.Token);
                completed = true;
            },
        });
        await BeginActivationAsync(worker);

        using var request = await worker.ReadAsync();
        Assert.Equal("worker.state-set", request.RootElement.GetProperty("type").GetString());
        cancellation.Cancel();
        using (var cancel = await worker.ReadAsync())
        {
            Assert.Equal("worker.cancel", cancel.RootElement.GetProperty("type").GetString());
            Assert.Equal(request.RootElement.GetProperty("id").GetString(), cancel.RootElement.GetProperty("id").GetString());
        }
        await worker.SendAsync(new
        {
            type = "host.result",
            id = request.RootElement.GetProperty("id").GetString(),
            value = (object?)null,
        });
        await FinishActivationAsync(worker);

        Assert.True(completed);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV2_ShutdownContinuesReadingUntilPendingMutationTerminates()
    {
        await using var worker = CreateWorker(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
                await context.State.SetValueAsync("pending.value", "value", cancellationToken),
        });
        await BeginActivationAsync(worker);

        using var request = await worker.ReadAsync();
        Assert.Equal("worker.state-set", request.RootElement.GetProperty("type").GetString());
        await worker.SendAsync(new
        {
            type = "host.shutdown",
            shutdownId = "shutdown1",
            reason = "activation-retired",
        });
        using (var cancel = await worker.ReadAsync())
        {
            Assert.Equal("worker.cancel", cancel.RootElement.GetProperty("type").GetString());
            Assert.Equal(request.RootElement.GetProperty("id").GetString(), cancel.RootElement.GetProperty("id").GetString());
        }
        await worker.SendAsync(new
        {
            type = "host.error",
            id = request.RootElement.GetProperty("id").GetString(),
            error = new
            {
                kind = "cancelled",
                code = "worker.call.cancelled",
                message = "The package-data call was cancelled.",
            },
        });
        using (var acknowledgement = await worker.ReadAsync())
        {
            Assert.Equal("worker.shutdown-ack", acknowledgement.RootElement.GetProperty("type").GetString());
            Assert.Equal("shutdown1", acknowledgement.RootElement.GetProperty("shutdownId").GetString());
        }
        await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WorkerV2_ShutdownWaitsForCrossedPayloadResultRelease()
    {
        using var callCancellation = new CancellationTokenSource();
        await using var worker = CreateWorker(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, shutdownCancellation) =>
            {
                try
                {
                    await context.State.GetValueAsync("pending.value", callCancellation.Token);
                }
                catch (OperationCanceledException) when (callCancellation.IsCancellationRequested)
                {
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, shutdownCancellation);
            },
        });
        await BeginActivationAsync(worker);

        using var request = await worker.ReadAsync();
        Assert.Equal("worker.state-get", request.RootElement.GetProperty("type").GetString());
        var requestId = request.RootElement.GetProperty("id").GetString();
        callCancellation.Cancel();
        using (var cancel = await worker.ReadAsync())
        {
            Assert.Equal("worker.cancel", cancel.RootElement.GetProperty("type").GetString());
            Assert.Equal(requestId, cancel.RootElement.GetProperty("id").GetString());
        }
        await worker.SendAsync(new
        {
            type = "host.shutdown",
            shutdownId = "shutdown1",
            reason = "activation-retired",
        });
        var bytes = Encoding.UTF8.GetBytes("crossed payload");
        var directory = Path.Combine(worker.DataPath, "payload");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "crossed-payload.data");
        await File.WriteAllBytesAsync(filePath, bytes);
        await worker.SendAsync(new
        {
            type = "host.result",
            id = requestId,
            value = new
            {
                kind = "payload",
                handleId = "crossed-payload",
                filePath,
                length = bytes.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            },
        });

        string releaseId;
        using (var release = await worker.ReadAsync())
        {
            Assert.Equal("worker.payload-release", release.RootElement.GetProperty("type").GetString());
            Assert.Equal("crossed-payload", release.RootElement.GetProperty("handleId").GetString());
            releaseId = release.RootElement.GetProperty("id").GetString()!;
        }
        var acknowledgement = worker.ReadAsync().AsTask();
        await Task.Delay(100);
        Assert.False(acknowledgement.IsCompleted);
        await worker.SendAsync(new { type = "host.result", id = releaseId, value = (object?)null });
        using (var output = await acknowledgement)
        {
            Assert.Equal("worker.shutdown-ack", output.RootElement.GetProperty("type").GetString());
        }
        await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WorkerV2_ShutdownCannotOvertakeDeliveredPayloadResult()
    {
        await using var worker = CreateWorker(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
                await context.State.GetValueAsync("pending.value", cancellationToken),
        });
        await BeginActivationAsync(worker);

        using var request = await worker.ReadAsync();
        var bytes = Encoding.UTF8.GetBytes("delivered payload");
        var directory = Path.Combine(worker.DataPath, "payload");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "delivered-payload.data");
        await File.WriteAllBytesAsync(filePath, bytes);
        await worker.SendAsync(new
        {
            type = "host.result",
            id = request.RootElement.GetProperty("id").GetString(),
            value = new
            {
                kind = "payload",
                handleId = "delivered-payload",
                filePath,
                length = bytes.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            },
        });
        await worker.SendAsync(new
        {
            type = "host.shutdown",
            shutdownId = "shutdown1",
            reason = "activation-retired",
        });

        string releaseId;
        using (var release = await worker.ReadAsync())
        {
            Assert.Equal("worker.payload-release", release.RootElement.GetProperty("type").GetString());
            releaseId = release.RootElement.GetProperty("id").GetString()!;
        }
        var acknowledgement = worker.ReadAsync().AsTask();
        await Task.Delay(100);
        Assert.False(acknowledgement.IsCompleted);
        await worker.SendAsync(new { type = "host.result", id = releaseId, value = (object?)null });
        using (var output = await acknowledgement)
        {
            Assert.Equal("worker.shutdown-ack", output.RootElement.GetProperty("type").GetString());
        }
        await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WorkerV2_ShutdownCannotDeleteDeliveredUploadAllocation()
    {
        var value = new string('x', 70 * 1024);
        await using var worker = CreateWorker(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
                await context.State.SetValueAsync("pending.value", value, cancellationToken),
        });
        await BeginActivationAsync(worker);

        using var allocation = await worker.ReadAsync();
        Assert.Equal("worker.payload-allocate", allocation.RootElement.GetProperty("type").GetString());
        var payloadDirectory = Path.Combine(worker.DataPath, "payload");
        Directory.CreateDirectory(payloadDirectory);
        var payloadPath = Path.Combine(payloadDirectory, "shutdown-upload.data");
        await File.WriteAllBytesAsync(payloadPath, []);
        await worker.SendAsync(new
        {
            type = "host.result",
            id = allocation.RootElement.GetProperty("id").GetString(),
            value = new
            {
                handleId = "shutdown-upload",
                filePath = payloadPath,
                length = Encoding.UTF8.GetByteCount(value),
            },
        });
        await worker.SendAsync(new
        {
            type = "host.shutdown",
            shutdownId = "shutdown1",
            reason = "activation-retired",
        });

        using var request = await worker.ReadAsync();
        var requestType = request.RootElement.GetProperty("type").GetString();
        string terminalId;
        object terminal;
        if (requestType == "worker.state-set")
        {
            terminalId = request.RootElement.GetProperty("id").GetString()!;
            using var cancellation = await worker.ReadAsync();
            Assert.Equal("worker.cancel", cancellation.RootElement.GetProperty("type").GetString());
            Assert.Equal(terminalId, cancellation.RootElement.GetProperty("id").GetString());
            File.Delete(payloadPath);
            terminal = new
            {
                type = "host.error",
                id = terminalId,
                error = new
                {
                    kind = "cancelled",
                    code = "worker.call.cancelled",
                    message = "The package-data call was cancelled.",
                },
            };
        }
        else
        {
            Assert.Equal("worker.payload-release", requestType);
            terminalId = request.RootElement.GetProperty("id").GetString()!;
            File.Delete(payloadPath);
            terminal = new { type = "host.result", id = terminalId, value = (object?)null };
        }

        var acknowledgement = worker.ReadAsync().AsTask();
        await Task.Delay(100);
        Assert.False(acknowledgement.IsCompleted);
        await worker.SendAsync(terminal);
        using (var output = await acknowledgement)
        {
            Assert.Equal("worker.shutdown-ack", output.RootElement.GetProperty("type").GetString());
        }
        await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WorkerV2_RejectsOversizedInlineHostResult()
    {
        await using var worker = CreateWorker(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
                await context.State.GetValueAsync("oversized.value", cancellationToken),
        });
        await BeginActivationAsync(worker);

        using var request = await worker.ReadAsync();
        await worker.SendAsync(new
        {
            type = "host.result",
            id = request.RootElement.GetProperty("id").GetString(),
            value = new { kind = "inline", value = new string('x', 64 * 1024 + 1) },
        });

        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void WorkerV2_InlineKeyArrayEnforcesExactWireByteBoundary()
    {
        var keys = Enumerable.Repeat("a", 16_383).ToArray();
        keys[^1] = "aaaa";
        var boundary = JsonSerializer.SerializeToElement(new { kind = "inline", keys });
        Assert.Equal(64 * 1024, Encoding.UTF8.GetByteCount(boundary.GetProperty("keys").GetRawText()));

        var accepted = WorkerWire.ReadDataKeys(boundary, 1024 * 1024);

        Assert.Equal(keys.Length, accepted.Keys?.Count);
        keys[^1] = "aaaaa";
        var oversized = JsonSerializer.SerializeToElement(new { kind = "inline", keys });
        Assert.Equal(64 * 1024 + 1, Encoding.UTF8.GetByteCount(oversized.GetProperty("keys").GetRawText()));
        Assert.Throws<WorkerProtocolException>(() => WorkerWire.ReadDataKeys(oversized, 1024 * 1024));
    }

    [Fact]
    public async Task WorkerV2_InvalidPackageDataIsRejectedBeforeWritingAFrame()
    {
        SunderWorkerContext? context = null;
        await using var worker = CreateWorker(value =>
        {
            context = value;
            return new SunderWorkerV2Options([WorkerHarness.Registration(new TestRpcHandler())]);
        });
        await worker.HandshakeAsync();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await context!.State.GetValueAsync("invalid key"));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await context!.State.SetValueAsync(
                "valid.key",
                new string('x', PackageStorageValidation.MaximumValueUtf8Bytes + 1)));
    }

    [Fact]
    public async Task WorkerV2_PayloadOutsideTemporaryPayloadDirectoryFaultsProtocol()
    {
        await using var worker = CreateWorker(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
                await context.State.GetValueAsync("unsafe.value", cancellationToken),
        });
        await BeginActivationAsync(worker);

        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.state-get", request.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    kind = "payload",
                    handleId = "unsafe-payload",
                    filePath = Path.Combine(worker.DataPath, "state.json"),
                    length = 0,
                    sha256 = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(),
                },
            });
        }

        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static WorkerHarness CreateWorker(
        Func<SunderWorkerContext, SunderWorkerV2Options> configure,
        WorkerLimits? limits = null)
        => new(configure, limits);

    private static PackageSettingsSchema CreateSettingsSchema()
        => new(
            "Worker settings.",
            [
                new PackageSettingsSection(
                    "general",
                    "General",
                    null,
                    [
                        new PackageSettingsField("name", "Name", PackageSettingsFieldKind.Text),
                        new PackageSettingsField("token", "Token", PackageSettingsFieldKind.Secret),
                        new PackageSettingsField(
                            "enabled",
                            "Enabled",
                            PackageSettingsFieldKind.Boolean,
                            defaultValue: "true"),
                        new PackageSettingsField(
                            "mode",
                            "Mode",
                            PackageSettingsFieldKind.Select,
                            defaultValue: "safe",
                            options:
                            [
                                new PackageSettingsOption("safe", "Safe"),
                                new PackageSettingsOption("fast", "Fast"),
                            ]),
                    ])
            ]);

    private static async Task BeginActivationAsync(WorkerHarness worker)
    {
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.SendAsync(new { type = "host.activate", sessionGeneration = 7 });
    }

    private static async Task FinishActivationAsync(WorkerHarness worker)
    {
        using var activated = await worker.ReadAsync();
        Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
    }

    private static async Task RespondToStringGetAsync(
        WorkerHarness worker,
        string type,
        string mode,
        string? value)
    {
        using var request = await worker.ReadAsync();
        Assert.Equal(type, request.RootElement.GetProperty("type").GetString());
        Assert.Equal(mode, request.RootElement.GetProperty("mode").GetString());
        await worker.SendAsync(new
        {
            type = "host.result",
            id = request.RootElement.GetProperty("id").GetString(),
            value = new { kind = "inline", value },
        });
    }

    private static async Task RespondToInlineSetAsync(
        WorkerHarness worker,
        string type,
        string expectedValue)
    {
        using var request = await worker.ReadAsync();
        Assert.Equal(type, request.RootElement.GetProperty("type").GetString());
        var value = request.RootElement.GetProperty("value");
        Assert.Equal("inline", value.GetProperty("kind").GetString());
        Assert.Equal(expectedValue, value.GetProperty("value").GetString());
        await worker.SendAsync(new
        {
            type = "host.result",
            id = request.RootElement.GetProperty("id").GetString(),
            value = (object?)null,
        });
    }

    private static async Task RespondToNullMutationAsync(WorkerHarness worker, string type)
    {
        using var request = await worker.ReadAsync();
        Assert.Equal(type, request.RootElement.GetProperty("type").GetString());
        await worker.SendAsync(new
        {
            type = "host.result",
            id = request.RootElement.GetProperty("id").GetString(),
            value = (object?)null,
        });
    }
}
