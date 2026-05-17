using Avalonia.Threading;

namespace Sunder.App.Services;

internal static class UiThread
{
    public static Task InvokeAsync(Action action)
        => InvokeAsync(action, DispatcherPriority.Normal);

    public static Task InvokeAsync(Action action, DispatcherPriority priority)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }, priority);
        return completion.Task;
    }

    public static Task InvokeAsync(Func<Task> action)
        => InvokeAsync(action, DispatcherPriority.Normal);

    public static Task InvokeAsync(Func<Task> action, DispatcherPriority priority)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return action();
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }, priority);
        return completion.Task;
    }

    public static Task<T> InvokeAsync<T>(Func<T> action)
        => InvokeAsync(action, DispatcherPriority.Normal);

    public static Task<T> InvokeAsync<T>(Func<T> action, DispatcherPriority priority)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return Task.FromResult(action());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }, priority);
        return completion.Task;
    }

    public static Task<T> InvokeAsync<T>(Func<Task<T>> action)
        => InvokeAsync(action, DispatcherPriority.Normal);

    public static Task<T> InvokeAsync<T>(Func<Task<T>> action, DispatcherPriority priority)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return action();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                completion.SetResult(await action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }, priority);
        return completion.Task;
    }
}
