using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Sunder.App.Services;
using Sunder.App.Tests.TestSupport;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed partial class StacksWindowViewModelTests
{
    [Fact]
    public async Task CompactLibraryAndWizardControlsRetainSelectionAndRespectMissingInputs()
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(SettingsWindowLayoutTests.SettingsTestApplication));
        await ui.Dispatch(async () =>
        {
            using var temp = TemporaryDirectoryScope.Create("sunder-stack-window");
            var file = await CreateStackArchiveAsync(temp.RootPath, "research", "Research desk", includeFragment: true,
                includeRequiredInput: true, includeDisplayDetails: true, fragmentDefaultSelected: true);
            var library = new LocalStackLibraryService(temp.GetPath("library"));
            await library.ImportAsync(file);
            const string agent = "younics.ai.agent";
            var runtime = new FakeRuntimeApiClient
            {
                ExportDiscoveryResponse = new([CreateExportItem(agent, "profile", "Agent profile", kind: "agent-profile")], [], []),
                ImportPreviewResponse = new(true, "review", DateTimeOffset.UtcNow.AddMinutes(5),
                    [new("profile", agent, "agent", "profile", "Agent profile", "Profile", true)],
                    [new("input", agent, "agent", "api-key", "API key", RuntimeStackInputSensitivity.Secret, true)], [], [], []),
            };
            var model = CreateViewModel(temp.RootPath, new FakeRegistryApiClient(), library, runtimeApiClient: runtime);
            model.ShowLocalCommand.Execute(null);
            var window = new StacksWindow { DataContext = model };
            window.Show();
            try
            {
                await AsyncAssert.WaitForConditionAsync(() => model.HasSelectedLocalDetails);
                await ManagementWindowAssert.CheckLayoutsAsync(window);
                ManagementWindowAssert.Click(window, window.FindControl<Button>("CompactStackBackButton")!);
                var list = window.GetVisualDescendants().OfType<ListBox>().Single(control => control.IsEffectivelyVisible);
                ManagementWindowAssert.Click(window, list.ContainerFromIndex(0)!);
                Assert.True(window.FindControl<Border>("StacksDetailPane")!.IsEffectivelyVisible);
                var contents = window.GetVisualDescendants().OfType<TabItem>().Single(tab => tab.IsEffectivelyVisible && Equals(tab.Header, "Contents"));
                ManagementWindowAssert.Click(window, contents);
                Assert.True(contents.IsSelected);

                var edit = Assert.IsType<CreateStackWizardViewModel>(model.CreateEditStackWizardViewModel());
                var wizard = new CreateStackWizardWindow { DataContext = edit };
                wizard.Show(window);
                try
                {
                    await AsyncAssert.WaitForConditionAsync(() => edit.CanGoNext);
                    await ManagementWindowAssert.CheckLayoutsAsync(wizard);
                    Assert.Contains(wizard.GetVisualDescendants().OfType<TextBox>(), field => field.IsReadOnly && field.Text == "research");
                    var next = wizard.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Next"));
                    ManagementWindowAssert.Click(wizard, next);
                    Assert.True(edit.IsItemsStep);
                    ManagementWindowAssert.Click(wizard, next);
                    Assert.True(edit.IsReviewStep);
                    Assert.Contains(wizard.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Save Changes") && button.IsEffectivelyEnabled);
                }
                finally { wizard.Close(); }

                var use = Assert.IsType<UseStackWizardViewModel>(model.CreateUseStackWizardViewModel());
                var useWindow = new UseStackWizardWindow { DataContext = use };
                useWindow.Show(window);
                try
                {
                    await AsyncAssert.WaitForConditionAsync(() => use.HasRequiredInputs);
                    await ManagementWindowAssert.CheckLayoutsAsync(useWindow);
                    var apply = useWindow.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Use Stack"));
                    Assert.False(apply.IsEffectivelyEnabled);
                    var input = useWindow.GetVisualDescendants().OfType<TextBox>().Single(text => text.IsEffectivelyVisible);
                    Assert.Equal('*', input.PasswordChar);
                    input.Text = "test input";
                    Assert.True(apply.IsEffectivelyEnabled);
                }
                finally { useWindow.Close(); }
            }
            finally { window.CloseForShutdown(); }
            return true;
        }, default);
    }
}
