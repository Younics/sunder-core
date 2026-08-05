using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var modePath = Path.Combine(Environment.CurrentDirectory, "mode.txt");
var mode = File.Exists(modePath) ? File.ReadAllText(modePath).Trim() : "normal";
var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
var error = Console.Error;

var hello = await ReadFrameAsync(input);
if (hello.RootElement.GetProperty("type").GetString() != "host.hello") return 64;
if (mode == "startup-hang")
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}
if (mode == "stderr")
{
    await error.WriteLineAsync(new string('x', 32 * 1024));
    await error.FlushAsync();
}

var protocol = hello.RootElement.GetProperty("protocol").GetString();
var protocolVersion = hello.RootElement.GetProperty("protocolVersion").GetInt32();
if (mode == "protocol-mismatch")
{
    protocol = "sunder.worker.v1";
    protocolVersion = 1;
}
if (hello.RootElement.GetProperty("protocol").GetString() == "sunder.worker.v2")
{
    var settingsSchema = mode switch
    {
        "v2-package-data" => CreateSettingsSchema(invalid: false),
        "v2-invalid-settings-schema" => CreateSettingsSchema(invalid: true),
        _ => null,
    };
    await WriteFrameAsync(output, new
    {
        type = "worker.ready",
        protocol,
        protocolVersion,
        challenge = hello.RootElement.GetProperty("challenge").GetString(),
        packageId = hello.RootElement.GetProperty("packageId").GetString(),
        packageVersion = hello.RootElement.GetProperty("packageVersion").GetString(),
        activationId = hello.RootElement.GetProperty("activationId").GetString(),
        sessionId = hello.RootElement.GetProperty("sessionId").GetString(),
        contributions = new
        {
            settingsSchema,
            runtimeOperations = Array.Empty<object>(),
            runtimeStreams = Array.Empty<object>(),
            callbackHandlers = Array.Empty<object>(),
            authHandler = false,
            rpcProviders = hello.RootElement.GetProperty("providers"),
            candidateLifecycle = false,
            generationLifecycle = false,
        },
    });
}
else
{
    await WriteFrameAsync(output, new
    {
        type = "worker.ready",
        protocol,
        protocolVersion,
        challenge = hello.RootElement.GetProperty("challenge").GetString(),
        packageId = hello.RootElement.GetProperty("packageId").GetString(),
        packageVersion = hello.RootElement.GetProperty("packageVersion").GetString(),
        activationId = hello.RootElement.GetProperty("activationId").GetString(),
        sessionId = hello.RootElement.GetProperty("sessionId").GetString(),
        providers = hello.RootElement.GetProperty("providers"),
    });
}

if (mode == "v2-ready-logging")
{
    await WriteFrameAsync(output, new
    {
        type = "worker.logging-write",
        id = "readyLog",
        channel = "event",
        level = 2,
        category = (string?)null,
        eventId = 0,
        eventName = "worker.ready",
        message = "Worker ready",
        attributes = new { },
        exceptions = Array.Empty<object>(),
    });
    using (await ReadExpectedHostResultAsync(input, "readyLog"))
    {
    }
    await WriteStatusAsync("v2-ready-logging.json", new { accepted = true });
}

