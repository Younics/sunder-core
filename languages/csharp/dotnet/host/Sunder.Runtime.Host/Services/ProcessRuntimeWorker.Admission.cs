using System.Text.Json;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class ProcessRuntimeWorker
{
    private void ReleaseRejectedWorkerPayload(string type, JsonElement root)
    {
        JsonElement value;
        if (type is "worker.settings-set" or "worker.state-set" or "worker.secrets-set")
        {
            value = SunderWorkerProtocol.RequiredValue(root, "value");
            if (ReadDataString(value, "kind") != "payload") return;
        }
        else if (type == "worker.files-write") value = SunderWorkerProtocol.RequiredValue(root, "contents");
        else return;

        // Submission transfers upload ownership even when admission rejects the operation. Do
        // not leave the SDK's forgotten handle occupying Host capacity. The caller holds the
        // call gate through this synchronous close/delete, so no extra cleanup slot is needed.
        var payload = RemoveWorkerPayload(ReadSafeId(value, "handleId"), call: null, WorkerPayloadDirection.Upload);
        try
        {
            if (SunderWorkerProtocol.RequiredNonNegativeInt64(value, "length") != payload.Length)
                throw new SunderWorkerProtocolException("Process worker package-data payload length does not match its allocation.");
        }
        finally { DisposeAndDeleteWorkerPayload(payload); }
    }

    // Admission is not an escape from the closed wire envelopes, parent-call checks, or
    // payload/handle shapes. Validate without invoking package code or acquiring resources.
    private void ValidateRejectedWorkerCall(string type, JsonElement root)
    {
        if (IsPackageDataCall(type))
        {
            ValidatePackageDataEnvelope(type, root);
            return;
        }
        switch (type)
        {
            case "worker.get-provider":
                _ = ReadOptionalWorkerCallScopeId(root, "type", "id", "endpointReference");
                _ = SunderWorkerProtocol.RequiredString(root, "endpointReference", 256);
                return;
            case "worker.discover":
                _ = ReadOptionalWorkerCallScopeId(root, "type", "id", "contractId");
                _ = SunderWorkerProtocol.RequiredString(root, "contractId", 256);
                return;
            case "worker.watch":
                _ = ReadOptionalWorkerCallScopeId(root, "type", "id", "afterRevision", "afterSequence");
                _ = SunderWorkerProtocol.RequiredNonNegativeInt64(root, "afterRevision");
                _ = SunderWorkerProtocol.RequiredNonNegativeInt64(root, "afterSequence");
                return;
            case "worker.invoke":
            case "worker.subscribe":
                _ = ReadOptionalWorkerCallScopeId(root, "type", "id", "endpointReference", "serviceId",
                    "methodId", "request", "deadlineUtc", "parentInvocationId");
                _ = SunderWorkerProtocol.RequiredString(root, "endpointReference", 256);
                _ = SunderWorkerProtocol.RequiredString(root, "serviceId", 128);
                _ = SunderWorkerProtocol.RequiredString(root, "methodId", 128);
                _ = SunderWorkerProtocol.RequiredValue(root, "request");
                _ = ReadCallOptions(root);
                return;
            case "worker.scope-open":
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "deadlineUtc", "parentInvocationId");
                _ = ReadCallOptions(root);
                return;
            case "worker.content-open":
            case "worker.content-register":
                var scoped = root.TryGetProperty("scopeId", out _);
                var ownerProperty = scoped ? "scopeId" : "invocationId";
                _ = ReadSafeId(root, ownerProperty);
                if (type == "worker.content-open")
                {
                    SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", ownerProperty, "reference");
                    _ = ReadContentReference(SunderWorkerProtocol.RequiredValue(root, "reference"));
                }
                else
                {
                    if (scoped)
                    {
                        SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", ownerProperty,
                            "endpointReference", "filePath", "options");
                        _ = SunderWorkerProtocol.RequiredString(root, "endpointReference", 256);
                    }
                    else
                    {
                        SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", ownerProperty, "filePath", "options");
                    }
                    _ = ResolveWorkerOwnedFilePath(SunderWorkerProtocol.RequiredString(root, "filePath", 4096));
                    _ = ReadContentRegistrationOptions(SunderWorkerProtocol.RequiredValue(root, "options"));
                }
                return;
            case "worker.payload-allocate":
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "length");
                if (SunderWorkerProtocol.RequiredNonNegativeInt64(root, "length") > _policy.MaxWorkerFilePayloadBytes)
                    throw new SunderWorkerProtocolException("Process worker payload allocation exceeds the configured limit.");
                return;
            case "worker.report-invariant-violation":
                _ = ReadOptionalWorkerCallScopeId(root, "type", "id", "endpointReference", "exceptionMessage");
                _ = SunderWorkerProtocol.RequiredString(root, "endpointReference", 256);
                _ = SunderWorkerProtocol.RequiredString(root, "exceptionMessage", 512);
                return;
            default:
                throw new SunderWorkerProtocolException($"Unknown worker Host-call type '{type}'.");
        }
    }
}
