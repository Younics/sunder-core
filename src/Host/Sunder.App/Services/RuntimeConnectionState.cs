namespace Sunder.App.Services;

public sealed class RuntimeConnectionState(Uri runtimeUrl)
{
    private readonly object _syncRoot = new();
    private Uri _runtimeUrl = RuntimeUrlHelper.Normalize(runtimeUrl);

    public Uri RuntimeUrl
    {
        get
        {
            lock (_syncRoot)
            {
                return _runtimeUrl;
            }
        }
        set
        {
            lock (_syncRoot)
            {
                _runtimeUrl = RuntimeUrlHelper.Normalize(value);
            }
        }
    }
}
