using System.Text.Json;

namespace Sunder.Runtime.Host.Services;

internal static class ProcessRuntimeWorkerLifecycleEnvelopeCodec
{
    public static WorkerCandidateStartedEnvelope ParseCandidateStarted(JsonElement root)
    {
        SunderWorkerProtocol.RequireOnlyProperties(root, "type");
        return new WorkerCandidateStartedEnvelope();
    }

    public static WorkerGenerationCommittedEnvelope ParseGenerationCommitted(JsonElement root)
    {
        SunderWorkerProtocol.RequireOnlyProperties(root, "type", "activationId", "sessionGeneration");
        return new WorkerGenerationCommittedEnvelope(
            SunderWorkerProtocol.RequiredString(root, "activationId", 64),
            SunderWorkerProtocol.RequiredNonNegativeInt64(root, "sessionGeneration"));
    }

    public static WorkerStorageMigrationsPreparedEnvelope ParseStorageMigrationsPrepared(JsonElement root)
    {
        SunderWorkerProtocol.RequireOnlyProperties(
            root,
            "type",
            "activationId",
            "sessionGeneration");
        return new WorkerStorageMigrationsPreparedEnvelope(
            SunderWorkerProtocol.RequiredString(root, "activationId", 64),
            SunderWorkerProtocol.RequiredNonNegativeInt64(root, "sessionGeneration"));
    }

    public static WorkerActivatedEnvelope ParseActivated(JsonElement root)
    {
        SunderWorkerProtocol.RequireOnlyProperties(root, "type", "sessionGeneration");
        return new WorkerActivatedEnvelope(
            SunderWorkerProtocol.RequiredNonNegativeInt64(root, "sessionGeneration"));
    }

    public static WorkerGenerationFaultEnvelope ParseGenerationFault(JsonElement root)
    {
        SunderWorkerProtocol.RequireOnlyProperties(
            root,
            "type",
            "activationId",
            "sessionGeneration",
            "code",
            "message");
        var activationId = SunderWorkerProtocol.RequiredString(root, "activationId", 64);
        var sessionGeneration = SunderWorkerProtocol.RequiredNonNegativeInt64(root, "sessionGeneration");
        var code = SunderWorkerProtocol.RequiredString(root, "code", 128);
        var message = SunderWorkerProtocol.RequiredString(root, "message", 512);
        if (!code.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))
        {
            throw new SunderWorkerProtocolException(
                "Process worker generation-fault code contains unsupported characters.");
        }
        return new WorkerGenerationFaultEnvelope(
            activationId,
            sessionGeneration,
            code,
            message);
    }

    public static WorkerShutdownAcknowledgementEnvelope ParseShutdownAcknowledgement(JsonElement root)
    {
        SunderWorkerProtocol.RequireOnlyProperties(root, "type", "shutdownId");
        return new WorkerShutdownAcknowledgementEnvelope(
            SunderWorkerProtocol.RequiredString(root, "shutdownId", 128));
    }
}

internal readonly record struct WorkerCandidateStartedEnvelope
{
}

internal readonly record struct WorkerGenerationCommittedEnvelope(
    string ActivationId,
    long SessionGeneration);

internal readonly record struct WorkerStorageMigrationsPreparedEnvelope(
    string ActivationId,
    long SessionGeneration);

internal readonly record struct WorkerActivatedEnvelope(long SessionGeneration);

internal readonly record struct WorkerGenerationFaultEnvelope(
    string ActivationId,
    long SessionGeneration,
    string Code,
    string Message);

internal readonly record struct WorkerShutdownAcknowledgementEnvelope(string ShutdownId);
