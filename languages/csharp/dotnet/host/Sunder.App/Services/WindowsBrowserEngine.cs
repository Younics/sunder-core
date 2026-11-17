using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Web.WebView2.Core;

namespace Sunder.App.Services;

// One environment per App session. Controllers/profiles are view-owned; keeping
// this object alive neither grants package authority nor proves browser liveness.
[SupportedOSPlatform("windows")]
internal sealed class WindowsBrowserEngine(IUiDispatcher ui) : IAsyncDisposable
{
    private readonly string _root = AppLocalState.GetPath("webview-engines", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<int, Process> _processes = [];
    private readonly List<Exception> _observationFailures = [];
    private Task<CoreWebView2Environment>? _environment;
    private Task? _disposal;
    private nint _parking;
    private bool _closing;
    internal IReadOnlyList<int> BrowserProcessIds => _processes.Keys.ToArray();
    internal string DataDirectory => _root;

    internal nint ParkingWindow
    {
        get
        {
            if (_parking == 0) _parking = CreateWindow(0, "STATIC", "", 0x80000000, 0, 0, 1, 1, 0, 0, 0, 0);
            if (_parking == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return _parking;
        }
    }

    internal async Task<(CoreWebView2Environment Environment, CoreWebView2Controller Controller)> CreateAsync(nint parent, string profile)
    {
        if (!ui.CheckAccess()) throw new InvalidOperationException("WebView2 creation requires its original STA.");
        ObjectDisposedException.ThrowIf(_closing, this);
        var environment = await (_environment ??= CreateEnvironmentAsync());
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.ProfileName = profile;
        options.IsInPrivateModeEnabled = true;
        // WebView2 also uses the root HWND during asynchronous acquisition. A
        // surviving child is insufficient if its transient top-level is closed
        // or reparented meanwhile. Acquire under the engine's stable hidden root,
        // then attach the completed controller to its session-owned child.
        var controller = await environment.CreateCoreWebView2ControllerAsync(ParkingWindow, options);
        try
        {
            controller.IsVisible = false;
            controller.ParentWindow = parent;
            ObserveProcess(controller.CoreWebView2.BrowserProcessId);
            return (environment, controller);
        }
        catch { controller.Close(); throw; }
    }

    private async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        AppWebViewProfile.ValidateWindowsPath(_root, AppWebViewProfile.WindowsMaximumSharedUserDataPathLength);
        var environment = await CoreWebView2Environment.CreateAsync(null, _root,
            new CoreWebView2EnvironmentOptions { AllowSingleSignOnUsingOSPrimaryAccount = false });
        environment.BrowserProcessExited += (_, args) =>
            AppSessionLog.WriteInfo($"Shared package browser exited; pid={args.BrowserProcessId}; kind={args.BrowserProcessExitKind}.", visibleInDeveloperLog: false);
        environment.ProcessInfosChanged += (_, _) =>
        {
            if (_closing) return;
            // Also retain a process started by an acquisition which subsequently
            // fails. Controller publication is not the first ownership boundary.
            try
            {
                foreach (var process in environment.GetProcessInfos())
                    if (process.Kind == CoreWebView2ProcessKind.Browser) ObserveProcess(checked((uint)process.ProcessId));
            }
            catch (Exception error)
            {
                // Never throw across a native event callback or silently accept
                // missing process-ownership evidence as successful final cleanup.
                _observationFailures.Add(error);
                AppSessionLog.WriteError("Shared browser process ownership observation failed.", error);
            }
        };
        return environment;
    }

    private void ObserveProcess(uint id)
    {
        foreach (var prior in _processes.Where(pair => pair.Value.HasExited).ToArray())
        {
            _processes.Remove(prior.Key);
            prior.Value.Dispose();
        }
        if (_processes.ContainsKey(checked((int)id))) return;
        var process = Process.GetProcessById(checked((int)id));
        _ = process.SafeHandle; // Hold the exact native identity across PID reuse.
        _processes.Add(process.Id, process);
    }

    public ValueTask DisposeAsync() => new(_disposal ??= ui.InvokeAsync(CloseAsync));

    private async Task CloseAsync()
    {
        var started = Stopwatch.GetTimestamp();
        void Stage(string stage) => AppSessionLog.WriteInfo($"Shared browser engine retirement: stage={stage}; elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.###}; utc={DateTime.UtcNow:O}; threadPoolThreads={ThreadPool.ThreadCount}; pendingThreadPoolWork={ThreadPool.PendingWorkItemCount}.", visibleInDeveloperLog: false);
        _closing = true;
        Stage("environment-drain-started");
        Exception? initializationFailure = null;
        if (_environment is { } creation)
        {
            try { await creation; }
            catch (Exception error) { initializationFailure = error; }
        }
        Stage("environment-drained");
        // Profiles/controllers have already drained. Empty GetProcessInfos or a
        // missing SDK exit event is not accepted in place of held process exit.
        AppSessionLog.WriteInfo($"Shared browser final exit observation started; processes={string.Join(',', _processes.Keys)}.", visibleInDeveloperLog: false);
        if (_environment is { IsCompletedSuccessfully: true } ready)
            AppSessionLog.WriteInfo("Shared browser native process snapshot: " + string.Join(',', ready.Result.GetProcessInfos().Select(process => $"{process.Kind}:{process.ProcessId}")), visibleInDeveloperLog: false);
        Stage("process-exit-drain-started");
        await Task.WhenAll(_processes.Values.ToArray().Select(process => process.WaitForExitAsync()));
        Stage("process-exit-drained");
        foreach (var process in _processes.Values)
            AppSessionLog.WriteInfo($"Shared browser held process exited: pid={process.Id}; nativeExitUtc={process.ExitTime.ToUniversalTime():O}.", visibleInDeveloperLog: false);
        AppSessionLog.WriteInfo("Shared browser native processes exited; reclaiming its data root.", visibleInDeveloperLog: false);
        Stage("data-reclamation-started");
        await Task.Run(async () =>
        {
            while (Directory.Exists(_root))
            {
                try { Directory.Delete(_root, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { await Task.Delay(50); }
            }
        });
        Stage("data-reclaimed");
        foreach (var process in _processes.Values) process.Dispose();
        _processes.Clear();
        if (_parking != 0) { DestroyWindow(_parking); _parking = 0; }
        Stage("native-owner-released");
        if (_observationFailures.Count != 0)
            throw new AggregateException("Shared browser process ownership could not be proven.", _observationFailures);
        if (initializationFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(initializationFailure).Throw();
    }

    internal nint CreateChild() => CreateWindow(0, "STATIC", "", 0x40000000 | 0x02000000, 0, 0, 1, 1, ParkingWindow, 0, 0, 0);
    internal void Park(nint child) => Reparent(child, ParkingWindow);
    internal static nint ParentOf(nint child) => GetParent(child);
    internal static void Reparent(nint child, nint parent)
    {
        Marshal.SetLastPInvokeError(0);
        if (SetParent(child, parent) == 0 && Marshal.GetLastPInvokeError() != 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
    }
    internal static void DestroyChild(nint child) { if (child != 0) DestroyWindow(child); }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindow(uint extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll")] private static extern nint GetParent(nint child);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
}
