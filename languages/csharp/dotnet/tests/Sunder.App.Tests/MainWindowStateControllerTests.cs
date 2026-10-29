using Avalonia.Controls;
using Avalonia.Headless;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.Views;
using Xunit;
using static Sunder.App.Tests.TestSupport.TestPaths;

namespace Sunder.App.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class MainWindowStateControllerTests
{
    [Fact]
    public async Task RestoredWindowCannotBeForcedOffScreenByItsPreviousMinimumSize()
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(MainWindowViewModelShellViewTests.SplitterTestApplication));
        await ui.Dispatch(() =>
        {
            var window = new Window { Width = 1400, Height = 850, MinWidth = 10000, MinHeight = 10000 };
            var screen = Assert.Single(window.Screens.All);
            ShellWindowPlacementService.Apply(window, new ShellWindowPlacement
            {
                X = 100000, Y = 100000, Width = 1400, Height = 850,
            });
            window.Show();
            window.UpdateLayout();
            Assert.True(window.MinWidth <= window.Width);
            Assert.True(window.MinHeight <= window.Height);
            Assert.True(window.Width <= screen.WorkingArea.Width / screen.Scaling);
            Assert.True(window.Height <= screen.WorkingArea.Height / screen.Scaling);
            Assert.InRange(window.Position.X, screen.WorkingArea.X, screen.WorkingArea.Right);
            Assert.InRange(window.Position.Y, screen.WorkingArea.Y, screen.WorkingArea.Bottom);
            window.Close();
            return true;
        }, default);
    }

    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Maximized)]
    [InlineData(WindowState.FullScreen)]
    public async Task RestoreAndFlushKeepModeAndNormalBoundsSeparate(WindowState mode)
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(MainWindowViewModelShellViewTests.SplitterTestApplication));
        await ui.Dispatch(async () =>
        {
            using var service = new ShellStateService(System.IO.Path.Combine(CreateTempDirectory(), "shell-state.json"));
            var state = new ShellState();
            var window = new Window { Width = 1400, Height = 850 };
            using (var controller = new MainWindowStateController(window, state, service, () => state.RightPanelWidth = 1550))
            {
                window.Show();
                window.UpdateLayout();
                controller.EnablePersistence();
                window.WindowState = mode;
                if (mode != WindowState.Normal)
                {
                    window.Width = 3000;
                    window.Height = 1800;
                    window.UpdateLayout();
                }
                await controller.FlushAsync();
            }
            window.Close();
            var saved = service.Load();
            var placement = Assert.IsType<ShellWindowPlacement>(saved.MainWindowPlacement);
            Assert.Equal(1400, placement.Width);
            Assert.Equal(850, placement.Height);
            Assert.Equal(mode == WindowState.FullScreen, placement.IsFullScreen);
            Assert.Equal(mode == WindowState.Maximized, placement.IsMaximized);
            Assert.Equal(1550, saved.RightPanelWidth);
            var reopened = new Window { Width = 900, Height = 700 };
            using var restored = new MainWindowStateController(reopened, saved, service, () => { });
            Assert.Equal(mode, reopened.WindowState);
            // Headless screens may clamp the width; no fullscreen dimensions become
            // the normal preference even when the available screen has changed.
            Assert.InRange(reopened.Width, reopened.MinWidth, placement.Width);
            reopened.Close();
            return true;
        }, default);
    }

    [Fact]
    public async Task FullScreenCloseExitDoesNotEraseReopenModeAndCanceledCloseResumesRecording()
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(MainWindowViewModelShellViewTests.SplitterTestApplication));
        await ui.Dispatch(async () =>
        {
            using var service = new ShellStateService(System.IO.Path.Combine(CreateTempDirectory(), "shell-state.json"));
            var state = new ShellState();
            var window = new Window { Width = 1200, Height = 800 };
            using var controller = new MainWindowStateController(window, state, service, () => { });
            window.Show();
            controller.EnablePersistence();
            window.WindowState = WindowState.FullScreen;
            controller.CaptureForClose();
            window.WindowState = WindowState.Normal; // Native close-to-hide transition.
            controller.SaveImmediately();
            Assert.True(service.Load().MainWindowPlacement?.IsFullScreen);
            window.Hide();
            controller.RestoreForReopen();
            Assert.Equal(WindowState.FullScreen, window.WindowState);
            window.Show();
            controller.CaptureForClose();
            controller.CancelClose();
            window.WindowState = WindowState.Normal;
            window.Width = 1100;
            window.UpdateLayout();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), Avalonia.Threading.DispatcherPriority.Background);
            await controller.FlushAsync();
            Assert.False(service.Load().MainWindowPlacement?.IsFullScreen);
            Assert.True(service.Load().MainWindowPlacement?.Width == 1100,
                $"Requested={window.Width}, bounds={window.Bounds}, state={window.WindowState}, saved={service.Load().MainWindowPlacement?.Width}");
            window.Close();
            return true;
        }, default);
    }
}
