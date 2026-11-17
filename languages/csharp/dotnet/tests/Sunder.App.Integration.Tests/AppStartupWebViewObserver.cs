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
using Sunder.App.Models;
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
        Require(browser.NativeBrowserProcessId is not null, "The normal package view did not create WebView2.");
        WindowsWebViewBrowserProcess.Record(browser.NativeBrowserProcessId!.Value);
        Require(await browser.InvokeScriptAsync("document.getElementById('press').click(); document.getElementById('press').textContent").WaitAsync(token) == "\"1\"",
            "The package's real native page did not respond to interaction.");
        Require(model.ClosePackageViewPanel("test.startup.webview.view"), "The package view did not close.");
        Require(await model.OpenPackageViewPanelAsync("test.startup.webview.view"), "The package view did not reopen.");
        browser = await PageAsync(window, token);
        Require(await browser.InvokeScriptAsync("document.getElementById('press').textContent").WaitAsync(token) == "\"0\"", "Explicit close retained the old document.");

        var retained = browser;
        await browser.InvokeScriptAsync("globalThis.moveState=123").WaitAsync(token);
        model.MovePackageView("test.startup.webview.view", RailPlacement.RightTop, null);
        browser = await PageAsync(window, token);
        Require(ReferenceEquals(retained, browser) && await browser.InvokeScriptAsync("moveState").WaitAsync(token) == "123", "Moving a shell view recreated its document.");
        model.MovePackageView("test.startup.webview.view", RailPlacement.Middle, null);
        browser = await PageAsync(window, token);
        Require(ReferenceEquals(retained, browser), "Moving a view back replaced its browser session.");

        Require(await model.ReloadPackageViewAsync("test.startup.webview.view"), "The shell did not reload its web view.");
        browser = await PageAsync(window, token);
        Require(!ReferenceEquals(retained, browser) && await browser.InvokeScriptAsync("typeof moveState").WaitAsync(token) == "\"undefined\"", "Reload retained the previous document's authority or state.");
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var old = window.GetVisualDescendants().OfType<AppWebPackageViewControl>().Single();
            Require(model.ClosePackageViewPanel("test.startup.webview.view"), "Rapid close was rejected.");
            // Reopen without waiting for old native cleanup: the facade must
            // serialize retirement, not return a cached retiring control.
            Require(await model.OpenPackageViewPanelAsync("test.startup.webview.view"), "Rapid reopen was rejected.");
            browser = await PageAsync(window, token);
            await old.Retired.WaitAsync(TimeSpan.FromSeconds(3), token);
            Require(!ReferenceEquals(old.Browser, browser), "Rapid reopen returned a retiring browser lease.");
        }

        var removed = window.GetVisualDescendants().OfType<AppWebPackageViewControl>().Single();
        Require(model.RemovePackageViewFromHotbar("test.startup.webview.view"), "Hotbar removal was rejected.");
        Require(await model.AddPackageViewToDefaultHotbarAsync("test.startup.webview.view", openPanel: true), "Hotbar re-add was rejected.");
        browser = await PageAsync(window, token);
        await removed.Retired.WaitAsync(TimeSpan.FromSeconds(3), token);
        Require(!ReferenceEquals(removed.Browser, browser), "Hotbar re-add returned a retired document.");
        Console.WriteLine("startup-webview: move-preserved; reload-retired; rapid-reopen-fenced; hotbar-removal-retired");

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

    private static async Task<IAppWebView> PageAsync(MainWindow window, CancellationToken token)
    {
        IAppWebView? browser = null;
        await UntilAsync(() => (browser = window.GetVisualDescendants().OfType<AppWebPackageViewControl>().FirstOrDefault()?.Browser)?.CanCapture == true, token);
        while (await browser!.InvokeScriptAsync("globalThis.startupReady === true").WaitAsync(token) != "true")
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