while (true)
{
    using var frame = await ReadFrameAsync(input);
    var root = frame.RootElement;
    var type = root.GetProperty("type").GetString();
    if (type == "host.start-candidate")
    {
        if (mode is "candidate-hang" or "candidate-shutdown-hang") continue;
        await WriteFrameAsync(output, new { type = "worker.candidate-started" });
        continue;
    }
    if (type == "host.commit-generation")
    {
        if (mode is "commit-hang" or "commit-shutdown-hang") continue;
        await WriteFrameAsync(output, new
        {
            type = "worker.generation-committed",
            activationId = root.GetProperty("activationId").GetString(),
            sessionGeneration = root.GetProperty("sessionGeneration").GetInt64(),
        });
        continue;
    }
    if (type == "host.activate")
    {
        if (mode == "crash") return 42;
        if (mode == "activation-hang") continue;
        if (mode == "malformed-frame")
        {
            await output.WriteAsync("Content-Length: nope\r\n\r\n"u8.ToArray());
            await output.FlushAsync();
            continue;
        }
        if (mode == "activation-host-call")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.discover",
                id = "activationCall1",
                contractId = "example.rpc",
            });
            using var response = await ReadFrameAsync(input);
            if (response.RootElement.GetProperty("type").GetString() is not ("host.result" or "host.error")) return 66;
        }
        await WriteFrameAsync(output, new
        {
            type = "worker.activated",
            sessionGeneration = root.GetProperty("sessionGeneration").GetInt64(),
        });
        if (mode is "v2-scope-roundtrip" or "v2-scope-handle-limit")
        {
            await RunV2ScopeRoundTripAsync(
                input,
                output,
                testHandleLimit: mode == "v2-scope-handle-limit");
            continue;
        }
        if (mode == "v2-scope-leak")
        {
            await RunV2LeakedScopeAsync(input, output);
            continue;
        }
        if (mode == "v2-scope-permission-denied")
        {
            await RunV2DeniedScopeAsync(input, output);
            continue;
        }
        if (mode == "v2-invariant-report")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.report-invariant-violation",
                id = "invariantReport1",
                endpointReference = "rpc1_exact_provider",
                exceptionMessage = "sanitized invariant failure",
            });
            using var response = await ReadFrameAsync(input);
            await WriteStatusAsync("v2-invariant-report.json", new
            {
                responseType = response.RootElement.GetProperty("type").GetString(),
                accepted = response.RootElement.TryGetProperty("value", out var value) && value.GetBoolean(),
            });
            continue;
        }
        if (mode == "v2-package-data")
        {
            await RunV2PackageDataAsync(input, output);
            continue;
        }
        if (mode == "v2-files-secrets")
        {
            await RunV2FilesAndSecretsAsync(input, output);
            continue;
        }
        if (mode == "v2-logging")
        {
            await RunV2LoggingAsync(input, output);
            continue;
        }
        if (mode is "v2-stale-package-data" or "v2-stale-malformed-payload")
        {
            await RunV2StalePackageDataAsync(
                input,
                output,
                malformedPayload: mode == "v2-stale-malformed-payload");
            continue;
        }
        if (mode == "v2-oversized-inline")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.state-set",
                id = "oversizedInline1",
                key = "state.value",
                value = new { kind = "inline", value = new string('x', 64 * 1024 + 1) },
            });
            continue;
        }
        if (mode == "v2-shutdown-worker-call")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.watch",
                id = "shutdownWatch1",
                afterRevision = 0,
                afterSequence = 0,
            });
            await WriteStatusAsync("v2-shutdown-worker-call.json", new { started = true });
            continue;
        }
        if (mode == "v1-scope-open")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.scope-open",
                id = "invalidV1Scope",
                deadlineUtc = (string?)null,
            });
        }
        if (mode == "v1-package-data")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.state-get",
                id = "invalidV1State",
                key = "state.value",
                mode = "value",
            });
        }
        if (mode == "v1-logging")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.logging-write",
                id = "invalidV1Logging",
                channel = "event",
                level = 2,
                category = (string?)null,
                eventId = 0,
                eventName = "worker.event",
                message = "invalid",
                attributes = new { },
                exceptions = Array.Empty<object>(),
            });
        }
        if (mode == "v2-unknown-scope")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.discover",
                id = "unknownScopeCall",
                scopeId = "worker-scope-unknown",
                contractId = "example.rpc",
            });
        }
        if (mode == "caller-spoof")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.discover",
                id = "spoof1",
                contractId = "example.rpc",
                caller = new { packageId = "spoofed.package" },
            });
        }
        if (mode == "content-spoof")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.content-open",
                id = "contentSpoof1",
                invocationId = "notAHostInvocation",
                reference = new
                {
                    id = "rpc-content-spoof",
                    length = 0,
                    sha256 = new string('0', 64),
                    mediaType = "application/octet-stream",
                    fileName = "spoof.bin",
                    expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
                    repeatability = "single-use",
                },
            });
        }
        continue;
    }
    if (type == "host.invoke")
    {
        var id = root.GetProperty("id").GetString();
        var kind = root.GetProperty("kind").GetString();
        var method = root.GetProperty("methodId").GetString();
        if (mode == "late-worker-cancel")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.discover",
                id = "lateCancel1",
                contractId = "example.rpc",
            });
            using var response = await ReadFrameAsync(input);
            if (response.RootElement.GetProperty("type").GetString() is not ("host.result" or "host.error")) return 65;
            await WriteFrameAsync(output, new { type = "worker.cancel", id = "lateCancel1" });
        }
        if (method == "wait") continue;
        if (method == "content-roundtrip")
        {
            var invocationId = id!;
            var request = root.GetProperty("request");
            await WriteFrameAsync(output, new
            {
                type = "worker.content-open",
                id = invocationId + "open",
                invocationId,
                reference = request.GetProperty("content"),
            });
            using var openResponse = await ReadFrameAsync(input);
            var opened = openResponse.RootElement.GetProperty("value");
            var handleId = opened.GetProperty("handleId").GetString()!;
            var openedPath = opened.GetProperty("filePath").GetString()!;
            var content = await File.ReadAllTextAsync(openedPath);
            var openedFileDiscarded = false;
            if (mode == "v1-content-control-lane")
            {
                openedFileDiscarded = await DiscardWithSaturatedRpcLaneAsync(
                    input,
                    output,
                    invocationId,
                    handleId,
                    openedPath);
            }
            var outputDirectory = mode == "v1-temp-content"
                ? Path.GetTempPath()
                : Path.Combine(
                    Environment.GetEnvironmentVariable("SUNDER_PACKAGE_DATA_PATH")!,
                    "tmp");
            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, invocationId + ".txt");
            var outputContent = content + "-processed";
            await File.WriteAllTextAsync(outputPath, outputContent);
            await WriteFrameAsync(output, new
            {
                type = "worker.content-register",
                id = invocationId + "register",
                invocationId,
                filePath = outputPath,
                options = new
                {
                    mediaType = "text/plain",
                    fileName = "processed.txt",
                    length = Encoding.UTF8.GetByteCount(outputContent),
                    expiresAtUtc = (string?)null,
                    repeatability = "single-use",
                    maximumUses = 1,
                },
            });
            using var registerResponse = await ReadFrameAsync(input);
            var registered = registerResponse.RootElement.GetProperty("value").Clone();
            if (mode != "v1-content-control-lane")
            {
                await WriteFrameAsync(output, new
                {
                    type = "worker.content-discard",
                    id = invocationId + "discard",
                    invocationId,
                    handleId,
                });
                using var discardResponse = await ReadFrameAsync(input);
                _ = discardResponse.RootElement.GetProperty("value");
                openedFileDiscarded = !File.Exists(openedPath);
            }
            await WriteFrameAsync(output, new
            {
                type = "worker.result",
                id,
                value = new
                {
                    accepted = true,
                    content = registered,
                    openedFileDiscarded,
                },
            });
            continue;
        }
        if (kind == "server-stream")
        {
            await WriteFrameAsync(output, new { type = "worker.event", id, value = new { accepted = true, sequence = 1 } });
            await WriteFrameAsync(output, new { type = "worker.event", id, value = new { accepted = true, sequence = 2 } });
            await WriteFrameAsync(output, new { type = "worker.complete", id });
        }
        else
        {
            if (mode == "v2-provider-fault")
            {
                const string secret = "provider-secret=must-never-persist";
                var exception = new SecretBearingProviderException(secret);
                var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
                await error.WriteLineAsync(exception.ToString());
                await error.FlushAsync();
                var diagnosticId = id + "providerFaultDiagnostic";
                await WriteFrameAsync(output, new
                {
                    type = "worker.provider-fault-diagnostic",
                    id = diagnosticId,
                    invocationId = id,
                    providerId = root.GetProperty("providerId").GetString(),
                    serviceId = root.GetProperty("serviceId").GetString(),
                    methodId = root.GetProperty("methodId").GetString(),
                    exceptionType,
                    exceptionFingerprint = CreateExceptionFingerprint(exceptionType),
                });
                using (await ReadExpectedHostResultAsync(input, diagnosticId!))
                {
                }
                await WriteFrameAsync(output, new
                {
                    type = "worker.error",
                    id,
                    error = new
                    {
                        kind = "provider-fault",
                        code = "rpc.provider.handler-fault",
                        message = "The process provider handler failed.",
                    },
                });
                continue;
            }
            if (mode == "handler-fault")
            {
                await WriteFrameAsync(output, new
                {
                    type = "worker.error",
                    id,
                    error = new { kind = "provider-fault", code = "rpc.provider.handler-fault", message = "handler failed" },
                });
                continue;
            }
            if (mode == "forwarded-error")
            {
                await WriteFrameAsync(output, new
                {
                    type = "worker.error",
                    id,
                    error = new { kind = "permission-denied", code = "rpc.permission.denied", message = "permission denied" },
                });
                continue;
            }
            object value = mode switch
            {
                "invalid-output" => new { accepted = "invalid" },
                "environment" => new
                {
                    accepted = true,
                    nodeOptions = Environment.GetEnvironmentVariable("NODE_OPTIONS"),
                    runtimeToken = Environment.GetEnvironmentVariable("SUNDER_RUNTIME_BEARER_TOKEN"),
                    packageId = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_ID"),
                    packageVersion = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_VERSION"),
                    activationId = Environment.GetEnvironmentVariable("SUNDER_ACTIVATION_ID"),
                    sessionId = Environment.GetEnvironmentVariable("SUNDER_SESSION_ID"),
                    dataPath = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_DATA_PATH"),
                    statePath = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_STATE_PATH"),
                    workingPath = Environment.CurrentDirectory,
                    deadlineUtc = root.GetProperty("context").GetProperty("deadlineUtc").GetString(),
                },
                _ => new { accepted = true },
            };
            await WriteFrameAsync(output, new { type = "worker.result", id, value });
            if (mode == "duplicate-terminal")
            {
                await WriteFrameAsync(output, new { type = "worker.result", id, value });
            }
        }
        continue;
    }
    if (type == "host.cancel")
    {
        await WriteFrameAsync(output, new
        {
            type = "worker.error",
            id = root.GetProperty("id").GetString(),
            error = new { kind = "cancelled", code = "rpc.call.cancelled", message = "cancelled" },
        });
        continue;
    }
    if (type == "host.shutdown")
    {
        if (mode is "shutdown-hang" or "candidate-shutdown-hang" or "commit-shutdown-hang")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        if (mode == "v2-malformed-shutdown-logging")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.logging-write",
                id = "malformedShutdownLog",
                channel = "event",
                level = 2,
                category = (string?)null,
                eventId = 0,
                eventName = "worker.stopping",
                message = "invalid",
                attributes = new { },
                exceptions = Enumerable.Range(0, 6).Select(static _ => new
                {
                    type = "System.Exception",
                    message = "invalid",
                    stackTrace = (string?)null,
                }).ToArray(),
            });
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        if (mode == "v2-logging")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.logging-write",
                id = "shutdownLog",
                channel = "logger",
                level = 3,
                category = "Worker.Shutdown",
                eventId = 0,
                eventName = (string?)null,
                message = "Worker stopping",
                attributes = new { },
                exceptions = Array.Empty<object>(),
            });
            using (await ReadExpectedHostResultAsync(input, "shutdownLog"))
            {
            }
        }
        await WriteFrameAsync(output, new
        {
            type = "worker.shutdown-ack",
            shutdownId = root.GetProperty("shutdownId").GetString(),
        });
        return 0;
    }
}

