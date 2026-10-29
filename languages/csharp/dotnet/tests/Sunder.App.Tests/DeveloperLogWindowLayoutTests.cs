using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Sdk.Logging;
using Xunit;

namespace Sunder.App.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class DeveloperLogWindowLayoutTests
{
    [Fact]
    public async Task BoundPackageNavigationSelectsActualOptionAndRevealsLatestMatchingEntryOnReopen()
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(SettingsWindowLayoutTests.SettingsTestApplication));
        await ui.Dispatch(async () =>
        {
            using var logs = new DeveloperLogService();
            logs.Enable();
            var packageId = "local.log.navigation." + Guid.NewGuid().ToString("N");
            for (var index = 0; index < 160; index++)
            {
                logs.WritePackageLog(PackageLogLevel.Information, packageId, "matching entry " + index);
                logs.WritePackageLog(PackageLogLevel.Information, packageId + ".other", "unrelated entry " + index);
            }
            using var model = new DeveloperLogWindowViewModel(logs);
            var window = new DeveloperLogWindow { DataContext = model };
            window.Show();
            try
            {
                var list = window.FindControl<ListBox>("LogEntries")!;
                var combo = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>());
                foreach (var empty in new[] { false, true, false })
                {
                    window.Hide();
                    model.SearchText = "stale search";
                    window.Show();
                    window.SelectPackage(empty ? packageId + ".empty" : packageId);
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                    Assert.Empty(model.SearchText);
                    Assert.Same(model.SelectedFilter, combo.SelectedItem);
                    Assert.Contains(model.SelectedFilter, model.FilterOptions);
                    if (empty) { Assert.Empty(model.FilteredEntries); continue; }
                    Assert.All(model.FilteredEntries, entry => Assert.Equal(packageId, entry.Source));
                    Assert.Equal("matching entry 159", model.FilteredEntries.Last().Message);
                    var latest = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(model.FilteredEntries.Count - 1));
                    var top = latest.TranslatePoint(default, list)!.Value.Y;
                    Assert.True(top < list.Bounds.Height && top + latest.Bounds.Height > 0,
                        $"Latest matching entry was not revealed: {top}, list height {list.Bounds.Height}.");
                    Assert.True(list.GetVisualDescendants().OfType<ScrollViewer>().First().Offset.Y > 0);
                }
            }
            finally { window.CloseForShutdown(); }
            return true;
        }, default);
    }
}
