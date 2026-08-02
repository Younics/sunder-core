using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class SdkContractValidationTests
{
    [Fact]
    public void BackgroundProcessContracts_DefensivelyFreezeMetadata()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["item"] = "original",
        };
        var request = new BackgroundProcessRequest(
            "Work",
            "work",
            BackgroundProcessIndicator.Hidden,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            true,
            _ => Task.CompletedTask,
            metadata);
        var snapshot = new BackgroundProcessSnapshot(
            Guid.NewGuid(),
            request.Title,
            request.GroupKey,
            request.Indicator,
            request.ConcurrencyMode,
            BackgroundProcessState.Queued,
            "Queued",
            null,
            true,
            metadata,
            null,
            DateTimeOffset.UtcNow,
            null,
            null);

        metadata["item"] = "changed";

        Assert.Equal("original", request.Metadata!["ITEM"]);
        Assert.Equal("original", snapshot.Metadata["ITEM"]);
        Assert.False((snapshot with { State = BackgroundProcessState.Completed }).CanCancel);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)request.Metadata).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)snapshot.Metadata).Clear());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void BackgroundProcessContext_RejectsNonFiniteProgress(double progress)
    {
        var context = new BackgroundProcessContext(
            CancellationToken.None,
            _ => { },
            (_, _) => { },
            _ => { });

        Assert.Throws<ArgumentOutOfRangeException>(() => context.ReportProgress(progress));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(42.5, 42.5)]
    [InlineData(101, 100)]
    public void BackgroundProcessContext_ClampsFiniteProgress(double progress, double expected)
    {
        double? reported = null;
        var context = new BackgroundProcessContext(
            CancellationToken.None,
            _ => { },
            (value, _) => reported = value,
            _ => { });

        context.ReportProgress(progress);

        Assert.Equal(expected, reported);
    }

    [Fact]
    public void PackageViewRegistration_ValidatesConstructorInputs()
    {
        Assert.Throws<ArgumentException>(() => new PackageViewRegistration(" ", "View"));
        Assert.Throws<ArgumentException>(() => new PackageViewRegistration("package.view", " "));
        Assert.Throws<ArgumentException>(() => new PackageViewRegistration(
            "package.view",
            "View",
            "icons\\view.png"));
        Assert.Throws<ArgumentException>(() => new PackageViewRegistration(
            "package.view",
            "View",
            "../view.png"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackageViewRegistration(
            "package.view",
            "View",
            defaultPlacement: (PackageViewPlacement)99));

        var registration = new PackageViewRegistration("package.view", "View", "icons/view.png");
        Assert.Equal("icons/view.png", registration.IconAssetPath);
    }

    [Fact]
    public async Task UnavailableOptionalCapabilities_ValidateInputsAndCancellationConsistently()
    {
        Assert.Throws<ArgumentException>(() =>
            NullPackageCallbackClient.Instance.OpenLaunchUriAsync(new Uri("relative", UriKind.Relative)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NullPackageCallbackClient.Instance.WaitForCompletionAsync("session", TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>(() =>
            NullPackageNotificationService.Instance.PublishAsync(null!));
        var oversizedParameters = Enumerable.Range(0, PackageCallbackParameters.MaximumCount + 1)
            .ToDictionary(index => $"key{index}", static _ => "value");
        Assert.Throws<ArgumentException>(() =>
            NullPackageCallbackClient.Instance.StartAsync("handler", oversizedParameters));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NullPackageCallbackClient.Instance.CancelAsync("session", cancellation.Token).AsTask());
    }

    [Fact]
    public void PackageCallbackSessionStatus_IdentifiesTerminalStates()
    {
        var pending = new PackageCallbackSessionStatus(
            "package",
            "handler",
            "session",
            PackageCallbackSessionState.Pending,
            "Pending",
            null,
            DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.False(pending.IsTerminal);
        Assert.True((pending with { State = PackageCallbackSessionState.Cancelled }).IsTerminal);
    }

    [Fact]
    public void PackageSettingsSchema_RejectsDuplicateFieldKeysAcrossSections()
    {
        var field = new PackageSettingsField("enabled", "Enabled", PackageSettingsFieldKind.Boolean);

        var exception = Assert.Throws<ArgumentException>(() => new PackageSettingsSchema(
            null,
            [
                new PackageSettingsSection("general", "General", null, [field]),
                new PackageSettingsSection("advanced", "Advanced", null, [field]),
            ]));

        Assert.Contains("declared more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageSettingsField_RejectsSecretDefault()
        => Assert.Throws<ArgumentException>(() => new PackageSettingsField(
            "api-key",
            "API key",
            PackageSettingsFieldKind.Secret,
            defaultValue: "must-not-ship"));

    [Fact]
    public void StackRequiredInput_RejectsSecretDefault()
    {
        Assert.Throws<ArgumentException>(() => new StackRequiredInputDescriptor(
            "api-key",
            "API key",
            StackValueSensitivity.Secret,
            DefaultValue: "must-not-ship"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StackRequiredInputDescriptor(
            "input",
            "Input",
            (StackValueSensitivity)99));
    }

    [Fact]
    public void StackSelectionHelpers_DefaultMissingDetailOverrideAndHonorItemSelection()
    {
        var selected = new StackExportRequest(
        [
            new StackExportItemSelection("profile",
            [
                new StackExportDetailSelection(
                    "model",
                    IsSelected: false,
                    ValueOverride: "ignored",
                    SensitivityOverride: StackValueSensitivity.Secret),
                new StackExportDetailSelection(
                    "instructions",
                    ValueOverride: "  concise  ",
                    SensitivityOverride: StackValueSensitivity.Secret),
            ]),
        ]);

        Assert.True(selected.IsItemSelected("PROFILE"));
        Assert.False(selected.IsDetailSelected("profile", "model", defaultSelected: true));
        Assert.True(selected.IsDetailSelected("profile", "instructions", defaultSelected: false));
        Assert.False(selected.IsDetailSelected("other", "instructions", defaultSelected: true));
        Assert.Equal("fallback", selected.GetDetailValue("profile", "model", "fallback", defaultSelected: true));
        Assert.Equal(StackValueSensitivity.Public, selected.GetDetailSensitivity("profile", "model", StackValueSensitivity.Public, defaultSelected: true));
        Assert.Equal("concise", selected.GetDetailValue("profile", "instructions", "fallback", defaultSelected: false));
        Assert.Equal(StackValueSensitivity.Secret, selected.GetDetailSensitivity("profile", "instructions", StackValueSensitivity.Public, defaultSelected: false));
        Assert.Equal("fallback", selected.GetDetailValue("other", "instructions", "fallback", defaultSelected: true));
        Assert.Equal(StackValueSensitivity.Public, selected.GetDetailSensitivity("other", "instructions", StackValueSensitivity.Public, defaultSelected: true));

        var defaults = new StackExportRequest([new StackExportItemSelection("profile")]);
        Assert.False(defaults.IsDetailSelected("profile", "disabled", defaultSelected: false));
        Assert.True(defaults.IsDetailSelected("profile", "enabled", defaultSelected: true));
    }

    [Fact]
    public void StackContracts_DefensivelyFreezePublicCollectionInputs()
    {
        var exportDetails = new List<StackExportItemDetail>
        {
            new("Detail", "Value", StackValueSensitivity.Public),
        };
        var descriptor = new StackExportItemDescriptor("item", "Item", "test", Details: exportDetails);
        AssertFrozen(exportDetails, descriptor.Details);

        var detailSelections = new List<StackExportDetailSelection> { new("detail") };
        var itemSelection = new StackExportItemSelection("item", detailSelections);
        AssertFrozen(detailSelections, itemSelection.Details);

        var itemSelections = new List<StackExportItemSelection> { itemSelection };
        var exportRequest = new StackExportRequest(itemSelections);
        AssertFrozen(itemSelections, exportRequest.ItemSelections);

        var fragment = new StackFragmentExport("fragment", "schema", 1, "Fragment", "{}");
        var fragments = new List<StackFragmentExport> { fragment };
        var packageRequirements = new List<StackPackageRequirement> { new("test.package") };
        var exportWarnings = new List<string> { "warning" };
        var contribution = new StackExportContribution(fragments, packageRequirements, exportWarnings);
        AssertFrozen(fragments, contribution.Fragments);
        AssertFrozen(packageRequirements, contribution.PackageRequirements);
        AssertFrozen(exportWarnings, contribution.Warnings);

        var requiredInputs = new List<StackRequiredInputDescriptor> { new("input", "Input", StackValueSensitivity.Secret) };
        var exportFiles = new List<StackExportPayloadHandle>
        {
            new("value.bin", _ => ValueTask.FromResult<Stream>(new MemoryStream())),
        };
        var fragmentExport = new StackFragmentExport(
            "fragment",
            "schema",
            1,
            "Fragment",
            "{}",
            RequiredInputs: requiredInputs,
            Files: exportFiles);
        AssertFrozen(requiredInputs, fragmentExport.RequiredInputs);
        AssertFrozen(exportFiles, fragmentExport.Files);

        var importFiles = new List<StackImportPayloadHandle>
        {
            new("value.bin", _ => ValueTask.FromResult<Stream>(new MemoryStream()), 0),
        };
        var fragmentImport = new StackFragmentImport(
            "fragment",
            "test.package",
            "test.contributor",
            "schema",
            1,
            "Fragment",
            "{}",
            Files: importFiles);
        AssertFrozen(importFiles, fragmentImport.Files);

        var previewFragments = new List<StackFragmentImport> { fragmentImport };
        var previewInputs = MutableDictionary("input", "value");
        var previewRemaps = MutableDictionary("source", "target");
        var previewRequest = new StackImportPreviewRequest(previewFragments, previewInputs, previewRemaps);
        AssertFrozen(previewFragments, previewRequest.Fragments);
        AssertFrozen(previewInputs, previewRequest.InputValues);
        AssertFrozen(previewRemaps, previewRequest.IdRemaps);

        var actions = new List<StackImportAction> { new("action", "Action", StackImportActionKind.Create) };
        var previewRequiredInputs = new List<StackRequiredInputDescriptor> { new("input", "Input", StackValueSensitivity.Secret) };
        var conflicts = new List<StackImportConflict> { new("conflict", "Conflict", StackImportConflictSeverity.Warning) };
        var previewWarnings = new List<string> { "warning" };
        var preview = new StackImportPreview(actions, previewRequiredInputs, conflicts, previewWarnings);
        AssertFrozen(actions, preview.Actions);
        AssertFrozen(previewRequiredInputs, preview.RequiredInputs);
        AssertFrozen(conflicts, preview.Conflicts);
        AssertFrozen(previewWarnings, preview.Warnings);

        var importFragments = new List<StackFragmentImport> { fragmentImport };
        var importInputs = MutableDictionary("input", "value");
        var importRemaps = MutableDictionary("source", "target");
        var selectedActionIds = new List<string> { "action" };
        var importRequest = new StackImportRequest(importFragments, importInputs, importRemaps, selectedActionIds);
        AssertFrozen(importFragments, importRequest.Fragments);
        AssertFrozen(importInputs, importRequest.InputValues);
        AssertFrozen(importRemaps, importRequest.IdRemaps);
        AssertFrozen(selectedActionIds, importRequest.SelectedActionIds);

        var importedItems = new List<StackImportedItem> { new("item", "Item", "test") };
        var resultRemaps = MutableDictionary("source", "target");
        var resultWarnings = new List<string> { "warning" };
        var resultErrors = new List<string> { "error" };
        var result = new StackImportResult(
            StackImportOutcome.Partial,
            importedItems,
            resultRemaps,
            resultWarnings,
            resultErrors);
        AssertFrozen(importedItems, result.ImportedItems);
        AssertFrozen(resultRemaps, result.IdRemaps);
        AssertFrozen(resultWarnings, result.Warnings);
        AssertFrozen(resultErrors, result.Errors);

        var appliedFragmentIds = new List<string> { "fragment" };
        var appliedItems = new List<StackImportedItem> { new("item", "Item", "test") };
        var applied = new StackImportAppliedContext(
            "test.package",
            "test.contributor",
            appliedFragmentIds,
            appliedItems);
        AssertFrozen(appliedFragmentIds, applied.FragmentIds);
        AssertFrozen(appliedItems, applied.ImportedItems);
    }

    [Fact]
    public async Task PackageFileStore_DefaultStreamMembersRoundTrip()
    {
        IPackageFileStore store = new MemoryPackageFileStore();
        await using var source = new MemoryStream([1, 2, 3]);

        await store.WriteAsync("data.bin", source);
        await using var result = await store.OpenReadAsync("data.bin");

        Assert.NotNull(result);
        using var copy = new MemoryStream();
        await result.CopyToAsync(copy);
        Assert.Equal([1, 2, 3], copy.ToArray());
    }

    private sealed class MemoryPackageFileStore : IPackageFileStore
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
            => Task.FromResult(_files.TryGetValue(relativePath, out var value) ? value.ToArray() : null);

        public Task WriteAsync(
            string relativePath,
            ReadOnlyMemory<byte> contents,
            CancellationToken cancellationToken = default)
        {
            _files[relativePath] = contents.ToArray();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            _files.Remove(relativePath);
            return Task.CompletedTask;
        }
    }

    private static Dictionary<string, string> MutableDictionary(string key, string value)
        => new(StringComparer.OrdinalIgnoreCase) { [key] = value };

    private static void AssertFrozen<T>(List<T> source, IReadOnlyList<T>? snapshot)
    {
        var expected = source.ToArray();

        source.Clear();

        Assert.NotNull(snapshot);
        Assert.Equal(expected, snapshot);
        Assert.Throws<NotSupportedException>(() => ((IList<T>)snapshot).Clear());
    }

    private static void AssertFrozen(
        Dictionary<string, string> source,
        IReadOnlyDictionary<string, string> snapshot)
    {
        var expected = source.ToArray();

        source.Clear();

        Assert.Equal(expected, snapshot);
        Assert.Equal(expected[0].Value, snapshot[expected[0].Key.ToUpperInvariant()]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)snapshot).Clear());
    }
}