static async Task RunV2StalePackageDataAsync(
    Stream input,
    Stream output,
    bool malformedPayload)
{
    const string value = "stale payload";
    var bytes = Encoding.UTF8.GetBytes(value);
    await WriteFrameAsync(output, new
    {
        type = "worker.payload-allocate",
        id = "staleAllocate1",
        length = bytes.Length,
    });
    using var allocation = await ReadFrameAsync(input);
    var descriptor = allocation.RootElement.GetProperty("value");
    var handleId = descriptor.GetProperty("handleId").GetString()!;
    var filePath = descriptor.GetProperty("filePath").GetString()!;
    await WriteUploadPayloadAsync(filePath, bytes);
    await WriteFrameAsync(output, new
    {
        type = "worker.state-set",
        id = "staleSet1",
        key = "state.value",
        value = new
        {
            kind = "payload",
            handleId,
            length = bytes.Length,
            sha256 = malformedPayload
                ? new string('0', 64)
                : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        },
    });
    if (malformedPayload) return;
    using var response = await ReadFrameAsync(input);
    await WriteStatusAsync("v2-stale-package-data.json", new
    {
        kind = response.RootElement.GetProperty("error").GetProperty("kind").GetString(),
        code = response.RootElement.GetProperty("error").GetProperty("code").GetString(),
        uploadConsumed = !File.Exists(filePath),
    });
}

