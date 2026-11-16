using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.Runtime.Host.TestSupport;

internal sealed record AgentFamilyArchiveInput(string PackageId, string FileName, long Length, string Sha256)
{
    internal static AgentFamilyArchiveInput Read(string packageId, string path)
    {
        using var input = File.OpenRead(path);
        return new(packageId, Path.GetFileName(path), input.Length, Convert.ToHexStringLower(SHA256.HashData(input)));
    }
}

internal sealed record AgentFamilyArtifactHandoff(int SchemaVersion, string Profile, bool InstalledAcceptance,
    ActivePackageDescriptor Package, PackageUiSnapshotDescriptor Snapshot, string InventorySha256, string ContractsSha256,
    string CatalogSha256, IReadOnlyList<AgentFamilyArchiveInput> Archives)
{
    internal static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
    internal const string DevelopmentProfile = "unpublished-development-import";

    internal void VerifyInputs(IReadOnlyDictionary<string, string> archives, string catalog, string inventorySha256, string contractsSha256)
    {
        Assert.Equal(2, SchemaVersion);
        Assert.Equal(DevelopmentProfile, Profile);
        Assert.False(InstalledAcceptance);
        Assert.Equal(inventorySha256, InventorySha256);
        Assert.Equal(contractsSha256, ContractsSha256);
        Assert.Equal(Hash(catalog), CatalogSha256);
        Assert.Equal(archives.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => AgentFamilyArchiveInput.Read(pair.Key, pair.Value)), Archives);
        Assert.Equal(PackageSourceKind.Dev, Snapshot.SourceKind);
        Assert.Equal(Package.PackageId, Snapshot.PackageId);
        Assert.Null(Snapshot.ProjectionReference);
    }

    internal static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
}
