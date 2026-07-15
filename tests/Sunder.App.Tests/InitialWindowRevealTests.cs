using Avalonia.Platform;
using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class InitialWindowRevealTests
{
    [Fact]
    public void Reveal_OrdersNativeRevealBeforeDesktopOwnershipActivationAndLoadingClose()
    {
        var events = new List<string>();
        var platform = new RecordingRevealPlatform(events);
        var reveal = new InitialWindowReveal(platform);
        var mainWindow = new RecordingWindow("main", events);
        var loadingWindow = new RecordingWindow("loading", events);

        reveal.ShowConcealed(mainWindow, loadingWindow);

        Assert.Equal(
            [
                "main-show-activated:false",
                "native-conceal",
                "main-show",
                "native-conceal",
                "loading-activate",
            ],
            events
        );

        events.Clear();
        reveal.Reveal(
            mainWindow,
            loadingWindow,
            () => events.Add("desktop-main-window"),
            () => events.Add("runtime-presentation-release")
        );

        Assert.Equal(
            [
                "native-reveal",
                "desktop-main-window",
                "main-activate",
                "loading-close",
                "runtime-presentation-release",
            ],
            events
        );
    }

    [Fact]
    public void MainWindowReveal_DoesNotUseAvaloniaWindowOpacity()
    {
        var source = File.ReadAllText(
            Path.Combine(
                GetRepositoryRoot(),
                "src",
                "Host",
                "Sunder.App",
                "Services",
                "ShellSession.cs"
            )
        );

        Assert.DoesNotContain("MainWindow.Opacity", source, StringComparison.Ordinal);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (
            directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))
        )
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the Sunder Core repository root."
            );
    }

    private sealed class RecordingRevealPlatform(List<string> events) : IInitialWindowRevealPlatform
    {
        public bool TryConceal(IInitialRevealWindow window)
        {
            events.Add("native-conceal");
            return true;
        }

        public bool TryReveal(IInitialRevealWindow window)
        {
            events.Add("native-reveal");
            return true;
        }
    }

    private sealed class RecordingWindow(string name, List<string> events) : IInitialRevealWindow
    {
        public bool ShowActivated
        {
            set => events.Add($"{name}-show-activated:{value.ToString().ToLowerInvariant()}");
        }

        public IPlatformHandle? PlatformHandle => null;

        public void Show() => events.Add($"{name}-show");

        public void Activate() => events.Add($"{name}-activate");

        public void Close() => events.Add($"{name}-close");
    }
}
