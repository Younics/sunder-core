using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sunder.Package.Format;

internal static class SunderExecutionRuntimeCanonicalJson
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        MaxDepth = 64,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static SunderExecutionRuntimeManifest? DeserializeManifest(ReadOnlySpan<byte> bytes)
        => Deserialize<SunderExecutionRuntimeManifest>(bytes);

    public static SunderExecutionRuntimeContentIndex? DeserializeContentIndex(ReadOnlySpan<byte> bytes)
        => Deserialize<SunderExecutionRuntimeContentIndex>(bytes);

    public static SunderExecutionRuntimeNativeAttestation? DeserializeNativeAttestation(ReadOnlySpan<byte> bytes)
        => Deserialize<SunderExecutionRuntimeNativeAttestation>(bytes);

    public static byte[] SerializeManifest(SunderExecutionRuntimeManifest manifest)
        => Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", manifest.SchemaVersion);
            WriteString(writer, "id", manifest.Id);
            WriteString(writer, "version", manifest.Version);
            writer.WriteNumber("packagingRevision", manifest.PackagingRevision);
            WriteString(writer, "rid", manifest.Rid);
            WriteString(writer, "moduleAbi", manifest.ModuleAbi);
            WriteString(writer, "workerProtocol", manifest.WorkerProtocol);
            writer.WritePropertyName("launch");
            WriteLaunch(writer, manifest.Launch);
            WriteString(writer, "contentIndex", manifest.ContentIndex);
            writer.WritePropertyName("upstream");
            WriteUpstream(writer, manifest.Upstream);
            writer.WritePropertyName("provenance");
            WriteProvenance(writer, manifest.Provenance);
            writer.WritePropertyName("licenses");
            WriteStrings(writer, manifest.Licenses);
            writer.WritePropertyName("signing");
            WriteSigning(writer, manifest.Signing);
            writer.WriteEndObject();
        });

    public static byte[] SerializeContentIndex(SunderExecutionRuntimeContentIndex contentIndex)
        => Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", contentIndex.SchemaVersion);
            writer.WritePropertyName("files");
            if (contentIndex.Files is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartArray();
                foreach (var file in contentIndex.Files)
                {
                    if (file is null)
                    {
                        writer.WriteNullValue();
                        continue;
                    }

                    writer.WriteStartObject();
                    WriteString(writer, "path", file.Path);
                    WriteString(writer, "sha256", file.Sha256);
                    writer.WriteNumber("size", file.Size);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        });

    public static byte[] SerializeNativeAttestation(SunderExecutionRuntimeNativeAttestation attestation)
        => SerializeNativeAttestation(attestation, includeSignature: true);

    internal static byte[] SerializeNativeAttestationStatement(SunderExecutionRuntimeNativeAttestation attestation)
        => SerializeNativeAttestation(attestation, includeSignature: false);

    private static byte[] SerializeNativeAttestation(
        SunderExecutionRuntimeNativeAttestation attestation,
        bool includeSignature)
        => Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", attestation.SchemaVersion);
            WriteString(writer, "runtimeId", attestation.RuntimeId);
            WriteString(writer, "runtimeVersion", attestation.RuntimeVersion);
            writer.WriteNumber("packagingRevision", attestation.PackagingRevision);
            WriteString(writer, "rid", attestation.Rid);
            WriteString(writer, "upstreamSha256", attestation.UpstreamSha256);
            writer.WritePropertyName("workflow");
            WriteWorkflow(writer, attestation.Workflow);
            writer.WritePropertyName("toolchain");
            WriteToolchain(writer, attestation.Toolchain);
            writer.WritePropertyName("launcherSmoke");
            WriteLauncherSmoke(writer, attestation.LauncherSmoke);
            writer.WritePropertyName("platform");
            WritePlatform(writer, attestation.Platform);
            writer.WritePropertyName("signing");
            WriteAttestationSigning(writer, attestation.Signing, includeSignature);
            writer.WriteEndObject();
        });

    private static T? Deserialize<T>(ReadOnlySpan<byte> bytes)
    {
        RejectDuplicateProperties(bytes);
        return JsonSerializer.Deserialize<T>(bytes, ReadOptions);
    }

    private static byte[] Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
        var objects = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objects.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    objects.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    objects.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var properties = objects.Peek()
                                     ?? throw new JsonException("A JSON property appeared outside an object.");
                    var propertyName = reader.GetString()!;
                    if (!properties.Add(propertyName))
                    {
                        throw new JsonException($"Duplicate JSON property '{propertyName}' is not allowed.");
                    }
                    break;
            }
        }
    }

    private static void WriteLaunch(Utf8JsonWriter writer, SunderExecutionRuntimeLaunch? launch)
    {
        if (launch is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        WriteString(writer, "executable", launch.Executable);
        writer.WritePropertyName("arguments");
        WriteStrings(writer, launch.Arguments);
        writer.WriteEndObject();
    }

    private static void WriteUpstream(Utf8JsonWriter writer, SunderExecutionRuntimeUpstream? upstream)
    {
        if (upstream is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        WriteString(writer, "name", upstream.Name);
        WriteString(writer, "version", upstream.Version);
        WriteString(writer, "sourceUri", upstream.SourceUri);
        WriteString(writer, "sourceSha256", upstream.SourceSha256);
        writer.WriteEndObject();
    }

    private static void WriteProvenance(Utf8JsonWriter writer, SunderExecutionRuntimeProvenance? provenance)
    {
        if (provenance is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        WriteString(writer, "attestationPath", provenance.AttestationPath);
        WriteString(writer, "attestationSha256", provenance.AttestationSha256);
        WriteString(writer, "attestationKeyId", provenance.AttestationKeyId);
        writer.WriteEndObject();
    }

    private static void WriteSigning(Utf8JsonWriter writer, SunderExecutionRuntimeSigning? signing)
    {
        if (signing is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        WriteString(writer, "algorithm", signing.Algorithm);
        WriteString(writer, "keyId", signing.KeyId);
        WriteString(writer, "signature", signing.Signature);
        writer.WriteEndObject();
    }

    private static void WriteWorkflow(Utf8JsonWriter writer, SunderExecutionRuntimeAttestationWorkflow? workflow)
    {
        if (workflow is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        WriteString(writer, "name", workflow.Name);
        WriteString(writer, "runId", workflow.RunId);
        WriteString(writer, "sourceRevision", workflow.SourceRevision);
        writer.WriteEndObject();
    }

    private static void WriteToolchain(Utf8JsonWriter writer, SunderExecutionRuntimeAttestationToolchain? toolchain)
    {
        if (toolchain is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        WriteString(writer, "name", toolchain.Name);
        WriteString(writer, "version", toolchain.Version);
        writer.WriteEndObject();
    }

    private static void WriteLauncherSmoke(Utf8JsonWriter writer, SunderExecutionRuntimeAttestationLauncherSmoke? smoke)
    {
        if (smoke is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        WriteString(writer, "result", smoke.Result);
        writer.WriteEndObject();
    }

    private static void WritePlatform(Utf8JsonWriter writer, SunderExecutionRuntimeAttestationPlatform? platform)
    {
        if (platform is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        WriteString(writer, "signature", platform.Signature);
        WriteString(writer, "notarization", platform.Notarization);
        writer.WriteEndObject();
    }

    private static void WriteAttestationSigning(
        Utf8JsonWriter writer,
        SunderExecutionRuntimeAttestationSigning? signing,
        bool includeSignature)
    {
        if (signing is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        WriteString(writer, "algorithm", signing.Algorithm);
        WriteString(writer, "keyId", signing.KeyId);
        if (includeSignature)
        {
            WriteString(writer, "signature", signing.Signature);
        }
        writer.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter writer, IReadOnlyList<string?>? values)
    {
        if (values is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        foreach (var value in values)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(value);
            }
        }
        writer.WriteEndArray();
    }

    private static void WriteString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        writer.WritePropertyName(propertyName);
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}
