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
    public static readonly string ContractSha256 = new('c', 64);

    private readonly CancellationTokenSource _runCancellation = new();
    private readonly WorkerFrameReader _outputReader;
    private readonly WorkerProtocolIdentity _protocol;
    private readonly string _root;

    public WorkerHarness(SunderWorkerOptions options, WorkerLimits? limits = null)
        : this(WorkerProtocol.V1, limits, (input, output, diagnostics, environment, selectedLimits, cancellationToken) =>
            SunderWorker.RunAsync(
                options,
                input,
                output,
                diagnostics,
                environment,
                selectedLimits,
                cancellationToken))
    {
    }

    public WorkerHarness(
        Func<SunderWorkerContext, SunderWorkerV2Options> configure,
        WorkerLimits? limits = null)
        : this(WorkerProtocol.V2, limits, (input, output, diagnostics, environment, selectedLimits, cancellationToken) =>
            SunderWorkerV2.RunAsync(
                configure,
                input,
                output,
                diagnostics,
                environment,
                selectedLimits,
                cancellationToken))
    {
    }

    private WorkerHarness(
        WorkerProtocolIdentity protocol,
        WorkerLimits? limits,
        Func<Stream, Stream, TextWriter, Func<string, string?>, WorkerLimits, CancellationToken, Task> run)
    {
        _protocol = protocol;
        Limits = (limits ?? WorkerLimits.Default) with { };
        Input = new AsyncByteStream();
        Output = new AsyncByteStream();
        Diagnostics = new StringWriter();
        _outputReader = new WorkerFrameReader(Output, Limits);
        _root = Path.Combine(Path.GetTempPath(), "sunder-sdk-worker-tests", Guid.NewGuid().ToString("N"));
        ContentPath = Path.Combine(_root, "content");
        DataPath = Path.Combine(_root, "data");
        Directory.CreateDirectory(ContentPath);
        Directory.CreateDirectory(DataPath);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["SUNDER_WORKER_PROTOCOL"] = protocol.Name,
            ["SUNDER_PACKAGE_ID"] = PackageId,
            ["SUNDER_PACKAGE_VERSION"] = PackageVersion,
            ["SUNDER_ACTIVATION_ID"] = ActivationId,
            ["SUNDER_SESSION_ID"] = SessionId,
            ["SUNDER_PACKAGE_CONTENT_PATH"] = ContentPath,
            ["SUNDER_PACKAGE_DATA_PATH"] = DataPath,
        };
        RunTask = run(
            Input,
            Output,
            Diagnostics,
            name => environment.GetValueOrDefault(name),
            Limits,
            _runCancellation.Token);
    }

    public AsyncByteStream Input { get; }
    public AsyncByteStream Output { get; }
    public StringWriter Diagnostics { get; }
    public WorkerLimits Limits { get; }
    public Task RunTask { get; }
    public string ContentPath { get; }
    public string DataPath { get; }

    public void Cancel() => _runCancellation.Cancel();

    public static SunderWorkerProviderRegistration Registration(ISunderRpcServiceHandler handler)
        => new(ProviderId, ContractId, ContractVersion, ContractSha256, handler);

    public async Task HandshakeAndActivateAsync(long sessionGeneration = 7)
    {
        await HandshakeAsync();
        if (_protocol.UsesV2Lifecycle)
        {
            await StartCandidateAndCommitAsync(sessionGeneration);
        }
        await SendAsync(new { type = "host.activate", sessionGeneration });
        using var activated = await ReadAsync();
        Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        Assert.Equal(sessionGeneration, activated.RootElement.GetProperty("sessionGeneration").GetInt64());
    }

    public async Task HandshakeAsync()
    {
        await SendAsync(new
        {
            type = "host.hello",
            protocol = _protocol.Name,
            protocolVersion = _protocol.Version,
            challenge = "challenge",
            packageId = PackageId,
            packageVersion = PackageVersion,
            activationId = ActivationId,
            sessionId = SessionId,
            providers = new[]
            {
                new
                {
                    providerId = ProviderId,
                    contractId = ContractId,
                    contractVersion = ContractVersion,
                    contractSha256 = ContractSha256,
                },
            },
        });
        using (var ready = await ReadAsync())
        {
            Assert.Equal("worker.ready", ready.RootElement.GetProperty("type").GetString());
            Assert.Equal("challenge", ready.RootElement.GetProperty("challenge").GetString());
            if (_protocol.UsesV2Lifecycle)
            {
                var contributions = ready.RootElement.GetProperty("contributions");
                ReadyContributions = contributions.Clone();
                Assert.Single(contributions.GetProperty("rpcProviders").EnumerateArray());
            }
        }

    }

    public JsonElement? ReadyContributions { get; private set; }

    public async Task StartCandidateAndCommitAsync(long sessionGeneration = 7)
    {
        if (!_protocol.UsesV2Lifecycle)
        {
            throw new InvalidOperationException("Only the V2 harness has candidate and generation-commit phases.");
        }
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

    public Task SendInvocationAsync(
        string id,
        string kind,
        string methodId,
        object? request = null,
        long sessionGeneration = 7)
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
                callerPackageVersion = "1.0.0",
                deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
                callDepth = 1,
                provider = ProviderSnapshot(sessionGeneration),
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

    public static object ProviderSnapshot(long sessionGeneration = 7)
        => new
        {
            packageId = PackageId,
            packageVersion = PackageVersion,
            providerId = ProviderId,
            contractId = ContractId,
            contractVersion = ContractVersion,
            contractSha256 = ContractSha256,
            activationId = ActivationId,
            activationEpoch = 1,
            sessionGeneration,
            endpointReference = "rpc1_example",
            catalogRevision = 1,
            state = "active",
            faultCode = (string?)null,
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
