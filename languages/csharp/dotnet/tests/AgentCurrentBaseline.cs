using System.IO.Compression;
using System.Text.Json;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Core.Tests;

// Linked only into the Agent artifact consumers. Expectations come from reviewed
// CURRENT inputs, independently of the archives, owned baselines and runtime output.
internal sealed class AgentCurrentBaseline
{
    internal const string RootVariable = "SUNDER_AGENT_CURRENT_BASELINE_ROOT";
    internal const string InventoryPath = AgentCurrentInputs.InventoryPath;
    internal const string ContractsPath = AgentCurrentInputs.ContractsPath;
    internal const string AgentId = "younics.ai.agent";
    internal const string StackId = "sunder.stack.contributor";
    private readonly AgentCurrentInputs _inputs;

    private AgentCurrentBaseline(string root)
    {
        _inputs = AgentCurrentInputs.Load(root);
    }

    private AgentCurrentInputs.Inventory Inventory => _inputs.Expected;
    internal AgentCurrentInputs.ExpectedPackage[] Packages => Inventory.Packages;
    internal string PackageVersion => Inventory.PackageVersion;
    internal string InventorySha256 => _inputs.InventorySha256;
    internal string ContractsSha256 => _inputs.ContractsSha256;
    internal IEnumerable<string> AppPackageIds => Packages.Where(package => package.HasApp).Select(package => package.PackageId).Order(StringComparer.Ordinal);

    internal static AgentCurrentBaseline Load()
    {
        var selected = Environment.GetEnvironmentVariable(RootVariable);
        if (!string.IsNullOrWhiteSpace(selected)) return new(Path.GetFullPath(selected));
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx")))
                return new(Path.GetFullPath(Path.Combine(directory.FullName, "..", "sunder-agent-package")));
        throw new InvalidDataException($"Set {RootVariable} to the independently reviewed Agent checkout containing {InventoryPath} and {ContractsPath}.");
    }

    internal string VerifyArchive(string path)
        => VerifyArchive(path, SunderPackageFormat.SupportedRuntimeIdentifiers);

    internal string VerifyCurrentRidArchive(string path, string rid)
    {
        Assert.Contains(rid, SunderPackageFormat.SupportedRuntimeIdentifiers);
        return VerifyArchive(path, [rid]);
    }

