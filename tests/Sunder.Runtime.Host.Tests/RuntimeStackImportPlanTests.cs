using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeStackImportPlanTests
{
    [Fact]
    public async Task ImportAsync_WhenRuntimeGenerationChanged_RejectsStalePlanWithoutCallingContributor()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));
        var preview = await fixture.PreviewAsync(["one-fragment"]);

        await fixture.PublishSessionAsync(new TestContributor("one"));
        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(preview.PlanId!, ["one-fragment"], ["one-action"]));

        Assert.Equal(RuntimeStackImportOutcome.Failed, result.Outcome);
        Assert.Contains(result.Errors, error => error.Contains("generation", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, fixture.Contributors[0].ImportCount);
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.TransferRootPath));
    }

    [Fact]
    public async Task ImportAsync_WhenPlanExpired_RejectsPlanAndReleasesUpload()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));
        var preview = await fixture.PreviewAsync(["one-fragment"]);

        fixture.Clock.Advance(new RuntimeStackPolicyOptions().ImportPlanLifetime);
        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(preview.PlanId!, ["one-fragment"], ["one-action"]));

        Assert.Equal(RuntimeStackImportOutcome.Failed, result.Outcome);
        Assert.Contains(result.Errors, error => error.Contains("expired", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, fixture.Contributors[0].ImportCount);
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.TransferRootPath));
    }

    [Fact]
    public async Task SweepExpired_WhenPlanExpired_ReleasesUploadAndInvalidatesPlan()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));
        var preview = await fixture.PreviewAsync(["one-fragment"]);

        fixture.Clock.Advance(new RuntimeStackPolicyOptions().ImportPlanLifetime);
        fixture.Service.SweepExpired();
        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(preview.PlanId!, ["one-fragment"], ["one-action"]));

        Assert.Equal(RuntimeStackImportOutcome.Failed, result.Outcome);
        Assert.Contains(result.Errors, error => error.Contains("not found", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.TransferRootPath));
    }

    [Fact]
    public async Task DiscardPlan_ReleasesUploadAndInvalidatesPlan()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));
        var preview = await fixture.PreviewAsync(["one-fragment"]);

        Assert.True(fixture.Service.DiscardPlan(preview.PlanId!));
        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(preview.PlanId!, ["one-fragment"], ["one-action"]));

        Assert.Equal(RuntimeStackImportOutcome.Failed, result.Outcome);
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.TransferRootPath));
    }

    [Fact]
    public async Task ImportAsync_WhenPlanConsumedTwice_OnlyFirstCallInvokesContributor()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));
        var preview = await fixture.PreviewAsync(["one-fragment"]);
        var request = new RuntimeStackImportRequest(preview.PlanId!, ["one-fragment"], ["one-action"]);

        var first = await fixture.Service.ImportAsync(request);
        var duplicate = await fixture.Service.ImportAsync(request);

        Assert.Equal(RuntimeStackImportOutcome.Completed, first.Outcome);
        Assert.Equal(RuntimeStackImportOutcome.Failed, duplicate.Outcome);
        Assert.Contains(duplicate.Errors, error => error.Contains("already consumed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, fixture.Contributors[0].ImportCount);
    }

    [Fact]
    public async Task PreviewAsync_WhenFragmentIdUnknown_RejectsSelectionAndReleasesUpload()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));

        var preview = await fixture.PreviewAsync(["unknown-fragment"]);

        Assert.False(preview.Success);
        Assert.Null(preview.PlanId);
        Assert.Contains(preview.Errors, error => error.Contains("unknown fragment", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.TransferRootPath));
    }

    [Fact]
    public async Task ImportAsync_WhenActionIdUnknown_RejectsAndConsumesPlan()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));
        var preview = await fixture.PreviewAsync(["one-fragment"]);

        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(preview.PlanId!, ["one-fragment"], ["unknown-action"]));
        var duplicate = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(preview.PlanId!, ["one-fragment"], ["one-action"]));

        Assert.Equal(RuntimeStackImportOutcome.Failed, result.Outcome);
        Assert.Contains(result.Errors, error => error.Contains("unknown action", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(RuntimeStackImportOutcome.Failed, duplicate.Outcome);
        Assert.Equal(0, fixture.Contributors[0].ImportCount);
    }

    [Fact]
    public async Task ImportAsync_WhenFragmentIdUnknown_RejectsAndConsumesPlan()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"), new TestContributor("two"));
        var preview = await fixture.PreviewAsync(["one-fragment", "two-fragment"]);

        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(preview.PlanId!, ["one-fragment", "unknown-fragment"], ["one-action"]));

        Assert.Equal(RuntimeStackImportOutcome.Failed, result.Outcome);
        Assert.Contains(result.Errors, error => error.Contains("unknown fragment", StringComparison.OrdinalIgnoreCase));
        Assert.All(fixture.Contributors, contributor => Assert.Equal(0, contributor.ImportCount));
    }

    [Fact]
    public async Task ImportAsync_WhenOneContributorFails_ReturnsPartialWithPerContributorResults()
    {
        await using var fixture = await StackImportFixture.CreateAsync(
            new TestContributor("one"),
            new TestContributor("two", succeeds: false));
        var preview = await fixture.PreviewAsync(["one-fragment", "two-fragment"]);

        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(
            preview.PlanId!,
            ["one-fragment", "two-fragment"],
            ["one-action", "two-action"]));

        Assert.Equal(RuntimeStackImportOutcome.Partial, result.Outcome);
        Assert.Collection(
            result.ContributorResults,
            completed =>
            {
                Assert.Equal("one", completed.ContributorId);
                Assert.Equal(RuntimeStackImportOutcome.Completed, completed.Outcome);
                Assert.Single(completed.ImportedItems);
            },
            failed =>
            {
                Assert.Equal("two", failed.ContributorId);
                Assert.Equal(RuntimeStackImportOutcome.Failed, failed.Outcome);
                Assert.NotEmpty(failed.Errors);
            });
        Assert.Single(result.ContributorResults, contributor => contributor.ImportedItems.Count > 0);
        Assert.All(fixture.Contributors, contributor => Assert.Equal(1, contributor.ImportCount));
    }

    private sealed class StackImportFixture : IAsyncDisposable
    {
        private StackImportFixture(
            RuntimePackagePaths paths,
            RuntimeContentTransferStore transfers,
            RuntimeSessionOwner owner,
            TestTimeProvider clock,
            IReadOnlyList<TestContributor> contributors)
        {
            Paths = paths;
            Transfers = transfers;
            Owner = owner;
            Clock = clock;
            Contributors = contributors;
            Service = new RuntimeStackImportService(owner, transfers, clock);
        }

        public RuntimePackagePaths Paths { get; }
        public RuntimeContentTransferStore Transfers { get; }
        public RuntimeSessionOwner Owner { get; }
        public TestTimeProvider Clock { get; }
        public IReadOnlyList<TestContributor> Contributors { get; }
        public RuntimeStackImportService Service { get; }

        public static async Task<StackImportFixture> CreateAsync(params TestContributor[] contributors)
        {
            var root = Path.Combine(Path.GetTempPath(), "sunder-stack-import-plan-tests", Guid.NewGuid().ToString("N"));
            var paths = new RuntimePackagePaths(root);
            var transfers = new RuntimeContentTransferStore(paths);
            var owner = new RuntimeSessionOwner(NullLogger<RuntimeSessionOwner>.Instance, new RuntimeEventStreamService());
            var fixture = new StackImportFixture(paths, transfers, owner, new TestTimeProvider(), contributors);
            await fixture.PublishSessionAsync(contributors);
            return fixture;
        }

        public async Task PublishSessionAsync(params TestContributor[] contributors)
        {
            var catalog = new RuntimePackageExtensionCatalog();
            foreach (var contributor in contributors)
            {
                catalog.Add(contributor.PackageId, SunderStackExtensionPoints.StackContributors, contributor);
            }

            await Owner.PublishAsync(new ActivePackageSession(
                sessionFolder: null,
                new Dictionary<string, ActiveLoadedPackage>(),
                new Dictionary<string, SessionPackageDescriptor>(),
                catalog));
        }

        public async Task<RuntimeStackImportPreviewResponse> PreviewAsync(IReadOnlyList<string> selectedFragmentIds)
        {
            var archivePath = Path.Combine(Paths.RootPath, $"{Guid.NewGuid():N}.sunderstack");
            Directory.CreateDirectory(Paths.RootPath);
            var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
            var fragments = new List<SunderStackFragmentManifest>();
            foreach (var contributor in Contributors)
            {
                var fragmentId = contributor.ContributorId + "-fragment";
                var payloadPath = Path.Combine(Paths.RootPath, fragmentId + ".json");
                await File.WriteAllTextAsync(payloadPath, "{\"value\":true}");
                var archivePayloadPath = $"payload/fragments/{fragmentId}.json";
                payloads[archivePayloadPath] = payloadPath;
                fragments.Add(new SunderStackFragmentManifest
                {
                    FragmentId = fragmentId,
                    OwnerPackageId = contributor.PackageId,
                    ContributorId = contributor.ContributorId,
                    SchemaId = contributor.ContributorId + "/schema",
                    SchemaVersion = 1,
                    DisplayName = contributor.ContributorId,
                    DefaultSelected = true,
                    PayloadPath = archivePayloadPath,
                });
            }

            await SunderStackArchiveWriter.WriteAsync(new SunderStackManifest
            {
                SchemaVersion = SunderStackFormat.CurrentSchemaVersion,
                MinReaderVersion = SunderStackFormat.CurrentReaderVersion,
                StackId = "test.stack",
                Name = "Test Stack",
                CreatedAtUtc = Clock.GetUtcNow(),
                UpdatedAtUtc = Clock.GetUtcNow(),
                Packages = [],
                Fragments = fragments,
            }, archivePath, payloads);
            await using var stream = File.OpenRead(archivePath);
            var upload = await Transfers.CreateUploadAsync(
                RuntimeUploadKind.Stack,
                stream,
                stream.Length,
                expectedHash: null,
                Path.GetFileName(archivePath),
                "application/vnd.sunder.stack",
                Owner.Generation,
                CancellationToken.None);

            return await Service.PreviewAsync(new RuntimeStackImportPreviewRequest(
                upload.UploadId,
                selectedFragmentIds,
                new Dictionary<string, string> { ["input"] = "preview-bound" },
                new Dictionary<string, string> { ["source"] = "target" }));
        }

        public ValueTask DisposeAsync()
        {
            Service.Dispose();
            Transfers.Dispose();
            try
            {
                Directory.Delete(Paths.RootPath, recursive: true);
            }
            catch
            {
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestContributor(string contributorId, bool succeeds = true) : IPackageStackContributor
    {
        public string PackageId => "test.package." + contributorId;
        public string ContributorId => contributorId;
        public string DisplayName => contributorId;
        public int ImportCount { get; private set; }

        public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
            StackExportDiscoveryContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<StackExportItemDescriptor>>([]);

        public ValueTask<StackExportContribution> ExportAsync(
            StackExportRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new StackExportContribution([], [], []));

        public ValueTask<StackImportPreview> PreviewImportAsync(
            StackImportPreviewRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new StackImportPreview(
                [new StackImportAction(ContributorId + "-action", "Import " + ContributorId, StackImportActionKind.Create)],
                [],
                [],
                []));

        public ValueTask<StackImportResult> ImportAsync(
            StackImportRequest request,
            CancellationToken cancellationToken = default)
        {
            ImportCount++;
            Assert.Equal("preview-bound", request.InputValues["input"]);
            Assert.Equal("target", request.IdRemaps["source"]);
            return ValueTask.FromResult(succeeds
                ? new StackImportResult(
                    StackImportOutcome.Completed,
                    [new StackImportedItem(ContributorId + "-item", ContributorId, "test")],
                    new Dictionary<string, string>(),
                    [],
                    [])
                : new StackImportResult(
                    StackImportOutcome.Failed,
                    [],
                    new Dictionary<string, string>(),
                    [],
                    ["Contributor failed."]));
        }
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value) => _utcNow += value;
    }
}
