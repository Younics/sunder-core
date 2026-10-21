using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args is ["--sunder-test-policy-peer", var peerAddress])
{
    if (OperatingSystem.IsWindows())
    {
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", peerAddress, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000);
        await pipe.WriteAsync(new byte[] { 1 });
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    else
    {
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(peerAddress));
        await socket.SendAsync(new byte[] { 1 });
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    return 0;
}

if (args is ["--sunder-test-hold-file-lock", var lockMode, var lockPath])
{
    var share = lockMode switch
    {
        "exclusive" => FileShare.None,
        "shared" => FileShare.ReadWrite,
        _ => throw new ArgumentException("Unknown test file-lock mode.", nameof(args)),
    };
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(lockPath))!);
    using var fileLock = new FileStream(
        lockPath,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        share,
        bufferSize: 1,
        FileOptions.None);
    await Console.Out.WriteLineAsync("ready");
    await Console.Out.FlushAsync();
    await Console.In.ReadLineAsync();
    return 0;
}

var modePath = Path.Combine(Environment.CurrentDirectory, "mode.txt");
var mode = File.Exists(modePath) ? File.ReadAllText(modePath).Trim() : "normal";
var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
var error = Console.Error;
if (mode == "containment-hang")
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}
if (mode == "containment-echo")
{
    await input.CopyToAsync(output);
    await output.FlushAsync();
    return 0;
}
if (mode == "containment-detach" && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
{
    await Task.Delay(TimeSpan.FromMilliseconds(250));
    if (setsid() < 0)
    {
        throw new InvalidOperationException(
            $"The containment detach fixture could not create a session: {Marshal.GetLastPInvokeError()}.");
    }
    await File.WriteAllTextAsync(
        Path.Combine(Environment.CurrentDirectory, "containment-detach.ready"),
        "ready");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}
if (mode is "containment-child" or "containment-child-ignore-term" or "containment-child-exit")
{
    var isChild = string.Equals(
        Environment.GetEnvironmentVariable("SUNDER_TEST_CONTAINMENT_CHILD"),
        "1",
        StringComparison.Ordinal);
    using var ignoredTermination = isChild
                                   && mode == "containment-child-ignore-term"
                                   && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        ? PosixSignalRegistration.Create(
            PosixSignal.SIGTERM,
            context =>
            {
                context.Cancel = true;
                File.WriteAllText(
                    Path.Combine(Environment.CurrentDirectory, "containment-child.sigterm"),
                    "ignored");
            })
        : null;
    if (isChild)
    {
        await File.WriteAllTextAsync(
            Path.Combine(Environment.CurrentDirectory, "containment-child.ready"),
            "ready");
    }
    else
    {
        var childStartInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath
                       ?? throw new InvalidOperationException("The containment child executable is unavailable."),
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrWhiteSpace(entryAssemblyPath)
            && !string.Equals(
                Path.GetFileNameWithoutExtension(childStartInfo.FileName),
                Path.GetFileNameWithoutExtension(entryAssemblyPath),
                StringComparison.OrdinalIgnoreCase))
        {
            childStartInfo.ArgumentList.Add(entryAssemblyPath);
        }
        childStartInfo.Environment["SUNDER_TEST_CONTAINMENT_CHILD"] = "1";
        var child = Process.Start(childStartInfo)
                    ?? throw new InvalidOperationException("The containment child did not start.");
        await File.WriteAllTextAsync(
            Path.Combine(Environment.CurrentDirectory, "containment-child.pid"),
            child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (mode == "containment-child-exit")
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            return 0;
        }
    }
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

[DllImport("libc", SetLastError = true)]
static extern int setsid();

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
var packageId = hello.RootElement.GetProperty("packageId").GetString()!;
var packageVersion = hello.RootElement.GetProperty("packageVersion").GetString()!;
var resourceProfileHash = hello.RootElement.GetProperty("resourceProfileHash").GetString()!;
if (mode == "resource-profile-mismatch") resourceProfileHash = new string('0', 64);
var activationStages = packageId == "startup.counted.package"
    ? new List<string> { $"{packageVersion}:configure", $"{packageVersion}:register" }
    : [];
string? workspaceRoot = null;
if (mode == "protocol-mismatch")
{
    protocol = "sunder.worker.unknown";
    protocolVersion = 99;
}
else if (mode == "protocol-v3-mismatch")
{
    protocol = "sunder.worker.v3";
    protocolVersion = 3;
}
var settingsSchema = mode switch
{
    "v1-package-data" => CreateSettingsSchema(invalid: false),
    "v1-invalid-settings-schema" => CreateSettingsSchema(invalid: true),
    _ => null,
};
object[] storageMigrations = mode is
    "v1-skip-declared-migration"
    or "v1-transaction-migration"
    or "v1-transaction-migration-fail"
    ?
    [
        new
        {
            store = "state",
            rules = new object[]
            {
                new
                {
                    kind = "exact",
                    legacyKey = "legacy.state.v1",
                    destinationKey = "state.current.v1",
                },
            },
        },
        new
        {
            store = "secrets",
            rules = new object[]
            {
                new
                {
                    kind = "exact",
                    legacyKey = "legacy.secret.v1",
                    destinationKey = "secret.current.v1",
                },
            },
        },
    ]
    : [];
var advertisedContributions = new Dictionary<string, object?>
{
    ["settingsSchema"] = settingsSchema,
    ["storageMigrations"] = storageMigrations,
    ["callbackHandlers"] = mode is "v1-callback-mismatch" or "v1-retained-recovery" ? new[] { "fixture.callback" } : Array.Empty<string>(),
    ["authHandler"] = false,
    ["rpcProviders"] = hello.RootElement.GetProperty("providers"),
    ["candidateLifecycle"] = false,
    ["generationLifecycle"] = false,
    ["migrationPreparationLifecycle"] = true,
};
if (mode != "legacy-budgets" && Environment.GetEnvironmentVariable("SUNDER_WORKER_OPERATIONAL_OUTCOMES_V1") == "1")
    advertisedContributions.Add("operationalOutcomesV1", true);
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
    resourceProfileHash,
    contributions = advertisedContributions,
});

