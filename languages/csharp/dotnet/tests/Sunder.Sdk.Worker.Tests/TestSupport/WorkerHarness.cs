using System.Text;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Sunder.Sdk.Worker.Internal;

namespace Sunder.Sdk.Worker.Tests.TestSupport;

internal sealed class WorkerHarness : IAsyncDisposable
{
    public const string PackageId = "example.package";
    public const string PackageVersion = "1.0.0";
    public const string ActivationId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string SessionId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public const string ProviderId = "example.provider";
    public const string ContractId = "example.rpc";
    public const string ContractVersion = "1.0.0";
    public static readonly string DescriptorSha256 = new('c', 64);
    public static readonly string AbiSha256 = new('d', 64);
    public static readonly WorkerResourceProfile ResourceProfile = new(
        Version: 1,
        RpcValueBytes: 8 * 1024 * 1024,
        ValueDepth: 64,
        ValueNodes: 524_288,
        ObjectMembers: 4_096,
        InlineDecodedBytes: 512 * 1024,
        ContentReferenceBytes: 64 * 1024 * 1024);
    public static readonly string ResourceProfileHash = WorkerWire.ComputeResourceProfileHash(ResourceProfile);

    private readonly CancellationTokenSource _runCancellation = new();
    private readonly WorkerFrameReader _outputReader;
    private readonly string _root;

