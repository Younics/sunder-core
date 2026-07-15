using Avalonia.Threading;

namespace Sunder.App.Services;

public interface IUiDispatcher
{
    bool CheckAccess();

    Task InvokeAsync(Action action);

    Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
        }).WaitAsync(cancellationToken);
    }

    Task InvokeAsync(Func<Task> action);

    Task<T> InvokeAsync<T>(Func<T> action);

    Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return action();
        }).WaitAsync(cancellationToken);
    }

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

    public async Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                action();
                return true;
            },
            DispatcherPriority.Normal,
            cancellationToken).GetTask().ConfigureAwait(false);
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

    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CheckAccess())
        {
            return Task.FromResult(action());
        }

        return Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return action();
            },
            DispatcherPriority.Normal,
            cancellationToken).GetTask();
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
