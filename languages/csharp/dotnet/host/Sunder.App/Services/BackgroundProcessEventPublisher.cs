using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class BackgroundProcessEventPublisher(object sender)
{
    public event EventHandler<BackgroundProcessChangedEventArgs>? ProcessChanged;

    public void Publish(BackgroundProcessSnapshot? snapshot)
    {
        var handlers = ProcessChanged;
        if (snapshot is null || handlers is null)
        {
            return;
        }

        var args = new BackgroundProcessChangedEventArgs(snapshot);
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<BackgroundProcessChangedEventArgs>)handler)(sender, args);
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("A background process change subscriber failed.", ex);
            }
        }
    }
}
