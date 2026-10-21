using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPolicyShutdownTests
{
    [Fact]
    public async Task PolicyShutdown_DrainsOwnersBeforeExitingOnceWithFailureCode()
    {
        var operations = new ConcurrentQueue<string>();
        var completionPosted = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = 0;
        using var desktop = new ClassicDesktopStyleApplicationLifetime();
        var exitCodes = new List<int>();
        var app = new Sunder.App.App(action =>
        {
            Interlocked.Increment(ref posts);
            completionPosted.TrySetResult(action);
        }, (lifetime, exitCode) =>
        {
            Assert.Same(desktop, lifetime);
            operations.Enqueue("desktop shutdown");
            exitCodes.Add(exitCode);
        });
        app.ApplicationLifetime = desktop;
        var cleanup = new BlockingCleanup(operations);
        var services = new ServiceCollection();
        services.AddSingleton(_ => cleanup);
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<BlockingCleanup>();
        AppField("_serviceProvider").SetValue(app, provider);
        var tasks = Assert.IsType<OwnedTaskObserver>(AppField("_tasks").GetValue(app));
        var startupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tasks.Run(async token =>
        {
            using var registration = token.Register(() => startupCanceled.TrySetResult());
            startupStarted.TrySetResult();
            await releaseStartup.Task;
            operations.Enqueue("startup stopped");
        }, "draining startup work");
        Task shutdown = Task.CompletedTask;

        try
        {
            await startupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            app.RequestPolicyShutdown();
            shutdown = Assert.IsAssignableFrom<Task>(AppField("_desktopShutdownTask").GetValue(app));
            app.RequestPolicyShutdown();
            await startupCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(cleanup.Started.Task.IsCompleted);
            Assert.False(completionPosted.Task.IsCompleted);
            Assert.Empty(exitCodes);
            releaseStartup.SetResult();
            await cleanup.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            app.RequestPolicyShutdown();
            Assert.False(shutdown.IsCompleted);
            Assert.False(completionPosted.Task.IsCompleted);
            Assert.Empty(exitCodes);

            cleanup.Release.SetResult();
            var completeOnUiThread = await completionPosted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await shutdown;
            Assert.Empty(exitCodes);
            completeOnUiThread();

            Assert.Equal(["startup stopped", "services stopped", "desktop shutdown"], operations.ToArray());
            Assert.Equal(1, cleanup.Calls);
            Assert.Equal(1, Volatile.Read(ref posts));
            Assert.Equal(1, Assert.Single(exitCodes));
        }
        finally
        {
            releaseStartup.TrySetResult();
            cleanup.Release.TrySetResult();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            await provider.DisposeAsync();
            await tasks.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            tasks.Dispose();
        }
    }

    private static FieldInfo AppField(string name)
        => typeof(Sunder.App.App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing App ownership field '{name}'.");

    private sealed class BlockingCleanup(ConcurrentQueue<string> operations) : IAsyncDisposable
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            await Release.Task;
            operations.Enqueue("services stopped");
        }
    }

}
