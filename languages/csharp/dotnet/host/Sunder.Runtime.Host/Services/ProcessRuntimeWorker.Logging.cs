using System.Collections;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Host.Infrastructure.Logging;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class ProcessRuntimeWorker
{
    private async Task<bool> TryRunWorkerLoggingCallAsync(
        string type,
        JsonElement root,
        WorkerCall call)
    {
        if (!IsWorkerLogging(type)) return false;

        RequireV2WorkerCall(type);
        if (type == "worker.provider-fault-diagnostic")
        {
            await RunWorkerProviderFaultDiagnosticAsync(root, call).ConfigureAwait(false);
            return true;
        }

        SunderWorkerProtocol.RequireOnlyProperties(
            root,
            "type",
            "id",
            "channel",
            "level",
            "category",
            "eventId",
            "eventName",
            "message",
            "attributes",
            "exceptions");
        var channel = SunderWorkerProtocol.RequiredString(root, "channel", 16);
        var level = ReadWorkerLogLevel(root);
        var category = ReadNullableWorkerLogString(root, "category", 256);
        var eventId = ReadWorkerLogEventId(root);
        var eventName = ReadNullableWorkerLogString(root, "eventName", 256);
        var message = ReadWorkerLogString(root, "message", 16384, allowEmpty: true);
        var attributes = ReadWorkerLogAttributes(root);
        var exception = ReadWorkerLogException(root);

        call.Cancellation.Token.ThrowIfCancellationRequested();
        switch (channel)
        {
            case "event":
                if (category is not null || eventId != 0 || string.IsNullOrWhiteSpace(eventName))
                {
                    throw new SunderWorkerProtocolException(
                        "Structured worker log events require an event name and cannot specify a logger category or event id.");
                }
                await _packageContext.Logging.Events.WriteAsync(
                    level,
                    eventName,
                    message,
                    attributes,
                    exception,
                    call.Cancellation.Token).ConfigureAwait(false);
                break;
            case "logger":
                if (string.IsNullOrWhiteSpace(category))
                {
                    throw new SunderWorkerProtocolException(
                        "Conventional worker log entries require a logger category.");
                }
                var logger = _packageContext.Logging.LoggerFactory.CreateLogger(category);
                var state = new WorkerLogState(message, attributes);
                logger.Log(
                    ToMicrosoftLogLevel(level),
                    new EventId(eventId, eventName),
                    state,
                    exception,
                    static (value, _) => value.Message);
                break;
            default:
                throw new SunderWorkerProtocolException(
                    $"Worker logging channel '{channel}' is unsupported.");
        }

        QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
        return true;
    }

    private async Task RunWorkerProviderFaultDiagnosticAsync(JsonElement root, WorkerCall workerCall)
    {
        SunderWorkerProtocol.RequireOnlyProperties(
            root,
            "type",
            "id",
            "invocationId",
            "providerId",
            "serviceId",
            "methodId",
            "exceptionType",
            "exceptionFingerprint");
        var invocationId = SunderWorkerProtocol.RequiredString(root, "invocationId", 128);
        var providerId = SunderWorkerProtocol.RequiredString(root, "providerId", 256);
        var serviceId = SunderWorkerProtocol.RequiredString(root, "serviceId", 128);
        var methodId = SunderWorkerProtocol.RequiredString(root, "methodId", 128);
        var exceptionType = SunderWorkerProtocol.RequiredString(
            root,
            "exceptionType",
            RuntimeRpcFailureContextStore.MaximumExceptionTypeCharacters);
        if (!exceptionType.All(IsProviderExceptionTypeCharacter))
        {
            throw new SunderWorkerProtocolException(
                "Worker provider-fault exceptionType contains unsupported characters.");
        }
        var exceptionFingerprint = SunderWorkerProtocol.RequiredString(
            root,
            "exceptionFingerprint",
            RuntimeRpcFailureContextStore.MaximumExceptionFingerprintCharacters);
        var expectedFingerprint = RuntimeRpcFailureContextStore.CreateExceptionFingerprint(exceptionType);
        if (!string.Equals(exceptionFingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            throw new SunderWorkerProtocolException(
                "Worker provider-fault exception fingerprint does not match its bounded exception type.");
        }

        HostCall invocation;
        var invocationCancelled = false;
        lock (_callGate)
        {
            if (!_hostCalls.TryGetValue(invocationId, out invocation!) || invocation.Terminal)
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker sent a provider-fault diagnostic for inactive invocation '{invocationId}'.");
            }
            if (!string.Equals(providerId, invocation.ProviderId, StringComparison.Ordinal)
                || !string.Equals(providerId, invocation.Context.Provider.ProviderId, StringComparison.Ordinal)
                || !string.Equals(serviceId, invocation.ServiceId, StringComparison.Ordinal)
                || !string.Equals(methodId, invocation.MethodId, StringComparison.Ordinal))
            {
                throw new SunderWorkerProtocolException(
                    "Process worker provider-fault diagnostic does not match the Host-owned invocation identity.");
            }
            if (invocation.ProviderFaultDiagnosticPending || invocation.ProviderExceptionType is not null)
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker sent a duplicate provider-fault diagnostic for invocation '{invocationId}'.");
            }
            if (invocation.Cancelled)
            {
                invocationCancelled = true;
            }
            else
            {
                invocation.ProviderFaultDiagnosticPending = true;
            }
        }
        if (invocationCancelled)
        {
            QueueEnvelope(new { type = "host.result", id = workerCall.Id, value = (object?)null });
            return;
        }

        try
        {
            workerCall.Cancellation.Token.ThrowIfCancellationRequested();
            var context = invocation.Context;
            try
            {
                await _packageContext.Logging.Events.WriteAsync(
                    PackageLogLevel.Error,
                    "runtime.rpc.provider-fault",
                    "A process RPC provider handler faulted.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["rpc.invocation_id"] = invocation.Id,
                        ["rpc.invocation_kind"] = invocation.Kind == HostCallKind.Unary ? "unary" : "server-stream",
                        ["rpc.caller_package_id"] = context.CallerPackageId,
                        ["rpc.caller_package_version"] = context.CallerPackageVersion,
                        ["rpc.provider_package_id"] = context.Provider.PackageId,
                        ["rpc.provider_package_version"] = context.Provider.PackageVersion,
                        ["rpc.provider_id"] = context.Provider.ProviderId,
                        ["rpc.contract_id"] = context.Provider.ContractId,
                        ["rpc.provider_activation_id"] = context.Provider.ActivationId,
                        ["rpc.service_id"] = invocation.ServiceId,
                        ["rpc.method_id"] = invocation.MethodId,
                        ["rpc.error_kind"] = nameof(SunderRpcErrorKind.ProviderFaulted),
                        ["rpc.error_code"] = "rpc.provider.handler-fault",
                        ["rpc.exception_type"] = exceptionType,
                        ["rpc.exception_fingerprint"] = exceptionFingerprint,
                    },
                    cancellationToken: workerCall.Cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (workerCall.Cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Host-mediated provider-fault diagnostic persistence failed.",
                    exception);
            }

            lock (_callGate)
            {
                if (!_hostCalls.TryGetValue(invocationId, out var current)
                    || !ReferenceEquals(current, invocation)
                    || invocation.Terminal
                    || !invocation.ProviderFaultDiagnosticPending)
                {
                    throw new SunderWorkerProtocolException(
                        $"Process invocation '{invocationId}' changed while its provider-fault diagnostic was persisted.");
                }
                invocation.ProviderExceptionType = exceptionType;
                invocation.ProviderExceptionFingerprint = exceptionFingerprint;
                invocation.ProviderFaultDiagnosticPending = false;
            }
        }
        catch
        {
            lock (_callGate)
            {
                invocation.ProviderFaultDiagnosticPending = false;
            }
            throw;
        }

        QueueEnvelope(new { type = "host.result", id = workerCall.Id, value = (object?)null });
    }

    private static PackageLogLevel ReadWorkerLogLevel(JsonElement root)
    {
        var value = SunderWorkerProtocol.RequiredValue(root, "level");
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number)
            || !Enum.IsDefined((PackageLogLevel)number))
        {
            throw new SunderWorkerProtocolException(
                "Worker logging level must be a supported integer value.");
        }
        return (PackageLogLevel)number;
    }

    private static int ReadWorkerLogEventId(JsonElement root)
    {
        var value = SunderWorkerProtocol.RequiredValue(root, "eventId");
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var eventId))
        {
            throw new SunderWorkerProtocolException("Worker logging eventId must be an integer.");
        }
        return eventId;
    }

    private static string ReadWorkerLogString(
        JsonElement root,
        string propertyName,
        int maximumCharacters,
        bool allowEmpty = false)
    {
        var value = SunderWorkerProtocol.RequiredValue(root, propertyName);
        if (value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text
            || !allowEmpty && string.IsNullOrEmpty(text)
            || text.Length > maximumCharacters)
        {
            throw new SunderWorkerProtocolException(
                $"Worker logging property '{propertyName}' must be a bounded string.");
        }
        return text;
    }

    private static string? ReadNullableWorkerLogString(
        JsonElement root,
        string propertyName,
        int maximumCharacters)
    {
        var value = SunderWorkerProtocol.RequiredValue(root, propertyName);
        if (value.ValueKind == JsonValueKind.Null) return null;
        return ReadWorkerLogString(root, propertyName, maximumCharacters, allowEmpty: true);
    }

    private static IReadOnlyDictionary<string, object?> ReadWorkerLogAttributes(JsonElement root)
    {
        var value = SunderWorkerProtocol.RequiredValue(root, "attributes");
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new SunderWorkerProtocolException("Worker logging attributes must be an object.");
        }
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (attributes.Count == 64
                || string.IsNullOrWhiteSpace(property.Name)
                || property.Name.Length > 128
                || !attributes.TryAdd(property.Name, ReadWorkerLogAttributeValue(property.Value)))
            {
                throw new SunderWorkerProtocolException(
                    "Worker logging attributes exceed their count, key, or uniqueness bounds.");
            }
        }
        return attributes;
    }

    private static object? ReadWorkerLogAttributeValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.GetRawText().Length > 4096)
        {
            throw new SunderWorkerProtocolException(
                "Worker logging numeric attribute values must be bounded.");
        }
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String when value.GetString()!.Length <= 4096 => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.Number when value.TryGetDouble(out var floating) && double.IsFinite(floating) => floating,
            _ => throw new SunderWorkerProtocolException(
                "Worker logging attribute values must be bounded JSON scalars."),
        };
    }

    private static Exception? ReadWorkerLogException(JsonElement root)
    {
        var value = SunderWorkerProtocol.RequiredValue(root, "exceptions");
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 5)
        {
            throw new SunderWorkerProtocolException(
                "Worker logging exceptions must be a bounded array.");
        }
        var descriptors = new List<(string Type, string Message, string? StackTrace)>();
        foreach (var item in value.EnumerateArray())
        {
            SunderWorkerProtocol.RequireOnlyProperties(item, "type", "message", "stackTrace");
            descriptors.Add((
                ReadWorkerLogString(item, "type", 512),
                ReadWorkerLogString(item, "message", 4096, allowEmpty: true),
                ReadNullableWorkerLogString(item, "stackTrace", 32768)));
        }

        Exception? exception = null;
        for (var index = descriptors.Count - 1; index >= 0; index--)
        {
            var descriptor = descriptors[index];
            exception = new RemotePackageLogException(
                descriptor.Type,
                descriptor.Message,
                descriptor.StackTrace,
                exception);
        }
        return exception;
    }

    private static LogLevel ToMicrosoftLogLevel(PackageLogLevel level)
        => level switch
        {
            PackageLogLevel.Trace => LogLevel.Trace,
            PackageLogLevel.Debug => LogLevel.Debug,
            PackageLogLevel.Information => LogLevel.Information,
            PackageLogLevel.Warning => LogLevel.Warning,
            PackageLogLevel.Error => LogLevel.Error,
            PackageLogLevel.Critical => LogLevel.Critical,
            _ => throw new ArgumentOutOfRangeException(nameof(level)),
        };

    private static bool IsProviderExceptionTypeCharacter(char character)
        => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+' or '`';

    private sealed class WorkerLogState(
        string message,
        IReadOnlyDictionary<string, object?> attributes)
        : IEnumerable<KeyValuePair<string, object?>>
    {
        public string Message { get; } = message;

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
            => attributes.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
