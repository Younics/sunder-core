using System.Text.Json;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Settings;

namespace Sunder.Sdk.Worker.Internal;

internal sealed record WorkerProviderIdentity(
    string ProviderId,
    string ContractId,
    string ContractVersion,
    string ContractSha256);

internal sealed record WorkerContentFileHandle(string HandleId, string FilePath);

internal sealed record WorkerCallScopeIdentity(string ScopeId, DateTimeOffset DeadlineUtc);

internal static partial class WorkerWire
{
    public static WorkerProviderIdentity ReadProviderIdentity(JsonElement value)
    {
        WorkerJson.RequireOnlyProperties(
            value,
            "providerId",
            "contractId",
            "contractVersion",
            "contractSha256");
        return new WorkerProviderIdentity(
            WorkerJson.RequiredString(value, "providerId", 256),
            WorkerJson.RequiredString(value, "contractId", 256),
            WorkerJson.RequiredString(value, "contractVersion", 128),
            WorkerJson.RequiredString(value, "contractSha256", 64));
    }

    public static SunderRpcProviderSnapshot ReadProviderSnapshot(JsonElement value)
    {
        WorkerJson.RequireOnlyProperties(
            value,
            "packageId",
            "packageVersion",
            "providerId",
            "contractId",
            "contractVersion",
            "contractSha256",
            "activationId",
            "activationEpoch",
            "sessionGeneration",
            "endpointReference",
            "catalogRevision",
            "state",
            "faultCode");
        var activationId = WorkerJson.RequiredSafeId(value, "activationId", 64);
        if (!Guid.TryParseExact(activationId, "N", out var activationGuid))
        {
            throw new WorkerProtocolException("Host provider activationId is not canonical.");
        }

        var state = WorkerJson.RequiredString(value, "state", 32) switch
        {
            "active" => SunderRpcProviderState.Active,
            "inactive" => SunderRpcProviderState.Inactive,
            "faulted" => SunderRpcProviderState.Faulted,
            _ => throw new WorkerProtocolException("Host provider state is invalid."),
        };
        var faultCode = WorkerJson.OptionalStringOrNull(value, "faultCode", 128);
        if (faultCode is not null && !WorkerJson.IsSafeErrorCode(faultCode))
        {
            throw new WorkerProtocolException("Host provider faultCode is invalid.");
        }

        return new SunderRpcProviderSnapshot(
            WorkerJson.RequiredString(value, "packageId", 256),
            WorkerJson.RequiredString(value, "packageVersion", 128),
            WorkerJson.RequiredString(value, "providerId", 256),
            WorkerJson.RequiredString(value, "contractId", 256),
            WorkerJson.RequiredString(value, "contractVersion", 128),
            ReadSha256(value, "contractSha256"),
            activationGuid,
            WorkerJson.RequiredNonNegativeInt64(value, "activationEpoch"),
            WorkerJson.RequiredNonNegativeInt64(value, "sessionGeneration"),
            new SunderRpcEndpointReference(WorkerJson.RequiredString(value, "endpointReference", 256)),
            WorkerJson.RequiredNonNegativeInt64(value, "catalogRevision"),
            state,
            faultCode);
    }

    public static SunderRpcCatalogSnapshot ReadCatalogSnapshot(JsonElement value)
    {
        WorkerJson.RequireOnlyProperties(value, "revision", "sequence", "providers", "resetRequired");
        var providersValue = WorkerJson.RequiredValue(value, "providers");
        if (providersValue.ValueKind != JsonValueKind.Array)
        {
            throw new WorkerProtocolException("Host provider catalog providers must be an array.");
        }

        var providers = providersValue.EnumerateArray().Select(ReadProviderSnapshot).ToArray();
        return new SunderRpcCatalogSnapshot(
            WorkerJson.RequiredNonNegativeInt64(value, "revision"),
            WorkerJson.RequiredNonNegativeInt64(value, "sequence"),
            providers,
            WorkerJson.RequiredBoolean(value, "resetRequired"));
    }

