using Avalonia;
using Avalonia.Controls;
using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed partial class AppPackageDevelopmentServiceTests
{
    [Theory]
    [InlineData("package")]
    [InlineData("view")]
    [InlineData("revision")]
    public async Task ViewInspectionRejectsBackendBindingsOutsideTheExactAppliedSelection(string boundary)
    {
        var identity = new string('a', 64);
        var backend = new Backend { HasApp = true, State = PackageReadinessState.Ready, AppReadiness = "Activated",
            SourceContentIdentity = identity, AppliedContentIdentity = identity };
        var root = new Panel(); root.Arrange(new Rect(0, 0, 300, 200));
        backend.OpenView = (package, view, revision, _) => Task.FromResult(new AppDevelopmentViewBinding(
            boundary == "package" ? "other.package" : package, boundary == "view" ? "other.view" : view,
            boundary == "revision" ? new(Guid.NewGuid(), revision.SessionGeneration, revision.SourceContentIdentity) : revision,
            root, new QueuedViewDispatcher(), () => throw new InvalidOperationException("Out-of-scope binding must not be inspected.")));
        var publication = new AppPackageGenerationPublication(); publication.Publish();
        await using var service = new AppPackageDevelopmentService(backend, publication, Guid.NewGuid());
        await using var session = await service.LoadAsync("first", false);
        await Assert.ThrowsAsync<InvalidDataException>(() => session.InspectViewAsync("demo.view"));
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("publication")]
    [InlineData("session")]
    public async Task ViewOperationsDrainTheirQueuedDispatcherWorkBeforeReleasingTheDevelopmentOwner(string stop)
    {
        var identity = new string('a', 64);
        var backend = new Backend { HasApp = true, State = PackageReadinessState.Ready, AppReadiness = "Activated",
            SourceContentIdentity = identity, AppliedContentIdentity = identity };
        var root = new Panel(); root.Arrange(new Rect(0, 0, 300, 200));
        var dispatcher = new QueuedViewDispatcher();
        backend.OpenView = (package, view, revision, _) => Task.FromResult(new AppDevelopmentViewBinding(package, view, revision, root, dispatcher, () => { }));
        var publication = new AppPackageGenerationPublication(); publication.Publish();
        await using var service = new AppPackageDevelopmentService(backend, publication, Guid.NewGuid());
        var session = await service.LoadAsync("first", false);
        using var caller = new CancellationTokenSource();
        var inspection = session.InspectViewAsync("demo.view", caller.Token);
        Assert.False(inspection.IsCompleted);
        if (stop == "caller") caller.Cancel();
        if (stop == "publication") publication.Revoke();
        var release = session.DisposeAsync().AsTask();
        try
        {
            Assert.Equal(0, Assert.Single(backend.Owners).Disposals);
            Assert.False(release.IsCompleted);
            dispatcher.Flush();
            if (stop == "caller")
            {
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspection.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(caller.Token, error.CancellationToken);
            }
            else await Assert.ThrowsAsync<ObjectDisposedException>(() => inspection.WaitAsync(TimeSpan.FromSeconds(5)));
            await release.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, backend.Owners[0].Disposals);
        }
        finally { dispatcher.Flush(); await session.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeInputSettlementOwnsTheSessionUntilItsReceiptIsRecorded(bool releaseOwner)
    {
        var identity = new string('a', 64);
        var backend = new Backend { HasApp = true, State = PackageReadinessState.Ready, AppReadiness = "Activated",
            SourceContentIdentity = identity, AppliedContentIdentity = identity };
        var root = new SessionDocumentControl(); root.Arrange(new Rect(0, 0, 300, 200));
        backend.OpenView = (package, view, revision, _) => Task.FromResult(new AppDevelopmentViewBinding(package, view, revision,
            root, new QueuedViewDispatcher { Queue = false }, () => { }));
        var publication = new AppPackageGenerationPublication(); publication.Publish();
        await using var service = new AppPackageDevelopmentService(backend, publication, Guid.NewGuid());
        await using var session = await service.LoadAsync("first", false);
        var snapshot = await session.InspectViewAsync("demo.view");
        using var caller = new CancellationTokenSource();
        var request = new PackageDevelopmentViewInput(Guid.NewGuid().ToString("N"), snapshot.SnapshotId, Assert.Single(snapshot.Elements).Reference, "invoke");
        var input = session.InteractViewAsync(request, caller.Token);
        Task? release = null;
        try
        {
            Assert.Equal(1, root.Inputs);
            if (releaseOwner) release = session.DisposeAsync().AsTask(); else caller.Cancel();
            Assert.False(input.IsCompleted);
            Assert.Equal(0, Assert.Single(backend.Owners).Disposals);
            if (release is not null) Assert.False(release.IsCompleted);
            root.Reply.SetResult(true);
            var receipt = await input.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(receipt.OutcomeUnknown);
            Assert.Equal("unknown", receipt.State);
            if (release is not null)
            {
                await release.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, backend.Owners[0].Disposals);
            }
            else Assert.Equal(receipt, await session.InteractViewAsync(request));
            Assert.Equal(1, root.Inputs);
        }
        finally { root.Reply.TrySetResult(true); }
    }

    private sealed class SessionDocumentControl : Control, IAppDevelopmentDocumentOwner, IAppDevelopmentDocument
    {
        internal readonly TaskCompletionSource<bool> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Inputs;
        public IAppDevelopmentDocument DevelopmentDocument => this;
        public void RequireCurrent(string documentId) => Assert.Equal("document", documentId);
        public Task<AppDevelopmentDocumentSnapshot> InspectAsync(string snapshotId, CancellationToken token)
            => Task.FromResult(new AppDevelopmentDocumentSnapshot("document", [new(Guid.NewGuid().ToString("N"), "button", "Native action", true, ["invoke"])], false));
        public Task<bool> InteractAsync(string documentId, PackageDevelopmentViewInput request, string nativeElementReference, CancellationToken token)
        { Inputs++; return Reply.Task; }
    }

    private sealed class QueuedViewDispatcher : IUiDispatcher
    {
        internal bool Queue = true;
        private readonly Queue<Action> _queued = [];
        internal void Flush() { while (_queued.TryDequeue(out var action)) action(); }
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });
        public Task InvokeAsync(Func<Task> action) => InvokeAsync<Task>(action).Unwrap();
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => InvokeAsync<Task<T>>(action).Unwrap();
        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            if (!Queue) return Task.FromResult(action());
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queued.Enqueue(() =>
            {
                try { completion.SetResult(action()); }
                catch (OperationCanceledException error) { completion.SetException(error); }
                catch (Exception error) { completion.SetException(error); }
            });
            return completion.Task;
        }
    }
}
