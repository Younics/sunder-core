using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcOwnedContractCommandTests
{
    private const string BatchSource = """
        using System.Threading.Tasks;
        using Sunder.Sdk.Packaging;
        using Sunder.Sdk.Services;
        [assembly: SunderPackage(Id = "example.generated", Name = "Batch review")]
        public static class Program { public static Task Main() => Task.CompletedTask; }
        [SunderService(SunderServiceExposure.Public)]
        public sealed partial class FirstService { public ValueTask<SharedValue> ReadAsync() => throw new System.NotImplementedException(); }
        [SunderService(SunderServiceExposure.Public)]
        public sealed partial class SecondService { public ValueTask<SharedValue> ReadAsync() => throw new System.NotImplementedException(); }
        [SunderService(SunderServiceExposure.Public)]
        public sealed partial class UnchangedService { public ValueTask<string> ReadAsync() => throw new System.NotImplementedException(); }
        public sealed record SharedValue { public required string Text { get; init; } }
        """;

    [Fact]
    public void BatchAccept_ReviewsBothSharedModelChangesAndPreservesOtherFrozenRecordsAndHistory()
    {
        var (before, path) = ProposeBatch();
        var output = Snapshot(Generated);
        var proposal = SunderRpcAuthoringDocuments.ParseOwnedContractReview(File.ReadAllBytes(path));
        Assert.Equal(new[] { "first", "second" }, proposal.Contracts.Keys);
        Assert.All(proposal.Contracts.Values, entry => Assert.False(entry.AcknowledgeBreakingChange));
        Assert.Equal(before.ExactBytes, File.ReadAllBytes(BaselinePath));
        Assert.All(proposal.Contracts.Values, entry => Assert.Equal("2.0.0", entry.NextVersion));
        var single = Command("Accept"); single.ContractKey = "first"; single.ContractCurrentVersion = "1.0.0";
        single.ContractNextVersion = "2.0.0"; single.AcknowledgeBreakingChange = true;
        Failure(single, "unchanged snapshots outside");
        Failure(BatchAccept(path), "explicit breaking-change acknowledgement");
        AcknowledgeTestReview(path);
        var accept = BatchAccept(path); Success(accept);
        Assert.Empty(accept.FileWrites); Assert.Empty(accept.CompileItems); Assert.Equal(output, Snapshot(Generated));
        var after = Baseline();
        foreach (var key in new[] { "first", "second" })
        {
            Assert.Equal("2.0.0", after.Contracts[key].Current!.Descriptor.Version);
            Assert.Equal(SunderRpcAuthoringDocuments.WriteDeterministicDocument(before.Contracts[key].Current!.SnapshotJson),
                SunderRpcAuthoringDocuments.WriteDeterministicDocument(Assert.Single(after.Contracts[key].History).SnapshotJson));
            Assert.Equal(proposal.Contracts[key].ExpectedNextDescriptorSha256, after.Contracts[key].Current!.Descriptor.Sha256);
            Assert.Equal(proposal.Contracts[key].ExpectedNextAbiSha256, after.Contracts[key].Current!.Descriptor.AbiSha256);
        }
        Assert.Equal(before.Contracts["unchanged"].Current!.SnapshotJson.GetRawText(), after.Contracts["unchanged"].Current!.SnapshotJson.GetRawText());
        Assert.Empty(after.Contracts["unchanged"].History);
        Success(Command("Check"));

        var noChange = Path.Combine(_root, "no-change-review.json");
        var check = Command("Check"); check.ContractReviewOutputPath = noChange; Success(check);
        Assert.Empty(SunderRpcAuthoringDocuments.ParseOwnedContractReview(File.ReadAllBytes(noChange)).Contracts);
        var timestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc); File.SetLastWriteTimeUtc(BaselinePath, timestamp);
        Success(BatchAccept(noChange));
        Assert.Equal(after.ExactBytes, File.ReadAllBytes(BaselinePath)); Assert.Equal(timestamp, File.GetLastWriteTimeUtc(BaselinePath));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("current-tuple")]
    [InlineData("next-tuple")]
    [InlineData("one-unacknowledged")]
    [InlineData("version-floor")]
    public void BatchAccept_RejectsIncompleteOrWrongReviewWithoutPartialAcceptance(string failure)
    {
        var (before, path) = ProposeBatch(); AcknowledgeTestReview(path);
        var json = JsonNode.Parse(File.ReadAllBytes(path))!;
        var entries = json["contracts"]!.AsObject();
        switch (failure)
        {
            case "missing": entries.Remove("second"); break;
            case "extra": entries["unchanged"] = entries["first"]!.DeepClone(); break;
            case "current-tuple": entries["second"]!["expectedCurrentDescriptorSha256"] = new string('a', 64); break;
            case "next-tuple": entries["second"]!["expectedNextAbiSha256"] = new string('a', 64); break;
            case "one-unacknowledged": entries["second"]!["acknowledgeBreakingChange"] = false; break;
            case "version-floor":
                var source = Command().PrepareOwnedContractCandidate(before, validateBaseline: false)!.Contracts.Single(contract => contract.ContractKey == "second");
                entries["second"]!["nextVersion"] = "1.0.0";
                entries["second"]!["expectedNextDescriptorSha256"] = source.Descriptor.Sha256;
                entries["second"]!["expectedNextAbiSha256"] = source.Descriptor.AbiSha256;
                break;
        }
        WriteReview(path, json);
        var output = Snapshot(Generated);
        Assert.False(BatchAccept(path).Execute());
        Assert.Equal(before.ExactBytes, File.ReadAllBytes(BaselinePath)); Assert.Equal(output, Snapshot(Generated));
        Assert.Empty(Directory.GetFiles(_root, ".sunder-contract-stage-*"));
    }

    [Fact]
    public void BatchReview_RejectsStaleSourceEvenWhenDescriptorBytesHaveNotChanged()
    {
        var (before, path) = ProposeBatch(); AcknowledgeTestReview(path);
        File.AppendAllText(SourcePath, "\n// source frame changed after review\n");
        Failure(BatchAccept(path), "source/reference input frame");
        Assert.Equal(before.ExactBytes, File.ReadAllBytes(BaselinePath));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("review")]
    public void BatchReview_CommitFenceRollsBackTheWholeProposalOnLateInputChange(string input)
    {
        var (before, path) = ProposeBatch(); AcknowledgeTestReview(path);
        var candidate = Command().PrepareOwnedContractCandidate(before, validateBaseline: false)!;
        var reviewBytes = candidate.Inputs.Read(path, SunderRpcAuthoringDocuments.MaximumOwnedContractReviewBytes, "review");
        var review = SunderRpcAuthoringDocuments.ParseOwnedContractReview(reviewBytes);
        var proposed = SunderRpcAuthoringDocuments.PlanOwnedBaselineBatchAcceptance(before, candidate.Contracts, review,
            "7.6.5", candidate.InputFingerprint, candidate.LockSha256);
        Assert.Throws<IOException>(() => GenerateSunderRpcBindingsTask.CommitOwnedBaseline(_root, BaselinePath, before.ExactBytes, proposed,
            candidate.Inputs, () => File.AppendAllText(input == "source" ? SourcePath : path, "\n")));
        Assert.Equal(before.ExactBytes, File.ReadAllBytes(BaselinePath));
        Assert.Empty(Directory.GetFiles(_root, ".sunder-contract-stage-*"));
    }

    [Fact]
    public void BatchReview_IsClosedBoundedAndCannotReplaceExistingProposalOrCombineSingleSelection()
    {
        var (before, path) = ProposeBatch();
        var original = File.ReadAllBytes(path);
        var unknown = JsonNode.Parse(original)!; unknown["approval"] = true;
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseOwnedContractReview(
            SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(unknown))));
        var oversized = new byte[SunderRpcAuthoringDocuments.MaximumOwnedContractReviewBytes + 1];
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseOwnedContractReview(oversized));
        var tooMany = JsonNode.Parse(original)!; var map = tooMany["contracts"]!.AsObject(); var entry = map["first"]!.DeepClone(); map.Clear();
        for (var index = 0; index < 129; index++) map[$"contract{index:D3}"] = entry.DeepClone();
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseOwnedContractReview(
            SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(tooMany))));
        var repeat = Command("Check"); repeat.ContractReviewOutputPath = path; Assert.False(repeat.Execute());
        Assert.Equal(original, File.ReadAllBytes(path));
        var mixed = BatchAccept(path); mixed.AcknowledgeBreakingChange = true; Failure(mixed, "cannot be combined");
        var wrongCommand = Command("Freeze"); wrongCommand.ContractReviewPath = path; Failure(wrongCommand, "Accept-only");
        Assert.Equal(before.ExactBytes, File.ReadAllBytes(BaselinePath));
    }

    private (SunderRpcOwnedPackageBaseline Baseline, string ReviewPath) ProposeBatch()
    {
        File.WriteAllText(SourcePath, BatchSource);
        Success(Command("Accept")); Success(Command("Freeze")); Success(Command());
        var before = Baseline(); var outputs = Snapshot(Generated);
        File.WriteAllText(SourcePath, BatchSource.Replace("required string Text", "required int Text", StringComparison.Ordinal));
        var path = Path.Combine(_root, "review.json");
        var check = Command("Check"); check.ContractReviewOutputPath = path; Success(check);
        Assert.Equal(before.ExactBytes, File.ReadAllBytes(BaselinePath)); Assert.Equal(outputs, Snapshot(Generated));
        return (before, path);
    }

    private GenerateSunderRpcBindingsTask BatchAccept(string path)
    {
        var task = Command("Accept"); task.ContractReviewPath = path; return task;
    }

    private static void AcknowledgeTestReview(string path)
    {
        var json = JsonNode.Parse(File.ReadAllBytes(path))!;
        foreach (var entry in json["contracts"]!.AsObject()) entry.Value!["acknowledgeBreakingChange"] = true;
        WriteReview(path, json);
    }

    private static void WriteReview(string path, JsonNode json)
        => File.WriteAllBytes(path, SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(json)));
}
