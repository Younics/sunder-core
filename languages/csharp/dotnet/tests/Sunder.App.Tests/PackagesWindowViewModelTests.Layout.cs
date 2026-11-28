using Avalonia.Controls;
using Avalonia.VisualTree;
using Sunder.App.Services;
using Sunder.App.Tests.TestSupport;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed partial class PackagesWindowViewModelTests
{
    [Fact]
    public async Task BoundTechnicalTabKeepsTheReviewedReleaseAcrossRefreshAndCancelledInstall()
    {
        await HeadlessAppCollection.Session.Dispatch(async () =>
        {
            const string id = "younics.ai.agent";
            const string description = "An agent for local projects.";
            var chosenDigest = new string('c', 64);
            var now = DateTimeOffset.UtcNow;
            var profile = new RegistryPackageProfile(id, description, "# Getting started\nChoose a project.", "https://example.test/",
                "https://example.test/source", "https://example.test/issues", "MIT", ["agent", "local"], [], now);
            var artifact = CreateResolvedRuntimeArtifact(AppPackageTargetEnvironment.CurrentRid, RuntimeExecutionRuntimeSupportState.Supported);
            var requirement = new RegistryExecutionRuntimeRequirement("sunder.runtime.dotnet", ">=10.0.0 <11.0.0", "sunder.dotnet-worker-module.v1");
            var plan = CreateRuntimePlan(requirement, artifact);
            var runtime = new FakeRuntimeApiClient([CreateInstalledPackage("other", true)])
            {
                RegistryPlanResult = plan with { ResolutionFingerprint = new string('a', 64) },
                ExecutionRuntimes = [CreateInstalledRuntime(artifact)],
            };
            var registry = new FakeRegistryApiClient
            {
                SearchResults = _ => [CreateRegistryPackage(id, "2.0.0")],
                Overview = (package, version, rid, _) => Task.FromResult<RegistryPackageDetails?>(
                    CreateRegistryPackageDetails(package, "2.0.0", profile) with
                    {
                        Stats = new(4, 0, 0, [], 2, runtime.LastRegistryStarRequest is not null),
                        Creator = new("publisher", "Publisher"),
                        Maintainers = [new("publisher", "Publisher", IsOwner: true), new("team", "Team")],
                        Versions = [new("2.0.0", false, null, now), new("1.0.0", false, null, now.AddDays(-1))],
                        SelectedRelease = new(package, version ?? "2.0.0", rid, "Release summary", false, null, now,
                            [new("runtime", rid)], [requirement],
                            new(new string('a', 64), [new("runtime", rid, version == "1.0.0" ? "core.v1" : "secrets.v1")],
                                [new("other", ">=1.0.0", "optional", [])], [], []),
                            version == "1.0.0" ? chosenDigest : new string('d', 64), 8L * 1024 * 1024),
                    }),
            };
            var window = new PackagesWindow();
            var opened = new TaskCompletionSource<PackageCapabilityConsentWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var queue = new BackgroundTaskQueueService();
            using var installer = new RegistryPackageInstallService((review, _) => AvaloniaUiDispatcher.Instance.InvokeAsync(async () =>
            {
                var dialog = new PackageCapabilityConsentWindow(new(review));
                var result = dialog.ShowDialog<bool>(window);
                opened.SetResult(dialog);
                return await result;
            }), queue);
            using var operations = new PackageOperationService(queue, new FakeRuntimeApiClientFactory(runtime),
                (_, _) => Task.CompletedTask, CreateNotificationCenter(), installer, _ => registry);
            var model = new PackagesWindowViewModel(runtime, new FakePackageArchivePicker(), operations, queue, _ => registry)
                { RegistryUrlText = registry.RegistryUrl.AbsoluteUri };
            var releaseTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var unrelated = queue.Enqueue(new("Unrelated package task", "other", BackgroundTaskIndicator.Packages,
                BackgroundTaskConcurrencyMode.SequentialWithinGroup, true, _ => releaseTask.Task));
            window.DataContext = model;
            window.Show();
            try
            {
                await AsyncAssert.WaitForConditionAsync(() => model.MarketplacePackageDetailsLoaded && !model.Operations.IsBusy);
                Assert.Equal(1, registry.OverviewCalls);
                Assert.Equal(description, model.SelectedPackageSummary);
                Assert.Equal(["Creator", "Maintainer"], model.Marketplace.Attributions.Select(person => person.Role));
                Assert.True(model.PackageProcesses.ShowFooter);
                await ManagementWindowAssert.CheckLayoutsAsync(window);

                var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(control => control.IsEffectivelyVisible);
                void OpenTab(string header) => ManagementWindowAssert.Click(window, tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, header)));
                var viewAccess = tabs.GetVisualDescendants().OfType<Button>().Single(button => button.IsEffectivelyVisible && Equals(button.Content, "View access ›"));
                ManagementWindowAssert.Click(window, viewAccess);
                Assert.Equal(1, model.MarketplaceTabIndex);
                OpenTab("Versions");
                var versions = tabs.GetVisualDescendants().OfType<ListBox>().Single(list => list.IsEffectivelyVisible);
                ManagementWindowAssert.Click(window, versions.ContainerFromIndex(1)!);
                Assert.Equal("1.0.0", model.SelectedMarketplaceVersion?.Version);
                OpenTab("Technical details");
                Assert.Equal(3, model.MarketplaceTabIndex);
                ManagementWindowAssert.Click(window, window.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("star-action")));
                Assert.True(model.SelectedMarketplacePackageIsStarred);
                await model.RefreshCommand.ExecuteAsync(null);
                window.UpdateLayout();
                Assert.Equal(3, tabs.SelectedIndex);
                Assert.Contains(tabs.GetVisualDescendants().OfType<SelectableTextBlock>(), text => text.IsEffectivelyVisible && text.Text == chosenDigest);
                Assert.Equal(description, model.SelectedPackageSummary);
                Assert.True(model.SelectedMarketplacePackageIsStarred);
                Assert.Equal(0, runtime.PlanCalls);

                ManagementWindowAssert.Click(window, window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Install")));
                var review = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, runtime.PlanCalls);
                var change = Assert.Single(runtime.LastPlanRequest!.Packages);
                Assert.Equal(id, change.PackageId);
                Assert.Equal("1.0.0", change.Version);
                Assert.Contains(chosenDigest, ((PackageCapabilityConsentViewModel)review.DataContext!).ReviewDetails);
                Assert.Equal("Install package from marketplace", review.Title);
                Assert.Contains(review.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == review.Title);
                await ManagementWindowAssert.CheckLayoutsAsync(review);
                ManagementWindowAssert.Click(review, review.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel")));
                await model.InstallSelectedMarketplacePackageCommand.ExecutionTask!;
                Assert.Null(runtime.LastRegistryPackageRequest);
                Assert.Equal(unrelated.TaskId, Assert.Single(queue.ListTasks()).TaskId);
                Assert.Equal(3, tabs.SelectedIndex);
                Assert.Equal("1.0.0", model.SelectedMarketplaceVersion?.Version);
                Assert.Equal(description, model.SelectedPackageSummary);
                Assert.True(model.SelectedMarketplacePackageIsStarred);
                await model.ShowInstalledCommand.ExecuteAsync(null);
                window.UpdateLayout();
                Assert.Equal(0, model.InstalledTabIndex);
                await model.ShowMarketplaceCommand.ExecuteAsync(null);
                window.UpdateLayout();
                Assert.Equal(3, tabs.SelectedIndex);
                Assert.True(model.SelectedMarketplacePackageIsStarred);
            }
            finally
            {
                releaseTask.TrySetResult();
                window.CloseForShutdown();
            }
            return true;
        }, default);
    }
}
