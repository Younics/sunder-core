using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Core.Tests;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

// Local current inputs are an explicit scope, separate from universal release activation.
public sealed class AgentCurrentInputsTests
{
    [ExternalArtifactFact("SUNDER_REQUIRE_AGENT_CURRENT_INPUTS", "SUNDER_AGENT_CURRENT_BASELINE_ROOT", "SUNDER_AGENT_PACKAGE_ARCHIVE_DIR")]
    public void QualifiedCurrentRidArchivesMatchIndependentRecords()
    {
        var baseline = AgentCurrentBaseline.Load();
        var archives = Directory.GetFiles(Environment.GetEnvironmentVariable("SUNDER_AGENT_PACKAGE_ARCHIVE_DIR")!, "*.sunderpkg");
        Assert.Equal(baseline.Packages.Select(package => package.PackageId).Order(StringComparer.Ordinal),
            archives.Select(path => baseline.VerifyCurrentRidArchive(path, RuntimeInformation.RuntimeIdentifier)).Order(StringComparer.Ordinal));
        // A current-RID observation cannot qualify the unchanged universal-release gate.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => baseline.VerifyArchive(archives[0]));
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
