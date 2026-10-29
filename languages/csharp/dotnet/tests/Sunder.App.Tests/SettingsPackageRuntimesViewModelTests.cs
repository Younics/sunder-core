using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Tests.TestSupport;
using Sunder.Runtime.Contracts;
using Xunit;
using static Sunder.App.Tests.TestSupport.RuntimeManagementTestClient;

namespace Sunder.App.Tests;

public sealed class SettingsPackageRuntimesViewModelTests
{
    [Fact]
    public void ANewRuntimeMajorIsNotPresentedAsAnUpdateToAnOlderPackageRuntime()
    {
        var family = new PackageRuntimeFamilyViewModel(ConfiguredOfficialSourceId.DotNet, ".NET", "Package runtime");
        var installed = new InstalledPackageRuntimeItem(Installed(), null);
        var available = new AvailablePackageRuntimeItem(new("Future major", Resolution().Artifact with
            { Identity = Identity with { VendorVersion = "11.0.0" } }, false));
        family.Apply(installed, available, null, true, null, ["10.0.9"]);
        Assert.Equal("Installed", family.Status);
        Assert.False(family.HasPrimaryAction);
        Assert.Contains("Retained package runtimes: 10.0.9", family.Details);
    }

    [Fact]
    public async Task RuntimeFamilies_ShowTwoStableCardsAndOnlyTheLatestReleaseOfEachSource()
    {
        var current = Installed() with { Identity = Identity with { VendorVersion = "10.0.12" } };
        var client = new RuntimeManagementTestClient
        {
            Inventory = [Installed() with { Identity = Identity with { VendorVersion = "10.0.9" } }, current, Installed()],
            LoadCatalog = (request, _) => Task.FromResult(new RuntimeExecutionRuntimeCatalogPage(Identity.Rid, 0, 1, 50,
                [new(request.SourceId!.Value.ToString(), request.SourceId == ConfiguredOfficialSourceId.DotNet
                    ? Resolution().Artifact with { Identity = current.Identity }
                    : Resolution().Artifact with { Identity = Identity with { SourceId = ConfiguredOfficialSourceId.Node, VendorVersion = "24.21.0" } }, false)])),
        };
        using var page = Page(client);
        await page.ActivateAsync();
        Assert.Equal([".NET", "Node.js"], page.Families.Select(family => family.Title));
        Assert.Equal("Up to date", page.Families[0].Status);
        Assert.Same(current, page.Families[0].Installed!.Runtime);
        Assert.Equal("Not installed", page.Families[1].Status);
        Assert.Equal("Install", page.Families[1].PrimaryActionText);
        Assert.StartsWith("Last checked ", page.LastCheckedText);
        Assert.Equal([ConfiguredOfficialSourceId.DotNet, ConfiguredOfficialSourceId.Node], client.CatalogRequests.Select(request => request.SourceId!.Value));
        Assert.All(client.CatalogRequests, request => { Assert.Equal(0, request.Skip); Assert.Equal(1, request.Take); });
    }

