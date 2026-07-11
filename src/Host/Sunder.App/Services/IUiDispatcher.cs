using Avalonia.Threading;

namespace Sunder.App.Services;

public interface IUiDispatcher
{
    bool CheckAccess();

    Task InvokeAsync(Action action);

    Task InvokeAsync(Func<Task> action);

    Task<T> InvokeAsync<T>(Func<T> action);

    Task<T> InvokeAsync<T>(Func<Task<T>> action);
}

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public static AvaloniaUiDispatcher Instance { get; } = new();

    private AvaloniaUiDispatcher()
    {
    }

    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public Task InvokeAsync(Action action)
    {
        if (CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    public Task InvokeAsync(Func<Task> action)
    {
        if (CheckAccess())
        {
            return action();
        }

        return Dispatcher.UIThread.InvokeAsync(action);
    }

    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        if (CheckAccess())
        {
            return Task.FromResult(action());
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        if (CheckAccess())
        {
            return action();
        }

        return Dispatcher.UIThread.InvokeAsync(action);
    }
}
