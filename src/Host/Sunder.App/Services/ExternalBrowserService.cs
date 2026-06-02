using System.Diagnostics;

namespace Sunder.App.Services;

public sealed class ExternalBrowserService
{
    public void Open(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError($"Failed to open browser for '{uri}': {ex.Message}", ex);
            throw;
        }
    }
}
