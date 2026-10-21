using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Rpc.Authoring;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcOwnedContractCommandTests
{
    [Fact]
    public void ProjectionReview_CommandsNeverBackfillMissingEvidenceFromCurrentSource()
    {
        Success(Command("Accept")); Success(Command("Freeze"));
        var generation = Command(); Success(generation);
        var dev = BuildDevPackage(generation);
        var before = Baseline();
        var legacy = JsonNode.Parse(before.ExactBytes)!;
        foreach (var contract in legacy["contracts"]!.AsObject()) contract.Value!.AsObject().Remove("projectionReviews");
        File.WriteAllBytes(BaselinePath, SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(legacy)));
        var unreviewed = File.ReadAllBytes(BaselinePath);
        Failure(Command(), "source-projection-unreviewed");
        Failure(Command("Check"), "source-projection-unreviewed");
        Failure(Command("Freeze"), "source-projection-unreviewed");
        Failure(Command("Accept"), "source-projection-unreviewed");
        Failure(Pack(dev), "source-projection-unreviewed");
        Assert.Equal(unreviewed, File.ReadAllBytes(BaselinePath));
        // Recovery restores actual reviewed evidence, never relabels current source as history.
        File.WriteAllBytes(BaselinePath, before.ExactBytes);
        Success(Command("Check")); Success(Command("Freeze"));
        Assert.Equal(before.ExactBytes, File.ReadAllBytes(BaselinePath));
    }
}
