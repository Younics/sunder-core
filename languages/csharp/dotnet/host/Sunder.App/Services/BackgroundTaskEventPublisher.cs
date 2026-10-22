using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class BackgroundTaskEventPublisher(object sender)
{
    public event EventHandler<BackgroundTaskChangedEventArgs>? TaskChanged;

    public void Publish(BackgroundTaskSnapshot? snapshot)
    {
        var handlers = TaskChanged;
        if (snapshot is null || handlers is null)
        {
            return;
        }

        var args = new BackgroundTaskChangedEventArgs(snapshot);
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<BackgroundTaskChangedEventArgs>)handler)(sender, args);
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("A background task change subscriber failed.", ex);
            }
        }
    }
}
