using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunder.Sdk.Worker.Internal;

internal sealed partial class WorkerRpcClient
{
    private const int MaximumProviderExceptionTypeCharacters = 256;

    internal async ValueTask WriteProviderFaultDiagnosticAsync(
        string invocationId,
        string providerId,
        string serviceId,
        string methodId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var exceptionType = GetSafeProviderExceptionType(exception);
        var exceptionFingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(exceptionType)))
            .ToLowerInvariant();
        var value = await UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.provider-fault-diagnostic");
                writer.WriteString("id", id);
                writer.WriteString("invocationId", invocationId);
                writer.WriteString("providerId", providerId);
                writer.WriteString("serviceId", serviceId);
                writer.WriteString("methodId", methodId);
                writer.WriteString("exceptionType", exceptionType);
                writer.WriteString("exceptionFingerprint", exceptionFingerprint);
                writer.WriteEndObject();
            }),
            cancellationToken,
            logging: true,
            awaitHostTerminalAfterCancellation: true).ConfigureAwait(false);
        if (value.ValueKind != JsonValueKind.Null)
        {
            throw FailProtocol(new WorkerProtocolException(
                "Host returned a value for worker.provider-fault-diagnostic."));
        }
    }

    internal async ValueTask WritePackageLogAsync(
        WorkerLogEntry entry,
        CancellationToken cancellationToken)
    {
        var value = await UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.logging-write");
                writer.WriteString("id", id);
                writer.WriteString("channel", entry.Channel);
                writer.WriteNumber("level", (int)entry.Level);
                if (entry.Category is null) writer.WriteNull("category");
                else writer.WriteString("category", entry.Category);
                writer.WriteNumber("eventId", entry.EventId);
                if (entry.EventName is null) writer.WriteNull("eventName");
                else writer.WriteString("eventName", entry.EventName);
                writer.WriteString("message", entry.Message);
                writer.WritePropertyName("attributes");
                writer.WriteStartObject();
                foreach (var attribute in entry.Attributes)
                {
                    writer.WritePropertyName(attribute.Key);
                    WriteAttributeValue(writer, attribute.Value);
                }
                writer.WriteEndObject();
                writer.WritePropertyName("exceptions");
                writer.WriteStartArray();
                foreach (var exception in entry.Exceptions)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", exception.Type);
                    writer.WriteString("message", exception.Message);
                    if (exception.StackTrace is null) writer.WriteNull("stackTrace");
                    else writer.WriteString("stackTrace", exception.StackTrace);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }),
            cancellationToken,
            logging: true,
            awaitHostTerminalAfterCancellation: true).ConfigureAwait(false);
        if (value.ValueKind != JsonValueKind.Null)
        {
            throw FailProtocol(new WorkerProtocolException(
                "Host returned a value for worker.logging-write."));
        }
    }

    internal void WritePackageLogDetached(WorkerLogEntry entry)
    {
        try
        {
            _ = ObservePackageLogAsync(WritePackageLogAsync(entry, CancellationToken.None).AsTask());
        }
        catch
        {
        }
    }

    private static async Task ObservePackageLogAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static void WriteAttributeValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case byte number:
                writer.WriteNumberValue(number);
                break;
            case sbyte number:
                writer.WriteNumberValue(number);
                break;
            case short number:
                writer.WriteNumberValue(number);
                break;
            case ushort number:
                writer.WriteNumberValue(number);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case uint number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case ulong number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            default:
                throw new WorkerProtocolException("A worker log attribute was not normalized to a scalar value.");
        }
    }

    private static string GetSafeProviderExceptionType(Exception exception)
    {
        var source = exception.GetType().FullName ?? exception.GetType().Name;
        var result = new StringBuilder(Math.Min(source.Length, MaximumProviderExceptionTypeCharacters));
        foreach (var character in source)
        {
            if (result.Length == MaximumProviderExceptionTypeCharacters) break;
            result.Append(IsProviderExceptionTypeCharacter(character) ? character : '_');
        }
        return result.Length == 0 ? "System.Exception" : result.ToString();
    }

    private static bool IsProviderExceptionTypeCharacter(char character)
        => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+' or '`';
}
