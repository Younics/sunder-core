using System.Runtime.Versioning;

namespace Sunder.App.Services;

internal static class WindowsShellAssociationService
{
    private const string ProtocolKey = @"Software\Classes\sunder";
    private const string StackExtensionKey = @"Software\Classes\.sunderstack";
    private const string StackProgId = "Sunder.Stack";
    private const string StackProgIdKey = @"Software\Classes\" + StackProgId;
    private const string StackContentType = "application/vnd.sunder.stack";

    public static void RegisterForCurrentUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        RegisterProtocol(executablePath);
        RegisterStackFileType(executablePath);
    }

    public static void UnregisterForCurrentUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DeleteCurrentUserSubKeyTree(ProtocolKey);
        DeleteCurrentUserSubKeyTree(StackProgIdKey);
        DeleteCurrentUserSubKeyTree(StackExtensionKey);
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterProtocol(string executablePath)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ProtocolKey);
        key.SetValue(null, "URL:Sunder Protocol");
        key.SetValue("URL Protocol", string.Empty);

        using var iconKey = key.CreateSubKey("DefaultIcon");
        iconKey.SetValue(null, BuildIconValue(executablePath));

        using var commandKey = key.CreateSubKey(@"shell\open\command");
        commandKey.SetValue(null, BuildOpenCommand(executablePath));
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterStackFileType(string executablePath)
    {
        using (var extensionKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(StackExtensionKey))
        {
            extensionKey.SetValue(null, StackProgId);
            extensionKey.SetValue("Content Type", StackContentType);
            extensionKey.SetValue("PerceivedType", "data");
        }

        using var progIdKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(StackProgIdKey);
        progIdKey.SetValue(null, "Sunder Stack");

        using var defaultIconKey = progIdKey.CreateSubKey("DefaultIcon");
        defaultIconKey.SetValue(null, BuildIconValue(executablePath));

        using var commandKey = progIdKey.CreateSubKey(@"shell\open\command");
        commandKey.SetValue(null, BuildOpenCommand(executablePath));
    }

    private static string BuildOpenCommand(string executablePath)
        => $"\"{executablePath}\" \"%1\"";

    private static string BuildIconValue(string executablePath)
        => $"\"{executablePath}\",0";

    [SupportedOSPlatform("windows")]
    private static void DeleteCurrentUserSubKeyTree(string subKey)
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        }
        catch
        {
            // Shell association cleanup is best effort during uninstall.
        }
    }
}
