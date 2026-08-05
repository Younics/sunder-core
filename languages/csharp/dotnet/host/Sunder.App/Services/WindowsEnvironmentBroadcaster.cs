using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Sunder.App.Services;

internal static partial class WindowsEnvironmentBroadcaster
{
    private const int HwndBroadcast = 0xffff;
    private const int WmSettingChange = 0x001a;
    private const int SmtoAbortIfHung = 0x0002;

    public static void BroadcastEnvironmentChanged()
    {
        try
        {
            SendMessageTimeout(
                HwndBroadcast,
                WmSettingChange,
                UIntPtr.Zero,
                "Environment",
                SmtoAbortIfHung,
                5000,
                out _);
        }
        catch
        {
            // PATH changes still apply for new terminals even if broadcasting fails.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Classic DllImport avoids requiring unsafe blocks in the app project.")]
    private static extern IntPtr SendMessageTimeout(
        int hWnd,
        int msg,
        UIntPtr wParam,
        string lParam,
        int flags,
        int timeout,
        out UIntPtr result);
}
