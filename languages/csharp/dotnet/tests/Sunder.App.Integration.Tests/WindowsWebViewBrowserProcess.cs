using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;
using Sunder.Core.TestSupport;

// Fixture ownership evidence travels in the existing captured stdout. Never
// discover/kill a browser by name or reclaim a profile from an earlier run.
internal static class WindowsWebViewBrowserProcess
{
    private const string Prefix = "owned-webview2-process:";
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBrowserProcessId(nint self, out uint processId);

    internal static void Record(object? sender, WebViewAdapterEventArgs args)
    {
        if (!OperatingSystem.IsWindows() || args.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle handle)
            throw new InvalidOperationException("The native Windows fixture did not create WebView2.");
        var id = new Guid("76eceacb-0462-4d94-ac83-423a6793775e");
        var borrowedView = handle.CoreWebView2;
        nint view = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(borrowedView, in id, out view));
            // get_BrowserProcessId is slot 37 of the original ICoreWebView2 ABI.
            var getId = Marshal.GetDelegateForFunctionPointer<GetBrowserProcessId>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(view), 37 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(getId(view, out var pid));
            Record(checked((int)pid));
        }
        finally
        {
            if (view != 0) Marshal.Release(view);
        }
    }

    internal static void Record(int pid)
    {
            using var browser = Process.GetProcessById(pid);
            _ = browser.SafeHandle;
            var birth = browser.StartTime.ToUniversalTime().Ticks;
            using var app = Process.GetCurrentProcess();
            if (birth < app.StartTime.ToUniversalTime().Ticks)
                throw new InvalidOperationException("The isolated fixture reused an older browser process.");
            Console.WriteLine(FormattableString.Invariant($"{Prefix}{pid}:{birth}"));
    }

    internal static async Task<Sunder.Core.TestSupport.CapturedProcessResult> EnsureSuccessAndExitAsync(CapturedProcess process, TimeSpan timeout,
        Func<Task>? beforeExit = null)
    {
        Sunder.Core.TestSupport.CapturedProcessResult? result = null;
        Exception? actionFailure = null;
        try
        {
            if (beforeExit is not null) await beforeExit();
            result = await process.EnsureSuccessAsync(timeout);
        }
        catch (Exception error) { actionFailure = error; }
        try
        {
            // This also proves the App/probe child has exited on its failure path.
            await process.DisposeAsync();
            var owners = process.StandardOutput.Split('\n').Select(line => line.Trim())
                .Where(line => line.StartsWith(Prefix, StringComparison.Ordinal)).Distinct().ToArray();
            if (actionFailure is null && owners.Length == 0)
                throw new InvalidOperationException("The native fixture did not record its browser process identity.");
            foreach (var owner in owners)
            {
                var identity = owner[Prefix.Length..].Split(':');
                var pid = int.Parse(identity[0], NumberStyles.None, CultureInfo.InvariantCulture);
                var birth = long.Parse(identity[1], NumberStyles.None, CultureInfo.InvariantCulture);
                Process browser;
                try { browser = Process.GetProcessById(pid); }
                catch (ArgumentException) { continue; } // The exact observed process already exited.
                using (browser)
                {
                    _ = browser.SafeHandle;
                    if (browser.HasExited || browser.StartTime.ToUniversalTime().Ticks != birth) continue;
                    // Hold the kernel process handle, not a PID which may be reused.
                    await browser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                }
            }
        }
        catch (Exception cleanupFailure) when (actionFailure is not null)
        {
            throw new AggregateException("Native fixture action and browser exit both failed.", actionFailure, cleanupFailure);
        }
        if (actionFailure is not null) ExceptionDispatchInfo.Capture(actionFailure).Throw();
        Console.WriteLine(result!.StandardOutput);
        if (!string.IsNullOrWhiteSpace(result.StandardError)) Console.Error.WriteLine(result.StandardError);
        return result;
    }
}