(string Id, byte[] Body)? shutdownBufferedWorkerCall = null;
string? shutdownBufferedWorkerCallScopeId = null;
long committedSessionGeneration = 0;
if (mode == "v1-ready-buffered-discover")
{
    shutdownBufferedWorkerCall = CreateShutdownBufferedWorkerCall(mode);
    await WriteFrameHeaderAsync(output, shutdownBufferedWorkerCall.Value.Body);
    await WriteStatusAsync($"{mode}.json", new { started = true });
}

if (mode == "v1-ready-logging")
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
    await WriteStatusAsync("v1-ready-logging.json", new { accepted = true });
}

while (true)
{
    using var frame = await ReadFrameAsync(input);
    var root = frame.RootElement;
    var type = root.GetProperty("type").GetString();
    if (type == "host.start-candidate")
    {
        if (mode is "candidate-hang" or "candidate-shutdown-hang") continue;
        if (packageId == "startup.counted.package")
        {
            activationStages.Add($"{packageVersion}:start");
        }
        await WriteFrameAsync(output, new { type = "worker.candidate-started" });
        continue;
    }
    if (type == "host.commit-generation")
    {
        committedSessionGeneration = root.GetProperty("sessionGeneration").GetInt64();
        if (mode is "commit-hang" or "commit-shutdown-hang") continue;
        if (packageId == "generation.failing.package") return 42;
        if (TryGetGenerationCommitStartedPath(packageId, packageVersion, out var commitStartedPath))
        {
            File.WriteAllText(
                commitStartedPath,
                root.GetProperty("sessionGeneration").GetInt64().ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            continue;
        }
        await WriteFrameAsync(output, new
        {
            type = "worker.generation-committed",
            activationId = root.GetProperty("activationId").GetString(),
            sessionGeneration = root.GetProperty("sessionGeneration").GetInt64(),
        });
        continue;
    }
    if (type == "host.prepare-storage-migrations")
    {
        if (mode is "migration-preparation-hang" or "migration-preparation-shutdown-hang") continue;
        if (mode is "v1-migration-preparation-state" or "v1-recovery-candidate-package-data")
        {
            await RunMigrationPreparationStateReadAsync(input, output);
        }
        else if (mode == "v1-migration-preparation-mutation")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.state-set",
                id = "migrationPreparationMutation",
                key = "legacy.value",
                value = new { kind = "inline", value = "forbidden" },
            });
            continue;
        }
        await WriteFrameAsync(output, new
        {
            type = "worker.storage-migrations-prepared",
            activationId = root.GetProperty("activationId").GetString(),
            sessionGeneration = root.GetProperty("sessionGeneration").GetInt64(),
        });
        continue;
    }
    if (type == "host.callback-start" && mode is "v1-callback-mismatch" or "v1-retained-recovery")
    {
        var value = root.GetProperty("value");
        await WriteFrameAsync(output, new
        {
            type = "worker.callback-started",
            id = root.GetProperty("id").GetString(),
            activationId = root.GetProperty("activationId").GetString(),
            sessionGeneration = root.GetProperty("sessionGeneration").GetInt64(),
            value = new
            {
                packageId = mode == "v1-callback-mismatch" ? "wrong.package" : packageId,
                callbackSessionId = value.GetProperty("callbackSessionId").GetString(),
                flow = "browser",
                launchUrl = "https://example.invalid/callback",
                message = mode == "v1-callback-mismatch" ? "mismatched" : "started",
            },
        });
        continue;
    }
    if (type == "host.callback-complete" && mode == "v1-retained-recovery")
    {
        await WriteFrameAsync(output, new
        {
            type = "worker.callback-completed",
            id = root.GetProperty("id").GetString(),
            activationId = root.GetProperty("activationId").GetString(),
            sessionGeneration = root.GetProperty("sessionGeneration").GetInt64(),
            value = new
            {
                packageId,
                callbackSessionId = root.GetProperty("value").GetProperty("callbackSessionId").GetString(),
                state = "completed",
                message = "completed",
            },
        });
        continue;
    }
    if (type == "host.activate")
    {
        workspaceRoot = root.GetProperty("workspaceRootPath").GetString();
        if (string.IsNullOrWhiteSpace(workspaceRoot)
            || !Path.IsPathFullyQualified(workspaceRoot)
            || !Directory.Exists(workspaceRoot))
        {
            return 65;
        }
        if (activationStages.Count > 0)
        {
            File.AppendAllLines(Path.Combine(workspaceRoot, "activation-counters.txt"), activationStages);
            activationStages.Clear();
        }
        if (mode == "crash") return 42;
        if (mode is "activation-hang" or "activation-delay")
        {
            await WriteStatusAsync("activation-entered.json", new
            {
                mode,
                sessionGeneration = root.GetProperty("sessionGeneration").GetInt64(),
            });
            if (mode == "activation-hang") continue;
            var releasePath = Path.Combine(WorkerTemporaryPath(), "activation-delay.release");
            while (!File.Exists(releasePath))
            {
                await Task.Delay(25);
            }
        }
        if (mode is "v1-transaction-migration" or "v1-transaction-migration-fail")
        {
            await ApplyExactMigrationAsync(
                input,
                output,
                "state",
                "legacy.state.v1",
                "state.current.v1",
                "transactionStateMigration");
            await ApplyExactMigrationAsync(
                input,
                output,
                "secrets",
                "legacy.secret.v1",
                "secret.current.v1",
                "transactionSecretMigration");
            if (mode == "v1-transaction-migration-fail") return 42;
        }
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
            var providerCount = 0;
            string? endpointReference = null;
            if (response.RootElement.TryGetProperty("value", out var value)
                && value.TryGetProperty("providers", out var providers))
            {
                providerCount = providers.GetArrayLength();
                if (providerCount > 0)
                {
                    endpointReference = providers[0].GetProperty("endpointReference").GetString();
                }
            }
            await WriteStatusAsync("activation-host-call.json", new
            {
                responseType = response.RootElement.GetProperty("type").GetString(),
                providerCount,
                endpointReference,
                errorCode = response.RootElement.TryGetProperty("error", out var errorValue)
                            && errorValue.TryGetProperty("code", out var code)
                    ? code.GetString()
                    : null,
            });
        }
        if (mode == "v1-retained-recovery")
        {
            // Exercise package-owned work during initial and recovery activation,
            // before publication, with the exact new Worker birth identity.
            await WriteFrameAsync(output, new
            {
                type = "worker.state-set",
                id = "retentionActivation",
                key = "retention.activation",
                value = new
                {
                    kind = "inline",
                    value = JsonSerializer.Serialize(new
                    {
                        activationId = hello.RootElement.GetProperty("activationId").GetString(),
                        sessionGeneration = root.GetProperty("sessionGeneration").GetInt64(),
                    }),
                },
            });
            using (await ReadExpectedHostResultAsync(input, "retentionActivation"))
            {
            }
        }
        if (mode == "v1-shutdown-buffered-scoped-discover")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.scope-open",
                id = "shutdownScopeOpen",
            });
            using var scopeResponse = await ReadFrameAsync(input);
            if (scopeResponse.RootElement.GetProperty("type").GetString() != "host.result") return 68;
            shutdownBufferedWorkerCallScopeId = scopeResponse.RootElement
                .GetProperty("value")
                .GetProperty("scopeId")
                .GetString();
        }
        await WriteFrameAsync(output, new
        {
            type = "worker.activated",
            sessionGeneration = root.GetProperty("sessionGeneration").GetInt64(),
        });
        if (mode == "unknown-worker-cancel")
            await WriteFrameAsync(output, new { type = "worker.cancel", id = "never-admitted" });
        if (mode is "v1-shutdown-buffered-get-provider"
            or "v1-shutdown-buffered-discover"
            or "v1-shutdown-buffered-scoped-discover"
            or "v1-shutdown-buffered-invalid-discover")
        {
            shutdownBufferedWorkerCall = CreateShutdownBufferedWorkerCall(
                mode,
                shutdownBufferedWorkerCallScopeId);
            await WriteFrameHeaderAsync(output, shutdownBufferedWorkerCall.Value.Body);
            await WriteStatusAsync($"{mode}.json", new { started = true });
            continue;
        }
        if (mode == "v1-generation-fault")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.generation-fault",
                activationId = Environment.GetEnvironmentVariable("SUNDER_ACTIVATION_ID"),
                sessionGeneration = root.GetProperty("sessionGeneration").GetInt64(),
                code = "worker.test-fault",
                message = "The test worker reported a bounded fault.",
            });
            continue;
        }
        if (mode == "v1-environment")
        {
            await WriteStatusAsync("v1-environment.json", new
            {
                contentRoot = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_CONTENT_ROOT"),
                temporaryRoot = Environment.GetEnvironmentVariable("SUNDER_WORKER_TEMP_ROOT"),
                operationalOutcomes = Environment.GetEnvironmentVariable("SUNDER_WORKER_OPERATIONAL_OUTCOMES_V1"),
                workspaceRoot,
                workspaceEnvironment = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_WORKSPACE_ROOT"),
                legacyContentPath = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_CONTENT_PATH"),
                legacyDataPath = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_DATA_PATH"),
                legacyWorkingPath = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_WORKING_PATH"),
                environmentNames = GetEnvironmentVariableNames(),
            });
            continue;
        }
        if (mode is "v1-scope-roundtrip" or "v1-scope-handle-limit")
        {
            await RunV1ScopeRoundTripAsync(
                input,
                output,
                testHandleLimit: mode == "v1-scope-handle-limit");
            continue;
        }
        if (mode == "v1-scope-leak")
        {
            await RunV1LeakedScopeAsync(input, output);
            continue;
        }
        if (mode == "v1-scope-permission-denied")
        {
            await RunV1DeniedScopeAsync(input, output);
            continue;
        }
        if (mode == "v1-invariant-report")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.report-invariant-violation",
                id = "invariantReport1",
                endpointReference = "rpc1_exact_provider",
                exceptionMessage = "sanitized invariant failure",
            });
            using var response = await ReadFrameAsync(input);
            await WriteStatusAsync("v1-invariant-report.json", new
            {
                responseType = response.RootElement.GetProperty("type").GetString(),
                accepted = response.RootElement.TryGetProperty("value", out var value) && value.GetBoolean(),
            });
            continue;
        }
        if (mode == "v1-package-data")
        {
            await RunV1PackageDataAsync(input, output);
            continue;
        }
        if (mode == "v1-files-secrets")
        {
            await RunV1FilesAndSecretsAsync(input, output);
            continue;
        }
        if (mode == "v1-logging")
        {
            await RunV1LoggingAsync(input, output);
            continue;
        }
        if (mode is "v1-stale-package-data" or "v1-stale-malformed-payload" or "v1-recovery-candidate-package-data")
        {
            await RunV1StalePackageDataAsync(
                input,
                output,
                malformedPayload: mode == "v1-stale-malformed-payload");
            continue;
        }
        if (mode == "v1-oversized-inline")
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
        if (mode == "v1-shutdown-worker-call")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.watch",
                id = "shutdownWatch1",
                afterRevision = 0,
                afterSequence = 0,
            });
            await WriteStatusAsync("v1-shutdown-worker-call.json", new { started = true });
            continue;
        }
        if (mode == "v1-unknown-scope")
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
        if (mode == "nonopt-lineage-field"
            && root.GetProperty("context").GetProperty("callDepth").GetInt32() == 1)
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.invoke",
                id = "undeclaredLineageCall",
                endpointReference = root.GetProperty("context").GetProperty("provider").GetProperty("endpointReference").GetString(),
                serviceId = "messages",
                methodId = "send",
                request = new { message = "undeclared-lineage" },
                deadlineUtc = DateTimeOffset.UtcNow.AddHours(1).UtcDateTime.ToString("O"),
                parentInvocationId = id,
            });
            continue;
        }
        if (mode == "nested-call-missing-parent"
            && root.GetProperty("context").GetProperty("callDepth").GetInt32() == 1)
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.invoke",
                id = "missingParentCall",
                endpointReference = root.GetProperty("context").GetProperty("provider").GetProperty("endpointReference").GetString(),
                serviceId = "messages",
                methodId = "send",
                request = new { message = "missing-parent" },
                deadlineUtc = DateTimeOffset.UtcNow.AddHours(1).UtcDateTime.ToString("O"),
            });
            continue;
        }
        if (mode == "nested-call-forged-parent"
            && root.GetProperty("context").GetProperty("callDepth").GetInt32() == 1)
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.invoke",
                id = "forgedParentCall",
                endpointReference = root.GetProperty("context").GetProperty("provider").GetProperty("endpointReference").GetString(),
                serviceId = "messages",
                methodId = "send",
                request = new { message = "forged-parent" },
                deadlineUtc = DateTimeOffset.UtcNow.AddHours(1).UtcDateTime.ToString("O"),
                parentInvocationId = "unknownHostCall",
            });
            continue;
        }
        if (mode == "nested-call-completed-parent"
            && root.GetProperty("context").GetProperty("callDepth").GetInt32() == 1)
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.result",
                id,
                value = new { accepted = true },
            });
            await WriteFrameAsync(output, new
            {
                type = "worker.invoke",
                id = "completedParentCall",
                endpointReference = root.GetProperty("context").GetProperty("provider").GetProperty("endpointReference").GetString(),
                serviceId = "messages",
                methodId = "send",
                request = new { message = "completed-parent" },
                deadlineUtc = DateTimeOffset.UtcNow.AddHours(1).UtcDateTime.ToString("O"),
                parentInvocationId = id,
            });
            continue;
        }
        if (mode is "nested-call-context" or "nested-call-null-parent" or "nested-call-concurrent-frames"
            && root.GetProperty("context").GetProperty("callDepth").GetInt32() == 1)
        {
            var parentContext = root.GetProperty("context");
            await WriteFrameAsync(output, new
            {
                type = "worker.invoke",
                id = "nestedCall1",
                endpointReference = parentContext.GetProperty("provider").GetProperty("endpointReference").GetString(),
                serviceId = "messages",
                methodId = "send",
                request = new { message = "nested-child" },
                deadlineUtc = DateTimeOffset.UtcNow.AddHours(1).UtcDateTime.ToString("O"),
                parentInvocationId = mode == "nested-call-null-parent" ? null : id,
            });
            using var nestedInvocation = await ReadFrameAsync(input);
            var nestedRoot = nestedInvocation.RootElement;
            if (nestedRoot.GetProperty("type").GetString() != "host.invoke") return 67;
            var nestedContext = nestedRoot.GetProperty("context");
            int? concurrentCallDepth = null;
            if (mode == "nested-call-concurrent-frames")
            {
                await WriteFrameAsync(output, new
                {
                    type = "worker.invoke",
                    id = "nestedCall2",
                    endpointReference = parentContext.GetProperty("provider").GetProperty("endpointReference").GetString(),
                    serviceId = "messages",
                    methodId = "send",
                    request = new { message = "nested-grandchild" },
                    deadlineUtc = DateTimeOffset.UtcNow.AddHours(1).UtcDateTime.ToString("O"),
                    parentInvocationId = id,
                });
                using var concurrentInvocation = await ReadFrameAsync(input);
                var concurrentRoot = concurrentInvocation.RootElement;
                if (concurrentRoot.GetProperty("type").GetString() != "host.invoke") return 68;
                concurrentCallDepth = concurrentRoot.GetProperty("context").GetProperty("callDepth").GetInt32();
                await WriteFrameAsync(output, new
                {
                    type = "worker.result",
                    id = concurrentRoot.GetProperty("id").GetString(),
                    value = new { accepted = true },
                });
                using (await ReadExpectedHostResultAsync(input, "nestedCall2"))
                {
                }
            }
            await WriteFrameAsync(output, new
            {
                type = "worker.result",
                id = nestedRoot.GetProperty("id").GetString(),
                value = new { accepted = true },
            });
            using var nestedResult = await ReadExpectedHostResultAsync(input, "nestedCall1");
            await WriteStatusAsync("nested-call-context.json", new
            {
                parentCallDepth = parentContext.GetProperty("callDepth").GetInt32(),
                nestedCallDepth = nestedContext.GetProperty("callDepth").GetInt32(),
                concurrentCallDepth,
                parentDeadlineUtc = parentContext.GetProperty("deadlineUtc").GetString(),
                nestedDeadlineUtc = nestedContext.GetProperty("deadlineUtc").GetString(),
                extendedDeadlineCapability = Environment.GetEnvironmentVariable("SUNDER_RPC_EXTENDED_DEADLINE_V1"),
            });
            await WriteFrameAsync(output, new
            {
                type = "worker.result",
                id,
                value = new { accepted = true },
            });
            continue;
        }
        if (mode is "late-worker-cancel" or "completed-id-expired-history")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.discover",
                id = "lateCancel1",
                contractId = "example.rpc",
            });
            using var response = await ReadFrameAsync(input);
            if (response.RootElement.GetProperty("type").GetString() is not ("host.result" or "host.error")) return 65;
            if (mode == "completed-id-expired-history")
            {
                for (var index = 0; index < 3; index++)
                {
                    var churnId = "churn" + index;
                    await WriteFrameAsync(output, new { type = "worker.discover", id = churnId, contractId = "example.rpc" });
                    using var churn = await ReadExpectedHostResultAsync(input, churnId);
                }
            }
            await WriteFrameAsync(output, new { type = "worker.cancel", id = "lateCancel1" });
        }
        if (mode is "late-worker-cancel-history" or "duplicate-active-after-history")
        {
            // Keep a nested Host call live while more than the configured two
            // completion-history slots turn over. Complete it only after churn.
            var nested = new
            {
                type = "worker.invoke", id = "lateCancel1",
                endpointReference = root.GetProperty("context").GetProperty("provider").GetProperty("endpointReference").GetString(),
                serviceId = "messages", methodId = "send", request = new { message = "held" },
            };
            await WriteFrameAsync(output, nested);
            using var invocation = await ReadFrameAsync(input);
            if (invocation.RootElement.GetProperty("type").GetString() != "host.invoke") return 69;
            for (var index = 0; index < 3; index++)
            {
                var churnId = "churn" + index;
                await WriteFrameAsync(output, new { type = "worker.discover", id = churnId, contractId = "example.rpc" });
                using var response = await ReadExpectedHostResultAsync(input, churnId);
            }
            if (mode == "duplicate-active-after-history")
            {
                await WriteFrameAsync(output, nested);
                continue;
            }
            await WriteFrameAsync(output, new { type = "worker.result", id = invocation.RootElement.GetProperty("id").GetString(), value = new { accepted = true } });
            using var terminal = await ReadExpectedHostResultAsync(input, "lateCancel1");
            await WriteFrameAsync(output, new { type = "worker.cancel", id = "lateCancel1" });
        }
        if (mode == "v1-publication-discovery")
        {
            var callId = "publicationDiscovery" + id;
            await WriteFrameAsync(output, new
            {
                type = "worker.discover",
                id = callId,
                contractId = "example.rpc",
            });
            using var response = await ReadFrameAsync(input);
            await WriteStatusAsync("publication-discovery-" + root.GetProperty("request").GetProperty("message").GetString() + ".json",
                response.RootElement);
        }
        if (method == "wait")
        {
            await WriteStatusAsync("host-invoke-wait-entered.json", new { id, kind, method });
            continue;
        }
        if (mode.StartsWith("admission-", StringComparison.Ordinal)
            && root.GetProperty("request").GetProperty("message").GetString() == "overloaded")
        {
            await ProbeAdmissionAsync(input, output, root, mode == "admission-malformed");
        }
        if (mode.StartsWith("operational-", StringComparison.Ordinal)
            && root.GetProperty("request").GetProperty("message").GetString() == "overloaded")
        {
            var outcome = mode["operational-".Length..];
            var failure = new Dictionary<string, object?>
            {
                ["kind"] = "operational",
                ["code"] = outcome is "details" or "dispatch" ? "resource-exhausted" : outcome,
                ["message"] = "worker-secret=must-not-reach-caller",
            };
            if (outcome == "details") failure["details"] = new { forged = true };
            if (outcome == "dispatch") failure["dispatchState"] = "not-dispatched";
            await WriteFrameAsync(output, new { type = "worker.error", id, error = failure });
            continue;
        }
        if (mode == "response-limit"
            && root.GetProperty("request").TryGetProperty("message", out var requestMessage)
            && requestMessage.GetString() == "oversized")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.error",
                id,
                error = new
                {
                    kind = "resource-exhausted",
                    code = "rpc.worker.response-limit",
                    message = "worker-secret=must-not-reach-caller",
                },
            });
            continue;
        }
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
            var openedFileReleased = false;
            if (mode == "content-control-lane")
            {
                openedFileReleased = await ReleaseWithSaturatedRpcLaneAsync(
                    input,
                    output,
                    invocationId,
                    handleId,
                    openedPath);
            }
            var outputDirectory = Path.Combine(
                WorkerTemporaryPath(),
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
            if (mode != "content-control-lane")
            {
                await WriteFrameAsync(output, new
                {
                    type = "worker.content-release",
                    id = invocationId + "release",
                    invocationId,
                    handleId,
                });
                using var releaseResponse = await ReadFrameAsync(input);
                _ = releaseResponse.RootElement.GetProperty("value");
                openedFileReleased = !File.Exists(openedPath);
            }
            await WriteFrameAsync(output, new
            {
                type = "worker.result",
                id,
                value = new
                {
                    accepted = true,
                    content = registered,
                    openedFileReleased,
                },
            });
            continue;
        }
        if (kind == "server-stream")
        {
            if (mode == "stream-queue-overflow")
            {
                for (var sequence = 1; sequence <= 34; sequence++)
                {
                    await WriteFrameAsync(output, new { type = "worker.event", id, value = new { accepted = true } });
                }
                continue;
            }
            await WriteFrameAsync(output, new { type = "worker.event", id, value = new { accepted = true, sequence = 1 } });
            await WriteFrameAsync(output, new { type = "worker.event", id, value = new { accepted = true, sequence = 2 } });
            await WriteFrameAsync(output, new { type = "worker.complete", id });
        }
        else
        {
            if (mode == "v1-provider-fault")
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
            if (mode == "domain-details")
            {
                await WriteFrameAsync(output, new
                {
                    type = "worker.error",
                    id,
                    error = new
                    {
                        kind = "domain",
                        code = "messages.rejected",
                        message = "The message was rejected.",
                        details = new { reason = "duplicate" },
                    },
                });
                continue;
            }
            if (mode == "forged-error-details")
            {
                await WriteFrameAsync(output, new
                {
                    type = "worker.error",
                    id,
                    error = new
                    {
                        kind = "permission-denied",
                        code = "rpc.permission.denied",
                        message = "Forged.",
                        details = new { reason = "forged" },
                    },
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
                    contentRoot = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_CONTENT_ROOT"),
                    temporaryRoot = Environment.GetEnvironmentVariable("SUNDER_WORKER_TEMP_ROOT"),
                    workspaceRoot,
                    workspaceEnvironment = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_WORKSPACE_ROOT"),
                    dataPath = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_DATA_PATH"),
                    statePath = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_STATE_PATH"),
                    path = Environment.GetEnvironmentVariable("PATH"),
                    home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME"),
                    environmentNames = GetEnvironmentVariableNames(),
                    workingPath = Environment.CurrentDirectory,
                    deadlineUtc = root.GetProperty("context").GetProperty("deadlineUtc").GetString(),
                    extendedDeadlineCapability = Environment.GetEnvironmentVariable("SUNDER_RPC_EXTENDED_DEADLINE_V1"),
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
        if (mode == "stream-queue-overflow")
        {
            await WriteStatusAsync("stream-queue-overflow-cancelled.json", new
            {
                id = root.GetProperty("id").GetString(),
            });
        }
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
        if (mode == "v1-generation-fault-on-shutdown")
        {
            await WriteFrameAsync(output, new
            {
                type = "worker.generation-fault",
                activationId = Environment.GetEnvironmentVariable("SUNDER_ACTIVATION_ID"),
                sessionGeneration = committedSessionGeneration,
                code = "worker.test-fault",
                message = "The test worker reported a bounded fault.",
            });
        }
        if (mode is "shutdown-hang" or "candidate-shutdown-hang" or "commit-shutdown-hang" or "migration-preparation-shutdown-hang")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        if (shutdownBufferedWorkerCall is { } bufferedCall)
        {
            await output.WriteAsync(bufferedCall.Body);
            await output.FlushAsync();
            using var response = await ReadFrameAsync(input);
            if (response.RootElement.GetProperty("type").GetString() != "host.error"
                || response.RootElement.GetProperty("id").GetString() != bufferedCall.Id
                || response.RootElement.GetProperty("error").GetProperty("code").GetString() != "rpc.call.cancelled"
                || response.RootElement.GetProperty("error").GetProperty("dispatchState").GetString() != "not-dispatched"
                || response.RootElement.GetProperty("error").GetProperty("retrySafety").GetString() != "safe")
            {
                return 67;
            }
        }
        if (mode == "v1-malformed-shutdown-logging")
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
        if (mode is "v1-logging" or "v1-generation-fault" or "v1-generation-fault-on-shutdown")
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
        if (mode is "v1-generation-fault" or "v1-generation-fault-on-shutdown") return 70;
        await WriteFrameAsync(output, new
        {
            type = "worker.shutdown-ack",
            shutdownId = root.GetProperty("shutdownId").GetString(),
        });
        return 0;
    }
}

static bool TryGetGenerationCommitStartedPath(
    string packageId,
    string packageVersion,
    out string startedPath)
{
    startedPath = string.Empty;
    if (packageId != "generation.cancelling.package") return false;
    var contentRoot = Environment.GetEnvironmentVariable("SUNDER_PACKAGE_CONTENT_ROOT");
    if (string.IsNullOrWhiteSpace(contentRoot)) return false;
    var controlPath = Path.Combine(contentRoot, "generation-commit-control.json");
    if (!File.Exists(controlPath)) return false;
    using var control = JsonDocument.Parse(File.ReadAllBytes(controlPath));
    var triggerPath = control.RootElement.GetProperty("triggerPath").GetString();
    startedPath = control.RootElement.GetProperty("startedPath").GetString() ?? string.Empty;
    return !string.IsNullOrWhiteSpace(triggerPath)
           && !string.IsNullOrWhiteSpace(startedPath)
           && File.Exists(triggerPath)
           && string.Equals(File.ReadAllText(triggerPath).Trim(), packageVersion, StringComparison.Ordinal);
}

static async Task RunV1StalePackageDataAsync(
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
    await WriteStatusAsync("v1-stale-package-data.json", new
    {
        kind = response.RootElement.GetProperty("error").GetProperty("kind").GetString(),
        code = response.RootElement.GetProperty("error").GetProperty("code").GetString(),
        uploadConsumed = !File.Exists(filePath),
    });
}

static async Task RunV1FilesAndSecretsAsync(Stream input, Stream output)
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

    await WriteStatusAsync("v1-files-secrets.json", new
    {
        secretWasMissing,
        secretRoundTripped,
        fileRoundTripped,
        downloadReleased,
    });
}

static async Task RunV1LoggingAsync(Stream input, Stream output)
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

    await WriteStatusAsync("v1-logging.json", new { accepted = true });
}

