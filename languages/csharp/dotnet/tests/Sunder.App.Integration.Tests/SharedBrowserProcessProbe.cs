using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Web.WebView2.Core;
using Sunder.App.Services;
using Sunder.Package.Format;

internal static class SharedBrowserProcessProbe
{
    internal static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var probeStarted = Stopwatch.GetTimestamp();
        var deadline = new BrowserProbeDeadline(WindowsWebViewCaptureProcessProbe.ScenarioWatchdog - AppShutdownBudgets.BrowserCleanup, TimeProvider.System);
        void Stage(string stage) => Console.WriteLine($"shared-browser-stage: {stage}; elapsedMs={Stopwatch.GetElapsedTime(probeStarted).TotalMilliseconds:0.###}");
        Environment.SetEnvironmentVariable("SUNDER_APP_STATE_ROOT", Path.Combine(root, "state"));
        var ui = AvaloniaUiDispatcher.Instance;
        await using var engine = new WindowsBrowserEngine(ui);
        var created = new List<WindowsBrowserView>();
        await using var manager = new AppBrowserManager(ui, _ =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var native = new WindowsBrowserView(engine, ui); created.Add(native); return native;
        }, () => OperatingSystem.IsWindows() ? engine.DisposeAsync().AsTask() : throw new PlatformNotSupportedException());
        Require(engine.BrowserProcessIds.Count == 0 && !Directory.Exists(engine.DataDirectory), "No-demand browser started eagerly.");
        var content = Directory.CreateDirectory(Path.Combine(root, "content")).FullName;
        await File.WriteAllTextAsync(Path.Combine(content, "index.html"), "<!doctype html><html><body style='margin:0;background:rgb(10,180,30)'><div style='position:absolute;left:40px;top:30px;width:100px;height:80px;background:rgb(20,40,220)'></div><script src='page.js'></script></body></html>");
        await File.WriteAllTextAsync(Path.Combine(content, "page.js"), """
            globalThis.fixtureId=crypto.randomUUID(); globalThis.freshStorage=false;
            globalThis.fixtureBridge=window.sunder ? Promise.resolve() :
              new Promise(resolve=>window.addEventListener('sunder:ready',resolve,{once:true}));
            globalThis.fixtureWorkers=(async()=>{
              globalThis.worker=new Worker('worker.js'); globalThis.shared=new SharedWorker('shared.js');
              const started=(port,source)=>new Promise((resolve,reject)=>{
                port.onmessage=e=>e.data==='ready' ? resolve() : reject(new Error('Unexpected worker response'));
                source.onerror=e=>reject(new Error(e.message || 'Worker startup failed'));
              });
              const dedicated=started(worker,worker), pooled=started(shared.port,shared); shared.port.start();
              const dbs=await indexedDB.databases();
              freshStorage=localStorage.getItem('old')===null && sessionStorage.getItem('old')===null
                && !document.cookie.includes('old=') && (await caches.keys()).length===0 && dbs.length===0;
              localStorage.setItem('old','old'); sessionStorage.setItem('old','old'); document.cookie='old=old; SameSite=Strict';
              const cache=await caches.open('old'); await cache.put('old',new Response('old'));
              await new Promise((resolve,reject)=>{const request=indexedDB.open('old',1);
                request.onupgradeneeded=()=>request.result.createObjectStore('old');
                request.onerror=()=>reject(request.error); request.onsuccess=()=>{request.result.close(); resolve()}});
              const r=await navigator.serviceWorker.register('service.js'); const w=r.active??r.installing??r.waiting;
              if(w.state!=='activated')await new Promise((resolve,reject)=>{
                const check=()=>{if(w.state==='activated')resolve();else if(w.state==='redundant')reject(new Error('Service worker became redundant'));};
                w.addEventListener('statechange',check); check();
              });
              const channel=new MessageChannel();
              const service=new Promise((resolve,reject)=>{channel.port1.onmessage=e=>e.data==='ready' ? resolve() : reject(new Error('Unexpected service response'));});
              r.active.postMessage('start',[channel.port2]); await Promise.all([dedicated,pooled,service]); channel.port1.close();
            })();
            """);
        await File.WriteAllTextAsync(Path.Combine(content, "worker.js"), "setInterval(()=>fetch('page.js').catch(()=>{}),25);postMessage('ready');");
        await File.WriteAllTextAsync(Path.Combine(content, "shared.js"), "onconnect=e=>{e.ports[0].postMessage('ready')};setInterval(()=>fetch('page.js').catch(()=>{}),25);");
        await File.WriteAllTextAsync(Path.Combine(content, "service.js"), "self.addEventListener('install',e=>e.waitUntil(skipWaiting()));self.addEventListener('activate',e=>e.waitUntil(clients.claim()));self.addEventListener('message',e=>e.waitUntil(new Promise(resolve=>{const timer=setInterval(()=>fetch('page.js').catch(()=>{}),25);e.ports[0].postMessage('ready');setTimeout(()=>{clearInterval(timer);resolve()},60000)})));");
        var target = new SunderPackageTargetManifest { Role = "app", Rid = RuntimeInformation.RuntimeIdentifier, Kind = "web", EntryPoint = "index.html", RequiredHostCapabilities = ["core.v1"], Views = [new() { ViewId = "probe", DisplayName = "Probe", Route = "/", DefaultPlacement = "middle", ShowInHotbar = true }] };
        await using var server = await AppWebContentServer.StartAsync(content, target, default);
        using var documents = new SharedBrowserDocumentProbe(manager, server.BaseUri, lease => created.SingleOrDefault(view =>
            ReferenceEquals(view.Control, lease.Control) || view.Control.GetVisualAncestors().Contains(lease.Control)), deadline);
        var contracts = AppWebRpcContractCatalog.Load(content, new SunderPackageManifest { ContractBundles = [] },
            new SunderPackageContentIndex(SunderPackageFormat.CurrentContentIndexVersion, []));
        var ownerRoot = Path.Combine(root, "activation");
        var aKey = AppWebViewProfile.ForView(ownerRoot, "a");
        using var aPackage = Create(aKey);
        using var bPackage = Create(AppWebViewProfile.ForView(ownerRoot, "b"));
        var a = aPackage.Browser;
        var b = bPackage.Browser;
        var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        panel.Children.Add(aPackage); Grid.SetColumn(bPackage, 1); panel.Children.Add(bPackage);
        var shell = new DockPanel();
        var sentinel = new Border { Width = 60, Background = Brushes.Red };
        DockPanel.SetDock(sentinel, Dock.Left); shell.Children.Add(sentinel); shell.Children.Add(panel);
        var window = new Window { Title = "Sunder shared browser qualification", Width = 900, Height = 340, Content = shell };
        desktop.MainWindow = window;
        window.Show();
        try
        {
            await BrowserProbeDeadline.BothAsync(documents.ReadyAsync(a, "initial-a"), documents.ReadyAsync(b, "initial-b"));
            Console.WriteLine($"shared-browser-runtime: {created[0].Native!.Environment.BrowserVersionString}");
            foreach (var pid in engine.BrowserProcessIds) WindowsWebViewBrowserProcess.Record(pid);
        }
        catch (Exception error)
        {
            Stage($"readiness-failed; initialization={string.Join(',', created.Select(view => view.Initialization?.Status.ToString() ?? "not-started"))}");
            WindowsWebViewCaptureProcessProbe.ReportFailure(error);
            try { Console.Error.WriteLine($"shared-browser-available-runtime: {CoreWebView2Environment.GetAvailableBrowserVersionString()}"); }
            catch (Exception versionError) { Console.Error.WriteLine($"shared-browser-runtime-unavailable: {versionError.GetType().Name}"); }
            throw;
        }
        Require(await a.InvokeScriptAsync("freshStorage") == "true" && await b.InvokeScriptAsync("freshStorage") == "true", "Sibling views shared browser storage.");
        var aInfo = await created[0].Native!.CallDevToolsProtocolMethodAsync("Target.getTargetInfo", "{}");
        string aContext;
        using (var info = JsonDocument.Parse(aInfo)) aContext = info.RootElement.GetProperty("targetInfo").GetProperty("browserContextId").GetString()!;
        await RequireWorkersAsync(created[0], aContext, present: true, () => deadline.Remaining);
        Require(created.Count == 2 && engine.BrowserProcessIds.Count == 1, "Views did not share one browser process.");
        Stage("documents-and-workers-ready");
        foreach (var pid in engine.BrowserProcessIds) WindowsWebViewBrowserProcess.Record(pid);
        Stage("whole-native-capture-started");
        var pixels = await a.CaptureAsync(a.CaptureViewport, 128, default);
        WindowsWebViewCaptureProcessProbe.RecordPng("shared-whole", pixels);
        Require(Math.Max(AppDevelopmentViewOperations.ValidatePng(pixels).Width, AppDevelopmentViewOperations.ValidatePng(pixels).Height) == 128, "Capture dimensions changed.");
        Require(WindowsWebViewCaptureProcessProbe.HasColor(WindowsWebViewCaptureProcessProbe.Pixels(pixels), 10, 180, 30)
            && WindowsWebViewCaptureProcessProbe.HasColor(WindowsWebViewCaptureProcessProbe.Pixels(pixels), 20, 40, 220), "The capture omitted native package pixels.");
        Require(!WindowsWebViewCaptureProcessProbe.HasColor(WindowsWebViewCaptureProcessProbe.Pixels(pixels), 255, 0, 0), "Native capture included the neighboring shell.");
        var crop = await a.CaptureAsync(new Rect(a.CaptureViewport.X + 50, a.CaptureViewport.Y + 40, 40, 30), 128, default);
        WindowsWebViewCaptureProcessProbe.RecordPng("shared-crop", crop);
        Require(AppDevelopmentViewOperations.ValidatePng(crop) == (40, 30), "Native crop dimensions changed.");
        Require(WindowsWebViewCaptureProcessProbe.EveryColor(WindowsWebViewCaptureProcessProbe.Pixels(crop), 20, 40, 220), "Native crop included neighboring pixels.");
        Stage("whole-and-crop-native-captures-passed");
        using (var cancel = new CancellationTokenSource())
        {
            var pending = a.CaptureAsync(a.CaptureViewport, 128, cancel.Token);
            Require(!pending.IsCompleted, "The fixture missed asynchronous native capture.");
            var concurrent = a.CaptureAsync(a.CaptureViewport, 128, default);
            cancel.Cancel();
            try { await concurrent; throw new InvalidDataException("Concurrent native capture was accepted."); }
            catch (InvalidOperationException) { }
            try { await pending; throw new InvalidDataException("Canceled capture returned an image."); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        }
        Stage("canceled-native-capture-drained");
        var control = a.Control;
        await a.InvokeScriptAsync("globalThis.preserved=123");
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Detached(object? sender, VisualTreeAttachmentEventArgs e) => detached.TrySetResult();
        control.DetachedFromVisualTree += Detached;
        try
        {
            panel.Children.Remove(aPackage);
            await deadline.ObserveAsync("reparent/detach", detached.Task);
        }
        finally { control.DetachedFromVisualTree -= Detached; }
        panel.Children.Add(aPackage);
        await SharedBrowserDocumentProbe.OnLayoutAsync(control, () => a.CanCapture, task => deadline.ObserveAsync("reparent/viewport", task));
        Require(await a.InvokeScriptAsync("preserved") == "123" && created.Count == 2, "Reparenting recreated the document.");
        Stage("native-reparenting-preserved-document");
        await b.InvokeScriptAsync("localStorage.setItem('sibling','survives');globalThis.sibling=456");
        var started = Stopwatch.GetTimestamp();
        var retiredNonce = aPackage.Bridge.CaptureDevelopmentDocument();
        var retiredNative = created[0].Native!;
        var script = a.InvokeScriptAsync("(()=>{const end=performance.now()+100;while(performance.now()<end){};return 1})()");
        var pendingCapture = a.CaptureAsync(a.CaptureViewport, 128, default);
        Stage($"in-flight-retirement-started; script={script.Status}; capture={pendingCapture.Status}");
        Require(!script.IsCompleted && !pendingCapture.IsCompleted, "The fixture missed in-flight native work.");
        panel.Children.Remove(aPackage);
        try { await aPackage.DisposeAsync().AsTask().WaitAsync(AppShutdownBudgets.BrowserCleanup); }
        catch (Exception error)
        {
            Stage($"in-flight-retirement-failed; script={script.Status}; capture={pendingCapture.Status}; retired={aPackage.Retired.Status}");
            WindowsWebViewCaptureProcessProbe.ReportFailure(error);
            throw;
        }
        Stage($"in-flight-retirement-settled; script={script.Status}; capture={pendingCapture.Status}");
        Require(script.IsCompleted && pendingCapture.IsCompleted, "Retirement abandoned a native callback.");
        try { await script; } catch (Exception error) when (error is OperationCanceledException or COMException) { }
        try { await pendingCapture; throw new InvalidDataException("A retired native viewport returned an image."); }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException or COMException) { }
        Require(await b.InvokeScriptAsync("sibling") == "456" && await b.InvokeScriptAsync("localStorage.getItem('sibling')") == "\"survives\"", "Reset changed a sibling profile.");
        Require(created[0].Native is null, "Retirement retained the controller that owned a submitted capture.");
        // The SDK can still expose the last Source value after Close; metadata
        // is not proof of live document authority. Try actual native execution.
        try
        {
            await WindowsBrowserScript.ExecuteAsync(retiredNative, "globalThis.retiredUsabilityProbe = 1").WaitAsync(TimeSpan.FromSeconds(3));
            throw new InvalidDataException("The retired native document still accepted script execution.");
        }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException or COMException) { }
        await RequireWorkersAsync(created[1], aContext, present: false, () => CleanupRemaining(started));
        Console.WriteLine($"shared-browser: in-flight-controller-closed; callbacks-drained; sibling-preserved; resetMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.###}");
        CoreWebView2? reusedNative = null;
        for (var cycle = 0; cycle < 12; cycle++)
        {
            using var reopened = Create(aKey);
            Require(ReferenceEquals(control, reopened.Browser.Control) && created.Count == 2, "Cleaned controller was not reused.");
            panel.Children.Add(reopened);
            var current = reopened.Browser;
            await documents.ReadyAsync(current, $"reuse-{cycle}");
            if (reusedNative is null)
            {
                reusedNative = created[0].Native!;
                Require(!ReferenceEquals(reusedNative, retiredNative), "Reopen reused the closed in-flight controller.");
                using var info = JsonDocument.Parse(await reusedNative.CallDevToolsProtocolMethodAsync("Target.getTargetInfo", "{}"));
                var newContext = info.RootElement.GetProperty("targetInfo").GetProperty("browserContextId").GetString()!;
                Require(newContext != aContext, "Reopen reused the retired profile's native context.");
                aContext = newContext;
            }
            Require(ReferenceEquals(reusedNative, created[0].Native), "A quiescent reset recreated its reusable controller.");
            Require(await current.InvokeScriptAsync("freshStorage && typeof preserved==='undefined'") == "true", "Reopened document retained old globals or storage.");
            var currentNonce = reopened.Bridge.CaptureDevelopmentDocument();
            Require(currentNonce != retiredNonce, "Reopened native view retained old bridge authority.");
            await reopened.Bridge.HandleMessageAsync(JsonSerializer.Serialize(new
            {
                protocol = AppWebBridge.Protocol, version = AppWebBridge.ProtocolVersion, nonce = retiredNonce,
                origin = server.Origin.GetLeftPart(UriPartial.Authority), requestId = "b_stale", method = "navigation.navigate",
                deadlineUtc = (string?)null, payload = new { route = "/" },
            }));
            Require(reopened.Bridge.CaptureDevelopmentDocument() == currentNonce && reopened.Bridge.PendingRequestCount == 0, "A stale bridge request acquired new authority.");
            await current.InvokeScriptAsync("history.pushState({},'',location.pathname+'#old');globalThis.preserved=123");
            await RequireWorkersAsync(created[0], aContext, present: true, () => deadline.Remaining);
            panel.Children.Remove(reopened);
            var resetStarted = Stopwatch.GetTimestamp();
            await reopened.DisposeAsync().AsTask().WaitAsync(AppShutdownBudgets.BrowserCleanup);
            await RequireCleanAsync(created[0], aContext, resetStarted);
            Require(await b.InvokeScriptAsync("sibling") == "456" && await b.InvokeScriptAsync("localStorage.getItem('sibling')") == "\"survives\"", "A repeated reset changed its sibling.");
            Console.WriteLine($"shared-browser-cycle: cycle={cycle}; resetMs={Stopwatch.GetElapsedTime(resetStarted).TotalMilliseconds:0.###}; sessions={manager.NativeSessionCount}");
            retiredNonce = currentNonce;
        }
        Console.WriteLine("shared-browser: lazy; isolated-profiles; retained-controller; sibling-preserved");
        // A real reopen before reset finishes must wait without acquiring another
        // native view, then forward navigation/bridge/capture through its placeholder.
        using (var old = Create(aKey))
        {
            panel.Children.Add(old);
            await documents.ReadyAsync(old.Browser, "before-deferred-reopen");
            var oldNonce = old.Bridge.CaptureDevelopmentDocument();
            var busy = old.Browser.InvokeScriptAsync("(()=>{const end=performance.now()+100;while(performance.now()<end){};return 1})()");
            panel.Children.Remove(old);
            var cleanup = old.DisposeAsync().AsTask();
            Require(!cleanup.IsCompleted, "The reopen fixture missed pending document cleanup.");
            using var reopened = Create(aKey);
            panel.Children.Add(reopened);
            Require(created.Count == 2 && !reopened.CanCapture, "Reopen acquired a native view before previous cleanup.");
            await cleanup.WaitAsync(AppShutdownBudgets.BrowserCleanup);
            try { await busy; } catch (Exception error) when (error is OperationCanceledException or COMException) { }
            await documents.ReadyAsync(reopened.Browser, "deferred-reopen");
            Require(await reopened.Browser.InvokeScriptAsync("freshStorage") == "true", "Deferred reopen retained old storage.");
            Require(created.Count == 2 && reopened.Bridge.CaptureDevelopmentDocument() != oldNonce, "Reopen did not reuse clean native ownership with fresh authority.");
            var reopenedPixels = await reopened.Browser.CaptureAsync(reopened.Browser.CaptureViewport, 128, default);
            WindowsWebViewCaptureProcessProbe.RecordPng("pending-reopen", reopenedPixels);
            Require(WindowsWebViewCaptureProcessProbe.HasColor(WindowsWebViewCaptureProcessProbe.Pixels(reopenedPixels), 20, 40, 220), "Deferred reopen omitted native package pixels.");
            panel.Children.Remove(reopened);
            await reopened.DisposeAsync().AsTask().WaitAsync(AppShutdownBudgets.BrowserCleanup);
            Require(await b.InvokeScriptAsync("sibling") == "456", "Pending reopen disturbed its sibling.");
            Console.WriteLine("shared-browser: pending-reopen; no-extra-native-view; fresh-authority; native-capture; sibling-preserved");
        }
        // Final activation retirement differs from reset: it must keep a
        // submitted preview renderable even if its real parent HWND is closed,
        // then join native callbacks rather than cache that controller.
        var closingKey = AppWebViewProfile.ForView(ownerRoot, "closing");
        using (var closing = Create(closingKey))
        {
            var closingWindow = new Window { Title = "Sunder capture owner closure", Width = 420, Height = 300, Content = closing };
            closingWindow.Show();
            await documents.ReadyAsync(closing.Browser, "closing-owner");
            var closingNative = created[^1];
            using var info = JsonDocument.Parse(await closingNative.Native!.CallDevToolsProtocolMethodAsync("Target.getTargetInfo", "{}"));
            var closingContext = info.RootElement.GetProperty("targetInfo").GetProperty("browserContextId").GetString()!;
            var busy = closing.Browser.InvokeScriptAsync("(()=>{const end=performance.now()+100;while(performance.now()<end){};return 1})()");
            var capture = closing.Browser.CaptureAsync(closing.Browser.CaptureViewport, 128, default);
            Require(!busy.IsCompleted && !capture.IsCompleted, "The fixture missed in-flight work before real owner closure.");
            var closeStarted = Stopwatch.GetTimestamp();
            closingWindow.Close();
            var activationRetirement = manager.RetireActivationAsync(closingKey);
            await Task.WhenAll(activationRetirement, closing.DisposeAsync().AsTask()).WaitAsync(AppShutdownBudgets.BrowserCleanup - Stopwatch.GetElapsedTime(closeStarted));
            Require(busy.IsCompleted && capture.IsCompleted && closingNative.Native is null, "Final owner closure abandoned native capture or retained its controller.");
            try { await busy; } catch (Exception error) when (error is OperationCanceledException or COMException) { }
            try { await capture; throw new InvalidDataException("A closed native owner returned an image."); }
            catch (Exception error) when (error is InvalidOperationException or OperationCanceledException or COMException) { }
            await RequireWorkersAsync(created[1], closingContext, present: false, () => CleanupRemaining(closeStarted));
            Require(await b.InvokeScriptAsync("sibling") == "456" && await b.InvokeScriptAsync("localStorage.getItem('sibling')") == "\"survives\"", "Final owner closure disturbed its sibling.");
            Console.WriteLine($"shared-browser: capture-owner-closed; callbacks-drained; sibling-preserved; retirementMs={Stopwatch.GetElapsedTime(closeStarted).TotalMilliseconds:0.###}");
        }
        // Kill only the renderer selected by the SDK's exact frame/process map.
        // This is a native failure, not a synthetic Failed event or navigation.
        using (var failed = Create(aKey))
        {
            panel.Children.Add(failed);
            await documents.ReadyAsync(failed.Browser, "renderer-before-failure");
            var native = created[0].Native!;
            var oldNonce = failed.Bridge.CaptureDevelopmentDocument();
            var processes = await native.Environment.GetProcessExtendedInfosAsync();
            var renderer = processes.Single(info => info.AssociatedFrameInfos.Any(frame => frame.FrameId == native.FrameId));
            Require(renderer.ProcessInfo.Kind == CoreWebView2ProcessKind.Renderer
                && renderer.AssociatedFrameInfos.All(frame => frame.FrameId != created[1].Native!.FrameId),
                "The failure fixture did not isolate the selected view's renderer.");
            var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            failed.Browser.Failed += (_, _) => failure.TrySetResult();
            await KillOwnedAsync(renderer.ProcessInfo.ProcessId);
            await failure.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await failed.Retired.WaitAsync(AppShutdownBudgets.BrowserCleanup);
            Require(!failed.CanCapture && failed.Bridge.PendingRequestCount == 0, "A failed renderer retained native capture or bridge work.");
            Require(failed.Content is TextBlock, "A failed renderer did not show the unavailable-view fallback.");
            panel.Children.Remove(failed);
            using var replacement = Create(aKey);
            panel.Children.Add(replacement);
            await documents.ReadyAsync(replacement.Browser, "renderer-replacement");
            Require(await replacement.Browser.InvokeScriptAsync("freshStorage") == "true", "Renderer recovery retained old storage.");
            Require(!ReferenceEquals(control, replacement.Browser.Control)
                && replacement.Bridge.CaptureDevelopmentDocument() != oldNonce, "Renderer recovery reused failed native ownership or bridge authority.");
            Require(await b.InvokeScriptAsync("sibling") == "456"
                && await b.InvokeScriptAsync("localStorage.getItem('sibling')") == "\"survives\"", "Renderer recovery disturbed its sibling.");
            panel.Children.Remove(replacement);
            await replacement.DisposeAsync().AsTask().WaitAsync(AppShutdownBudgets.BrowserCleanup);
            Console.WriteLine("shared-browser: native-renderer-failure; unavailable-fallback; fresh-reload; sibling-preserved");
        }
        await bPackage.DisposeAsync();
        window.Content = null;
        await manager.RetireActivationAsync(ownerRoot).WaitAsync(AppShutdownBudgets.BrowserCleanup);
        await WindowsWebViewCaptureProcessProbe.RunHostedPackageAsync(desktop, root, "webview2", manager);
        window.Close(); // Match normal shutdown: no Avalonia top-level remains.
        await manager.DisposeAsync().AsTask().WaitAsync(AppShutdownBudgets.BrowserCleanup);
        Require(!Directory.Exists(engine.DataDirectory), "Shared engine profile survived final native retirement.");
        Console.WriteLine("shared-browser: native-capture; cancellation-drained; reparent-preserved; native-exit; profile-removed");
        await PendingAsync(desktop, root, closeOwner: false);
        await PendingAsync(desktop, root, closeOwner: true);

        AppWebPackageViewControl Create(string key) => new(server.BaseUri, key, server, documents,
            UnavailableAppWebRpcClient.Instance, contracts, new ExternalBrowserService(_ => throw new InvalidOperationException("The fixture must not open external browsers.")));
    }

    internal static async Task PendingResetRetirementAsync(IClassicDesktopStyleApplicationLifetime desktop, string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var ui = AvaloniaUiDispatcher.Instance;
        var engine = new WindowsBrowserEngine(ui);
        var native = new WindowsBrowserView(engine, ui);
        var manager = new AppBrowserManager(ui, _ => native, () => engine.DisposeAsync().AsTask());
        var activation = Path.Combine(root, "pending-reset");
        var view = manager.Create(Path.Combine(activation, "view"));
        var owner = new Window { Title = "Sunder pending reset retirement", Width = 320, Height = 240, Content = view.Control };
        desktop.MainWindow = owner;
        owner.Show();
        var initialization = native.Initialization ?? throw new InvalidOperationException("The fixture did not request a real controller.");
        Require(!initialization.IsCompleted, "The fixture missed pending native controller creation.");

        var sta = Environment.CurrentManagedThreadId;
        var clock = Stopwatch.StartNew();
        var observed = ObserveAcquisitionAsync();
        // Stay on this dispatcher turn until reset and final close both own
        // the pending creation. No sleeps or artificial native callbacks.
        var reset = view.RetireDocumentAsync();
        Require(!reset.IsCompleted, "Document reset did not wait for controller acquisition.");
        owner.Close();
        var retirement = manager.RetireActivationAsync(activation);
        Require(!retirement.IsCompleted && !initialization.IsCompleted && !view.CanCapture,
            "Final retirement did not revoke presentation while still owning acquisition.");
        try
        {
            await Task.WhenAll(observed, reset, retirement).WaitAsync(Remaining());
            Require(native.Native is null && manager.NativeSessionCount == 0, "Final retirement retained the late controller or cached its lease.");
            await manager.DisposeAsync().AsTask().WaitAsync(Remaining());
            Require(!Directory.Exists(engine.DataDirectory), "The native browser data root survived final retirement.");
            Console.WriteLine("shared-browser: pending-reset; acquisition-settled; native-exit; profile-removed");
        }
        catch (Exception)
        {
            Console.Error.WriteLine($"pending-reset state: initialization={initialization.Status}; observed={observed.Status}; reset={reset.Status}; retirement={retirement.Status}; sessions={manager.NativeSessionCount}");
            throw;
        }

        TimeSpan Remaining()
        {
            var remaining = AppShutdownBudgets.BrowserCleanup - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Pending reset retirement exceeded its original cleanup allowance.");
            return remaining;
        }

        async Task ObserveAcquisitionAsync()
        {
            await initialization;
            Require(Environment.CurrentManagedThreadId == sta && engine.BrowserProcessIds.Count == 1,
                "The late controller did not acquire a real browser on its original STA.");
            Console.WriteLine($"pending-reset: acquired WebView2 {native.Native!.Environment.BrowserVersionString}");
            WindowsWebViewBrowserProcess.Record(engine.BrowserProcessIds[0]);
        }
    }

    private static async Task KillOwnedAsync(int pid)
    {
        using var owner = Process.GetCurrentProcess();
        using var process = Process.GetProcessById(pid);
        _ = process.SafeHandle;
        Require(process.StartTime.ToUniversalTime() >= owner.StartTime.ToUniversalTime(), "The native failure fixture selected a pre-existing process.");
        Console.WriteLine($"owned-native-failure-process:{pid}:{process.StartTime.ToUniversalTime().Ticks}");
        process.Kill();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task RequireCleanAsync(WindowsBrowserView view, string context, long retirementStarted)
    {
        await RequireWorkersAsync(view, context, present: false, () => CleanupRemaining(retirementStarted));
        var remaining = CleanupRemaining(retirementStarted);
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("History verification exceeded the browser cleanup allowance.");
        using var history = JsonDocument.Parse(await view.Native!.CallDevToolsProtocolMethodAsync("Page.getNavigationHistory", "{}").WaitAsync(remaining));
        var entries = history.RootElement.GetProperty("entries");
        Require(entries.GetArrayLength() == 1 && entries[0].GetProperty("url").GetString() == "about:blank", "A cleaned controller retained old document history.");
    }

    private static TimeSpan CleanupRemaining(long started) => AppShutdownBudgets.BrowserCleanup - Stopwatch.GetElapsedTime(started);

    private static async Task RequireWorkersAsync(WindowsBrowserView view, string context, bool present, Func<TimeSpan> remaining)
    {
        var last = "no observation";
        TimeSpan Budget()
        {
            var time = remaining();
            return time > TimeSpan.Zero ? time : throw new TimeoutException($"Native worker {(present ? "startup" : "retirement")} deadline; {last}.");
        }
        while (true)
        {
            var available = Budget();
            using var targets = JsonDocument.Parse(await view.Native!.CallDevToolsProtocolMethodAsync("Target.getTargets", "{}").WaitAsync(available));
            var nativeWorkers = targets.RootElement.GetProperty("targetInfos").EnumerateArray()
                .Where(item => item.GetProperty("type").GetString() is "worker" or "shared_worker" or "service_worker").ToArray();
            var unscoped = nativeWorkers.Any(item => !item.TryGetProperty("browserContextId", out var owner) || string.IsNullOrEmpty(owner.GetString()));
            var workers = nativeWorkers.Where(item => item.TryGetProperty("browserContextId", out var owner) && owner.GetString() == context)
                .Select(item => item.GetProperty("type").GetString()).ToArray();
            last = $"unscoped={unscoped}; contextWorkers=[{string.Join(',', workers)}]";
            available = Budget();
            if (!unscoped && (present ? workers.Distinct().Count() == 3 : workers.Length == 0)) return;
            // A stopped service-worker target can briefly lose its context while
            // the browser removes it. Wait for conclusive absence; neither pid=0
            // nor detached debugger state is proof. Never grant a fresh budget.
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(20, available.TotalMilliseconds)));
        }
    }

    private static async Task PendingAsync(IClassicDesktopStyleApplicationLifetime desktop, string root, bool closeOwner)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var ui = AvaloniaUiDispatcher.Instance;
        await using var engine = new WindowsBrowserEngine(ui);
        WindowsBrowserView? native = null;
        await using var manager = new AppBrowserManager(ui, _ =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            return native = new WindowsBrowserView(engine, ui);
        }, () => OperatingSystem.IsWindows() ? engine.DisposeAsync().AsTask() : throw new PlatformNotSupportedException());
        var key = Path.Combine(root, "pending-" + closeOwner);
        using var view = manager.Create(key);
        var owner = new Window { Title = "Sunder pending native owner retirement", Width = 320, Height = 240, Content = view.Control };
        owner.Show();
        Require(native!.Initialization is { IsCompleted: false }, "The fixture missed pending native creation.");
        var originalThread = Environment.CurrentManagedThreadId;
        var started = Stopwatch.GetTimestamp();
        var stage = "detach-owner";
        try
        {
            Report();
            if (closeOwner) owner.Close(); // Genuine parent destruction, without a preparatory detach.
            else owner.Content = null;
            stage = "retire-activation";
            Report();
            var retirement = manager.RetireActivationAsync(key);
            Require(!view.CanCapture, "A retiring view retained capture authority.");
            try { view.Navigate(new Uri("https://retired.invalid")); throw new InvalidOperationException("A retiring view accepted navigation."); }
            catch (ObjectDisposedException) { }
            // The STA must remain responsive while SDK acquisition is pending.
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
            Require(Stopwatch.GetElapsedTime(started) < AppShutdownBudgets.UiCleanup, "Native cleanup blocked presentation retirement.");
            Console.WriteLine($"shared-browser: pending-revoked; presentation-detached; native-cleanup-owned; closedOwner={closeOwner}; responseMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.###}");
            await retirement.WaitAsync(AppShutdownBudgets.BrowserCleanup - Stopwatch.GetElapsedTime(started));
            Require(native.Initialization!.IsCompletedSuccessfully && Environment.CurrentManagedThreadId == originalThread,
                "Native retirement abandoned acquisition or its original STA.");
            foreach (var pid in engine.BrowserProcessIds) WindowsWebViewBrowserProcess.Record(pid);
            var remaining = AppShutdownBudgets.BrowserCleanup - Stopwatch.GetElapsedTime(started);
            Require(remaining > TimeSpan.Zero, "Pending native retirement exceeded its browser cleanup allowance.");
            stage = "retire-engine";
            Report();
            await manager.DisposeAsync().AsTask().WaitAsync(remaining);
            Require(!Directory.Exists(engine.DataDirectory), "Pending native retirement retained its engine profile.");
            owner.Close();
            Console.WriteLine($"shared-browser: pending-initialization-settled; original-sta; native-exit; profile-removed; closedOwner={closeOwner}; retirementMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.###}");
        }
        catch
        {
            Report();
            foreach (var entry in AppSessionLog.Snapshot())
                Console.Error.WriteLine(entry.Message + Environment.NewLine + entry.ExceptionText);
            throw;
        }

        void Report()
        {
            var environment = (Task?)typeof(WindowsBrowserEngine).GetField("_environment", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine);
            var engineRetirement = (Task?)typeof(WindowsBrowserEngine).GetField("_disposal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine);
            var owned = (Dictionary<int, Process>)typeof(WindowsBrowserEngine).GetField("_processes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
            var nativeExits = string.Join(',', owned.Select(pair =>
            {
                var exited = pair.Value.HasExited;
                return $"{pair.Key}:{exited}:{(exited ? pair.Value.ExitTime.ToUniversalTime().ToString("O") : "pending")}";
            }));
            var child = (nint)typeof(WindowsBrowserView).GetField("_child", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!;
            var parking = (nint)typeof(WindowsBrowserEngine).GetField("_parking", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
            Console.WriteLine($"shared-browser-pending: stage={stage}; closedOwner={closeOwner}; elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.###}; environment={environment?.Status}; initialization={native.Initialization?.Status}; childValid={IsWindow(child)}; parent={WindowsBrowserEngine.ParentOf(child)}; parking={parking}; parkingValid={IsWindow(parking)}; sessions={manager.NativeSessionCount}; browsers={string.Join(',', engine.BrowserProcessIds)}; dataRoot={Directory.Exists(engine.DataDirectory)}; thread={Environment.CurrentManagedThreadId}; engineRetirement={engineRetirement?.Status}; nativeExits={nativeExits}; utc={DateTime.UtcNow:O}; threadPoolThreads={ThreadPool.ThreadCount}; pendingThreadPoolWork={ThreadPool.PendingWorkItemCount}");
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
}
