using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcOwnedBaselineEvolutionTests
{
    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void PrivateArchiveSharesTheActiveEntryBudget(int count, bool valid)
    {
        var active = new Dictionary<string, SunderRpcOwnedContractBaseline>(StringComparer.Ordinal);
        var retired = new Dictionary<string, SunderRpcOwnedContractBaseline>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var key = "c" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            var candidate = ChangeDescriptor(Original.Value with { ContractKey = key, Exposure = i < 64 ? SunderServiceExposure.Public : SunderServiceExposure.Package },
                json => json["contractId"] = Owner + "." + key);
            (i < 64 ? active : retired).Add(key, new(candidate.Descriptor.ContractId, Snapshot(candidate), []));
        }
        var model = new SunderRpcOwnedPackageBaseline(Owner, active, [], string.Empty) { RetiredPrivateContracts = retired, HasRetiredPrivateContracts = true };
        if (!valid) Assert.Contains("baseline-combined-limit", Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(model)).Message, StringComparison.Ordinal);
        else
        {
            var bytes = SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(model); AssertSchema(bytes);
            var parsed = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(bytes);
            Assert.Equal(128, parsed.Contracts.Count + parsed.RetiredPrivateContracts.Count);
        }
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("release-frozen")]
    [InlineData("published")]
    [InlineData("history-only")]
    public void PrivateRetirementMovesExactCompleteRecordsAndPreservesPublicHistory(string state)
    {
        var privateSource = Compile(Source.Replace("Exposure.Public", "Exposure.Package", StringComparison.Ordinal));
        var old = AtVersion(privateSource, "1.0.0");
        var historical = Snapshot(old, "published", "4.5.6");
        var current = state == "history-only" ? null : Snapshot(AtVersion(privateSource, "1.1.0"), state, "4.5.7");
        var baseline = Baseline(current, historical);
        if (current is not null) Assert.Contains("private-baseline-migration-required", Assert.Throws<InvalidDataException>(() =>
            SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, [privateSource])).Message, StringComparison.Ordinal);
        var bytes = SunderRpcAuthoringDocuments.PlanPrivateBaselineRetirement(baseline, [privateSource], null);
        var migrated = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(bytes);
        Assert.Empty(migrated.Contracts); var archived = Assert.Single(migrated.RetiredPrivateContracts).Value;
        AssertSnapshotEqual(historical, Assert.Single(archived.History));
        if (current is not null) AssertSnapshotEqual(current, archived.Current!); else Assert.Null(archived.Current);
        Assert.Equal(bytes, SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(migrated)); AssertSchema(bytes);
        Assert.Equal(bytes, SunderRpcAuthoringDocuments.PlanPrivateBaselineRetirement(migrated, [privateSource], null));
        SunderRpcAuthoringDocuments.ValidateCompiledContracts(migrated, [privateSource]);
        Assert.Contains("private-retirement-mutated", Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(
            migrated with { RetiredPrivateContracts = new Dictionary<string, SunderRpcOwnedContractBaseline>() })).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicHistoryCannotEscapeByPrivateDemotionRenameOrRetirement()
    {
        var published = Snapshot(Original.Value, "published");
        var privateSource = Compile(Source.Replace("Exposure.Public", "Exposure.Package", StringComparison.Ordinal));
        var baseline = Baseline(Snapshot(AtVersion(privateSource, "2.0.0")), published);
        Assert.Contains("private-retirement-public-evidence", Assert.Throws<InvalidDataException>(() =>
            SunderRpcAuthoringDocuments.PlanPrivateBaselineRetirement(baseline, [privateSource], Key)).Message, StringComparison.Ordinal);
        Assert.Contains("baseline-drift", Assert.Throws<InvalidDataException>(() =>
            SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, [privateSource with { ContractKey = "renamed" }])).Message, StringComparison.Ordinal);
        Assert.Contains("baseline-drift", Assert.Throws<InvalidDataException>(() =>
            SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, [])).Message, StringComparison.Ordinal);
    }
}