    private string VerifyArchive(string path, IReadOnlyList<string> expectedRids)
    {
        using var archive = ZipFile.OpenRead(path);
        using var stream = archive.GetEntry(SunderPackageFormat.ManifestPath)!.Open();
        var manifest = JsonSerializer.Deserialize<SunderPackageManifest>(stream)!;
        var targets = manifest.Targets ?? throw new InvalidDataException("Current archive has no targets.");
        var bundles = manifest.ContractBundles ?? throw new InvalidDataException("Current archive has no contract bundles.");
        var providers = manifest.Provides ?? throw new InvalidDataException("Current archive has no provider inventory.");
        var package = Assert.Single(Packages, package => package.PackageId == manifest.Id);
        Assert.Equal(PackageVersion, manifest.Version);
        string[] roles = package.HasApp ? ["app", "runtime"] : ["runtime"];
        Assert.Equal(roles.SelectMany(role => expectedRids.Select(rid => $"{role}/{rid}")).Order(StringComparer.Ordinal),
            targets.Select(target => $"{target!.Role}/{target!.Rid}").Order(StringComparer.Ordinal));
        Assert.All(targets, target =>
        {
            Assert.NotNull(target);
            Assert.Equal(target.Role == "runtime" ? "worker" : "avalonia", target.Kind);
        });
        var dependencies = manifest.DependsOn ?? [];
        if (package.PackageId == AgentId) Assert.Empty(dependencies);
        else
        {
            var dependency = Assert.Single(dependencies);
            Assert.Equal(AgentId, dependency.PackageId);
            Assert.Equal(">=2.0.0 <3.0.0", dependency.VersionRange);
            Assert.Equal(package.PackageId == "younics.sunder.package.maker" ? "optional" : "required", dependency.Presence);
        }
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("Sunder.Package.Agent.Protocol", StringComparison.OrdinalIgnoreCase)
            || entry.FullName.Contains("Sunder.Package.Agent.Contracts", StringComparison.OrdinalIgnoreCase));
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".deps.json", StringComparison.Ordinal)))
        {
            using var dependenciesStream = entry.Open();
            using var dependenciesDocument = JsonDocument.Parse(dependenciesStream);
            Assert.DoesNotContain(dependenciesDocument.RootElement.GetProperty("libraries").EnumerateObject(), library =>
                library.Name.StartsWith("Sunder.Package.Agent.Protocol/", StringComparison.OrdinalIgnoreCase)
                || library.Name.StartsWith("Sunder.Package.Agent.Contracts/", StringComparison.OrdinalIgnoreCase));
        }
        Assert.Equal(package.Providers.Keys.Order(StringComparer.Ordinal), providers.Select(provider => provider!.ProviderId).Order(StringComparer.Ordinal));
        foreach (var bundle in bundles)
        {
            Assert.NotNull(bundle);
            var platform = bundle.ContractId == StackId;
            Assert.Equal(platform ? "platform" : "package", bundle.AuthorityKind);
            Assert.Equal(platform ? Inventory.Platform.AuthorityId : Contract(bundle.ContractId!).PackageId, bundle.AuthorityId);
            Assert.Equal(platform ? "public" : Contract(bundle.ContractId!).Exposure, bundle.Exposure);
            Assert.Equal(platform ? Inventory.Platform.Version : Version(Contract(bundle.ContractId!)), bundle.Version);
            Assert.Equal(bundle.AuthorityId == package.PackageId ? "owned" : "imported", bundle.Source);
            using var descriptorStream = archive.GetEntry(SunderPackageFormat.SharedPayloadRoot + bundle.DescriptorPath)!.Open();
            using var bytes = new MemoryStream();
            descriptorStream.CopyTo(bytes);
            var descriptor = SunderRpcContractDescriptor.Parse(bytes.ToArray());
            Assert.Equal(bytes.ToArray(), descriptor.GetCanonicalUtf8());
            Assert.Equal(bundle.ContractId, descriptor.ContractId);
            Assert.Equal(bundle.Version, descriptor.Version);
            Assert.Equal(bundle.DescriptorSha256, descriptor.Sha256);
            Assert.Equal(bundle.AbiSha256, descriptor.AbiSha256);
            if (!platform) Assert.Equal(_inputs.Descriptor(descriptor.ContractId).GetCanonicalUtf8(), bytes.ToArray());
        }
        foreach (var provider in providers)
        {
            Assert.NotNull(provider);
            Assert.Equal(package.Providers[provider.ProviderId!], provider.ContractId);
            var bundle = Assert.Single(bundles, bundle => bundle!.ContractId == provider.ContractId)!;
            Assert.Equal("runtime", provider.Role);
            Assert.Equal(bundle.Exposure == "public" ? "public" : "owner", provider.Audience);
            Assert.Equal(bundle.Version, provider.ContractVersion);
            Assert.Equal(bundle.DescriptorSha256, provider.DescriptorSha256);
            Assert.Equal(bundle.AbiSha256, provider.AbiSha256);
        }
        Assert.Equal(Inventory.Contracts.Where(contract => contract.PackageId == package.PackageId).Select(contract => contract.ContractId).Order(StringComparer.Ordinal),
            bundles.Where(bundle => bundle!.Source == "owned").Select(bundle => bundle!.ContractId).Order(StringComparer.Ordinal));
        return package.PackageId;
    }

    internal void VerifyProviders(IReadOnlyList<SunderRpcProviderSnapshot> providers)
    {
        Assert.Equal(Packages.SelectMany(package => package.Providers.Keys.Select(id => $"{package.PackageId}/{id}")).Order(StringComparer.Ordinal),
            providers.Select(provider => $"{provider.PackageId}/{provider.ProviderId}").Order(StringComparer.Ordinal));
        foreach (var provider in providers)
        {
            var package = Assert.Single(Packages, package => package.PackageId == provider.PackageId);
            Assert.Equal(package.Providers[provider.ProviderId], provider.ContractId);
            Assert.Equal(PackageVersion, provider.PackageVersion);
            Assert.Equal(SunderRpcProviderState.Active, provider.State);
            if (provider.ContractId == StackId) Assert.Equal(Inventory.Platform.Version, provider.ContractVersion);
            else
            {
                var descriptor = _inputs.Descriptor(provider.ContractId);
                Assert.Equal(descriptor.Version, provider.ContractVersion);
                Assert.Equal(descriptor.Sha256, provider.DescriptorSha256);
                Assert.Equal(descriptor.AbiSha256, provider.AbiSha256);
            }
        }
    }

    private AgentCurrentInputs.ExpectedContract Contract(string id) => _inputs.Contract(id);
    private string Version(AgentCurrentInputs.ExpectedContract contract) => _inputs.Version(contract);
}