static async Task RunV2FilesAndSecretsAsync(Stream input, Stream output)
{
    await WriteFrameAsync(output, new
    {
        type = "worker.secrets-get",
        id = "secretGetMissing",
        key = "provider.token",
    });
    bool secretWasMissing;
    using (var response = await ReadExpectedHostResultAsync(input, "secretGetMissing"))
    {
        secretWasMissing = response.RootElement.GetProperty("value").GetProperty("value").ValueKind
                           == JsonValueKind.Null;
    }
    await WriteFrameAsync(output, new
    {
        type = "worker.secrets-set",
        id = "secretSet",
        key = "provider.token",
        value = new { kind = "inline", value = "top-secret" },
    });
    using (await ReadExpectedHostResultAsync(input, "secretSet"))
    {
    }
    await WriteFrameAsync(output, new
    {
        type = "worker.secrets-get",
        id = "secretGet",
        key = "provider.token",
    });
    bool secretRoundTripped;
    using (var response = await ReadExpectedHostResultAsync(input, "secretGet"))
    {
        secretRoundTripped = response.RootElement.GetProperty("value").GetProperty("value").GetString()
                             == "top-secret";
    }
    await WriteFrameAsync(output, new
    {
        type = "worker.secrets-delete",
        id = "secretDelete",
        key = "provider.token",
    });
    using (await ReadExpectedHostResultAsync(input, "secretDelete"))
    {
    }

    var fileBytes = Enumerable.Range(0, 1024 * 1024 + 1)
        .Select(static index => (byte)(index % 251))
        .ToArray();
    await WriteFrameAsync(output, new
    {
        type = "worker.payload-allocate",
        id = "fileAllocate",
        length = fileBytes.Length,
    });
    string uploadHandle;
    string uploadPath;
    using (var response = await ReadExpectedHostResultAsync(input, "fileAllocate"))
    {
        var value = response.RootElement.GetProperty("value");
        uploadHandle = value.GetProperty("handleId").GetString()!;
        uploadPath = value.GetProperty("filePath").GetString()!;
    }
    await WriteUploadPayloadAsync(uploadPath, fileBytes);
    await WriteFrameAsync(output, new
    {
        type = "worker.files-write",
        id = "fileWrite",
        relativePath = "cache/model.bin",
        contents = new
        {
            kind = "payload",
            handleId = uploadHandle,
            length = fileBytes.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant(),
        },
    });
    using (await ReadExpectedHostResultAsync(input, "fileWrite"))
    {
    }
    await WriteFrameAsync(output, new
    {
        type = "worker.files-read",
        id = "fileRead",
        relativePath = "cache/model.bin",
    });
    string downloadHandle;
    string downloadPath;
    bool fileRoundTripped;
    using (var response = await ReadExpectedHostResultAsync(input, "fileRead"))
    {
        var value = response.RootElement.GetProperty("value");
        downloadHandle = value.GetProperty("handleId").GetString()!;
        downloadPath = value.GetProperty("filePath").GetString()!;
        var downloaded = await File.ReadAllBytesAsync(downloadPath);
        fileRoundTripped = fileBytes.SequenceEqual(downloaded);
    }
    await WriteFrameAsync(output, new
    {
        type = "worker.payload-release",
        id = "fileRelease",
        handleId = downloadHandle,
    });
    using (await ReadExpectedHostResultAsync(input, "fileRelease"))
    {
    }
    var downloadReleased = !File.Exists(downloadPath);
    await WriteFrameAsync(output, new
    {
        type = "worker.files-delete",
        id = "fileDelete",
        relativePath = "cache/model.bin",
    });
    using (await ReadExpectedHostResultAsync(input, "fileDelete"))
    {
    }

    await WriteStatusAsync("v2-files-secrets.json", new
    {
        secretWasMissing,
        secretRoundTripped,
        fileRoundTripped,
        downloadReleased,
    });
}

static async Task RunV2LoggingAsync(Stream input, Stream output)
{
    await WriteFrameAsync(output, new
    {
        type = "worker.logging-write",
        id = "structuredLog",
        channel = "event",
        level = 3,
        category = (string?)null,
        eventId = 0,
        eventName = "worker.event",
        message = "Structured message",
        attributes = new Dictionary<string, object?>
        {
            ["attempt"] = 3,
            ["api key"] = "must-not-leak",
            ["source"] = "forged-source",
            ["category"] = "forged-category",
        },
        exceptions = new object[]
        {
            new { type = "System.InvalidOperationException", message = "outer", stackTrace = "outer stack" },
            new { type = "System.ArgumentException", message = "inner", stackTrace = (string?)null },
        },
    });
    await WriteFrameAsync(output, new
    {
        type = "worker.logging-write",
        id = "conventionalLog",
        channel = "logger",
        level = 2,
        category = "Worker.Test",
        eventId = 42,
        eventName = "worker.ready",
        message = "Conventional message",
        attributes = new { attempt = 4 },
        exceptions = Array.Empty<object>(),
    });

    var accepted = new HashSet<string>(StringComparer.Ordinal);
    while (accepted.Count < 2)
    {
        using var response = await ReadFrameAsync(input);
        var responseRoot = response.RootElement;
        var responseType = responseRoot.GetProperty("type").GetString();
        var responseId = responseRoot.GetProperty("id").GetString();
        if (responseType == "host.result"
            && responseId is "structuredLog" or "conventionalLog")
        {
            accepted.Add(responseId);
            continue;
        }
        throw new InvalidDataException("Expected Host logging results.");
    }

    await WriteStatusAsync("v2-logging.json", new { accepted = true });
}

