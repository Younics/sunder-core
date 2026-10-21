using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Format;

namespace Sunder.Runtime.Host.Services;

internal static class DevelopmentWorkerContentIdentity
{
    // The entire manifest (including target/security/import metadata), contracts,
    // shared payload and every Runtime target remain pinned. Only App payload bytes
    // can differ. Input must be the index of a fully validated canonical shadow.
    internal static string Create(SunderPackageContentIndex validatedIndex)
    {
        var files = (validatedIndex.Files ?? throw new InvalidDataException("Missing validated package content index."))
            .Where(entry => !SunderPackageFormat.TryClassifyPayloadPath(entry.Path!, out _, out var role, out _, out _)
                || role != SunderPackageFormat.AppHostRole)
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Domain = "sunder-development-worker-continuation-v1",
            validatedIndex.SchemaVersion,
            Files = files,
        })));
    }
}