    public static SunderRpcCatalogEvent ReadCatalogEvent(JsonElement value)
    {
        WorkerJson.RequireOnlyProperties(value, "revision", "sequence", "kind", "provider");
        var kind = WorkerJson.RequiredString(value, "kind", 32) switch
        {
            "added" => SunderRpcCatalogEventKind.Added,
            "removed" => SunderRpcCatalogEventKind.Removed,
            "activated" => SunderRpcCatalogEventKind.Activated,
            "deactivated" => SunderRpcCatalogEventKind.Deactivated,
            "faulted" => SunderRpcCatalogEventKind.Faulted,
            "reset-required" => SunderRpcCatalogEventKind.ResetRequired,
            _ => throw new WorkerProtocolException("Host provider catalog event kind is invalid."),
        };
        var providerValue = WorkerJson.RequiredValue(value, "provider");
        var provider = providerValue.ValueKind == JsonValueKind.Null
            ? null
            : ReadProviderSnapshot(providerValue);
        return new SunderRpcCatalogEvent(
            WorkerJson.RequiredNonNegativeInt64(value, "revision"),
            WorkerJson.RequiredNonNegativeInt64(value, "sequence"),
            kind,
            provider);
    }

    public static SunderRpcContentReference ReadContentReference(JsonElement value)
    {
        WorkerJson.RequireOnlyProperties(
            value,
            "id",
            "length",
            "sha256",
            "mediaType",
            "fileName",
            "expiresAtUtc",
            "repeatability");
        var repeatability = WorkerJson.RequiredString(value, "repeatability", 32) switch
        {
            "single-use" => SunderRpcContentRepeatability.SingleUse,
            "repeatable" => SunderRpcContentRepeatability.Repeatable,
            _ => throw new WorkerProtocolException("Host RPC content repeatability is invalid."),
        };
        return new SunderRpcContentReference(
            WorkerJson.RequiredSafeId(value, "id"),
            WorkerJson.RequiredNonNegativeInt64(value, "length"),
            ReadSha256(value, "sha256"),
            WorkerJson.RequiredString(value, "mediaType", 128),
            WorkerJson.RequiredString(value, "fileName", 255),
            WorkerJson.RequiredUtcTimestamp(value, "expiresAtUtc"),
            repeatability);
    }

    public static WorkerContentFileHandle ReadContentFileHandle(JsonElement value)
    {
        WorkerJson.RequireOnlyProperties(value, "handleId", "filePath");
        return new WorkerContentFileHandle(
            WorkerJson.RequiredSafeId(value, "handleId"),
            WorkerJson.RequiredString(value, "filePath", 4096));
    }

    public static WorkerCallScopeIdentity ReadCallScopeIdentity(JsonElement value)
    {
        WorkerJson.RequireOnlyProperties(value, "scopeId", "deadlineUtc");
        return new WorkerCallScopeIdentity(
            WorkerJson.RequiredSafeId(value, "scopeId"),
            WorkerJson.RequiredUtcTimestamp(value, "deadlineUtc"));
    }

    public static SunderRpcException ReadHostError(JsonElement value)
    {
        WorkerJson.RequireOnlyProperties(value, "kind", "code", "message");
        var kind = WorkerJson.RequiredString(value, "kind", 64) switch
        {
            "domain" => SunderRpcErrorKind.Domain,
            "permission-denied" => SunderRpcErrorKind.PermissionDenied,
            "not-found" => SunderRpcErrorKind.NotFound,
            "stale-endpoint" => SunderRpcErrorKind.StaleEndpoint,
            "validation" => SunderRpcErrorKind.Validation,
            "deadline-exceeded" => SunderRpcErrorKind.DeadlineExceeded,
            "cancelled" => SunderRpcErrorKind.Cancelled,
            "resource-exhausted" => SunderRpcErrorKind.ResourceExhausted,
            "unavailable" => SunderRpcErrorKind.Unavailable,
            "provider-faulted" => SunderRpcErrorKind.ProviderFaulted,
            "protocol" => SunderRpcErrorKind.Protocol,
            _ => throw new WorkerProtocolException("Host returned an unknown RPC error kind."),
        };
        var code = WorkerJson.RequiredString(value, "code", 128);
        if (!WorkerJson.IsSafeErrorCode(code))
        {
            throw new WorkerProtocolException("Host returned an unsafe RPC error code.");
        }
        var message = WorkerJson.RequiredString(value, "message", 512);
        return new SunderRpcException(new SunderRpcError(kind, code, message), hostAuthenticated: true);
    }