static async Task ProbeAdmissionAsync(Stream input, Stream output, JsonElement invocation, bool malformed)
{
    const string watchId = "admissionWatch";
    const string pendingId = "admissionPending";
    await WriteFrameAsync(output, new { type = "worker.watch", id = watchId, afterRevision = 0, afterSequence = 0 });
    await WriteFrameAsync(output, new { type = "worker.discover", id = "admissionDiscovery", contractId = "example.rpc" });
    var discovered = false;
    var watching = false;
    while (!discovered || !watching)
    {
        using var response = await ReadFrameAsync(input);
        var root = response.RootElement;
        var id = root.GetProperty("id").GetString();
        if (id == "admissionDiscovery")
        {
            if (root.GetProperty("type").GetString() != "host.result") throw new InvalidDataException("Discovery had no finite-work headroom.");
            discovered = true;
        }
        if (id == watchId) watching = true;
    }
    await WriteFrameAsync(output, new { type = "worker.payload-allocate", id = "admissionUpload", length = 3 });
    string payloadHandle;
    string payloadPath;
    while (true)
    {
        using var response = await ReadFrameAsync(input);
        if (response.RootElement.GetProperty("id").GetString() != "admissionUpload") continue;
        var value = response.RootElement.GetProperty("value");
        payloadHandle = value.GetProperty("handleId").GetString()!;
        payloadPath = value.GetProperty("filePath").GetString()!;
        await File.WriteAllBytesAsync(payloadPath, "abc"u8.ToArray());
        break;
    }
    await WriteFrameAsync(output, new
    {
        type = "worker.invoke", id = pendingId,
        endpointReference = invocation.GetProperty("context").GetProperty("provider").GetProperty("endpointReference").GetString(),
        serviceId = "messages", methodId = "wait", request = new { message = "held" }, deadlineUtc = (string?)null,
    });
    string nestedId;
    while (true)
    {
        using var response = await ReadFrameAsync(input);
        if (response.RootElement.GetProperty("type").GetString() != "host.invoke") continue;
        nestedId = response.RootElement.GetProperty("id").GetString()!;
        break;
    }
    if (malformed)
    {
        await WriteFrameAsync(output, new { type = "worker.discover", id = "admissionRejected", contractId = "example.rpc", forbidden = true });
        using var unexpected = await ReadFrameAsync(input);
        throw new InvalidDataException("Host accepted a malformed request at the admission boundary.");
    }
    await WriteFrameAsync(output, new { type = "worker.discover", id = "admissionRejected", contractId = "example.rpc" });
    await WriteFrameAsync(output, new { type = "worker.watch", id = "admissionRejectedWatch", afterRevision = 0, afterSequence = 0 });
    await WriteFrameAsync(output, new
    {
        type = "worker.state-set", id = "admissionRejectedPayload", key = "rejected",
        value = new { kind = "payload", handleId = payloadHandle, length = 3, sha256 = Convert.ToHexStringLower(SHA256.HashData("abc"u8)) },
    });
    var rejections = new Dictionary<string, JsonElement>();
    while (rejections.Count < 3)
    {
        using var response = await ReadFrameAsync(input);
        var root = response.RootElement;
        var id = root.GetProperty("id").GetString()!;
        if (id is "admissionRejected" or "admissionRejectedWatch" or "admissionRejectedPayload") rejections.Add(id, root.GetProperty("error").Clone());
    }
    rejections.Add("payloadReleased", JsonSerializer.SerializeToElement(!File.Exists(payloadPath)));
    await WriteStatusAsync("admission-probe.json", rejections);
    await WriteFrameAsync(output, new { type = "worker.cancel", id = pendingId });
    await WriteFrameAsync(output, new { type = "worker.cancel", id = watchId });
    var terminals = new HashSet<string>();
    while (terminals.Count < 2)
    {
        using var response = await ReadFrameAsync(input);
        var root = response.RootElement;
        var id = root.GetProperty("id").GetString()!;
        var type = root.GetProperty("type").GetString();
        if (type == "host.cancel" && id == nestedId)
            await WriteFrameAsync(output, new { type = "worker.error", id, error = new { kind = "cancelled", code = "rpc.call.cancelled", message = "cancelled" } });
        else if (id is pendingId or watchId && type is "host.error" or "host.complete") terminals.Add(id);
    }
}