static async Task<bool> DiscardWithSaturatedRpcLaneAsync(
    Stream input,
    Stream output,
    string invocationId,
    string handleId,
    string openedPath)
{
    const string watchId = "saturatedV1Watch";
    var discardId = invocationId + "discard";
    await WriteFrameAsync(output, new
    {
        type = "worker.watch",
        id = watchId,
        afterRevision = 0,
        afterSequence = 0,
    });
    await WriteFrameAsync(output, new
    {
        type = "worker.content-discard",
        id = discardId,
        invocationId,
        handleId,
    });
    while (true)
    {
        using var response = await ReadFrameAsync(input);
        var root = response.RootElement;
        var id = root.GetProperty("id").GetString();
        if (id == discardId && root.GetProperty("type").GetString() == "host.result") break;
        if (id != watchId || root.GetProperty("type").GetString() != "host.event")
        {
            throw new InvalidDataException("Expected a watch event or content-discard result.");
        }
    }
    await WriteFrameAsync(output, new { type = "worker.cancel", id = watchId });
    while (true)
    {
        using var response = await ReadFrameAsync(input);
        var root = response.RootElement;
        if (root.GetProperty("id").GetString() != watchId) continue;
        if (root.GetProperty("type").GetString() is "host.error" or "host.complete") break;
    }
    return !File.Exists(openedPath);
}

static async Task RunV2ScopeRoundTripAsync(
    Stream input,
    Stream output,
    bool testHandleLimit)
{
    await WriteFrameAsync(output, new
    {
        type = "worker.scope-open",
        id = "scopeOpen1",
        deadlineUtc = (string?)null,
    });
    using var scopeOpen = await ReadExpectedHostResultAsync(input, "scopeOpen1");
    var scopeId = scopeOpen.RootElement.GetProperty("value").GetProperty("scopeId").GetString()!;

    await WriteFrameAsync(output, new
    {
        type = "worker.discover",
        id = "scopeDiscover1",
        scopeId,
        contractId = "example.rpc",
    });
    string endpointReference;
    using (var discovery = await ReadExpectedHostResultAsync(input, "scopeDiscover1"))
    {
        endpointReference = discovery.RootElement
            .GetProperty("value")
            .GetProperty("providers")[0]
            .GetProperty("endpointReference")
            .GetString()!;
    }

    const string callerContent = "caller-payload";
    var dataPath = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_DATA_PATH")!;
    var temporaryPath = Path.Combine(dataPath, "tmp");
    Directory.CreateDirectory(temporaryPath);
    var requestPath = Path.Combine(temporaryPath, "v2-scope-request.txt");
    await File.WriteAllTextAsync(requestPath, callerContent);
    await WriteFrameAsync(output, new
    {
        type = "worker.content-register",
        id = "scopeRegister1",
        scopeId,
        endpointReference,
        filePath = requestPath,
        options = new
        {
            mediaType = "text/plain",
            fileName = "caller.txt",
            length = Encoding.UTF8.GetByteCount(callerContent),
            expiresAtUtc = (string?)null,
            repeatability = "single-use",
            maximumUses = 1,
        },
    });
    JsonElement requestReference;
    using (var registration = await ReadExpectedHostResultAsync(input, "scopeRegister1"))
    {
        requestReference = registration.RootElement.GetProperty("value").Clone();
    }
    File.Delete(requestPath);

    await WriteFrameAsync(output, new
    {
        type = "worker.invoke",
        id = "scopeInvoke1",
        scopeId,
        endpointReference,
        serviceId = "messages",
        methodId = "content-roundtrip",
        request = new { message = "content", content = requestReference },
        deadlineUtc = (string?)null,
    });

    using var invocation = await ReadFrameAsync(input);
    if (invocation.RootElement.GetProperty("type").GetString() != "host.invoke") return;
    var invocationId = invocation.RootElement.GetProperty("id").GetString()!;
    var callerPackageId = invocation.RootElement
        .GetProperty("context")
        .GetProperty("callerPackageId")
        .GetString()!;
    var invocationRequestReference = invocation.RootElement
        .GetProperty("request")
        .GetProperty("content")
        .Clone();
    await WriteFrameAsync(output, new
    {
        type = "worker.content-open",
        id = "invocationOpen1",
        invocationId,
        reference = invocationRequestReference,
    });
    string invocationHandleId;
    string invocationFilePath;
    using (var openedRequest = await ReadExpectedHostResultAsync(input, "invocationOpen1"))
    {
        var handle = openedRequest.RootElement.GetProperty("value");
        invocationHandleId = handle.GetProperty("handleId").GetString()!;
        invocationFilePath = handle.GetProperty("filePath").GetString()!;
    }
    var providerInput = await File.ReadAllTextAsync(invocationFilePath);
    var providerOutput = providerInput + "-processed";
    var responsePath = Path.Combine(temporaryPath, "v2-scope-response.txt");
    await File.WriteAllTextAsync(responsePath, providerOutput);
    await WriteFrameAsync(output, new
    {
        type = "worker.content-register",
        id = "invocationRegister1",
        invocationId,
        filePath = responsePath,
        options = new
        {
            mediaType = "text/plain",
            fileName = "processed.txt",
            length = Encoding.UTF8.GetByteCount(providerOutput),
            expiresAtUtc = (string?)null,
            repeatability = testHandleLimit ? "repeatable" : "single-use",
            maximumUses = testHandleLimit ? 2 : 1,
        },
    });
    JsonElement responseReference;
    using (var registeredResponse = await ReadExpectedHostResultAsync(input, "invocationRegister1"))
    {
        responseReference = registeredResponse.RootElement.GetProperty("value").Clone();
    }
    File.Delete(responsePath);
    await WriteFrameAsync(output, new
    {
        type = "worker.content-release",
        id = "invocationRelease1",
        invocationId,
        handleId = invocationHandleId,
    });
    using (await ReadExpectedHostResultAsync(input, "invocationRelease1"))
    {
    }
    var invocationHandleReleased = !File.Exists(invocationFilePath);
    await WriteFrameAsync(output, new
    {
        type = "worker.result",
        id = invocationId,
        value = new
        {
            accepted = true,
            content = responseReference,
            openedFileDiscarded = invocationHandleReleased,
        },
    });

    JsonElement scopedResponseReference;
    using (var scopedInvocation = await ReadExpectedHostResultAsync(input, "scopeInvoke1"))
    {
        scopedResponseReference = scopedInvocation.RootElement
            .GetProperty("value")
            .GetProperty("content")
            .Clone();
    }
    await WriteFrameAsync(output, new
    {
        type = "worker.content-open",
        id = "scopeContentOpen1",
        scopeId,
        reference = scopedResponseReference,
    });
    string scopeHandleId;
    string scopeFilePath;
    using (var openedResponse = await ReadExpectedHostResultAsync(input, "scopeContentOpen1"))
    {
        var handle = openedResponse.RootElement.GetProperty("value");
        scopeHandleId = handle.GetProperty("handleId").GetString()!;
        scopeFilePath = handle.GetProperty("filePath").GetString()!;
    }
    var finalContent = await File.ReadAllTextAsync(scopeFilePath);
    string? handleLimitKind = null;
    string? handleLimitCode = null;
    if (testHandleLimit)
    {
        await WriteFrameAsync(output, new
        {
            type = "worker.content-open",
            id = "scopeContentOpenLimited",
            scopeId,
            reference = scopedResponseReference,
        });
        using var limited = await ReadFrameAsync(input);
        var limitedRoot = limited.RootElement;
        if (limitedRoot.GetProperty("type").GetString() != "host.error"
            || limitedRoot.GetProperty("id").GetString() != "scopeContentOpenLimited")
        {
            throw new InvalidDataException("Expected the second materialized content handle to be rejected.");
        }
        var error = limitedRoot.GetProperty("error");
        handleLimitKind = error.GetProperty("kind").GetString();
        handleLimitCode = error.GetProperty("code").GetString();
    }
    await WriteFrameAsync(output, new
    {
        type = "worker.content-release",
        id = "scopeContentRelease1",
        scopeId,
        handleId = scopeHandleId,
    });
    using (await ReadExpectedHostResultAsync(input, "scopeContentRelease1"))
    {
    }
    var scopeHandleReleased = !File.Exists(scopeFilePath);
    var handleLimitRetrySucceeded = false;
    if (testHandleLimit)
    {
        await WriteFrameAsync(output, new
        {
            type = "worker.content-open",
            id = "scopeContentOpenRetry",
            scopeId,
            reference = scopedResponseReference,
        });
        string retryHandleId;
        string retryFilePath;
        using (var retry = await ReadExpectedHostResultAsync(input, "scopeContentOpenRetry"))
        {
            var handle = retry.RootElement.GetProperty("value");
            retryHandleId = handle.GetProperty("handleId").GetString()!;
            retryFilePath = handle.GetProperty("filePath").GetString()!;
        }
        handleLimitRetrySucceeded = await File.ReadAllTextAsync(retryFilePath) == finalContent;
        await WriteFrameAsync(output, new
        {
            type = "worker.content-release",
            id = "scopeContentReleaseRetry",
            scopeId,
            handleId = retryHandleId,
        });
        using (await ReadExpectedHostResultAsync(input, "scopeContentReleaseRetry"))
        {
        }
        handleLimitRetrySucceeded &= !File.Exists(retryFilePath);
    }
    await WriteFrameAsync(output, new
    {
        type = "worker.scope-close",
        id = "scopeClose1",
        scopeId,
    });
    using (await ReadExpectedHostResultAsync(input, "scopeClose1"))
    {
    }

    var statusPath = Path.Combine(
        dataPath,
        testHandleLimit ? "v2-scope-handle-limit.json" : "v2-scope-roundtrip.json");
    var pendingStatusPath = statusPath + ".pending";
    await File.WriteAllBytesAsync(
        pendingStatusPath,
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            content = finalContent,
            callerPackageId,
            invocationHandleReleased,
            scopeHandleReleased,
            handleLimitKind,
            handleLimitCode,
            handleLimitRetrySucceeded,
        }));
    File.Move(pendingStatusPath, statusPath);
}

