using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.App.Views.Controls;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class SettingsWindowLayoutTests
{
    [Fact]
    public async Task PackagePagesOmitShellHeadingAndFrameForBothGeneratedFormsAndHostedViewsButCoreRetainsThem()
    {
        Assert.Null(Application.Current);
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(SettingsTestApplication));
        await ui.Dispatch(async () =>
        {
            using var temporary = TemporaryDirectoryScope.Create("sunder-settings-package-layout");
            using var client = new EmptySettingsClient
            {
                Schemas = [Schema("generated"), Schema("hosted")],
            };
            var services = new ServiceCollection().BuildServiceProvider();
            var registry = new AppPackageViewRegistry();
            registry.RegisterSettingsView<HostedLayoutView>("hosted", services);
            await using var host = new PackageViewHostService(registry, [], [services], [], sessionFolder: null);
            using var vm = new SettingsWindowViewModel(client, host, new CliInstallationService(),
                runtimeResourcePolicySettingsService: new AppRuntimeResourcePolicySettingsService(
                    Path.Combine(temporary.RootPath, "runtime-resource-policy.json")));
            await vm.RefreshPackageSectionsAsync();
            var window = new SettingsWindow { DataContext = vm };
            window.Show();
            try
            {
                var pane = window.FindControl<Border>("SettingsSelectionPane")!;
                var header = window.FindControl<StackPanel>("SettingsSelectionHeader")!;
                foreach (var package in new[] { "generated", "hosted" })
                {
                    Assert.True(await vm.SelectPackageSettingsAsync(package));
                    window.UpdateLayout();
                    Assert.False(header.IsEffectivelyVisible);
                    Assert.DoesNotContain("workspace-surface", pane.Classes);
                    Assert.Equal(new Thickness(0), pane.BorderThickness);
                    Assert.Equal(new Thickness(0), pane.Padding);
                    Assert.Equal(package + " package title", vm.SelectedTitle);
                    Assert.Contains(pane.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible
                        && text.Text == (package == "hosted" ? "Useful hosted heading" : "Useful form section"));
                    Assert.DoesNotContain(pane.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible
                        && (text.Text == vm.SelectedTitle || text.Text == vm.SelectedDescription));
                    if (package == "hosted")
                    {
                        var content = pane.GetVisualDescendants().OfType<HostedLayoutView>().Single();
                        Assert.InRange(Math.Abs(content.TranslatePoint(default, pane)!.Value.X + content.Bounds.Width - pane.Bounds.Width), 0, 0.5);
                        var scroll = Assert.IsType<ScrollViewer>(content.Content);
                        Assert.InRange(Math.Abs(scroll.TranslatePoint(default, pane)!.Value.X + scroll.Bounds.Width - pane.Bounds.Width), 0, 0.5);
                        AssertContentGuttersAndScrollbar(scroll, pane);
                    }
                    else
                    {
                        var scroll = window.FindControl<ScrollViewer>("SettingsSelectionScroll")!;
                        AssertContentGuttersAndScrollbar(scroll, pane);
                        var help = pane.GetVisualDescendants().OfType<SettingsHelpButton>().First(control => control.IsEffectivelyVisible);
                        var icon = Assert.IsType<Avalonia.Controls.Shapes.Path>(help.Content);
                        var origin = icon.TranslatePoint(default, help)!.Value;
                        Assert.InRange(Math.Abs(origin.X + icon.Bounds.Width / 2 - help.Bounds.Width / 2), 0, 0.5);
                        Assert.InRange(Math.Abs(origin.Y + icon.Bounds.Height / 2 - help.Bounds.Height / 2), 0, 0.5);
                        Assert.True(help.Focus());
                        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
                        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        var flyout = Assert.IsType<Flyout>(help.Flyout);
                        Assert.True(flyout.IsOpen);
                        var text = Assert.IsType<SelectableTextBlock>(flyout.Content);
                        var presenter = Assert.Single(text.GetVisualAncestors().OfType<FlyoutPresenter>());
                        Assert.Same(Application.Current!.Resources["Sunder.Brush.Surface.Hover"], presenter.Background);
                        Assert.Same(Application.Current.Resources["Sunder.Brush.Border.Strong"], presenter.BorderBrush);
                        Assert.NotEqual(Application.Current.Resources["Sunder.Brush.Surface.Base"], presenter.Background);
                        flyout.Hide();
                    }
                }
                Assert.True(await vm.SelectCoreSectionAsync("advanced"));
                window.UpdateLayout();
                Assert.True(header.IsEffectivelyVisible);
                Assert.Contains("workspace-surface", pane.Classes);
                Assert.Equal(new Thickness(1), pane.BorderThickness);
            }
            finally { window.CloseForShutdown(); }
            return true;
        }, default);
        Assert.Null(Application.Current);

        static PackageSettingsSchemaDescriptor Schema(string id) => new(id, id + " package title", id + " package summary",
            [new PackageSettingsSectionDescriptor("general", "Useful form section", "Section help", Enumerable.Range(0, 12)
                .Select(index => new PackageSettingsFieldDescriptor("field" + index, "Field " + index,
                    (PackageSettingsFieldKind)(index % 4), "Field-specific help", false, null, null,
                    [new("one", "One"), new("two", "Two")])).ToArray())]);
    }

    private static void AssertContentGuttersAndScrollbar(ScrollViewer scroll, Border pane)
    {
        var content = Assert.IsType<StackPanel>(scroll.Content);
        Assert.True(content.Margin.Left >= 16 && content.Margin.Right >= 16);
        Assert.InRange(content.TranslatePoint(default, pane)!.Value.X, 16, 24);
        var bar = scroll.GetVisualDescendants().OfType<ScrollBar>().Single(control => control.Orientation == Avalonia.Layout.Orientation.Vertical
            && ReferenceEquals(control.GetVisualAncestors().OfType<ScrollViewer>().First(), scroll));
        Assert.True(bar.IsEffectivelyVisible, $"Expected overflowing content: extent {scroll.Extent}, viewport {scroll.Viewport}.");
        Assert.InRange(Math.Abs(bar.TranslatePoint(default, pane)!.Value.X + bar.Bounds.Width - pane.Bounds.Width), 0, 1);
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 0.5);
    }

    public sealed class HostedLayoutView : UserControl
    {
        public HostedLayoutView() => Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(20), Children =
            { new TextBlock { Text = "Useful hosted heading" }, new Border { Height = 1800 } } } };
    }

    [Fact]
    public async Task SidebarReservesRightEdgeScrollbarAndPresetFieldsStayVisibleWithoutAnExpander()
    {
        Assert.Null(Application.Current);
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(SettingsTestApplication));
        await ui.Dispatch(async () =>
        {
            using var temporary = TemporaryDirectoryScope.Create("sunder-settings-layout");
            using var client = new EmptySettingsClient();
            using var vm = new SettingsWindowViewModel(client, PackageViewHostService.Empty, new CliInstallationService(),
                runtimeResourcePolicySettingsService: new AppRuntimeResourcePolicySettingsService(
                    Path.Combine(temporary.RootPath, "runtime-resource-policy.json")));
            await vm.RefreshPackageSectionsAsync();
            var root = new SettingsSectionItemViewModel("agent", "AI Agent", "", true, "agent");
            var children = Enumerable.Range(0, 25).Select(index => new SettingsSectionItemViewModel($"child-{index}",
                $"An intentionally very long dependent package name {index}", "", true, $"child-{index}")).ToArray();
            var group = new SettingsPackageNavigationGroup(root, children);
            vm.PackageSettings.NavigationGroups.Add(group);
            vm.PackageSettings.PackageSections.Add(root);
            var window = new SettingsWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.SelectCoreSectionAsync("advanced");
                window.UpdateLayout();
                Assert.False(group.IsExpanded);
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<Expander>(), control => control.IsEffectivelyVisible);
                var fields = window.GetVisualDescendants().OfType<TextBox>().Where(control => control.IsEffectivelyVisible).ToArray();
                Assert.Equal(6, fields.Length);
                Assert.All(fields, field => Assert.True(field.IsReadOnly));
                Assert.Contains(fields, field => field.Text == "32");
                var rootButton = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                    button => ReferenceEquals(button.CommandParameter, root));
                var toggle = Assert.Single(window.GetVisualDescendants().OfType<ToggleButton>(),
                    button => ReferenceEquals(button.DataContext, group));
                Assert.True(rootButton.Focusable && toggle.Focusable);
                var rootOrigin = rootButton.TranslatePoint(default, window)!.Value;
                var toggleOrigin = toggle.TranslatePoint(default, window)!.Value;
                Assert.True(toggleOrigin.X >= rootOrigin.X + rootButton.Bounds.Width);
                Assert.InRange(Math.Abs(toggleOrigin.Y - rootOrigin.Y), 0, 0.5);
                Assert.InRange(Math.Abs(toggle.Bounds.Height - rootButton.Bounds.Height), 0, 0.5);
                var toggleCenter = toggle.TranslatePoint(new Point(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2), window)!.Value;
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                window.MouseMove(toggleCenter);
                window.MouseDown(toggleCenter, MouseButton.Left);
                window.MouseUp(toggleCenter, MouseButton.Left);
                window.UpdateLayout();
                Assert.True(group.IsExpanded, $"Disclosure at {toggleCenter}, bounds {toggle.Bounds}, checked {toggle.IsChecked} must expand its group.");
                Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(toggle.Background).Color.A);
                var disclosurePresenter = Assert.Single(toggle.GetVisualDescendants().OfType<ContentPresenter>());
                Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(disclosurePresenter.Background).Color.A);
                Assert.Equal("Advanced", vm.SelectedTitle);
                var rootCenter = rootButton.TranslatePoint(new Point(rootButton.Bounds.Width / 2, rootButton.Bounds.Height / 2), window)!.Value;
                window.MouseDown(rootCenter, MouseButton.Left);
                window.MouseUp(rootCenter, MouseButton.Left);
                Assert.True(root.IsSelected);
                Assert.True(group.IsExpanded);
                Assert.Equal("AI Agent", vm.SelectedTitle);
                var rootRow = rootButton.GetVisualAncestors().OfType<Border>().Single(border => border.Classes.Contains("settings-package-row"));
                Assert.Same(Application.Current!.Resources["Sunder.Brush.Surface.Selected"], rootRow.Background);
                Assert.Same(Application.Current.Resources["Sunder.Brush.Accent"], rootRow.BorderBrush);
                Assert.True(toggle.Focus(NavigationMethod.Tab));
                Assert.True(rootButton.Focus(NavigationMethod.Tab));
                Assert.Same(Application.Current.Resources["Sunder.Brush.Accent.Overlay"], rootButton.Background);
                Assert.True(toggle.Focus(NavigationMethod.Tab));
                window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                Assert.False(group.IsExpanded);
                Assert.True(root.IsSelected);
                Assert.True(toggle.Focus(NavigationMethod.Tab));
                window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                Assert.True(group.IsExpanded);
                await vm.SelectCoreSectionAsync("advanced");
                window.UpdateLayout();
                var sidebar = window.FindControl<Border>("SettingsSidebarPane")!;
                var viewer = window.FindControl<ScrollViewer>("SettingsSidebarScrollViewer")!;
                var bar = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollBar>(), control => control.Orientation == Avalonia.Layout.Orientation.Vertical);
                Assert.True(bar.IsEffectivelyVisible);
                var barTop = bar.TranslatePoint(default, sidebar)!.Value;
                Assert.InRange(Math.Abs(barTop.X + bar.Bounds.Width - sidebar.Bounds.Width), 0, 1);
                Assert.All(viewer.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible
                    && button.Classes.Contains("settings-nav-button")), button =>
                {
                    var origin = button.TranslatePoint(default, sidebar)!.Value;
                    Assert.True(origin.X + button.Bounds.Width <= barTop.X + 0.5);
                });
                vm.RuntimeResources.SelectedPresetOption = vm.RuntimeResources.PresetOptions.Single(option => option.Preset == RuntimeResourcePolicyPreset.Custom);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                Assert.All(fields, field => Assert.False(field.IsReadOnly));
            }
            finally { window.CloseForShutdown(); }
            return true;
        }, default);
        Assert.Null(Application.Current);
    }

    public sealed class SettingsTestApplication : Application
    {
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<SettingsTestApplication>().UseHeadless(new());
        public override void Initialize()
        {
            RequestedThemeVariant = ThemeVariant.Dark;
            Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Sunder.App/"))
                { Source = new Uri("avares://Sunder.Sdk.Avalonia/Themes/SunderThemeResources.axaml") });
            Styles.Add(new FluentTheme());
            Styles.Add(new StyleInclude(new Uri("avares://Sunder.App/"))
                { Source = new Uri("avares://Sunder.Sdk.Avalonia/Themes/SunderPackageStyles.axaml") });
            Styles.Add(new StyleInclude(new Uri("avares://Sunder.App/"))
                { Source = new Uri("avares://Sunder.App/Styles/SunderShellStyles.axaml") });
            new ThemeManager(this).Initialize();
        }
    }

    private sealed class EmptySettingsClient : IRuntimePackageSettingsClient
    {
        public IReadOnlyList<PackageSettingsSchemaDescriptor> Schemas { get; init; } = [];
        public Task<IReadOnlyList<PackageSettingsSchemaDescriptor>> GetPackageSettingsSchemasAsync(CancellationToken cancellationToken = default) => Task.FromResult(Schemas);
        public Task<PackageSettingsValuesResponse?> GetPackageSettingsValuesAsync(string packageId, CancellationToken cancellationToken = default) => Task.FromResult<PackageSettingsValuesResponse?>(null);
        public Task SavePackageSettingsValuesAsync(string packageId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }
}