static async Task<bool> ReleaseWithSaturatedRpcLaneAsync(
    Stream input,
    Stream output,
    string invocationId,
    string handleId,
    string openedPath)
{
    const string watchId = "saturatedWatch";
    var releaseId = invocationId + "release";
    await WriteFrameAsync(output, new
    {
        type = "worker.watch",
        id = watchId,
        afterRevision = 0,
        afterSequence = 0,
    });
    await WriteFrameAsync(output, new
    {
        type = "worker.content-release",
        id = releaseId,
        invocationId,
        handleId,
    });
    while (true)
    {
        using var response = await ReadFrameAsync(input);
        var root = response.RootElement;
        var id = root.GetProperty("id").GetString();
        if (id == releaseId && root.GetProperty("type").GetString() == "host.result") break;
        if (id != watchId || root.GetProperty("type").GetString() != "host.event")
        {
            throw new InvalidDataException("Expected a watch event or content-release result.");
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

static async Task RunV1ScopeRoundTripAsync(
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
    var dataPath = WorkerTemporaryPath();
    var temporaryPath = Path.Combine(dataPath, "tmp");
    Directory.CreateDirectory(temporaryPath);
    var requestPath = Path.Combine(temporaryPath, "v1-scope-request.txt");
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
    var responsePath = Path.Combine(temporaryPath, "v1-scope-response.txt");
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
            openedFileReleased = invocationHandleReleased,
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
        testHandleLimit ? "v1-scope-handle-limit.json" : "v1-scope-roundtrip.json");
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

static async Task RunV1LeakedScopeAsync(Stream input, Stream output)
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

    var dataPath = WorkerTemporaryPath();
    var temporaryPath = Path.Combine(dataPath, "tmp");
    Directory.CreateDirectory(temporaryPath);
    var contentPath = Path.Combine(temporaryPath, "v1-leaked-scope.txt");
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

    var statusPath = Path.Combine(dataPath, "v1-scope-leak.json");
    var pendingStatusPath = statusPath + ".pending";
    await File.WriteAllBytesAsync(
        pendingStatusPath,
        JsonSerializer.SerializeToUtf8Bytes(new { scopeId }));
    File.Move(pendingStatusPath, statusPath);
}

static async Task RunV1DeniedScopeAsync(Stream input, Stream output)
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

    var dataPath = WorkerTemporaryPath();
    var statusPath = Path.Combine(dataPath, "v1-scope-permission-denied.json");
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

static async Task RunV1PackageDataAsync(Stream input, Stream output)
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

    var dataPath = WorkerTemporaryPath();
    var statusPath = Path.Combine(dataPath, "v1-package-data.json");
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

static async Task RunMigrationPreparationStateReadAsync(Stream input, Stream output)
{
    await WriteFrameAsync(output, new
    {
        type = "worker.state-get",
        id = "migrationPreparationGet",
        key = "legacy.value",
        mode = "value",
    });
    string? stateValue;
    using (var response = await ReadExpectedHostResultAsync(input, "migrationPreparationGet"))
    {
        stateValue = response.RootElement.GetProperty("value").GetProperty("value").GetString();
    }

    await WriteFrameAsync(output, new
    {
        type = "worker.state-list",
        id = "migrationPreparationList",
        prefix = "legacy",
    });
    string[] keys;
    using (var response = await ReadExpectedHostResultAsync(input, "migrationPreparationList"))
    {
        keys = response.RootElement.GetProperty("value").GetProperty("keys")
            .EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToArray();
    }

    await WriteStatusAsync("v1-migration-preparation-state.json", new { stateValue, keys });
}

static async Task ApplyExactMigrationAsync(
    Stream input,
    Stream output,
    string store,
    string legacyKey,
    string destinationKey,
    string id)
{
    await WriteFrameAsync(output, new
    {
        type = "worker.migration-apply",
        id,
        store,
        rules = new object[]
        {
            new
            {
                kind = "exact",
                legacyKey,
                destinationKey,
            },
        },
    });
    using var response = await ReadExpectedHostResultAsync(input, id);
    if (response.RootElement.GetProperty("value").ValueKind != JsonValueKind.Null)
    {
        throw new InvalidDataException("Host migration result must be null.");
    }
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
    var dataPath = WorkerTemporaryPath();
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

static string WorkerTemporaryPath()
    => Environment.GetEnvironmentVariable("SUNDER_WORKER_TEMP_ROOT")
       ?? throw new InvalidOperationException("The Host did not provide a worker temporary root.");

static string[] GetEnvironmentVariableNames()
    => Environment.GetEnvironmentVariables()
        .Keys
        .Cast<object>()
        .Select(static key => key.ToString()!)
        .Order(StringComparer.Ordinal)
        .ToArray();

static string CreateExceptionFingerprint(string exceptionType)
    => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exceptionType)))
        .ToLowerInvariant();

