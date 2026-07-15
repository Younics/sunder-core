using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Sunder.App.Services;

internal sealed class InitialWindowReveal(IInitialWindowRevealPlatform? platform = null)
{
    private readonly IInitialWindowRevealPlatform _platform =
        platform ?? MacOsInitialWindowRevealPlatform.Instance;
    private bool _nativeConcealed;

    public void ShowConcealed(Window mainWindow, Window loadingWindow) =>
        ShowConcealed(
            new AvaloniaInitialRevealWindow(mainWindow),
            new AvaloniaInitialRevealWindow(loadingWindow)
        );

    public void Reveal(
        Window mainWindow,
        Window loadingWindow,
        Action setDesktopMainWindow,
        Action releaseRuntimePresentation
    ) =>
        Reveal(
            new AvaloniaInitialRevealWindow(mainWindow),
            new AvaloniaInitialRevealWindow(loadingWindow),
            setDesktopMainWindow,
            releaseRuntimePresentation
        );

    internal void ShowConcealed(IInitialRevealWindow mainWindow, IInitialRevealWindow loadingWindow)
    {
        mainWindow.ShowActivated = false;
        _nativeConcealed = _platform.TryConceal(mainWindow);
        mainWindow.Show();
        _nativeConcealed = _platform.TryConceal(mainWindow) || _nativeConcealed;
        loadingWindow.Activate();
        AppSessionLog.WriteInfo(
            $"Initial main window shown for pre-rendering. Native concealment: {_nativeConcealed}."
        );
    }

    internal void Reveal(
        IInitialRevealWindow mainWindow,
        IInitialRevealWindow loadingWindow,
        Action setDesktopMainWindow,
        Action releaseRuntimePresentation
    )
    {
        ArgumentNullException.ThrowIfNull(setDesktopMainWindow);
        ArgumentNullException.ThrowIfNull(releaseRuntimePresentation);
        if (_nativeConcealed && !_platform.TryReveal(mainWindow))
        {
            throw new InvalidOperationException("The native main window could not be revealed.");
        }

        AppSessionLog.WriteInfo("Initial main window native reveal completed.");
        setDesktopMainWindow();
        mainWindow.Activate();
        loadingWindow.Close();
        releaseRuntimePresentation();
    }
}

internal interface IInitialRevealWindow
{
    bool ShowActivated { set; }

    IPlatformHandle? PlatformHandle { get; }

    void Show();

    void Activate();

    void Close();
}

internal interface IInitialWindowRevealPlatform
{
    bool TryConceal(IInitialRevealWindow window);

    bool TryReveal(IInitialRevealWindow window);
}

internal sealed class AvaloniaInitialRevealWindow(Window window) : IInitialRevealWindow
{
    public bool ShowActivated
    {
        set => window.ShowActivated = value;
    }

    public IPlatformHandle? PlatformHandle => window.TryGetPlatformHandle();

    public void Show() => window.Show();

    public void Activate() => window.Activate();

    public void Close() => window.Close();
}

internal sealed class MacOsInitialWindowRevealPlatform : IInitialWindowRevealPlatform
{
    public static MacOsInitialWindowRevealPlatform Instance { get; } = new();

    private MacOsInitialWindowRevealPlatform() { }

    public bool TryConceal(IInitialRevealWindow window) =>
        TrySetNativeVisibility(window, alpha: 0, ignoresMouseEvents: true);

    public bool TryReveal(IInitialRevealWindow window) =>
        TrySetNativeVisibility(window, alpha: 1, ignoresMouseEvents: false);

    private static bool TrySetNativeVisibility(
        IInitialRevealWindow window,
        double alpha,
        bool ignoresMouseEvents
    )
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        var handle = window.PlatformHandle;
        if (
            handle is null
            || handle.Handle == IntPtr.Zero
            || !string.Equals(handle.HandleDescriptor, "NSWindow", StringComparison.Ordinal)
        )
        {
            return false;
        }

        try
        {
            ObjectiveC.SetDouble(
                handle.Handle,
                ObjectiveC.sel_registerName("setAlphaValue:"),
                alpha
            );
            ObjectiveC.SetBoolean(
                handle.Handle,
                ObjectiveC.sel_registerName("setIgnoresMouseEvents:"),
                ignoresMouseEvents
            );
            return true;
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to change initial NSWindow visibility.", ex);
            return false;
        }
    }

    private static class ObjectiveC
    {
        private const string Library = "/usr/lib/libobjc.A.dylib";

        [DllImport(Library, EntryPoint = "sel_registerName")]
        public static extern IntPtr sel_registerName(string selectorName);

        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern void SetDouble(IntPtr receiver, IntPtr selector, double value);

        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern void SetBoolean(
            IntPtr receiver,
            IntPtr selector,
            [MarshalAs(UnmanagedType.I1)] bool value
        );
    }
}
