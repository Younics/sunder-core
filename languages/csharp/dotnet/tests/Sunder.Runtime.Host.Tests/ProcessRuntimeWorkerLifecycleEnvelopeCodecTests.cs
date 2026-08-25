using System.Text.Json;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ProcessRuntimeWorkerLifecycleEnvelopeCodecTests
{
    private const string ActivationId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Parse_ValidLifecycleEnvelopes_ReturnsTypedPayloads()
    {
        Assert.Equal(
            new WorkerCandidateStartedEnvelope(),
            ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseCandidateStarted(
                JsonSerializer.SerializeToElement(new { type = "worker.candidate-started" })));
        Assert.Equal(
            new WorkerGenerationCommittedEnvelope(ActivationId, 17),
            ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseGenerationCommitted(
                JsonSerializer.SerializeToElement(new
                {
                    type = "worker.generation-committed",
                    activationId = ActivationId,
                    sessionGeneration = 17,
                })));
        Assert.Equal(
            new WorkerStorageMigrationsPreparedEnvelope(ActivationId, 17),
            ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseStorageMigrationsPrepared(
                JsonSerializer.SerializeToElement(new
                {
                    type = "worker.storage-migrations-prepared",
                    activationId = ActivationId,
                    sessionGeneration = 17,
                })));
        Assert.Equal(
            new WorkerActivatedEnvelope(17),
            ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseActivated(
                JsonSerializer.SerializeToElement(new
                {
                    type = "worker.activated",
                    sessionGeneration = 17,
                })));
        Assert.Equal(
            new WorkerGenerationFaultEnvelope(
                ActivationId,
                17,
                "worker.test-fault",
                "The worker failed."),
            ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseGenerationFault(
                JsonSerializer.SerializeToElement(new
                {
                    type = "worker.generation-fault",
                    activationId = ActivationId,
                    sessionGeneration = 17,
                    code = "worker.test-fault",
                    message = "The worker failed.",
                })));
        Assert.Equal(
            new WorkerShutdownAcknowledgementEnvelope("shutdown_17"),
            ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseShutdownAcknowledgement(
                JsonSerializer.SerializeToElement(new
                {
                    type = "worker.shutdown-ack",
                    shutdownId = "shutdown_17",
                })));
    }

    [Theory]
    [InlineData(
        "worker.candidate-started",
        "{\"type\":\"worker.candidate-started\",\"unexpected\":true}",
        "Worker envelope type contains unsupported property 'unexpected'.")]
    [InlineData(
        "worker.generation-committed",
        "{\"type\":\"worker.generation-committed\",\"sessionGeneration\":17}",
        "Worker envelope property 'activationId' must be a non-empty bounded string.")]
    [InlineData(
        "worker.storage-migrations-prepared",
        "{\"type\":\"worker.storage-migrations-prepared\",\"activationId\":\"0123456789abcdef0123456789abcdef\",\"sessionGeneration\":-1}",
        "Worker envelope property 'sessionGeneration' must be an integer from 0 through 9007199254740991.")]
    [InlineData(
        "worker.activated",
        "{\"type\":\"worker.activated\"}",
        "Worker envelope property 'sessionGeneration' must be an integer from 0 through 9007199254740991.")]
    [InlineData(
        "worker.generation-fault",
        "{\"type\":\"worker.generation-fault\",\"activationId\":\"0123456789abcdef0123456789abcdef\",\"sessionGeneration\":17,\"code\":\"not safe\",\"message\":\"failed\"}",
        "Process worker generation-fault code contains unsupported characters.")]
    [InlineData(
        "worker.shutdown-ack",
        "{\"type\":\"worker.shutdown-ack\",\"shutdownId\":\"\"}",
        "Worker envelope property 'shutdownId' must be a non-empty bounded string.")]
    public void Parse_MalformedLifecycleEnvelope_ThrowsDeterministicProtocolError(
        string type,
        string json,
        string expectedMessage)
    {
        using var document = JsonDocument.Parse(json);

        var exception = Assert.Throws<SunderWorkerProtocolException>(() => Parse(type, document.RootElement));

        Assert.Equal(expectedMessage, exception.Message);
    }

    private static object Parse(string type, JsonElement root)
        => type switch
        {
            "worker.candidate-started" =>
                ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseCandidateStarted(root),
            "worker.generation-committed" =>
                ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseGenerationCommitted(root),
            "worker.storage-migrations-prepared" =>
                ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseStorageMigrationsPrepared(root),
            "worker.activated" => ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseActivated(root),
            "worker.generation-fault" =>
                ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseGenerationFault(root),
            "worker.shutdown-ack" =>
                ProcessRuntimeWorkerLifecycleEnvelopeCodec.ParseShutdownAcknowledgement(root),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
}