static async Task RunV2LeakedScopeAsync(Stream input, Stream output)
{
    await WriteFrameAsync(output, new
    {
        type = "worker.scope-open",
        id = "leakedScopeOpen",
        deadlineUtc = (string?)null,
    });
    using var scopeOpen = await ReadExpectedHostResultAsync(input, "leakedScopeOpen");
    var scopeId = scopeOpen.RootElement.GetProperty("value").GetProperty("scopeId").GetString()!;

    await WriteFrameAsync(output, new
    {
        type = "worker.discover",
        id = "leakedScopeDiscover",
        scopeId,
        contractId = "example.rpc",
    });
    string endpointReference;
    using (var discovery = await ReadExpectedHostResultAsync(input, "leakedScopeDiscover"))
    {
        endpointReference = discovery.RootElement
            .GetProperty("value")
            .GetProperty("providers")[0]
            .GetProperty("endpointReference")
            .GetString()!;
    }

    var dataPath = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_DATA_PATH")!;
    var temporaryPath = Path.Combine(dataPath, "tmp");
    Directory.CreateDirectory(temporaryPath);
    var contentPath = Path.Combine(temporaryPath, "v2-leaked-scope.txt");
    await File.WriteAllTextAsync(contentPath, "leaked-content");
    await WriteFrameAsync(output, new
    {
        type = "worker.content-register",
        id = "leakedScopeRegister",
        scopeId,
        endpointReference,
        filePath = contentPath,
        options = new
        {
            mediaType = "text/plain",
            fileName = "leaked.txt",
            length = Encoding.UTF8.GetByteCount("leaked-content"),
            expiresAtUtc = (string?)null,
            repeatability = "single-use",
            maximumUses = 1,
        },
    });
    using (await ReadExpectedHostResultAsync(input, "leakedScopeRegister"))
    {
    }
    File.Delete(contentPath);

    var statusPath = Path.Combine(dataPath, "v2-scope-leak.json");
    var pendingStatusPath = statusPath + ".pending";
    await File.WriteAllBytesAsync(
        pendingStatusPath,
        JsonSerializer.SerializeToUtf8Bytes(new { scopeId }));
    File.Move(pendingStatusPath, statusPath);
}