    public static void WriteProviderIdentity(Utf8JsonWriter writer, WorkerProviderIdentity provider)
    {
        writer.WriteStartObject();
        writer.WriteString("providerId", provider.ProviderId);
        writer.WriteString("contractId", provider.ContractId);
        writer.WriteString("contractVersion", provider.ContractVersion);
        writer.WriteString("contractSha256", provider.ContractSha256);
        writer.WriteEndObject();
    }

    public static void WriteContentReference(Utf8JsonWriter writer, SunderRpcContentReference reference)
    {
        writer.WriteStartObject();
        writer.WriteString("id", reference.Id);
        writer.WriteNumber("length", reference.Length);
        writer.WriteString("sha256", reference.Sha256);
        writer.WriteString("mediaType", reference.MediaType);
        writer.WriteString("fileName", reference.FileName);
        writer.WriteString("expiresAtUtc", WorkerJson.FormatUtc(reference.ExpiresAtUtc));
        writer.WriteString(
            "repeatability",
            reference.Repeatability == SunderRpcContentRepeatability.SingleUse ? "single-use" : "repeatable");
        writer.WriteEndObject();
    }

    public static void WriteContentOptions(Utf8JsonWriter writer, SunderRpcContentRegistrationOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("mediaType", options.MediaType);
        writer.WriteString("fileName", options.FileName);
        if (options.Length is { } length) writer.WriteNumber("length", length);
        else writer.WriteNull("length");
        if (options.ExpiresAtUtc is { } expiresAtUtc)
        {
            writer.WriteString("expiresAtUtc", WorkerJson.FormatUtc(expiresAtUtc));
        }
        else
        {
            writer.WriteNull("expiresAtUtc");
        }
        writer.WriteString(
            "repeatability",
            options.Repeatability == SunderRpcContentRepeatability.SingleUse ? "single-use" : "repeatable");
        writer.WriteNumber("maximumUses", options.MaximumUses);
        writer.WriteEndObject();
    }

    public static void WriteSettingsSchema(Utf8JsonWriter writer, PackageSettingsSchema schema)
    {
        writer.WriteStartObject();
        if (schema.Summary is null) writer.WriteNull("summary");
        else writer.WriteString("summary", schema.Summary);
        writer.WritePropertyName("sections");
        writer.WriteStartArray();
        foreach (var section in schema.Sections)
        {
            writer.WriteStartObject();
            writer.WriteString("sectionId", section.SectionId);
            writer.WriteString("title", section.Title);
            if (section.Description is null) writer.WriteNull("description");
            else writer.WriteString("description", section.Description);
            writer.WritePropertyName("fields");
            writer.WriteStartArray();
            foreach (var field in section.Fields)
            {
                writer.WriteStartObject();
                writer.WriteString("key", field.Key);
                writer.WriteString("label", field.Label);
                writer.WriteString("kind", field.Kind switch
                {
                    PackageSettingsFieldKind.Text => "text",
                    PackageSettingsFieldKind.Secret => "secret",
                    PackageSettingsFieldKind.Boolean => "boolean",
                    PackageSettingsFieldKind.Select => "select",
                    _ => throw new WorkerProtocolException("Worker settings schema contains an invalid field kind."),
                });
                if (field.Description is null) writer.WriteNull("description");
                else writer.WriteString("description", field.Description);
                writer.WriteBoolean("isRequired", field.IsRequired);
                if (field.Placeholder is null) writer.WriteNull("placeholder");
                else writer.WriteString("placeholder", field.Placeholder);
                if (field.DefaultValue is null) writer.WriteNull("defaultValue");
                else writer.WriteString("defaultValue", field.DefaultValue);
                writer.WritePropertyName("options");
                writer.WriteStartArray();
                foreach (var option in field.Options)
                {
                    writer.WriteStartObject();
                    writer.WriteString("value", option.Value);
                    writer.WriteString("label", option.Label);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static string ReadSha256(JsonElement value, string propertyName)
    {
        var sha256 = WorkerJson.RequiredString(value, propertyName, 64);
        if (sha256.Length != 64 || sha256.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new WorkerProtocolException($"Host envelope property '{propertyName}' is not a SHA-256 value.");
        }
        return sha256;
    }
}
