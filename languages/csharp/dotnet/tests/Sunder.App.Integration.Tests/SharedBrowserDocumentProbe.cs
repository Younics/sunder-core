using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Sunder.App.Services;

// Observe the production lease before the package requests navigation. In
// particular a deferred reopen may not even have a native controller yet.
[SupportedOSPlatform("windows")]
internal sealed class SharedBrowserDocumentProbe(AppBrowserManager manager, Uri page,
    Func<IAppWebView, WindowsBrowserView?> nativeForLease, BrowserProbeDeadline deadline) : IAppWebViewFactory, IDisposable
{
    private readonly Dictionary<IAppWebView, Document> _documents = [];

    public IAppWebView Create(string key)
    {
        var lease = manager.Create(key);
        _documents.Add(lease, new Document(lease, page, () => nativeForLease(lease)));
        return lease;
    }

    public Task RetireActivationAsync(string key) => manager.RetireActivationAsync(key);

    internal async Task ReadyAsync(IAppWebView lease, string name)
    {
        var document = _documents[lease];
        try { await document.ObserveAsync(deadline, name); }
        finally { document.Dispose(); _documents.Remove(lease); }
    }

    public void Dispose()
    {
        foreach (var document in _documents.Values) document.Dispose();
        _documents.Clear();
    }

    private sealed class Document : IDisposable
    {
        private readonly IAppWebView _lease;
        private readonly Uri _page;
        private readonly Func<WindowsBrowserView?> _native;
        private readonly TaskCompletionSource _navigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Exception> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Document(IAppWebView lease, Uri page, Func<WindowsBrowserView?> native)
        {
            _lease = lease; _page = page; _native = native;
            lease.NavigationCompleted += Navigated;
            lease.Failed += Failed;
        }

        private void Navigated(object? sender, AppWebNavigationCompletedEventArgs e)
        {
            if (e.Uri != _page) return;
            if (e.Succeeded) _navigation.TrySetResult();
            else _failure.TrySetResult(new InvalidDataException("The requested fixture navigation failed."));
        }

        private void Failed(object? sender, AppWebViewFailedEventArgs e) => _failure.TrySetResult(e.Error);

        internal async Task ObserveAsync(BrowserProbeDeadline deadline, string name)
        {
            var phase = "attachment";
            Task Wait(Task operation) => deadline.ObserveAsync(name + "/" + phase, operation, _failure.Task);
            try
            {
                if (_native() is { } direct && ReferenceEquals(_lease.Control, direct.Control))
                {
                    await OnLayoutAsync(_lease.Control, () => direct.Initialization is not null, Wait);
                    phase = "controller";
                    await Wait(direct.Initialization!);
                }
                phase = "navigation";
                await Wait(_navigation.Task);
                phase = "viewport";
                await OnLayoutAsync(_lease.Control, () => _lease.CanCapture && _lease.CaptureViewport.Width > 0 && _lease.CaptureViewport.Height > 0, Wait);
                var native = _native()?.Native ?? throw new InvalidDataException("The visible lease has no native controller.");
                phase = "document-script";
                var readId = _lease.InvokeScriptAsync("globalThis.fixtureId");
                await Wait(readId);
                var id = JsonSerializer.Deserialize<string>(await readId ?? "null");
                if (!Guid.TryParse(id, out _)) throw new InvalidDataException("The lease did not run the fixture page script.");
                phase = "workers-and-bridge";
                // Unlike ExecuteScript, Runtime.evaluate can join a JS promise.
                // Compare its document id with the id read via the live lease:
                // another controller/document cannot supply the readiness proof.
                var evaluation = native.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
                {
                    expression = "Promise.all([fixtureWorkers,fixtureBridge]).then(()=>fixtureId)",
                    awaitPromise = true, returnByValue = true,
                }));
                await Wait(evaluation);
                using var response = JsonDocument.Parse(await evaluation);
                if (response.RootElement.TryGetProperty("exceptionDetails", out var error))
                    throw new InvalidDataException($"Fixture page rejected setup: {error}");
                if (response.RootElement.GetProperty("result").GetProperty("value").GetString() != id)
                    throw new InvalidDataException("The ready document does not belong to the observed lease.");
                Console.WriteLine($"shared-browser-ready: view={name}; runtime={native.Environment.BrowserVersionString}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"shared-browser-readiness-failed: view={name}; phase={phase}; initialization={_native()?.Initialization?.Status}; navigation={_navigation.Task.Status}; {error}");
                throw;
            }
        }

        public void Dispose()
        {
            _lease.NavigationCompleted -= Navigated;
            _lease.Failed -= Failed;
            _failure.TrySetResult(new OperationCanceledException("Fixture observation ended."));
        }
    }

    internal static async Task OnLayoutAsync(Control control, Func<bool> ready, Func<Task, Task> wait)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, EventArgs e) { if (ready()) signal.TrySetResult(); }
        control.LayoutUpdated += Check;
        try { Check(null, EventArgs.Empty); await wait(signal.Task); }
        finally { control.LayoutUpdated -= Check; }
    }
}
