using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sunder.App;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;

// Observe the production entry point. Unlike the capture/RPC hooks this does
// not run an AppBuilder, set an apartment, replace Main or terminate before Main.
internal static class StartupHook
{
    private static int _mainThread;
    private static int _started;
    private static readonly TaskCompletionSource Forwarded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("SUNDER_TEST_STARTUP_WEBVIEW") != "1") return;
        Environment.SetEnvironmentVariable("SUNDER_TEST_STARTUP_WEBVIEW", null);
        _mainThread = Environment.CurrentManagedThreadId;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var entry in AppSessionLog.Snapshot())
                Console.Error.WriteLine(entry.Message + Environment.NewLine + entry.ExceptionText);
        };
        NativeWebView.AdapterInfoProperty.Changed.AddClassHandler<NativeWebView>(
            (browser, _) => browser.AdapterCreated += WindowsWebViewBrowserProcess.Record);
        Window.WindowOpenedEvent.AddClassHandler<MainWindow>((window, _) =>
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            Console.WriteLine($"startup-webview: ui-thread={Environment.CurrentManagedThreadId}; apartment={Thread.CurrentThread.GetApartmentState()}");
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
                    await RunAsync(window, deadline.Token);
                    RequestShutdown(0);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error);
                    RequestShutdown(1);
                }
            });
        });
    }

    private static async Task RunAsync(MainWindow window, CancellationToken token)
    {
        // MainWindow is first shown concealed. Wait for the actual initial view
        // reveal, rather than replacing startup's own view activation sequence.
        await UntilAsync(() => ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).MainWindow == window, token);
        Require(Environment.CurrentManagedThreadId == _mainThread, "Desktop startup left the original process main thread.");
        var model = (MainWindowViewModel)window.DataContext!;
        var browser = await PageAsync(window, token);
        Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "The normal desktop dispatcher is not STA.");
        Require(browser.TryGetPlatformHandle() is IWindowsWebView2PlatformHandle, "The normal package view did not create WebView2.");
        Require(await browser.InvokeScript("document.getElementById('press').click(); document.getElementById('press').textContent").WaitAsync(token) == "\"1\"",
            "The package's real native page did not respond to interaction.");
        Require(model.ClosePackageViewPanel("test.startup.webview.view"), "The package view did not close.");
        Require(await model.OpenPackageViewPanelAsync("test.startup.webview.view"), "The package view did not reopen.");
        browser = await PageAsync(window, token);
        Require(await browser.InvokeScript("document.getElementById('press') !== null").WaitAsync(token) == "true", "The reopened native package page is unavailable.");

        // Observe the real secondary-process handoff through the production pipe.
        // The shell's Stack UI is unrelated to this regression.
        Sunder.App.Program.SingleInstanceCoordinator!.SetLaunchRequestHandler((request, _) =>
        {
            if (request.Kind == AppLaunchRequestKind.StackDetails && request.StackId == "sunder.stack.startup-probe")
                Forwarded.TrySetResult();
            return Task.CompletedTask;
        });
        Console.WriteLine("startup-webview: ready-for-forwarding");
        await Forwarded.Task.WaitAsync(token);
        Require(Environment.CurrentManagedThreadId == _mainThread && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,
            "The desktop left the STA main thread after asynchronous work.");
        Console.WriteLine("startup-webview: original-main-thread; STA; native-package-page; interaction; reopen; forwarded-launch");
    }

    private static async Task<NativeWebView> PageAsync(MainWindow window, CancellationToken token)
    {
        NativeWebView? browser = null;
        await UntilAsync(() => (browser = window.GetVisualDescendants().OfType<NativeWebView>().FirstOrDefault())?.TryGetPlatformHandle() is not null, token);
        while (await browser!.InvokeScript("globalThis.startupReady === true").WaitAsync(token) != "true")
            await Task.Delay(25, token);
        return browser;
    }

    private static async Task UntilAsync(Func<bool> ready, CancellationToken token)
    {
        while (!ready()) await Task.Delay(25, token);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequestShutdown(int exitCode)
    {
        var app = (Sunder.App.App)Application.Current!;
        app.RequestDesktopShutdown((IClassicDesktopStyleApplicationLifetime)app.ApplicationLifetime!, exitCode);
    }
}