    public WorkerHarness(
        Func<SunderWorkerContext, SunderWorkerOptions> configure,
        WorkerLimits? limits = null,
        bool ignoreInputReadCancellation = false,
        bool rpcExtendedDeadlineV1 = false,
        string packageVersion = PackageVersion,
        bool operationalOutcomesV1 = true)
    {
        Limits = (limits ?? WorkerLimits.Default) with { };
        Input = new AsyncByteStream(ignoreInputReadCancellation);
        Output = new AsyncByteStream();
        Diagnostics = new StringWriter();
        _outputReader = new WorkerFrameReader(Output, Limits);
        _root = Path.Combine(Path.GetTempPath(), "sunder-sdk-worker-tests", Guid.NewGuid().ToString("N"));
        ContentPath = Path.Combine(_root, "content");
        DataPath = Path.Combine(_root, "data");
        WorkspacePath = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(ContentPath);
        Directory.CreateDirectory(DataPath);
        Directory.CreateDirectory(WorkspacePath);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["SUNDER_WORKER_PROTOCOL"] = WorkerProtocol.Name,
            ["SUNDER_PACKAGE_ID"] = PackageId,
            ["SUNDER_PACKAGE_VERSION"] = packageVersion,
            ["SUNDER_ACTIVATION_ID"] = ActivationId,
            ["SUNDER_SESSION_ID"] = SessionId,
            ["SUNDER_PACKAGE_CONTENT_ROOT"] = ContentPath,
            ["SUNDER_WORKER_TEMP_ROOT"] = DataPath,
        };
        if (rpcExtendedDeadlineV1)
        {
            environment["SUNDER_RPC_EXTENDED_DEADLINE_V1"] = "1";
        }
        if (operationalOutcomesV1)
        {
            environment["SUNDER_WORKER_OPERATIONAL_OUTCOMES_V1"] = "1";
        }
        try
        {
            RunTask = SunderWorker.RunAsync(
                configure,
                Input,
                Output,
                Diagnostics,
                name => environment.GetValueOrDefault(name),
                Limits,
                _runCancellation.Token);
            Version = packageVersion;
        }
        catch
        {
            Input.Dispose();
            Output.Dispose();
            Diagnostics.Dispose();
            _runCancellation.Dispose();
            Directory.Delete(_root, recursive: true);
            throw;
        }
    }

    public AsyncByteStream Input { get; }
    public AsyncByteStream Output { get; }
    public StringWriter Diagnostics { get; }
    public WorkerLimits Limits { get; }
    public Task RunTask { get; }
    public string ContentPath { get; }
    public string DataPath { get; }
    public string WorkspacePath { get; }
    public string Version { get; }

    public void Cancel() => _runCancellation.Cancel();

    public static SunderWorkerProviderRegistration Registration(ISunderRpcServiceHandler handler)
        => new(ProviderId, ContractId, ContractVersion, DescriptorSha256, AbiSha256, handler);

    public async Task HandshakeAndActivateAsync(long sessionGeneration = 7)
    {
        await HandshakeAsync();
        await StartCandidateAndCommitAsync(sessionGeneration);
        await PrepareStorageMigrationsAsync(sessionGeneration);
        await ActivateAsync(sessionGeneration);
        using var activated = await ReadAsync();
        Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        Assert.Equal(sessionGeneration, activated.RootElement.GetProperty("sessionGeneration").GetInt64());
    }

    public async Task HandshakeAsync(
        string challenge = "challenge",
        WorkerResourceProfile? resourceProfile = null)
    {
        var profile = resourceProfile ?? ResourceProfile;
        var profileHash = WorkerWire.ComputeResourceProfileHash(profile);
        await SendHelloAsync(challenge, profile, profileHash);
        using (var ready = await ReadAsync())
        {
            Assert.Equal("worker.ready", ready.RootElement.GetProperty("type").GetString());
            Assert.Equal(challenge, ready.RootElement.GetProperty("challenge").GetString());
            Assert.Equal(profileHash, ready.RootElement.GetProperty("resourceProfileHash").GetString());
            var contributions = ready.RootElement.GetProperty("contributions");
            ReadyContributions = contributions.Clone();
            Assert.Single(contributions.GetProperty("rpcProviders").EnumerateArray());
            Assert.True(contributions.GetProperty("migrationPreparationLifecycle").GetBoolean());
        }

    }

    public Task SendHelloAsync(
        string challenge,
        WorkerResourceProfile profile,
        string resourceProfileHash)
        => SendAsync(new
        {
            type = "host.hello",
            protocol = WorkerProtocol.Name,
            protocolVersion = WorkerProtocol.Version,
            challenge,
            packageId = PackageId,
            packageVersion = Version,
            activationId = ActivationId,
            sessionId = SessionId,
            providers = new[] { ProviderIdentity() },
            resourceProfile = new
            {
                version = profile.Version,
                rpcValueBytes = profile.RpcValueBytes,
                valueDepth = profile.ValueDepth,
                valueNodes = profile.ValueNodes,
                objectMembers = profile.ObjectMembers,
                inlineDecodedBytes = profile.InlineDecodedBytes,
                contentReferenceBytes = profile.ContentReferenceBytes,
            },
            resourceProfileHash,
        });

    public JsonElement? ReadyContributions { get; private set; }

    public async Task StartCandidateAndCommitAsync(long sessionGeneration = 7)
    {
        await SendAsync(new { type = "host.start-candidate" });
        using (var started = await ReadAsync())
        {
            Assert.Equal("worker.candidate-started", started.RootElement.GetProperty("type").GetString());
        }
        await SendAsync(new
        {
            type = "host.commit-generation",
            activationId = ActivationId,
            sessionGeneration,
        });
        using (var committed = await ReadAsync())
        {
            Assert.Equal("worker.generation-committed", committed.RootElement.GetProperty("type").GetString());
            Assert.Equal(sessionGeneration, committed.RootElement.GetProperty("sessionGeneration").GetInt64());
        }
    }

    public async Task PrepareStorageMigrationsAsync(long sessionGeneration = 7)
    {
        await SendAsync(new
        {
            type = "host.prepare-storage-migrations",
            activationId = ActivationId,
            sessionGeneration,
        });
        using var prepared = await ReadAsync();
        Assert.Equal("worker.storage-migrations-prepared", prepared.RootElement.GetProperty("type").GetString());
        Assert.Equal(ActivationId, prepared.RootElement.GetProperty("activationId").GetString());
        Assert.Equal(sessionGeneration, prepared.RootElement.GetProperty("sessionGeneration").GetInt64());
    }

    public Task ActivateAsync(long sessionGeneration = 7, string? workspaceRootPath = null)
        => SendAsync(new
        {
            type = "host.activate",
            sessionGeneration,
            workspaceRootPath = workspaceRootPath ?? WorkspacePath,
        });

    public Task SendInvocationAsync(
        string id,
        string kind,
        string methodId,
        object? request = null,
        long sessionGeneration = 7,
        string callerPackageVersion = "1.0.0")
        => SendAsync(new
        {
            type = "host.invoke",
            id,
            kind,
            providerId = ProviderId,
            serviceId = "messages",
            methodId,
            request = request ?? new { message = "hello" },
            context = new
            {
                callerPackageId = "caller.package",
                callerPackageVersion,
                deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
                callDepth = 1,
                provider = ProviderSnapshot(sessionGeneration, Version),
            },
        });

    public async Task ShutdownAsync(string reason = "test")
    {
        await SendAsync(new { type = "host.shutdown", shutdownId = "shutdown1", reason });
        using var acknowledgement = await ReadAsync();
        Assert.Equal("worker.shutdown-ack", acknowledgement.RootElement.GetProperty("type").GetString());
        Assert.Equal("shutdown1", acknowledgement.RootElement.GetProperty("shutdownId").GetString());
        await RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async Task SendAsync(object value)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await SendBodyAsync(body);
    }

    public async Task SendRawJsonAsync(string json)
        => await SendBodyAsync(Encoding.UTF8.GetBytes(json));

    public async Task SendBodyAsync(byte[] body, string? header = null)
    {
        var headerBytes = Encoding.ASCII.GetBytes(header ?? $"Content-Length: {body.Length}\r\n\r\n");
        await Input.WriteAsync(headerBytes);
        await Input.WriteAsync(body);
        await Input.FlushAsync();
    }

    public async ValueTask<JsonDocument> ReadAsync()
        => await _outputReader.ReadAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    public static object ProviderSnapshot(long sessionGeneration = 7, string packageVersion = PackageVersion)
        => new
        {
            packageId = PackageId,
            packageVersion,
            providerId = ProviderId,
            contractId = ContractId,
            contractVersion = ContractVersion,
            descriptorSha256 = DescriptorSha256,
            abiSha256 = AbiSha256,
            activationId = ActivationId,
            activationEpoch = 1,
            sessionGeneration,
            endpointReference = "rpc1_example",
            catalogRevision = 1,
            state = "active",
            faultCode = (string?)null,
        };

    private static object ProviderIdentity()
        => new
        {
            providerId = ProviderId,
            contractId = ContractId,
            contractVersion = ContractVersion,
            descriptorSha256 = DescriptorSha256,
            abiSha256 = AbiSha256,
        };

    public static object ContentReference(string id, long length)
        => new
        {
            id,
            length,
            sha256 = new string('d', 64),
            mediaType = "text/plain",
            fileName = id + ".txt",
            expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
            repeatability = "single-use",
        };

    public async ValueTask DisposeAsync()
    {
        if (!RunTask.IsCompleted) _runCancellation.Cancel();
        Input.CompleteWriting();
        try
        {
            await RunTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }
        Input.Dispose();
        Output.Dispose();
        _runCancellation.Dispose();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}
