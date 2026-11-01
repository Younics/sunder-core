using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Stacks;
using System.Text.Json;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RuntimeStackImportPlanTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExportAsync_HostFallbackDoesNotOverrideAuthoredTagOrInventAOnePointZeroMinimum(bool authored)
    {
        var contributor = new TestContributor("one", packageVersion: "0.5.0",
            exportRequirements: authored ? [new("test.package.one", "preview", "0.5.0", "0.3.0")] : []);
        await using var fixture = await StackImportFixture.CreateAsync(contributor);
        var operation = await fixture.ExportService.ExportAsync(new("test.stack", "Test", null,
            [new("test.package.one", "one", "item")], SelectedPackages: ["test.package.one"]));
        Assert.True(operation.Response.Success, string.Join(" ", operation.Response.Errors));
        var download = fixture.Transfers.AcquireDownload(operation.Response.Download!.DownloadId, fixture.Owner.Generation)!;
        var validation = await SunderStackArchiveInspector.ExtractAndValidateAsync(download.FilePath, Path.Combine(fixture.Paths.RootPath, "export-check"));
        Assert.True(validation.Success, string.Join(" ", validation.Errors));
        var requirement = Assert.Single(validation.Manifest!.Packages!);
        Assert.Equal(authored ? "preview" : "latest", requirement.InstallTag);
        Assert.Equal(authored ? "0.3.0" : null, requirement.MinimumVersion);
        Assert.Equal("0.5.0", requirement.CreatedWithVersion);
    }

    [Theory]
    [InlineData("missing.package", null, true, false)]
    [InlineData("missing.package", null, false, true)]
    [InlineData("test.package.one", "2.0.0", false, false)]
    [InlineData("test.package.one", "0.5.0", true, true)]
    public async Task PreviewAsync_EnforcesPackageRequirementsAtRuntime(
        string packageId, string? minimum, bool required, bool expectedSuccess)
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));
        var preview = await fixture.PreviewAsync(["one-fragment"],
            [new SunderStackPackageRequirement { PackageId = packageId, InstallTag = "preview", MinimumVersion = minimum, Required = required }]);
        Assert.Equal(expectedSuccess, preview.Success);
        Assert.Equal(0, fixture.Contributors[0].ImportCount);
        if (!expectedSuccess) { Assert.Null(preview.PlanId); Assert.NotEmpty(preview.Errors); }
        if (packageId == "missing.package" && !required) Assert.NotEmpty(preview.Warnings);
    }

    [Fact]
    public async Task ImportAsync_MissingRequiredInputsCannotBypassPreviewReview()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));
        var preview = await fixture.PreviewAsync(["one-fragment"], includeInputValues: false);
        Assert.True(preview.Success); // Inputs must still be discoverable before the user supplies them.
        var result = await fixture.Service.ImportAsync(new(preview.PlanId!, ["one-fragment"], ActionIds(preview)));
        Assert.Equal(RuntimeStackImportOutcome.Failed, result.Outcome);
        Assert.Contains(result.Errors, error => error.Contains("required input", StringComparison.Ordinal));
        Assert.Equal(0, fixture.Contributors[0].ImportCount);
    }

    [Fact]
    public async Task ImportAsync_ExplicitEmptySelectionNeverRestoresDefaultsOrCallsContributors()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));
        var preview = await fixture.PreviewAsync([], useDefaultFragments: false);
        Assert.True(preview.Success);
        Assert.Empty(preview.Actions);
        var result = await fixture.Service.ImportAsync(new(preview.PlanId!, [], []));
        Assert.Equal(RuntimeStackImportOutcome.Completed, result.Outcome);
        Assert.Equal(0, fixture.Contributors[0].ImportCount);
    }

    [Fact]
    public async Task ImportAsync_NoSelectedActionsDoesNotRequireValuesOrInvokeASelectedFragmentsContributor()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));
        var preview = await fixture.PreviewAsync(["one-fragment"], includeInputValues: false);
        Assert.True(preview.Success);
        Assert.Single(preview.RequiredInputs);
        var result = await fixture.Service.ImportAsync(new(preview.PlanId!, ["one-fragment"], []));
        Assert.Equal(RuntimeStackImportOutcome.Completed, result.Outcome);
        Assert.Equal(0, fixture.Contributors[0].ImportCount);
    }

    [Fact]
    public void PreviewRequest_OmittedSelectionModeRetainsLegacyEmptyMeansDefaultSemantics()
    {
        var request = JsonSerializer.Deserialize<RuntimeStackImportPreviewRequest>(
            """{"uploadId":"upload","selectedFragmentIds":[],"inputValues":{},"idRemaps":{}}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(request);
        Assert.True(request.UseDefaultFragments);
        Assert.False((request with { UseDefaultFragments = false }).UseDefaultFragments);
    }
}
