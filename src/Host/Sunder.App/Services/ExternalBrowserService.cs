using System.Diagnostics;

namespace Sunder.App.Services;

public sealed class ExternalBrowserService
{
    private readonly Action<ProcessStartInfo> _startProcess;

    public ExternalBrowserService()
        : this(static startInfo => Process.Start(startInfo))
    {
    }

    internal ExternalBrowserService(Action<ProcessStartInfo> startProcess)
    {
        _startProcess = startProcess ?? throw new ArgumentNullException(nameof(startProcess));
    }

    public void Open(Uri uri)
    {
        try
        {
            _startProcess(new ProcessStartInfo
            {
                FileName = uri.OriginalString,
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