static (string Id, byte[] Body) CreateShutdownBufferedWorkerCall(string mode, string? scopeId = null)
{
    const string id = "shutdownBufferedCall";
    object envelope = mode switch
    {
        "v1-shutdown-buffered-get-provider" => new
        {
            type = "worker.get-provider",
            id,
            endpointReference = "rpc1_exact_provider",
        },
        "v1-shutdown-buffered-invalid-discover" => new
        {
            type = "worker.discover",
            id,
            contractId = "example.rpc",
            unexpected = true,
        },
        "v1-shutdown-buffered-scoped-discover" => new
        {
            type = "worker.discover",
            id,
            scopeId = scopeId ?? throw new InvalidOperationException("The shutdown call scope was not created."),
            contractId = "example.rpc",
        },
        _ => new
        {
            type = "worker.discover",
            id,
            contractId = "example.rpc",
        },
    };
    return (id, SerializeFrameBody(envelope));
}

static async Task WriteFrameHeaderAsync(Stream output, byte[] body)
{
    var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
    await output.WriteAsync(header);
    await output.FlushAsync();
}

static byte[] SerializeFrameBody(object value)
    => JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    });

static async Task WriteFrameAsync(Stream output, object value)
{
    var body = SerializeFrameBody(value);
    var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
    await output.WriteAsync(header);
    await output.WriteAsync(body);
    await output.FlushAsync();
}

sealed class SecretBearingProviderException(string message) : Exception(message);
