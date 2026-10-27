using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.App.Views.Controls;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.App.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class PackageInformationLayoutTests
{
    [Theory]
    [InlineData(360)]
    [InlineData(900)]
    public async Task InformationWrapsLongPublicIdentitiesWithoutHorizontalScrolling(double width)
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(SettingsWindowLayoutTests.SettingsTestApplication));
        await ui.Dispatch(() =>
        {
            var identity = "example." + new string('x', 110);
            var facts = new SunderPackageInformation([
                new("runtime", "osx-arm64", "secrets.v1"), new("runtime", "osx-arm64", "docker-environment.v1"),
                new("app", "osx-arm64", "storage.v1")],
                [new(identity, ">=1.0.0 <2.0.0", "optional", [new("runtime", identity, "1.0.0", "implements")])],
                [new(identity, "1.0.0+" + new string('b', 70), "service", [new("runtime", "example.provider")])],
                [new("runtime", identity, "1.0.0", true, ["discover", "invoke", "subscribe"])]);
            var view = new PackageInformationView { DataContext = PackageInformationViewModel.Create(facts, "osx-arm64") };
            var scroll = new ScrollViewer { Content = view, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            var window = new Window { Width = width, Height = 700, Content = scroll };
            window.Show();
            try
            {
                window.UpdateLayout();
                var more = Assert.Single(view.GetVisualDescendants().OfType<Expander>(), expander => expander.Header is TextBlock { Text: "More" });
                Assert.False(more.IsExpanded);
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "Package storage");
                more.IsExpanded = true;
                window.UpdateLayout();
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "Package storage");
                more.IsExpanded = false;
                window.UpdateLayout();
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "Package storage");
                for (var pass = 0; pass < 3; pass++)
                {
                    foreach (var expander in view.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = true;
                    window.UpdateLayout();
                }
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
                Assert.InRange(scroll.Extent.Width, 0, scroll.Viewport.Width + 1);
                var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(value => value.IsEffectivelyVisible).ToArray();
                Assert.Contains(texts, value => value.Text == "Public bindings");
                Assert.Contains(texts, value => value.Text == "Sensitive access");
                Assert.DoesNotContain(texts, value => value.Text is "Important access" or "Show all Sunder features" or "RPC Access" or "discover | invoke | subscribe");
                Assert.All(texts, text =>
                {
                    var origin = text.TranslatePoint(default, view)!.Value;
                    Assert.True(origin.X >= -1 && origin.X + text.Bounds.Width <= view.Bounds.Width + 1,
                        $"Text overflows a {width}px details pane: {text.Text}; x={origin.X}, width={text.Bounds.Width}");
                });
            }
            finally { window.Close(); }
        }, default);
    }

    [Fact]
    public async Task NarrowPackageWindowUsesListDetailNavigationAndRestoresItsWideSidebar()
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(SettingsWindowLayoutTests.SettingsTestApplication));
        await ui.Dispatch(async () =>
        {
            var window = new PackagesWindow { Width = 1200, Height = 760 };
            window.Show();
            try
            {
                window.UpdateLayout();
                var grid = window.FindControl<Grid>("PackagesContentGrid")!;
                var list = window.FindControl<Border>("PackagesListPane")!;
                var details = window.FindControl<Border>("PackagesDetailPane")!;
                var back = window.FindControl<Button>("CompactPackageBackButton")!;
                var wide = grid.ColumnDefinitions[0].Width;
                Assert.True(list.IsVisible && details.IsVisible);
                window.Width = 650;
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                Assert.False(list.IsVisible, $"Window={window.Bounds.Width}; content={grid.Bounds.Width}");
                Assert.True(details.IsVisible && back.IsVisible);
                Assert.Equal(0, Grid.GetColumn(details));
                back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(list.IsVisible);
                Assert.False(details.IsVisible);
                window.Width = 1200;
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                Assert.True(list.IsVisible && details.IsVisible);
                Assert.False(back.IsVisible);
                Assert.Equal(wide, grid.ColumnDefinitions[0].Width);
            }
            finally { window.CloseForShutdown(); }
        }, default);
    }
}
