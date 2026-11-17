using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;

internal static class WindowsWebViewCaptureProcessProbe
{
    [STAThread]
    private static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", name.Name + ".dll"));
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        // The launcher supplies the owning test project's deps.json. Let .NET
        // select its exact native assets (including macOS's universal osx asset),
        // not an unrelated library copied beside another fixture or on PATH.
        return Start(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Start(string[] args) => AppBuilder.Configure<Application>().UsePlatformDetect().AfterSetup(builder =>
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)builder.Instance!.ApplicationLifetime!;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.Startup += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            try { await RunAsync(desktop, args[0], args.Length > 1 ? args[1] : "webview2").WaitAsync(TimeSpan.FromSeconds(45)); desktop.Shutdown(0); }
            catch (Exception error) { Console.Error.WriteLine(error); desktop.Shutdown(1); }
        });
    }).StartWithClassicDesktopLifetime(args);

    private static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, string root, string backend)
    {
        if (backend is "shared" or "webview2") { await SharedBrowserProcessProbe.RunAsync(desktop, root); return; }
        using var browser = new AppNativeWebView(Path.Combine(root, "browser"));
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pageRequested = false;
        browser.NavigationCompleted += (_, args) =>
        {
            Console.WriteLine($"navigation: scheme={args.Uri?.Scheme ?? "missing"}; succeeded={args.Succeeded}");
            if (!pageRequested) return;
            if (args.Succeeded) loaded.TrySetResult();
            else loaded.TrySetException(new InvalidOperationException("The native fixture page did not load."));
        };
        var panel = new DockPanel();
        var sentinel = new Border { Width = 60, Background = Brushes.Red };
        DockPanel.SetDock(sentinel, Dock.Left);
        panel.Children.Add(sentinel);
        panel.Children.Add(browser.Control);
        var window = new Window { Title = "Sunder native browser capture qualification", Width = 480, Height = 320, Content = panel };
        desktop.MainWindow = window;
        var native = (NativeWebView)((Decorator)browser.Control).Child!;
        SelectBackend(native, backend);
        native.EnvironmentRequested += (_, args) => Console.WriteLine("environment: " + args.GetType().Name);
        const string html = "<!doctype html><html><body style='margin:0;background:rgb(10,180,30)'><div style='position:absolute;left:40px;top:30px;width:100px;height:80px;background:rgb(20,40,220)'></div></body></html>";
        native.AdapterCreated += (_, args) =>
        {
            Console.WriteLine("adapter: " + args.TryGetPlatformHandle()?.GetType().Name);
            // A recreated adapter already replays NativeWebView's saved HTML.
            // Submitting it twice would cancel that genuine replay navigation.
            if (!pageRequested)
            {
                pageRequested = true;
                native.NavigateToString(html);
            }
        };
        window.Show();
        Console.WriteLine($"window: client={window.ClientSize}; viewport={browser.CaptureViewport}");
        await loaded.Task;
        var retainedAdapter = native.TryGetPlatformHandle();
        Require(browser.Control is Decorator { Child: NativeWebView }, "The fixture did not use the production native viewport.");
        Require(backend switch
        {
            "gtk" => native.TryGetPlatformHandle() is Avalonia.Platform.IGtkWebViewPlatformHandle,
            "wkwebview" => native.TryGetPlatformHandle() is Avalonia.Platform.IAppleWKWebViewPlatformHandle,
            _ => false,
        }, "The fixture did not initialize the required browser backend: " + backend);
        // Force document layout before asking the backend for its actual pixels.
        await browser.InvokeScriptAsync("document.body.getBoundingClientRect().width");
        var viewport = browser.CaptureViewport;
        var png = await browser.CaptureAsync(viewport, 128, default);
        RecordPng("native-whole", png);
        var dimensions = AppDevelopmentViewOperations.ValidatePng(png);
        Require(Math.Max(dimensions.Width, dimensions.Height) == 128, "The native image did not obey the requested edge.");
        var full = Pixels(png);
        Require(full.Length > 0, "The native image was empty.");
        Require(HasColor(full, 10, 180, 30) && HasColor(full, 20, 40, 220), "The native image did not contain the page's painted pixels.");
        Require(!HasColor(full, 255, 0, 0), "The native image included the neighboring shell sentinel.");

        var crop = new Rect(viewport.X + 50, viewport.Y + 40, 40, 30);
        var cropped = await browser.CaptureAsync(crop, 128, default);
        RecordPng("native-crop", cropped);
        Require(AppDevelopmentViewOperations.ValidatePng(cropped) == (40, 30), "The selected-region dimensions changed.");
        var cropPixels = Pixels(cropped);
        Require(EveryColor(cropPixels, 20, 40, 220), "The selected region included pixels outside the blue element.");
        using (var cancellation = new System.Threading.CancellationTokenSource())
        {
            var pending = browser.CaptureAsync(viewport, 128, cancellation.Token);
            Require(!pending.IsCompleted, "The fixture did not exercise an asynchronous native capture.");
            var concurrent = browser.CaptureAsync(viewport, 128, default);
            cancellation.Cancel();
            try { await concurrent; throw new InvalidDataException("Concurrent native capture was accepted."); }
            catch (InvalidOperationException) { }
            try { await pending; throw new InvalidOperationException("Canceled native capture returned an image."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }

        // Ordinary detach/move is not document retirement on either WebKit
        // backend. The session's native ownership spans the visual gap.
        await browser.InvokeScriptAsync("globalThis.preserved = 123");
        panel.Children.Remove(browser.Control);
        panel.Children.Add(browser.Control);
        Require(await browser.InvokeScriptAsync("preserved") == "123", "Moving a WebKit view discarded its document.");

        var destroyed = false;
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        native.AdapterDestroyed += (_, _) => { destroyed = true; retired.TrySetResult(); };
        var detached = browser.CaptureAsync(viewport, 128, default);
        Require(!detached.IsCompleted, "The fixture did not exercise capture during detachment.");
        window.Content = null;
        browser.Dispose();
        Require(!destroyed, "The wrapper destroyed its native adapter before the capture callback drained.");
        try { await detached; throw new InvalidDataException("A retired native viewport returned an image."); }
        catch (Exception error) when (error is InvalidOperationException or COMException) { }
        // NativeControlHost may schedule actual destruction on the next layout
        // turn. Observe its real event rather than assuming synchronous teardown.
        await retired.Task;
        Require(destroyed, "The native adapter did not retire after capture settlement.");
        await browser.DrainNativeRetirementAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Directory.Delete(Path.Combine(root, "browser"), recursive: true);
        Require(!Directory.Exists(Path.Combine(root, "browser")), "Native profile data survived the browser-group exit.");
        GC.KeepAlive(retainedAdapter);
        window.Close();
        Console.WriteLine("webview2-capture: native-pixels; package-only; selected-crop; cancellation-drained; detach-drained");
        await RunHostedPackageAsync(desktop, root, backend);
    }

    internal static async Task RunHostedPackageAsync(IClassicDesktopStyleApplicationLifetime desktop, string root, string backend, AppBrowserManager? sharedManager = null)
    {
        var packageRoot = Directory.CreateDirectory(Path.Combine(root, "package")).FullName;
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "index.html"),
            "<!doctype html><html><body style='margin:0;background:rgb(10,180,30)'><button aria-label='Capture target' style='position:absolute;left:40px;top:30px;width:100px;height:80px;border:0;border-radius:0;padding:0;background:rgb(20,40,220)'></button></body></html>");
        var target = new SunderPackageTargetManifest
        {
            Role = SunderPackageFormat.AppHostRole, Rid = RuntimeInformation.RuntimeIdentifier,
            Kind = SunderPackageFormat.WebTargetKind, EntryPoint = "index.html",
            RequiredHostCapabilities = ["core.v1"],
            Views = [new() { ViewId = "test.hosted.capture", DisplayName = "Hosted capture", Route = "/", DefaultPlacement = "middle", ShowInHotbar = true }],
        };
        await using var server = await AppWebContentServer.StartAsync(packageRoot, target, default);
        var contracts = AppWebRpcContractCatalog.Load(packageRoot, new SunderPackageManifest { ContractBundles = [] },
            new SunderPackageContentIndex(SunderPackageFormat.CurrentContentIndexVersion, []));
        var manager = sharedManager ?? new AppBrowserManager(AvaloniaUiDispatcher.Instance);
        var factory = new HostedBrowserFactory(server, backend, manager);
        using var package = new AppWebPackageViewControl(server.BaseUri, Path.Combine(root, "hosted-browser"), server,
            factory, UnavailableAppWebRpcClient.Instance, contracts, new ExternalBrowserService(_ => throw new InvalidOperationException("The capture fixture must not open an external browser.")));
        var panel = new DockPanel();
        var shell = new Border { Width = 60, Background = Brushes.Red };
        DockPanel.SetDock(shell, Dock.Left);
        panel.Children.Add(shell);
        panel.Children.Add(package);
        var window = new Window { Title = "Sunder hosted package capture qualification", Width = 480, Height = 320, Content = panel };
        desktop.MainWindow = window;
        window.Show();
        try
        {
            await factory.Loaded.Task;
            await factory.Browser!.InvokeScriptAsync("document.body.getBoundingClientRect().width");
            Require(package.Bridge.CaptureDevelopmentDocument().Length > 0, "The hosted package did not establish its real bridge lifetime.");
            var current = true;
            var binding = new AppDevelopmentViewBinding("test.hosted.capture", "test.hosted.capture",
                new(Guid.NewGuid(), 1, new string('a', 64)), package, AvaloniaUiDispatcher.Instance,
                () => { if (!current) throw new InvalidOperationException("The hosted package activation retired."); });
            var operations = new AppDevelopmentViewOperations();
            var snapshot = (await operations.QueryAsync(binding, new() { Role = "button", Name = "Capture target" }, default)).Snapshot;
            var element = snapshot.Elements.Single(item => item.Name == "Capture target");
            await using (var whole = await operations.CaptureAsync(binding, new(), 128, default))
            {
                var bytes = await ReadContentAsync(whole);
                RecordPng("hosted-whole", bytes);
                var dimensions = AppDevelopmentViewOperations.ValidatePng(bytes);
                Require(Math.Max(dimensions.Width, dimensions.Height) == 128, "The composed hosted image exceeded its requested edge.");
                var pixels = Pixels(bytes);
                Require(HasColor(pixels, 10, 180, 30) && HasColor(pixels, 20, 40, 220), "The composed image omitted native package pixels.");
                Require(!HasColor(pixels, 255, 0, 0), "The composed image included the neighboring shell.");
            }
            var selected = new PackageDevelopmentViewQuery { SnapshotId = snapshot.SnapshotId, ElementReference = element.Reference };
            await using (var crop = await operations.CaptureAsync(binding, selected, 128, default))
            {
                var bytes = await ReadContentAsync(crop);
                RecordPng("hosted-crop", bytes);
                Require(AppDevelopmentViewOperations.ValidatePng(bytes) == (100, 80), "The hosted document selection did not retain its exact bounds.");
                Require(EveryColor(Pixels(bytes), 20, 40, 220), "The hosted selected element included pixels outside its native bounds.");
            }
            var retiring = operations.CaptureAsync(binding, selected, 128, default);
            Require(!retiring.IsCompleted, "The hosted retirement check did not submit native work.");
            current = false;
            try { await using var invalid = await retiring; throw new InvalidDataException("A retired hosted activation returned an image."); }
            catch (InvalidOperationException) { }
            Console.WriteLine("hosted-capture: hosted-package; fixed-document-query; composed-capture; exact-revision");
        }
        finally
        {
            panel.Children.Remove(package);
            await package.DisposeAsync();
            await package.DrainNativeRetirementAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await manager.RetireActivationAsync(Path.Combine(root, "hosted-browser"));
            if (sharedManager is null) await manager.DisposeAsync();
            if (Directory.Exists(Path.Combine(root, "hosted-browser"))) Directory.Delete(Path.Combine(root, "hosted-browser"), recursive: true);
            Require(!Directory.Exists(Path.Combine(root, "hosted-browser")), "Hosted profile data survived native retirement.");
            window.Close();
        }
    }

    private static async Task<byte[]> ReadContentAsync(SunderContent content)
    {
        await using var stream = await content.OpenReadAsync();
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        return bytes.ToArray();
    }

    private static void SelectBackend(NativeWebView native, string backend)
    {
        if (backend == "wkwebview") return;
        if (backend == "gtk")
        {
            Require(native.AdapterPreference is { Count: 1 } preference && preference[0] == Avalonia.Platform.WebViewAdapterType.WebKitGtk,
                "The production Linux factory did not select only the qualified GTK backend.");
            return;
        }
        throw new ArgumentException("Unknown required WebKit backend.", nameof(backend));
    }

    private sealed class HostedBrowserFactory(AppWebContentServer server, string backend, AppBrowserManager manager) : IAppWebViewFactory
    {
        internal IAppWebView? Browser { get; private set; }
        internal TaskCompletionSource Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IAppWebView Create(string userDataDirectory)
        {
            var browser = Browser = manager.Create(userDataDirectory);
            if (!OperatingSystem.IsWindows()) SelectBackend((NativeWebView)((Decorator)browser.Control).Child!, backend);
            browser.NavigationCompleted += (_, args) =>
            {
                if (!server.Owns(args.Uri)) return;
                if (browser.NativeBrowserProcessId is { } pid) WindowsWebViewBrowserProcess.Record(pid);
                if (args.Succeeded) Loaded.TrySetResult();
                else Loaded.TrySetException(new InvalidOperationException("The hosted package fixture did not load."));
            };
            return browser;
        }
    }

    internal static byte[] Pixels(byte[] png)
    {
        using var stream = new MemoryStream(png, false);
        using var bitmap = new Bitmap(stream);
        var size = bitmap.PixelSize;
        var count = checked(size.Width * size.Height * 4);
        // Raw CopyPixels preserves the backend's native channel/alpha layout.
        // Explicitly transcode before applying the shared BGRA color assertions.
        using var normalized = new WriteableBitmap(size, new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Unpremul);
        using var buffer = normalized.Lock();
        bitmap.CopyPixels(buffer);
        var pixels = new byte[count];
        for (var row = 0; row < size.Height; row++)
            Marshal.Copy(buffer.Address + row * buffer.RowBytes, pixels, row * size.Width * 4, size.Width * 4);
        return pixels;
    }

    internal static void RecordPng(string stage, byte[] png)
    {
        var dimensions = AppDevelopmentViewOperations.ValidatePng(png);
        var colors = Pixels(png).Chunk(4).GroupBy(pixel => Convert.ToHexString(pixel)).OrderByDescending(group => group.Count())
            .Take(8).Select(group => $"{group.Key}:{group.Count()}");
        Console.WriteLine($"native-capture-png: stage={stage}; size={dimensions.Width}x{dimensions.Height}; bytes={png.Length}; sha256={Convert.ToHexStringLower(SHA256.HashData(png))}; bgraColors={string.Join(',', colors)}");
        if (Environment.GetEnvironmentVariable("SUNDER_TEST_COMMAND_LOG_DIRECTORY") is { Length: > 0 } directory)
        {
            var output = Directory.CreateDirectory(Path.Combine(directory, "native-capture")).FullName;
            File.WriteAllBytes(Path.Combine(output, stage + ".png"), png);
        }
    }
    internal static bool HasColor(byte[] pixels, byte red, byte green, byte blue)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
            if (ColorAt(pixels, offset, red, green, blue)) return true;
        return false;
    }
    internal static bool EveryColor(byte[] pixels, byte red, byte green, byte blue)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
            if (!ColorAt(pixels, offset, red, green, blue)) return false;
        return true;
    }
    private static bool ColorAt(byte[] pixels, int offset, byte red, byte green, byte blue)
        => Math.Abs(pixels[offset] - blue) <= 2 && Math.Abs(pixels[offset + 1] - green) <= 2
            && Math.Abs(pixels[offset + 2] - red) <= 2 && pixels[offset + 3] == 255;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
