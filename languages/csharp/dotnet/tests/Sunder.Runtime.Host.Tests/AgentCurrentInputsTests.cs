using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Core.Tests;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.Host.TestSupport;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

// Local current inputs are an explicit scope, separate from universal release activation.
public sealed class AgentCurrentInputsTests
{
    [Theory]
    [InlineData("schema")]
    [InlineData("profile")]
    [InlineData("installed-acceptance")]
    [InlineData("catalog-bytes")]
    [InlineData("archive-bytes")]
    [InlineData("missing-archive")]
    [InlineData("missing-handoff-input")]
    [InlineData("inventory")]
    [InlineData("contracts")]
    public void AppArtifactHandoff_RejectsWrongPhaseOrChangedAndIncompleteInputs(string mutation)
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-agent-handoff");
        var archives = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in new[] { "agent", "maker" })
        {
            var path = temporary.GetPath(id + ".sunderpkg");
            File.WriteAllText(path, id + " synthetic input bytes");
            archives.Add(id, path);
        }
        var catalog = temporary.GetPath("catalog.json");
        File.WriteAllText(catalog, "synthetic catalog input bytes");
        var package = new ActivePackageDescriptor("agent", "Agent", "2.0.0", PackageHostRoles.App, null, true, PackageReadinessState.Ready, []);
        var snapshot = new PackageUiSnapshotDescriptor("agent", PackageSourceKind.Dev, 1,
            new("app", "win-x64", "dotnet", "Agent.dll", "net10.0", null, []), "snapshot-hash", "old-alias", "old-uri");
        var handoff = new AgentFamilyArtifactHandoff(2, AgentFamilyArtifactHandoff.DevelopmentProfile, false, package, snapshot,
            "inventory-hash", "contracts-hash", AgentFamilyArtifactHandoff.Hash(catalog),
            archives.Select(pair => AgentFamilyArchiveInput.Read(pair.Key, pair.Value)).ToArray());
        // Exercise the wire representation, including separately allocated target collections.
        handoff = JsonSerializer.Deserialize<AgentFamilyArtifactHandoff>(JsonSerializer.SerializeToUtf8Bytes(handoff, AgentFamilyArtifactHandoff.Json), AgentFamilyArtifactHandoff.Json)!;
        handoff.VerifyInputs(archives, catalog, "inventory-hash", "contracts-hash");
        switch (mutation)
        {
            case "schema": handoff = handoff with { SchemaVersion = 1 }; break;
            case "profile": handoff = handoff with { Profile = "published-installed" }; break;
            case "installed-acceptance": handoff = handoff with { InstalledAcceptance = true }; break;
            case "catalog-bytes": File.AppendAllText(catalog, "changed"); break;
            case "archive-bytes": File.AppendAllText(archives["maker"], "changed"); break;
            case "missing-archive": archives.Remove("maker"); break;
            case "missing-handoff-input": handoff = handoff with { Archives = handoff.Archives.Take(1).ToArray() }; break;
            case "inventory": handoff = handoff with { InventorySha256 = "changed" }; break;
            case "contracts": handoff = handoff with { ContractsSha256 = "changed" }; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => handoff.VerifyInputs(archives, catalog, "inventory-hash", "contracts-hash"));
    }

    [ExternalArtifactFact("SUNDER_REQUIRE_AGENT_CURRENT_INPUTS", "SUNDER_AGENT_CURRENT_BASELINE_ROOT", "SUNDER_AGENT_PACKAGE_ARCHIVE_DIR")]
    public async Task QualifiedCurrentRidArchivesMatchIndependentRecords()
    {
        var baseline = AgentCurrentBaseline.Load();
        var archives = Directory.GetFiles(Environment.GetEnvironmentVariable("SUNDER_AGENT_PACKAGE_ARCHIVE_DIR")!, "*.sunderpkg")
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(baseline.Packages.Select(package => package.PackageId).Order(StringComparer.Ordinal),
            archives.Select(path => baseline.VerifyCurrentRidArchive(path, RuntimeInformation.RuntimeIdentifier)).Order(StringComparer.Ordinal));
        // A current-RID observation cannot qualify the unchanged universal-release gate.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => baseline.VerifyArchive(archives[0]));

        // Direct role-composition probes bypass Runtime admission. Exercise the
        // same UI-snapshot service used at App startup with these exact artifacts.
        using var temporary = TemporaryDirectoryScope.Create("sunder-agent-current-ui-admission");
        var paths = new RuntimePackagePaths(temporary.GetPath("runtime"));
        RuntimeLocalState.EnsureInitialized(paths.RootPath);
        var sources = new List<RuntimePackageSource>();
        foreach (var archive in archives)
        {
            var root = temporary.GetPath("packages", Path.GetFileNameWithoutExtension(archive));
            ZipFile.ExtractToDirectory(archive, root);
            var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(root);
            Assert.True(validation.Success, string.Join(" | ", validation.Errors));
            var worker = CanonicalPackageTestBuilder.CreateTargetSource(root, PackageSourceKind.Dev, SunderPackageFormat.RuntimeHostRole);
            Assert.Empty(SunderSdkCompatibilityProfile.Validate(worker.Source.PackageId, worker.TargetKey, worker.Target));
            if (worker.Source.PackageId == "younics.ai.agent.execution.docker")
                Assert.Contains(SunderSdkCapabilities.DockerEnvironmentV1, worker.Target.RequiredHostCapabilities!);
            sources.Add(worker.Source with
            {
                HostRoles = PackageHostRoles.Runtime | (baseline.AppPackageIds.Contains(worker.Source.PackageId) ? PackageHostRoles.App : PackageHostRoles.None),
                Dependencies = validation.Manifest!.DependsOn!.Select(dependency => new PackageDependencyDescriptor(dependency!.PackageId!, dependency.VersionRange!)).ToArray(),
            });
        }
        var descriptors = sources.ToDictionary(source => source.PackageId, source => new SessionPackageDescriptor(
            source.PackageId, source.Manifest!.Name!, source.Manifest.Version!, source.HostRoles, null, true,
            PackageReadinessState.Ready, [], null, null, null, 0));
        var session = new ActivePackageSession(null, new Dictionary<string, ActiveLoadedPackage>(), descriptors, readySources: sources);
        var owner = new RuntimeSessionOwner(NullLogger<RuntimeSessionOwner>.Instance, new RuntimeEventStreamService());
        using var snapshots = new PackageUiSnapshotStore(paths);
        try
        {
            await owner.PublishAsync(session, owner.Sources.Snapshot(), [], [], owner.Generation);
            var ui = new RuntimePackageUiService(owner, snapshots, new InstalledPackageStore(paths));
            var admitted = ui.GetActiveSnapshots(RuntimeInformation.RuntimeIdentifier);
            Assert.Equal(baseline.AppPackageIds, admitted.Select(snapshot => snapshot.PackageId).Order(StringComparer.Ordinal));
            foreach (var snapshot in admitted)
            {
                using var lease = ui.AcquireCurrent(snapshot.SnapshotId);
                Assert.NotNull(lease);
                Assert.True(lease.Length > 0);
            }
        }
        finally { await owner.State.ClearActiveSessionAsync(); }
    }

    [ExternalArtifactFact("SUNDER_REQUIRE_AGENT_CURRENT_INPUTS", "SUNDER_AGENT_CURRENT_BASELINE_ROOT")]
    public void ObservedInventoryCannotRedefineCurrentPackageOrProviderMembership()
    {
        var inputs = AgentCurrentInputs.Load(Root());
        var observed = JsonNode.Parse(File.ReadAllBytes(Path.Combine(Root(), "packages.json")))!;
        inputs.ValidatePackageInventory(JsonSerializer.SerializeToElement(observed));
        var maker = observed["packages"]!.AsArray().Single(package => package!["key"]!.GetValue<string>() == "agent-builder")!;
        maker["packageId"] = "younics.ai.agent.builder";
        Assert.Throws<InvalidDataException>(() => inputs.ValidatePackageInventory(JsonSerializer.SerializeToElement(observed)));
        maker["packageId"] = "younics.sunder.package.maker";
        maker["providerIds"]![0] = "unexpected.provider";
        Assert.Throws<InvalidDataException>(() => inputs.ValidatePackageInventory(JsonSerializer.SerializeToElement(observed)));
    }

    [ExternalArtifactFact("SUNDER_REQUIRE_AGENT_CURRENT_INPUTS", "SUNDER_AGENT_CURRENT_BASELINE_ROOT")]
    public void DetachedInventoryAndCorruptAbiCannotReplaceReviewedExpectations()
    {
        WithInputs(root =>
        {
            var inventory = Path.Combine(root, AgentCurrentInputs.InventoryPath);
            var bytes = File.ReadAllBytes(inventory);
            File.WriteAllBytes(inventory, [.. bytes, (byte)' ']);
            Assert.Contains("authenticate", Assert.Throws<InvalidDataException>(() => AgentCurrentInputs.Load(root)).Message);
            File.WriteAllBytes(inventory, bytes);
            var path = Path.Combine(root, AgentCurrentInputs.ContractsPath);
            var records = JsonNode.Parse(File.ReadAllBytes(path))!;
            records["contracts"]![0]!["abiSha256"] = new string('0', 64);
            File.WriteAllText(path, records.ToJsonString());
            Assert.Contains("descriptor tuple differs", Assert.Throws<InvalidDataException>(() => AgentCurrentInputs.Load(root)).Message);
        });
    }

    [ExternalArtifactFact("SUNDER_REQUIRE_AGENT_CURRENT_INPUTS", "SUNDER_AGENT_CURRENT_BASELINE_ROOT")]
    public void MissingReviewedRecordsFailWithRequiredInputDiagnostic()
    {
        WithInputs(root =>
        {
            File.Delete(Path.Combine(root, AgentCurrentInputs.ContractsPath));
            var error = Assert.Throws<InvalidDataException>(() => AgentCurrentInputs.Load(root));
            Assert.Contains("Missing reviewed CURRENT input", error.Message);
            Assert.Contains(AgentCurrentInputs.ContractsPath, error.Message.Replace('\\', '/'));
        });
    }

    [ExternalArtifactFact("SUNDER_REQUIRE_AGENT_CURRENT_INPUTS", "SUNDER_AGENT_CURRENT_BASELINE_ROOT")]
    public void PackageVersionsComeFromIndependentAuthoredProjectsNotObservedArchives()
    {
        WithInputs(root =>
        {
            var inputs = AgentCurrentInputs.Load(root);
            foreach (var (id, version) in new[] { (AgentCurrentInputs.AgentId, "2.0.0"), ("younics.ai.agent.provider.openai", "2.3.1-rc.2") })
            {
                var path = Path.Combine(root, inputs.Package(id).ProjectPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, $"<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>");
                Assert.Equal(version, inputs.PackageVersionFor(id));
            }
            var provider = Path.Combine(root, inputs.Package("younics.ai.agent.provider.openai").ProjectPath);
            var contractId = "younics.ai.agent.provider.openai.management";
            var projected = inputs.Descriptor(contractId);
            Assert.Equal("2.3.1-rc.2", projected.Version);
            using var records = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, AgentCurrentInputs.ContractsPath)));
            var reviewed = SunderRpcContractDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes(records.RootElement.GetProperty("contracts").EnumerateArray()
                .Single(record => record.GetProperty("contractId").GetString() == contractId).GetProperty("canonicalDescriptor").GetString()!));
            Assert.Equal(reviewed.AbiSha256, projected.AbiSha256);
            Assert.NotEqual(reviewed.Sha256, projected.Sha256);
            var restored = JsonNode.Parse(projected.GetCanonicalUtf8())!;
            restored["version"] = reviewed.Version;
            Assert.Equal(reviewed.GetCanonicalUtf8(), SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(restored)).GetCanonicalUtf8());
            File.WriteAllText(provider, "<Project><PropertyGroup><Version>2.3</Version></PropertyGroup></Project>");
            Assert.Throws<InvalidDataException>(() => inputs.PackageVersionFor("younics.ai.agent.provider.openai"));
            Assert.Equal("2.0.0", inputs.PackageVersionFor(AgentCurrentInputs.AgentId));
        });
    }

    private static string Root() => Environment.GetEnvironmentVariable(AgentCurrentBaseline.RootVariable)!;

    private static void WithInputs(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-current-inputs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "compatibility"));
        try
        {
            foreach (var path in new[] { AgentCurrentInputs.InventoryPath, AgentCurrentInputs.ContractsPath })
                File.Copy(Path.Combine(Root(), path), Path.Combine(root, path));
            test(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
