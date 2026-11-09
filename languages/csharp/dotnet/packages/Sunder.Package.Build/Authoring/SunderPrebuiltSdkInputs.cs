using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using static Sunder.Package.Build.Authoring.SunderPrebuiltRoleInputs;

namespace Sunder.Package.Build.Authoring;

/// <summary>Separates native SDK diagnostics/serialization from portable emitting inputs.</summary>
internal static class SunderPrebuiltSdkInputs
{
    internal const string HostProperty = "SdkHostRuntimeIdentifier";
    internal const string DiagnosticBytes = "sdk-diagnostic-bytes:";
    internal const string DiagnosticIdentity = "sdk-diagnostic-identity:";
    internal const string ImportXml = "sdk-import-xml:";
    private static readonly string[] DiagnosticNames = ["Microsoft.CodeAnalysis.NetAnalyzers.dll", "Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll"];

    internal static bool IsSdkImport(string path, string sdkDirectory)
        => sdkDirectory.Length != 0 && GeneratedOutputPathSafety.IsDescendantOf(path, Full(sdkDirectory))
            && Path.GetExtension(path) is ".props" or ".targets";

    internal static SunderPrebuiltFile XmlInput(string path, string identity, SunderPrebuiltFile raw, CancellationToken token = default)
    {
        // XML parsers normalize CRLF/CR before interpreting attributes or text,
        // including inline tasks. Keep the original bytes as a separate record.
        var bytes = StableBytes(path, raw, token);
        var text = new UTF8Encoding(false, true).GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var normalized = Encoding.UTF8.GetBytes(text);
        token.ThrowIfCancellationRequested();
        return new(identity, normalized.Length, Hash(normalized));
    }

    internal static SunderPrebuiltFile? DiagnosticInput(string path, string sdkDirectory, SunderPrebuiltFile raw, CancellationToken token = default)
    {
        var name = Path.GetFileName(path);
        if (sdkDirectory.Length == 0 || !DiagnosticNames.Contains(name, StringComparer.Ordinal)
            || Full(path) != Full(Path.Combine(sdkDirectory, "Sdks/Microsoft.NET.Sdk/analyzers", name))) return null;
        var bytes = StableBytes(path, raw, token);
        // A generator, even with an SDK diagnostic filename, is an emitting input
        // and must retain exact-byte comparison across hosts.
        using var reader = new PEReader(new MemoryStream(bytes, writable: false));
        var metadata = reader.GetMetadataReader(); var assembly = metadata.GetAssemblyDefinition();
        foreach (var handle in metadata.TypeReferences)
        {
            token.ThrowIfCancellationRequested();
            var type = metadata.GetTypeReference(handle);
            if (metadata.GetString(type.Namespace) == "Microsoft.CodeAnalysis"
                && metadata.GetString(type.Name) is "GeneratorAttribute" or "ISourceGenerator" or "IIncrementalGenerator") return null;
        }
        if (metadata.GetString(assembly.Name) + ".dll" != name) return null;
        var identity = Write(new
        {
            Name = metadata.GetString(assembly.Name), Version = assembly.Version.ToString(),
            Culture = metadata.GetString(assembly.Culture), PublicKey = Convert.ToHexStringLower(metadata.GetBlobBytes(assembly.PublicKey)),
        });
        return new(DiagnosticIdentity + name, identity.Length, Hash(identity));
    }

    internal static void RequireEqual(SunderPrebuiltFrame current, SunderPrebuiltFrame native)
    {
        var missing = native.ExternalInputs.Select(file => file.Path).Except(current.ExternalInputs.Select(file => file.Path), StringComparer.Ordinal).ToArray();
        var extra = current.ExternalInputs.Select(file => file.Path).Except(native.ExternalInputs.Select(file => file.Path), StringComparer.Ordinal).ToArray();
        if (missing.Length != 0 || extra.Length != 0) throw Invalid("External input membership differs: missing=" + string.Join(",", missing.Take(5)) + "; extra=" + string.Join(",", extra.Take(5)));
        if (!current.ExternalInputs.Select(file => file.Path).SequenceEqual(native.ExternalInputs.Select(file => file.Path), StringComparer.Ordinal))
            throw Invalid("External input order or multiplicity differs.");
        var host = current.Properties.GetValueOrDefault(HostProperty);
        var nativeHost = native.Properties.GetValueOrDefault(HostProperty);
        if (host == nativeHost || !Rids.Contains(host) || !Rids.Contains(nativeHost)
            || !current.Properties.TryGetValue("SdkVersion", out var sdk) || string.IsNullOrEmpty(sdk)
            || native.Properties.GetValueOrDefault("SdkVersion") != sdk)
        {
            Equal(current, native, "Reviewed authoring inputs");
            return;
        }
        // Both original frames remain intact in evidence. Only host SDK diagnostic
        // bytes and XML serialization differ; generators, Sunder tools, sources,
        // restored selections and the portable SDK records remain exact.
        Equal(Portable(current), Portable(native), "Reviewed authoring inputs");
    }

    private static SunderPrebuiltFrame Portable(SunderPrebuiltFrame frame)
    {
        var properties = new SortedDictionary<string, string>(frame.Properties, StringComparer.Ordinal);
        properties.Remove(HostProperty);
        var records = frame.ExternalInputs.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var portable = frame.ExternalInputs.Where(file =>
        {
            var diagnostic = DiagnosticNames.Any(name => file.Path == DiagnosticBytes + name
                && records.ContainsKey(DiagnosticIdentity + name));
            var import = file.Path.StartsWith("import:", StringComparison.Ordinal)
                && records.ContainsKey(ImportXml + file.Path["import:".Length..]);
            if (!diagnostic && !import) return true;
            RequireHash(file.Sha256);
            if (file.Bytes is < 0 or > 64 * 1024 * 1024) throw Invalid("Invalid native SDK input size.");
            return false;
        }).ToArray();
        return frame with { Properties = properties, ExternalInputs = portable };
    }

    private static byte[] StableBytes(string path, SunderPrebuiltFile raw, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, 64 * 1024 * 1024, "qualified SDK input");
        token.ThrowIfCancellationRequested();
        if (bytes.LongLength != raw.Bytes || Hash(bytes) != raw.Sha256) throw Invalid("An SDK input changed during capture.");
        return bytes;
    }
}
