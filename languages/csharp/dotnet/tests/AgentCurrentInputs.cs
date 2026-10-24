using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Core.TestSupport;

// Test/orchestration input reader, linked by consumers; not a product SDK surface.
// Authored expectations and reviewed byte records are separate from observed outputs.
internal sealed class AgentCurrentInputs
{
    internal const string InventoryPath = "compatibility/agent-current-inventory.json";
    internal const string ContractsPath = "compatibility/agent-current-contracts.json";
    internal const string AgentId = "younics.ai.agent";
    internal const string StackId = "sunder.stack.contributor";
    private readonly Dictionary<string, SunderRpcContractDescriptor> _descriptors;

    private AgentCurrentInputs(byte[] inventoryBytes, byte[] recordBytes)
    {
        InventorySha256 = Convert.ToHexStringLower(SHA256.HashData(inventoryBytes));
        ContractsSha256 = Convert.ToHexStringLower(SHA256.HashData(recordBytes));
        Expected = JsonSerializer.Deserialize<Inventory>(inventoryBytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Current Agent inventory is empty.");
        Require(Expected.SchemaVersion == 1 && Expected.PackageVersion == "2.0.0" && Expected.PublicVersion == "1.0.0",
            "Unsupported current Agent inventory/version.");
        Require(Packages.Length == 15 && Packages.Count(package => package.HasApp) == 12
                && Packages.Sum(package => package.Providers.Count) == 64, "Current Agent inventory requires 15 Worker / 12 App packages and 64 providers.");
        foreach (var values in new[] { Packages.Select(p => p.Key), Packages.Select(p => p.PackageId), Packages.Select(p => p.ProjectPath) })
            Require(values.All(value => !string.IsNullOrWhiteSpace(value)) && values.Distinct(StringComparer.Ordinal).Count() == 15,
                "Current Agent package keys, IDs and projects must be distinct.");
        Require(Packages.Single(p => p.Key == "agent-builder").PackageId == "younics.sunder.package.maker",
            "Package Maker must remain an explicit member of the current inventory.");
        Require(Packages.Single(p => p.Key == "agent").PackageId == AgentId, "Current public owner differs.");
        Require(Expected.Contracts.Length == 31 && Expected.Contracts.Count(c => c.Exposure == "public") == 18
                && Expected.Contracts.Count(c => c.Exposure == "package") == 13
                && Expected.Contracts.Select(c => c.ContractId).Distinct(StringComparer.Ordinal).Count() == 31,
            "Current Agent inventory requires 18 public / 13 private contracts.");
        Require(Expected.Platform == new Platform(StackId, "platform", "sunder.core", "2.0.0"), "Current platform authority differs.");
        foreach (var package in Packages)
        {
            SafeRelative(package.ProjectPath);
            var parts = package.ProjectPath.Split('/');
            Require(parts.Length == 3 && parts[0] == "src" && parts[2] == parts[1] + ".csproj",
                "Current packages require canonical root projects.");
            Require(package.RequiredRuntime.Distinct(StringComparer.Ordinal).Count() == package.RequiredRuntime.Length,
                "Duplicate required Runtime expectation.");
            foreach (var id in package.RequiredRuntime)
                Require(Contract(id).Exposure == "public", "Required Runtime contract must be public.");
            foreach (var id in package.Providers.Values)
                Require(id == StackId || Expected.Contracts.Any(c => c.ContractId == id), "Unknown current provider contract.");
        }
        foreach (var contract in Expected.Contracts)
        {
            SafeRelative(contract.Source);
            var owner = Package(contract.PackageId);
            Require(contract.Source.StartsWith(Path.GetDirectoryName(owner.ProjectPath)!.Replace('\\', '/') + "/", StringComparison.Ordinal),
                "Current contract source/owner differs.");
            Require(contract.Exposure != "public" || contract.PackageId == AgentId, "Current public contract authority differs.");
            Require(contract.Methods.Length > 0 && contract.Methods.SequenceEqual(contract.Methods.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                    && contract.Streams.Distinct(StringComparer.Ordinal).Count() == contract.Streams.Length
                    && contract.Streams.All(method => contract.Methods.Contains(method, StringComparer.Ordinal)), "Invalid current method/stream expectation.");
        }
        using var records = JsonDocument.Parse(recordBytes);
        Require(records.RootElement.GetProperty("schemaVersion").GetInt32() == 1, "Unsupported current contract records.");
        Require(records.RootElement.GetProperty("inventorySha256").GetString() == InventorySha256,
            "Current contract records do not authenticate the expected inventory bytes.");
        var entries = records.RootElement.GetProperty("contracts").EnumerateArray().ToArray();
        SameSet(entries.Select(entry => entry.GetProperty("contractId").GetString()!), Expected.Contracts.Select(c => c.ContractId), "current contract records");
        _descriptors = entries.ToDictionary(entry => entry.GetProperty("contractId").GetString()!, entry =>
        {
            var expected = Contract(entry.GetProperty("contractId").GetString()!);
            var bytes = Encoding.UTF8.GetBytes(entry.GetProperty("canonicalDescriptor").GetString()!);
            var descriptor = SunderRpcContractDescriptor.Parse(bytes);
            Require(entry.GetProperty("packageId").GetString() == expected.PackageId && entry.GetProperty("exposure").GetString() == expected.Exposure
                    && descriptor.ContractId == expected.ContractId && descriptor.Version == Version(expected)
                    && descriptor.SchemaProfile == SunderRpcProtocol.ValueSchemaProfile
                    && bytes.AsSpan().SequenceEqual(descriptor.GetCanonicalUtf8())
                    && entry.GetProperty("descriptorSha256").GetString() == descriptor.Sha256
                    && entry.GetProperty("abiSha256").GetString() == descriptor.AbiSha256, $"Current descriptor tuple differs: {expected.ContractId}.");
            Require(descriptor.Services.Count == 1 && descriptor.Services[0].ServiceId == expected.Service, "Current service identity differs.");
            SameSet(descriptor.Services[0].Methods.Select(method => method.MethodId), expected.Methods, "current method inventory");
            SameSet(descriptor.Services[0].Methods.Where(method => method.Kind == SunderRpcMethodKind.ServerStream).Select(method => method.MethodId),
                expected.Streams, "current stream inventory");
            return descriptor;
        }, StringComparer.Ordinal);
    }

    internal Inventory Expected { get; }
    internal ExpectedPackage[] Packages => Expected.Packages;
    internal string InventorySha256 { get; }
    internal string ContractsSha256 { get; }
    internal HashSet<string> AppPackageIds => Packages.Where(p => p.HasApp).Select(p => p.PackageId).ToHashSet(StringComparer.Ordinal);
    internal ExpectedPackage Package(string id) => Packages.Single(p => p.PackageId == id);
    internal ExpectedContract Contract(string id) => Expected.Contracts.Single(c => c.ContractId == id);
    internal string Version(ExpectedContract contract) => contract.ContractVersion ?? (contract.Exposure == "public" ? Expected.PublicVersion : Expected.PackageVersion);
    internal SunderRpcContractDescriptor Descriptor(string id) => _descriptors[id];

    internal static async Task<AgentCurrentInputs> LoadAsync(string agentRoot, CancellationToken token)
        => new(await ReadRequiredAsync(agentRoot, InventoryPath, token), await ReadRequiredAsync(agentRoot, ContractsPath, token));

    internal static AgentCurrentInputs Load(string agentRoot)
        => new(File.ReadAllBytes(RequiredPath(agentRoot, InventoryPath)), File.ReadAllBytes(RequiredPath(agentRoot, ContractsPath)));

    internal void ValidatePackageInventory(JsonElement actual)
    {
        Require(actual.GetProperty("schemaVersion").GetInt32() == 1 && actual.GetProperty("releaseVersion").GetString() == Expected.PackageVersion,
            "Observed current package inventory version differs.");
        var packages = actual.GetProperty("packages").EnumerateArray().ToArray();
        SameSet(packages.Select(p => p.GetProperty("packageId").GetString()!), Packages.Select(p => p.PackageId), "current package inventory");
        SameSet(actual.GetProperty("capabilities").EnumerateObject().Select(p => p.Name), Packages.Select(p => p.PackageId), "current capability owners");
        foreach (var expected in Packages)
        {
            var package = packages.Single(p => p.GetProperty("packageId").GetString() == expected.PackageId);
            Require(package.GetProperty("key").GetString() == expected.Key && package.GetProperty("projectPath").GetString() == expected.ProjectPath
                    && package.GetProperty("artifactType").GetString() == "sunderpkg", "Observed package/project pair differs from current expectations.");
            SameSet(package.GetProperty("providerIds").EnumerateArray().Select(p => p.GetString()!), expected.Providers.Keys, "current provider inventory");
        }
    }

    private static Task<byte[]> ReadRequiredAsync(string root, string path, CancellationToken token)
        => File.ReadAllBytesAsync(RequiredPath(root, path), token);

    private static string RequiredPath(string root, string path)
    {
        var full = Path.Combine(root, path);
        Require(File.Exists(full), $"Missing reviewed CURRENT input: {full}. Supply the reviewed inventory and qualified contract records, not generated outputs or historical captures.");
        return full;
    }

    private static void SafeRelative(string path) => Require(!Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':')
        && path.Split('/').All(part => part.Length > 0 && part is not "." and not ".."), "Unsafe current source/project path.");
    private static void SameSet(IEnumerable<string> actual, IEnumerable<string> expected, string label)
        => Require(actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)), $"Unexpected {label}.");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    internal sealed record ExpectedPackage(string Key, string PackageId, string ProjectPath, bool HasApp,
        bool PackageApi, string[] RequiredRuntime, Dictionary<string, string> Providers);
    internal sealed record ExpectedContract(string ContractId, string PackageId, string Exposure, string Source,
        string Service, string[] Methods, string[] Streams, bool Runtime, bool App, string? ContractVersion = null);
    internal sealed record Platform(string ContractId, string AuthorityKind, string AuthorityId, string Version);
    internal sealed record Inventory(int SchemaVersion, string PackageVersion, string PublicVersion,
        Platform Platform, ExpectedPackage[] Packages, ExpectedContract[] Contracts);
}