static async Task RunV2DeniedScopeAsync(Stream input, Stream output)
{
    await WriteFrameAsync(output, new
    {
        type = "worker.scope-open",
        id = "deniedScopeOpen",
        deadlineUtc = (string?)null,
    });
    using var scopeOpen = await ReadExpectedHostResultAsync(input, "deniedScopeOpen");
    var scopeId = scopeOpen.RootElement.GetProperty("value").GetProperty("scopeId").GetString()!;
    await WriteFrameAsync(output, new
    {
        type = "worker.discover",
        id = "deniedScopeDiscover",
        scopeId,
        contractId = "example.rpc",
    });
    using var denied = await ReadFrameAsync(input);
    var root = denied.RootElement;
    if (root.GetProperty("type").GetString() != "host.error"
        || root.GetProperty("id").GetString() != "deniedScopeDiscover")
    {
        throw new InvalidDataException("Expected scoped discovery to be denied.");
    }
    var error = root.GetProperty("error");
    await WriteFrameAsync(output, new
    {
        type = "worker.scope-close",
        id = "deniedScopeClose",
        scopeId,
    });
    using (await ReadExpectedHostResultAsync(input, "deniedScopeClose"))
    {
    }

    var dataPath = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_DATA_PATH")!;
    var statusPath = Path.Combine(dataPath, "v2-scope-permission-denied.json");
    var pendingStatusPath = statusPath + ".pending";
    await File.WriteAllBytesAsync(
        pendingStatusPath,
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = error.GetProperty("kind").GetString(),
            code = error.GetProperty("code").GetString(),
        }));
    File.Move(pendingStatusPath, statusPath);
}

static object CreateSettingsSchema(bool invalid)
    => new
    {
        summary = "Process worker settings.",
        sections = new[]
        {
            new
            {
                sectionId = "general",
                title = "General",
                description = (string?)null,
                fields = new object[]
                {
                    new
                    {
                        key = "enabled",
                        label = "Enabled",
                        kind = invalid ? "unknown" : "boolean",
                        description = (string?)null,
                        isRequired = false,
                        placeholder = (string?)null,
                        defaultValue = "true",
                        options = Array.Empty<object>(),
                    },
                    new
                    {
                        key = "token",
                        label = "Token",
                        kind = "secret",
                        description = (string?)null,
                        isRequired = false,
                        placeholder = (string?)null,
                        defaultValue = (string?)null,
                        options = Array.Empty<object>(),
                    },
                },
            },
        },
    };