    [Fact]
    public async Task RuntimeEvents_RefreshQuietlyWithoutReplacingCardsOrDisablingControls()
    {
        var events = new RuntimeChanges();
        var client = new RuntimeManagementTestClient();
        using var page = new SettingsPackageRuntimesViewModel(client, (_, _) => Task.FromResult(true),
            new BackgroundTaskQueueService(), () => client, changes: events, uiDispatcher: new ImmediateUiDispatcher());
        await page.ActivateAsync();
        var family = page.Families[0];
        var busyChanges = 0;
        page.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(page.IsBusy)) busyChanges++; };
        client.LoadInventory = _ => throw new HttpRequestException("Connection lost");
        events.Signal();
        await AsyncAssert.WaitForConditionAsync(() => page.HasError);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaded = new TaskCompletionSource<IReadOnlyList<InstalledExecutionRuntimeSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.LoadInventory = _ => { started.TrySetResult(); return loaded.Task; };
        for (var index = 0; index < 10; index++) events.Signal();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(page.IsBusy);
        Assert.True(page.CanAct);
        loaded.SetResult([]);
        await AsyncAssert.WaitForConditionAsync(() => family.Status == "Not installed" && !page.HasError);
        Assert.Same(family, page.Families[0]);
        Assert.False(page.HasError); // Reconnection clears the failed status read without a manual refresh.
        Assert.Equal(0, busyChanges);
        Assert.Equal(2, client.CatalogRequests.Count); // Notifications never hit the vendor catalogue.
        page.Deactivate();
        Assert.Equal(0, events.SubscriberCount);
        var count = client.Calls.Count;
        events.Signal();
        Assert.Equal(count, client.Calls.Count);
    }

    [Fact]
    public async Task OfflineCatalogKeepsInstalledStateAndDoesNotClaimUpToDate()
    {
        var client = new RuntimeManagementTestClient { LoadCatalog = (_, _) => throw new HttpRequestException("Offline") };
        using var page = Page(client);
        await page.ActivateAsync();
        Assert.Equal("Installed", page.Families[0].Status);
        Assert.True(page.Families[0].IsInstalled);
        Assert.True(page.Families[0].HasIssue);
        Assert.Equal("Not installed", page.Families[1].Status);
        Assert.False(page.Families[1].HasPrimaryAction);
    }

    [Fact]
    public async Task UnsupportedCatalogReleaseIsNotOfferedAsAnUpdate()
    {
        var client = new RuntimeManagementTestClient
        {
            LoadCatalog = (_, _) => Task.FromResult(new RuntimeExecutionRuntimeCatalogPage(Identity.Rid, 0, 1, 1,
                [new("End of support", Resolution().Artifact with
                {
                    Identity = Identity with { VendorVersion = "10.0.99" },
                    Support = Support() with { State = RuntimeExecutionRuntimeSupportState.EndOfLife },
                }, false)])),
        };
        using var page = Page(client);
        await page.ActivateAsync();
        Assert.Equal("Installed", page.Families[0].Status);
        Assert.False(page.Families[0].HasPrimaryAction);
        Assert.False(page.Families[1].HasPrimaryAction);
    }

    [Fact]
    public async Task ReopeningDuringNotificationRefreshDiscardsLateInventoryAndLoadsCurrentStatus()
    {
        var events = new RuntimeChanges();
        var client = new RuntimeManagementTestClient();
        using var queue = new BackgroundTaskQueueService();
        using var page = new SettingsPackageRuntimesViewModel(client, (_, _) => Task.FromResult(true), queue, () => client,
            changes: events, uiDispatcher: new ImmediateUiDispatcher());
        await page.ActivateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = new TaskCompletionSource<IReadOnlyList<InstalledExecutionRuntimeSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken refreshToken = default;
        client.LoadInventory = token => { refreshToken = token; started.TrySetResult(); return stale.Task; };
        events.Signal();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        page.Deactivate();
        Assert.True(refreshToken.IsCancellationRequested);
        client.LoadInventory = null;
        client.Status = new(false, DateTimeOffset.UtcNow.AddMinutes(1), null, null, [Prepared()]);
        var reopening = page.ActivateAsync();
        stale.SetResult([]); // A transport which ignores cancellation cannot overwrite the new page.
        await reopening.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(page.Families[0].IsInstalled);
        Assert.Equal("Update downloaded", page.Families[0].Status);
        Assert.True(page.Families[0].HasPreparedUpdate);
        Assert.Equal(1, events.SubscriberCount);
        Assert.True(page.CanAct);
        Assert.False(page.HasError);
    }

    [Fact]
    public async Task FamilyInstallProgressAndCompletionAreDrivenByQueueEvents()
    {
        var installed = new TaskCompletionSource<RuntimeExecutionRuntimeOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RuntimeManagementTestClient { Inventory = [], InstallHandler = (_, _) => installed.Task };
        using var queue = new BackgroundTaskQueueService();
        using var page = new SettingsPackageRuntimesViewModel(client, (_, _) => Task.FromResult(true), queue, () => client,
            uiDispatcher: new ImmediateUiDispatcher());
        await page.ActivateAsync();
        var family = page.Families[0];
        await page.PrimaryActionCommand.ExecuteAsync(family);
        await AsyncAssert.WaitForConditionAsync(() => client.InstallConfirmation is not null);
        Assert.True(family.IsWorking);
        Assert.False(page.IsBusy);
        client.Inventory = [Installed()];
        installed.SetResult(new(true, null, "Installed", "install", Installed(), false));
        await AsyncAssert.WaitForConditionAsync(() => family.IsInstalled && !family.IsWorking);
        Assert.Equal("Up to date", family.Status);
    }

    [Fact]
    public async Task HostDefaultOn_OptOutUsesCASAndSurvivesNewPageWithoutLocalPreference()
    {
        var client = new RuntimeManagementTestClient();
        using (var page = Page(client))
        {
            await page.ActivateAsync();
            Assert.True(page.AutomaticUpdatesEnabled);
            Assert.Empty(client.SettingsWrites);
            page.AutomaticUpdatesEnabled = false;
            Assert.False(client.Settings.AutomaticUpdatesEnabled);
            Assert.Equal(new(1, false), Assert.Single(client.SettingsWrites));
        }
        using var reopened = Page(client);
        await reopened.ActivateAsync();
        Assert.False(reopened.AutomaticUpdatesEnabled);
        Assert.Single(client.SettingsWrites);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedCASPreservesLastHostPreferenceWithoutDefaultingOnOrRetrying(bool previous)
    {
        var client = new RuntimeManagementTestClient { FailSettingsWrite = true, Settings = new(7, previous) };
        using var page = Page(client);
        await page.ActivateAsync();
        page.AutomaticUpdatesEnabled = !previous;
        Assert.Equal(previous, page.AutomaticUpdatesEnabled);
        Assert.Equal(previous, client.Settings.AutomaticUpdatesEnabled);
        Assert.Contains("not confirmed", page.ErrorText);
        Assert.Single(client.SettingsWrites);
    }

    [Fact]
    public async Task UnreadableHostPreferenceNeverWritesDefaultOrEnablesCheckbox()
    {
        var client = new RuntimeManagementTestClient { LoadSettings = _ => throw new InvalidOperationException("Corrupt settings") };
        using var page = Page(client);
        await page.ActivateAsync();
        Assert.False(page.CanChangePreference);
        Assert.NotEmpty(page.ErrorText);
        Assert.Empty(client.SettingsWrites);
    }

    [Fact]
    public async Task HideDuringRequestCancelsAndPreventsFollowOnRequestsEvenIfTransportIgnoresCancellation()
    {
        var loaded = new TaskCompletionSource<RuntimeExecutionRuntimeSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var client = new RuntimeManagementTestClient { LoadSettings = token => { observed = token; return loaded.Task; } };
        using var page = Page(client);
        var opening = page.ActivateAsync();
        page.Deactivate();
        Assert.True(observed.IsCancellationRequested);
        loaded.SetResult(new(1));
        await opening;
        Assert.Equal(["settings"], client.Calls);
        await page.RefreshAsync();
        Assert.Equal(["settings"], client.Calls);
    }

    [Fact]
    public async Task ReopenWhileCancelledRequestFinishesLoadsFreshAuthoritativePreference()
    {
        var first = new TaskCompletionSource<RuntimeExecutionRuntimeSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var client = new RuntimeManagementTestClient { LoadSettings = _ => ++reads == 1 ? first.Task : Task.FromResult(new RuntimeExecutionRuntimeSettings(4, false)) };
        using var page = Page(client);
        var opening = page.ActivateAsync();
        page.Deactivate();
        var reopening = page.ActivateAsync();
        first.SetResult(new(1));
        await Task.WhenAll(opening, reopening);
        Assert.Equal(2, reads);
        Assert.False(page.AutomaticUpdatesEnabled);
        Assert.True(page.CanChangePreference);
        Assert.Empty(client.SettingsWrites);
    }

    [Fact]
    public async Task ManualInstallIsQueuedAndRetainedUntilExplicitRemoval()
    {
        var client = new RuntimeManagementTestClient();
        using var page = Page(client);
        await page.ActivateAsync();
        await page.InstallCommand.ExecuteAsync(Assert.Single(page.Available));
        await TestSupport.AsyncAssert.WaitForConditionAsync(() => client.InstallConfirmation is not null);
        Assert.All(client.InstallPreviews, item => Assert.True(item.RetainUntilUninstall));
        Assert.Equal(Plan().Fingerprint, client.InstallConfirmation!.ExpectedFingerprint);
    }

    [Fact]
    public async Task ReferencedRuntimeRemovalIsDisabledAndDoesNotApply()
    {
        var client = new RuntimeManagementTestClient { Inventory = [Installed(1)] };
        using var page = Page(client);
        await page.ActivateAsync();
        var row = Assert.Single(page.Installed);
        Assert.False(row.CanRemove);
        Assert.Contains("demo", row.UsedBy);
        await page.RemoveCommand.ExecuteAsync(row);
        Assert.DoesNotContain("remove", client.Calls);
    }

    [Fact]
    public async Task RemoveRechecksUsageAndDisablesRowWhenAnotherPackageStartedUsingIt()
    {
        var client = new RuntimeManagementTestClient();
        using var page = Page(client);
        await page.ActivateAsync();
        var row = Assert.Single(page.Installed);
        Assert.True(row.CanRemove);
        client.Inventory = [Installed(1)];
        await page.RemoveCommand.ExecuteAsync(row);
        Assert.DoesNotContain("remove", client.Calls);
        Assert.False(Assert.Single(page.Installed).CanRemove);
        Assert.Contains("Still used", page.ErrorText);
    }

    [Fact]
    public async Task HidingDuringConfirmationCannotStartDownloadAfterLateApproval()
    {
        var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RuntimeManagementTestClient();
        using var page = new SettingsPackageRuntimesViewModel(client, (_, _) => approval.Task,
            new BackgroundTaskQueueService(), () => client, uiDispatcher: new ImmediateUiDispatcher());
        await page.ActivateAsync();
        var install = page.InstallCommand.ExecuteAsync(Assert.Single(page.Available));
        page.Deactivate();
        approval.SetResult(true);
        await install;
        Assert.Null(client.InstallConfirmation);
    }

    [Fact]
    public async Task ManualRuntimeUpdateWithAutoOffDefers_RestartUsesFreshImpactAndExplicitConsent()
    {
        var client = new RuntimeManagementTestClient { Settings = new(3, false) };
        var confirmations = new List<RuntimeConfirmationViewModel>();
        using var page = Page(client, review => { confirmations.Add(review); return true; });
        await page.ActivateAsync();
        await page.UpdateRuntimeCommand.ExecuteAsync(Assert.Single(page.Installed));
        Assert.False(page.AutomaticUpdatesEnabled);
        Assert.Equal(RuntimeExecutionRuntimeApplyMode.WhenStopped, client.UpdateApply!.Mode);
        Assert.Contains("waiting", page.StatusText);
        Assert.DoesNotContain("applied", page.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Single(confirmations);

        client.UpdatePreview = request => UpdatePlan(request.PreparedUpdateId, 'e') with
        {
            Usage = new(new('f', 64), [], ["demo", "other.active.package"], ["demo", "other.active.package"], 2, 0, 0, 1, true),
        };
        await page.RestartApplyCommand.ExecuteAsync(Assert.Single(page.Prepared));
        Assert.Equal(2, confirmations.Count);
        Assert.Contains(confirmations[1].Items, text => text.Contains("Sunder also needs to restart"));
        Assert.Contains(confirmations[1].Items, text => text.Contains("other.active.package"));
        Assert.Equal(RuntimeExecutionRuntimeApplyMode.RestartAffectedPackages, client.UpdateApply.Mode);
        Assert.Equal(new string('f', 64), client.UpdateApply.ExpectedImpactFingerprint);
        Assert.Equal(new string('e', 64), client.UpdateApply.Confirmation.ExpectedFingerprint);
        Assert.Contains("applied", page.StatusText);
    }

    [Fact]
    public async Task DecliningRestartKeepsPreparedUpdateAndMakesNoApplyCall()
    {
        var client = new RuntimeManagementTestClient { Status = new(false, null, null, null, [Prepared()]) };
        using var page = Page(client, _ => false);
        await page.ActivateAsync();
        await page.RestartApplyCommand.ExecuteAsync(Assert.Single(page.Prepared));
        Assert.Null(client.UpdateApply);
        Assert.Contains("still waiting", page.StatusText);
    }

    [Fact]
    public async Task ManuallyPreparedUpdateCanBeAppliedSafelyWithAutomaticUpdatesOff()
    {
        var client = new RuntimeManagementTestClient { Settings = new(4, false), Status = new(false, null, null, null, [Prepared()]) };
        using var page = Page(client, _ => throw new InvalidOperationException("Safe application must not ask for restart permission"));
        await page.ActivateAsync();
        await page.ApplyWhenStoppedCommand.ExecuteAsync(Assert.Single(page.Prepared));
        Assert.Equal(RuntimeExecutionRuntimeApplyMode.WhenStopped, client.UpdateApply!.Mode);
        Assert.False(page.AutomaticUpdatesEnabled);
    }

    [Theory]
    [InlineData(RuntimeExecutionRuntimeUpdateState.ReconciliationPending)]
    [InlineData(RuntimeExecutionRuntimeUpdateState.Applied)]
    public async Task RuntimeResultWithoutStoreCommitDoesNotClaimActivatedOrOfferSecurityBypass(RuntimeExecutionRuntimeUpdateState state)
    {
        var client = new RuntimeManagementTestClient
        {
            Status = new(false, null, null, null, [Prepared()]),
            ApplyResult = _ => new(true, null, "Runtime response", state, Prepared(state), StoreCommitted: false, RuntimeSessionApplied: false),
        };
        using var page = Page(client);
        await page.ActivateAsync();
        await page.ApplyWhenStoppedCommand.ExecuteAsync(Assert.Single(page.Prepared));
        Assert.DoesNotContain("Runtime update applied", page.StatusText);
        Assert.DoesNotContain("bindings saved", page.StatusText);
    }

    internal static SettingsPackageRuntimesViewModel Page(RuntimeManagementTestClient client, Func<RuntimeConfirmationViewModel, bool>? confirm = null)
        => new(client, (review, _) => Task.FromResult(confirm?.Invoke(review) ?? true), new BackgroundTaskQueueService(), () => client,
            uiDispatcher: new ImmediateUiDispatcher());

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task InvokeAsync(Func<Task> action) => action();
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }

    private sealed class RuntimeChanges : IRuntimeExecutionRuntimeStatusSource
    {
        public event Action? ExecutionRuntimesChanged;
        internal int SubscriberCount => ExecutionRuntimesChanged?.GetInvocationList().Length ?? 0;
        internal void Signal() => ExecutionRuntimesChanged?.Invoke();
    }
}