static async Task RunV2PackageDataAsync(Stream input, Stream output)
{
    await WriteFrameAsync(output, new
    {
        type = "worker.settings-get",
        id = "settingsEffective",
        key = "enabled",
        mode = "effective",
    });
    string? effective;
    using (var response = await ReadExpectedHostResultAsync(input, "settingsEffective"))
    {
        effective = response.RootElement.GetProperty("value").GetProperty("value").GetString();
    }

    await WriteFrameAsync(output, new
    {
        type = "worker.settings-get",
        id = "settingsStored",
        key = "enabled",
        mode = "stored",
    });
    bool storedWasNull;
    using (var response = await ReadExpectedHostResultAsync(input, "settingsStored"))
    {
        storedWasNull = response.RootElement.GetProperty("value").GetProperty("value").ValueKind
                        == JsonValueKind.Null;
    }

    await WriteFrameAsync(output, new
    {
        type = "worker.settings-set",
        id = "settingsSet",
        key = "enabled",
        value = new { kind = "inline", value = "false" },
    });
    using (await ReadExpectedHostResultAsync(input, "settingsSet"))
    {
    }

    await WriteFrameAsync(output, new
    {
        type = "worker.state-set",
        id = "stateSetEmpty",
        key = "empty.value",
        value = new { kind = "inline", value = string.Empty },
    });
    using (await ReadExpectedHostResultAsync(input, "stateSetEmpty"))
    {
    }

    await WriteFrameAsync(output, new
    {
        type = "worker.state-get",
        id = "stateGetEmpty",
        key = "empty.value",
        mode = "value",
    });
    string? emptyValue;
    using (var response = await ReadExpectedHostResultAsync(input, "stateGetEmpty"))
    {
        emptyValue = response.RootElement.GetProperty("value").GetProperty("value").GetString();
    }

    await WriteFrameAsync(output, new
    {
        type = "worker.state-get",
        id = "stateContains",
        key = "empty.value",
        mode = "contains",
    });
    bool contains;
    using (var response = await ReadExpectedHostResultAsync(input, "stateContains"))
    {
        contains = response.RootElement.GetProperty("value").GetBoolean();
    }

    await WriteFrameAsync(output, new
    {
        type = "worker.state-list",
        id = "stateList",
        prefix = (string?)null,
    });
    string[] keys;
    using (var response = await ReadExpectedHostResultAsync(input, "stateList"))
    {
        keys = response.RootElement.GetProperty("value").GetProperty("keys")
            .EnumerateArray().Select(static item => item.GetString()!).ToArray();
    }

    await WriteFrameAsync(output, new
    {
        type = "worker.settings-get",
        id = "secretRejected",
        key = "token",
        mode = "effective",
    });
    string? validationKind;
    using (var response = await ReadFrameAsync(input))
    {
        if (response.RootElement.GetProperty("type").GetString() != "host.error"
            || response.RootElement.GetProperty("id").GetString() != "secretRejected")
        {
            throw new InvalidDataException("Expected secret setting access to be rejected.");
        }
        validationKind = response.RootElement.GetProperty("error").GetProperty("kind").GetString();
    }

    var largeValue = new string('p', 1024 * 1024);
    var largeBytes = Encoding.UTF8.GetBytes(largeValue);
    await WriteFrameAsync(output, new
    {
        type = "worker.payload-allocate",
        id = "payloadAllocate",
        length = largeBytes.Length,
    });
    string uploadHandle;
    string uploadPath;
    using (var response = await ReadExpectedHostResultAsync(input, "payloadAllocate"))
    {
        var value = response.RootElement.GetProperty("value");
        uploadHandle = value.GetProperty("handleId").GetString()!;
        uploadPath = value.GetProperty("filePath").GetString()!;
    }
    await WriteUploadPayloadAsync(uploadPath, largeBytes);
    await WriteFrameAsync(output, new
    {
        type = "worker.state-set",
        id = "stateSetLarge",
        key = "large.value",
        value = new
        {
            kind = "payload",
            handleId = uploadHandle,
            length = largeBytes.Length,
            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(largeBytes)).ToLowerInvariant(),
        },
    });
    using (await ReadExpectedHostResultAsync(input, "stateSetLarge"))
    {
    }

    await WriteFrameAsync(output, new
    {
        type = "worker.state-get",
        id = "stateGetLarge",
        key = "large.value",
        mode = "value",
    });
    string downloadHandle;
    string downloadPath;
    bool largeRoundTripped;
    using (var response = await ReadExpectedHostResultAsync(input, "stateGetLarge"))
    {
        var value = response.RootElement.GetProperty("value");
        downloadHandle = value.GetProperty("handleId").GetString()!;
        downloadPath = value.GetProperty("filePath").GetString()!;
        var downloaded = await File.ReadAllBytesAsync(downloadPath);
        largeRoundTripped = downloaded.AsSpan().SequenceEqual(largeBytes)
                            && string.Equals(
                                value.GetProperty("sha256").GetString(),
                                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(downloaded)).ToLowerInvariant(),
                                StringComparison.Ordinal);
    }
    await WriteFrameAsync(output, new
    {
        type = "worker.payload-release",
        id = "payloadRelease",
        handleId = downloadHandle,
    });
    using (await ReadExpectedHostResultAsync(input, "payloadRelease"))
    {
    }
    var downloadReleased = !File.Exists(downloadPath);

    var dataPath = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_DATA_PATH")!;
    var statusPath = Path.Combine(dataPath, "v2-package-data.json");
    var pendingStatusPath = statusPath + ".pending";
    await File.WriteAllBytesAsync(
        pendingStatusPath,
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            effective,
            storedWasNull,
            emptyValue,
            contains,
            keys,
            validationKind,
            largeRoundTripped,
            uploadConsumed = !File.Exists(uploadPath),
            downloadReleased,
            dataPath,
            statePath = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_STATE_PATH"),
        }));
    File.Move(pendingStatusPath, statusPath);
}

static async Task<JsonDocument> ReadExpectedHostResultAsync(Stream input, string id)
{
    var response = await ReadFrameAsync(input);
    var root = response.RootElement;
    if (root.GetProperty("type").GetString() != "host.result"
        || root.GetProperty("id").GetString() != id)
    {
        response.Dispose();
        throw new InvalidDataException($"Expected host.result for '{id}'.");
    }
    return response;
}

static async Task<JsonDocument> ReadFrameAsync(Stream input)
{
    var header = new List<byte>();
    while (header.Count < 8192)
    {
        var value = input.ReadByte();
        if (value < 0) throw new EndOfStreamException();
        header.Add((byte)value);
        if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n') break;
    }
    var text = Encoding.ASCII.GetString([.. header]);
    const string prefix = "Content-Length: ";
    var length = int.Parse(text[prefix.Length..^4]);
    var body = new byte[length];
    await input.ReadExactlyAsync(body);
    return JsonDocument.Parse(body);
}

static async Task WriteStatusAsync(string fileName, object value)
{
    var dataPath = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_DATA_PATH")!;
    var statusPath = Path.Combine(dataPath, fileName);
    var pendingStatusPath = statusPath + ".pending";
    await File.WriteAllBytesAsync(
        pendingStatusPath,
        JsonSerializer.SerializeToUtf8Bytes(value));
    File.Move(pendingStatusPath, statusPath);
}

static async Task WriteUploadPayloadAsync(string filePath, byte[] bytes)
{
    await using var stream = new FileStream(
        filePath,
        FileMode.Open,
        FileAccess.Write,
        FileShare.ReadWrite,
        64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    stream.SetLength(0);
    await stream.WriteAsync(bytes);
    await stream.FlushAsync();
}

static string CreateExceptionFingerprint(string exceptionType)
    => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exceptionType)))
        .ToLowerInvariant();

static async Task WriteFrameAsync(Stream output, object value)
{
    var body = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    });
    var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
    await output.WriteAsync(header);
    await output.WriteAsync(body);
    await output.FlushAsync();
}

sealed class SecretBearingProviderException(string message) : Exception(message);
