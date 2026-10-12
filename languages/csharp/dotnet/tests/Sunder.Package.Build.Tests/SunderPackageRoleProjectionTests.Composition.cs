using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Build.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderPackageRoleProjectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedComposition_OwnsLocalServicesOnceAndPreservesStagedLifecycle(bool failPreparation)
    {
        var analysis = SunderPackageCompositionPlannerTests.Analyze(CompositionSource + """
            public sealed class Settings : Sunder.Sdk.Worker.ISunderWorkerSettingsSchemaProvider
            {
                public Sunder.Sdk.Settings.PackageSettingsSchema SettingsSchema { get; } = new("Generated settings",
                    [new Sunder.Sdk.Settings.PackageSettingsSection("general", "General", null,
                        [new Sunder.Sdk.Settings.PackageSettingsField("name", "Name", Sunder.Sdk.Settings.PackageSettingsFieldKind.Text, defaultValue: "Sunder")])]);
            }
            """);
        Assert.True(analysis.Success, Errors(analysis));
        var load = new AssemblyLoadContext("generated-local-composition", isCollectible: true);
        try
        {
            var worker = EmitCompositionRole(load, analysis, SunderPackageRoleMembership.Worker);
            // The transport is deliberately unusable: ordinary local APIs must not
            // need a broker, serialization or an invented invocation context.
            var rpc = new RoleRpc(analysis.Compilation.Contracts[0].Descriptor, DummyRegistration(analysis.Compilation.Contracts[0]));
            var configured = Configure(worker, rpc);
            Assert.Equal("Generated settings", configured.SettingsSchema!.Summary);
            await using var composition = configured.Composition!;
            var journal = composition.Services.GetRequiredService(worker.GetType("Example.Journal", true)!);
            var events = (List<string>)journal.GetType().GetProperty("Events")!.GetValue(journal)!;
            Assert.Empty(events);
            journal.GetType().GetProperty("FailPreparation")!.SetValue(journal, failPreparation);
            if (failPreparation)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => Stage(composition, "PrepareCandidateAsync", CancellationToken.None));
                await composition.DisposeAsync();
                Assert.DoesNotContain("shutdown:counter", events);
                Assert.Contains("shutdown:z", events);
            }
            else
            {
                await Stage(composition, "PrepareCandidateAsync", CancellationToken.None);
                Assert.Equal(new[] { "ctor:store", "ctor:z", "prepare:z", "ctor:counter", "prepare:counter" }, events);
                var generation = (SunderWorkerGenerationContext)Activator.CreateInstance(typeof(SunderWorkerGenerationContext), BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null, args: [Guid.NewGuid(), 1L], culture: null)!;
                await Stage(composition, "CommitGenerationAsync", generation, CancellationToken.None);
                // Lifecycle forwarding only: this opaque test context is never used
                // for Host operations or RPC authority.
                var context = (SunderWorkerContext)RuntimeHelpers.GetUninitializedObject(typeof(SunderWorkerContext));
                await Stage(composition, "ActivateAsync", context, CancellationToken.None);
                Assert.Equal(new[] { "commit:z", "commit:counter", "activate:only", "activate:z", "ctor:deferred", "activate:counter" }, events.Skip(5));
                var deferred = composition.Services.GetRequiredService(worker.GetType("Example.DeferredStore", true)!);
                var lifecycle = composition.Services.GetRequiredService(worker.GetType("Example.ZLifecycle", true)!);
                Assert.Same(deferred, lifecycle.GetType().GetProperty("Deferred")!.GetValue(lifecycle));
                var api = composition.Services.GetRequiredService(worker.GetType("Sunder.Generated.Packages.Example_Notes_Services.ICounterService", true)!);
                var service = composition.Services.GetRequiredService(worker.GetType("Example.CounterService", true)!);
                var first = (Task)api.GetType().GetMethod("NextAsync")!.Invoke(api, [CancellationToken.None])!;
                Assert.Same(first, service.GetType().GetProperty("LastTask")!.GetValue(service));
                Assert.Equal(8L, await Number(first));
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    var failed = Assert.Throws<TargetInvocationException>(() => api.GetType().GetMethod("NextAsync")!.Invoke(api, [cancelled.Token]));
                    Assert.Equal(cancelled.Token, Assert.IsAssignableFrom<OperationCanceledException>(failed.InnerException).CancellationToken);
                }
                var reader = composition.Services.GetRequiredService(worker.GetType("Example.ReaderService", true)!);
                Assert.Equal(9L, await Number((Task)reader.GetType().GetMethod("ReadAsync")!.Invoke(reader, null)!));
                var provider = Assert.Single(configured.Providers, provider => provider.ProviderId.EndsWith("reader.provider", StringComparison.Ordinal));
                var result = await provider.Handler.InvokeUnaryAsync(null!, "reader", "read", JsonSerializer.SerializeToElement(new { }));
                Assert.Equal(10, result.GetProperty("number").GetInt64());
                using (var cancelled = new CancellationTokenSource())
                {
                    var values = api.GetType().GetMethod("WatchAsync")!.Invoke(api, [CancellationToken.None])!;
                    Assert.Same(values, service.GetType().GetProperty("LastStream")!.GetValue(service));
                    await using var cursor = Assert.IsAssignableFrom<IAsyncEnumerable<object>>(values).GetAsyncEnumerator(cancelled.Token);
                    Assert.True(await cursor.MoveNextAsync());
                    var blocked = cursor.MoveNextAsync().AsTask(); Assert.False(blocked.IsCompleted);
                    cancelled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
                }
                Assert.Single(events, value => value == "stream:disposed");
                Assert.Equal(0, rpc.Created);
                var activeCompletion = (Task)typeof(SunderWorkerComposition).GetProperty("Completion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(composition)!;
                Assert.False(activeCompletion.IsCompleted);
                var activeFailure = new InvalidOperationException("Active service failed.");
                ((TaskCompletionSource)journal.GetType().GetProperty("ActiveCompletion")!.GetValue(journal)!).SetException(activeFailure);
                Assert.Same(activeFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => activeCompletion));
                await composition.DisposeAsync();
                Assert.Equal(new[] { "shutdown:counter", "shutdown:z" }, events.Where(value => value.StartsWith("shutdown:", StringComparison.Ordinal)));
            }
            await composition.DisposeAsync();
            Assert.Single(events, value => value == "ctor:store");
            Assert.Single(events, value => value == "dispose:store");
            Assert.Single(events, value => value == "dispose:z");
            Assert.Single(events, value => value == "dispose:counter");
            Assert.Single(events, value => value == "dispose:only");
            Assert.Equal(failPreparation ? 0 : 1, events.Count(value => value == "ctor:deferred"));
            Assert.Equal(failPreparation ? 0 : 1, events.Count(value => value == "dispose:deferred"));
        }
        finally { load.Unload(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedWorkerMixedApi_KeepsOrdinaryCallsLocalAndRoutesHostContextThroughExplicitBrokerUse(bool failDisposal)
    {
        const string source = """
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            namespace Example;
            [SunderService]
            [SunderRuntimeUse]
            public sealed partial class MixedService : System.IAsyncDisposable
            {
                public static TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
                public static TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
                public static System.Exception? DisposalFailure { get; set; }
                public static int DisposalCount { get; private set; }
                public int RemoteExecutions { get; private set; }
                public Task<Reply> LocalAsync() => Task.FromResult<Reply>(new NumberReply { Number = 5 });
                public Task<Reply> RemoteAsync(SunderRpcInvocationContext context)
                { RemoteExecutions++; throw new System.InvalidOperationException("Only the Host may supply this context."); }
                public async ValueTask DisposeAsync()
                {
                    DisposalCount++;
                    DisposalStarted.TrySetResult();
                    await ReleaseDisposal.Task;
                    if (DisposalFailure is not null) throw DisposalFailure;
                }
            }
            [SunderUnion(typeof(NumberReply), typeof(TextReply)), SunderDiscriminator("kind")]
            public abstract record Reply;
            [SunderVariant("number")] public sealed record NumberReply : Reply { public required long Number { get; init; } }
            [SunderVariant("text")] public sealed record TextReply : Reply { public required string Text { get; init; } }
            """;
        var analysis = SunderPackageCompositionPlannerTests.Analyze(source);
        Assert.True(analysis.Success, Errors(analysis));
        var load = new AssemblyLoadContext("generated-mixed-local-api", isCollectible: true);
        try
        {
            var worker = EmitCompositionRole(load, analysis, SunderPackageRoleMembership.Worker);
            var descriptor = Assert.Single(analysis.Compilation.Contracts).Descriptor;
            var rpc = new RoleRpc(descriptor, DummyRegistration(Assert.Single(analysis.Compilation.Contracts))) { UnaryResponse = JsonSerializer.SerializeToElement(new { kind = "number", number = 42 }) };
            var configured = Configure(worker, rpc);
            Assert.Null(configured.Composition);
            var shutdown = Assert.IsType<Func<SunderWorkerShutdownContext, CancellationToken, ValueTask>>(configured.OnShutdown);
            var reason = ShutdownReason();
            var services = (IServiceProvider)shutdown.Target!.GetType().GetProperty("Services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shutdown.Target)!;
            var service = worker.GetType("Example.MixedService", true)!;
            var started = (TaskCompletionSource)service.GetProperty("DisposalStarted")!.GetValue(null)!;
            var release = (TaskCompletionSource)service.GetProperty("ReleaseDisposal")!.GetValue(null)!;
            var failure = failDisposal ? new InvalidOperationException("Disposal failed.") : null;
            service.GetProperty("DisposalFailure")!.SetValue(null, failure);
            try
            {
                var api = services.GetRequiredService(worker.GetType("Sunder.Generated.Packages.Example_Notes_Services.IMixedService", true)!);
                Assert.Equal(5L, await Number((Task)api.GetType().GetMethod("LocalAsync")!.Invoke(api, null)!));
                Assert.Equal(0, rpc.Created);
                Assert.Equal(42L, await Number((Task)api.GetType().GetMethod("RemoteAsync")!.Invoke(api, null)!));
                Assert.Equal(1, rpc.UnaryCalls); Assert.Equal(rpc.Created, rpc.Disposed);
                var concrete = services.GetRequiredService(service);
                Assert.Equal(0, service.GetProperty("RemoteExecutions")!.GetValue(concrete));
                var first = shutdown(reason, CancellationToken.None).AsTask();
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var second = shutdown(reason, CancellationToken.None).AsTask();
                Assert.Same(first, second);
                Assert.False(first.IsCompleted);
            }
            finally
            {
                release.TrySetResult();
                var result = await Record.ExceptionAsync(() => shutdown(reason, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Same(failure, result);
            }
            Assert.Equal(1, service.GetProperty("DisposalCount")!.GetValue(null));
            Assert.Throws<ObjectDisposedException>(() => services.GetService(service));
        }
        finally { load.Unload(); }
    }

    [Fact]
    public async Task GeneratedAppComposition_UsesTransientModelsAndOneDisposableHelperForInterfaceConstructors()
    {
        const string source = """
            using System;
            using Sunder.Sdk.Packaging;
            namespace Example;
            public interface IFormatter { }
            public sealed class Formatter : IFormatter, IDisposable
            {
                public int Disposals { get; private set; }
                public void Dispose() => Disposals++;
            }
            [SunderPackageView("example.view", "View", ViewModel = typeof(ViewModel))]
            public sealed class View(ViewModel model) : Avalonia.Controls.UserControl { public ViewModel Model => model; }
            public sealed class ViewModel
            {
                public ViewModel() => throw new InvalidOperationException("The selected constructor was ignored.");
                public ViewModel(Formatter formatter, Sunder.Sdk.Abstractions.IPackageContext context,
                    Sunder.Sdk.Notifications.IPackageNotificationService notifications, Sunder.Sdk.Abstractions.IBackgroundProcessQueue background,
                    Sunder.Sdk.Abstractions.IPackageSettingsNavigationService navigation, Sunder.Sdk.Abstractions.IPackageShellViewService shell)
                    => throw new InvalidOperationException("Concrete argument types changed the selected overload.");
                public ViewModel(IFormatter formatter, Sunder.Sdk.Abstractions.IPackageContext context,
                    Sunder.Sdk.Notifications.IPackageNotificationService notifications, Sunder.Sdk.Abstractions.IBackgroundProcessQueue background,
                    Sunder.Sdk.Abstractions.IPackageSettingsNavigationService navigation, Sunder.Sdk.Abstractions.IPackageShellViewService shell, string? state)
                    => throw new InvalidOperationException("An untyped null changed the selected overload.");
                [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
                public ViewModel(IFormatter formatter, Sunder.Sdk.Abstractions.IPackageContext context,
                    Sunder.Sdk.Notifications.IPackageNotificationService notifications, Sunder.Sdk.Abstractions.IBackgroundProcessQueue background,
                    Sunder.Sdk.Abstractions.IPackageSettingsNavigationService navigation, Sunder.Sdk.Abstractions.IPackageShellViewService shell, object? state = null)
                { Formatter = formatter; Context = context; Notifications = notifications; Background = background; Navigation = navigation; Shell = shell; if (state is not null) throw new Exception("Default changed."); }
                public IFormatter Formatter { get; }
                public Sunder.Sdk.Abstractions.IPackageContext Context { get; }
                public Sunder.Sdk.Notifications.IPackageNotificationService Notifications { get; }
                public Sunder.Sdk.Abstractions.IBackgroundProcessQueue Background { get; }
                public Sunder.Sdk.Abstractions.IPackageSettingsNavigationService Navigation { get; }
                public Sunder.Sdk.Abstractions.IPackageShellViewService Shell { get; }
            }
            """;
        var analysis = SunderPackageCompositionPlannerTests.Analyze(source);
        Assert.True(analysis.Success, Errors(analysis));
        var load = new AssemblyLoadContext("generated-app-constructor-composition", isCollectible: true);
        try
        {
            var app = EmitCompositionRole(load, analysis, SunderPackageRoleMembership.App);
            var module = (ISunderAppPackageModule)Activator.CreateInstance(app.GetType("Sunder.Generated.Package.AppModule", true)!)!;
            var collection = new ServiceCollection();
            var context = DispatchProxy.Create<IPackageContext, MetadataOnlyContext>();
            var notifications = DispatchProxy.Create<IPackageNotificationService, MetadataOnlyContext>();
            var background = DispatchProxy.Create<IBackgroundProcessQueue, MetadataOnlyContext>();
            var navigation = DispatchProxy.Create<IPackageSettingsNavigationService, MetadataOnlyContext>();
            var shell = DispatchProxy.Create<IPackageShellViewService, MetadataOnlyContext>();
            module.ConfigureAppServices(collection, context);
            // The App accepts package registrations before supplying reserved host
            // capabilities. A generated module must not attempt to replace them.
            Assert.DoesNotContain(collection, descriptor => descriptor.ServiceType == typeof(IPackageContext)
                || descriptor.ServiceType == typeof(IPackageNotificationService) || descriptor.ServiceType == typeof(ISunderRpcClient)
                || descriptor.ServiceType == typeof(IBackgroundProcessQueue) || descriptor.ServiceType == typeof(IPackageSettingsNavigationService)
                || descriptor.ServiceType == typeof(IPackageShellViewService));
            collection.AddSingleton(context);
            collection.AddSingleton(notifications);
            collection.AddSingleton(background);
            collection.AddSingleton(navigation);
            collection.AddSingleton(shell);
            collection.AddSingleton(DispatchProxy.Create<ISunderRpcClient, MetadataOnlyContext>());
            await using var services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            var model = app.GetType("Example.ViewModel", true)!;
            var first = services.GetRequiredService(model); var second = services.GetRequiredService(model);
            Assert.NotSame(first, second);
            Assert.Same(context, model.GetProperty("Context")!.GetValue(first));
            Assert.Same(notifications, model.GetProperty("Notifications")!.GetValue(first));
            Assert.Same(background, model.GetProperty("Background")!.GetValue(first));
            Assert.Same(navigation, model.GetProperty("Navigation")!.GetValue(first));
            Assert.Same(shell, model.GetProperty("Shell")!.GetValue(first));
            var formatter = model.GetProperty("Formatter")!.GetValue(first)!;
            Assert.Same(formatter, model.GetProperty("Formatter")!.GetValue(second));
            Assert.Same(formatter, services.GetRequiredService(app.GetType("Example.Formatter", true)!));
            var viewType = app.GetType("Example.View", true)!;
            var view = (Avalonia.Controls.Control)services.GetRequiredService(viewType);
            Assert.Same(view.DataContext, viewType.GetProperty("Model")!.GetValue(view));
            Assert.NotSame(first, view.DataContext);
            await services.DisposeAsync();
            Assert.Equal(1, formatter.GetType().GetProperty("Disposals")!.GetValue(formatter));
        }
        finally { load.Unload(); }
    }

    private static Assembly EmitCompositionRole(AssemblyLoadContext load, SunderPackageAuthoringAnalysis analysis, SunderPackageRoleMembership role)
    {
        var files = SunderPackageRoleProjection.Project(analysis, role)
            .Concat(analysis.GeneratedSources.Where(file => role == SunderPackageRoleMembership.Worker || file.Path != "authoring.g.cs"))
            .Append(role == SunderPackageRoleMembership.Worker ? SunderPackageCompositionEmitter.EmitWorker(analysis) : SunderPackageCompositionEmitter.EmitApp(analysis))
            .Concat(role == SunderPackageRoleMembership.Worker ? SunderPackageCompositionEmitter.EmitWorkerClients(analysis, "example.notes", "1.0.0")
                : SunderRpcCSharpServiceApiEmitter.EmitAppClients(analysis.Compilation.Contracts, "example.notes", "1.0.0"));
        var compilation = CSharpCompilation.Create("Example.Composition." + role,
            files.Select(file => CSharpSyntaxTree.ParseText(file.Text, new CSharpParseOptions(LanguageVersion.CSharp14), file.Path)),
            References().Select(path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var bytes = new MemoryStream(); var result = compilation.Emit(bytes);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        bytes.Position = 0; return load.LoadFromStream(bytes);
    }

    private static SunderWorkerOptions Configure(Assembly worker, ISunderRpcClient rpc)
        => (SunderWorkerOptions)worker.GetType("Sunder.Generated.Package.WorkerEntryPoint", true)!
            .GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static, [typeof(IPackageContext), typeof(ISunderRpcClient)])!
            .Invoke(null, [DispatchProxy.Create<IPackageContext, MetadataOnlyContext>(), rpc])!;

    private static Task Stage(SunderWorkerComposition composition, string method, params object[] args)
        => (Task)typeof(SunderWorkerComposition).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(composition, args)!;

    private static SunderWorkerShutdownContext ShutdownReason()
        => (SunderWorkerShutdownContext)Activator.CreateInstance(typeof(SunderWorkerShutdownContext), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: ["generated-composition-test"], culture: null)!;

    private static async Task<long> Number(Task task)
    {
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return (long)result.GetType().GetProperty("Number")!.GetValue(result)!;
    }

    private static SunderWorkerProviderRegistration DummyRegistration(SunderRpcCSharpCompiledContract contract)
        => new(contract.Providers[0].ProviderId, contract.Descriptor.ContractId, contract.Descriptor.Version,
            contract.Descriptor.Sha256, contract.Descriptor.AbiSha256, new RejectingCompositionHandler());

    private sealed class RejectingCompositionHandler : ISunderRpcServiceHandler
    {
        public ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("This local-composition test must not dispatch through the broker.");
        public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("This local-composition test must not dispatch through the broker.");
    }

    private const string CompositionSource = """
        using System;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;
        using System.Threading;
        using System.Threading.Tasks;
        using Sunder.Sdk.Rpc;
        using Sunder.Sdk.Services;
        using Sunder.Sdk.Worker;
        using Sunder.Generated.Packages.Example_Notes_Services;
        namespace Example;
        public sealed class Journal
        {
            public List<string> Events { get; } = new(); public bool FailPreparation { get; set; }
            public TaskCompletionSource ActiveCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public interface IStore { long Next(); }
        public sealed class Store : IStore, IDisposable
        {
            private readonly Journal _journal; private long _value;
            public Store() => throw new InvalidOperationException("The selected constructor was ignored.");
            public Store(Journal journal) => throw new InvalidOperationException("Omitting defaults changed the selected overload.");
            [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
            internal Store(Journal journal, long initial = 7, bool deferred = true, DayOfWeek day = DayOfWeek.Monday,
                decimal amount = 1.25M, string text = "quoted \"owner\"", char marker = '\n', CancellationToken token = default)
            {
                if (!deferred || day != DayOfWeek.Monday || amount != 1.25M || text != "quoted \"owner\"" || marker != '\n' || token != default)
                    throw new InvalidOperationException("Authored constructor defaults changed.");
                _journal = journal; _value = initial; journal.Events.Add("ctor:store");
            }
            public long Next() => ++_value;
            public void Dispose() => _journal.Events.Add("dispose:store");
        }
        public sealed class ActivationOnly(Journal journal) : ISunderWorkerActivationService, IDisposable
        {
            public ValueTask ActivateAsync(SunderWorkerContext context, CancellationToken token = default)
            { token.ThrowIfCancellationRequested(); journal.Events.Add("activate:only"); return ValueTask.CompletedTask; }
            public void Dispose() => journal.Events.Add("dispose:only");
        }
        public sealed class ZLifecycle : ISunderWorkerLifecycleService, IDisposable
        {
            private readonly Journal _journal; public IStore Store { get; }
            private readonly Lazy<DeferredStore> _deferred;
            public DeferredStore Deferred => _deferred.Value;
            public ZLifecycle(IStore store, Journal journal, ActivationOnly activation, Lazy<DeferredStore> deferred)
            { Store = store; _journal = journal; _deferred = deferred; journal.Events.Add("ctor:z"); }
            public ValueTask PrepareCandidateAsync(CancellationToken token = default) { _journal.Events.Add("prepare:z"); return ValueTask.CompletedTask; }
            public ValueTask CommitGenerationAsync(SunderWorkerGenerationContext context, CancellationToken token = default) { _journal.Events.Add("commit:z"); return ValueTask.CompletedTask; }
            public ValueTask ActivateAsync(SunderWorkerContext context, CancellationToken token = default) { _journal.Events.Add("activate:z"); _ = Deferred; return ValueTask.CompletedTask; }
            public Task Completion => Task.CompletedTask;
            public ValueTask ShutdownAsync(SunderWorkerShutdownContext context, CancellationToken token = default) { _journal.Events.Add("shutdown:z"); return ValueTask.CompletedTask; }
            public void Dispose() => _journal.Events.Add("dispose:z");
        }
        public sealed class DeferredStore : IDisposable
        {
            private readonly Journal _journal;
            public DeferredStore(Journal journal) { _journal = journal; journal.Events.Add("ctor:deferred"); }
            public void Dispose() => _journal.Events.Add("dispose:deferred");
        }
        [SunderService]
        public sealed partial class CounterService : ISunderWorkerLifecycleService, ISunderWorkerActivationService, IDisposable
        {
            private readonly IStore _store; private readonly Journal _journal;
            public Task<Reply>? LastTask { get; private set; }
            public IAsyncEnumerable<Reply>? LastStream { get; private set; }
            public CounterService(ZLifecycle dependency, IStore store, Journal journal)
            { if (!ReferenceEquals(dependency.Store, store)) throw new Exception("Duplicate helper instance."); _store = store; _journal = journal; journal.Events.Add("ctor:counter"); }
            public Task<Reply> NextAsync(CancellationToken token = default)
            { token.ThrowIfCancellationRequested(); return LastTask = Task.FromResult(new Reply { Number = _store.Next() }); }
            public IAsyncEnumerable<Reply> WatchAsync(CancellationToken token = default) => LastStream = Values(token);
            private async IAsyncEnumerable<Reply> Values([EnumeratorCancellation] CancellationToken token)
            {
                try { yield return new Reply { Number = _store.Next() }; await Task.Delay(Timeout.Infinite, token); }
                finally { _journal.Events.Add("stream:disposed"); }
            }
            public ValueTask PrepareCandidateAsync(CancellationToken token = default)
            { _journal.Events.Add("prepare:counter"); if (_journal.FailPreparation) throw new InvalidOperationException("Candidate failed."); return ValueTask.CompletedTask; }
            public ValueTask CommitGenerationAsync(SunderWorkerGenerationContext context, CancellationToken token = default) { _journal.Events.Add("commit:counter"); return ValueTask.CompletedTask; }
            public ValueTask ActivateAsync(SunderWorkerContext context, CancellationToken token = default) { _journal.Events.Add("activate:counter"); return ValueTask.CompletedTask; }
            public Task Completion => _journal.ActiveCompletion.Task;
            public ValueTask ShutdownAsync(SunderWorkerShutdownContext context, CancellationToken token = default) { _journal.Events.Add("shutdown:counter"); return ValueTask.CompletedTask; }
            public void Dispose() => _journal.Events.Add("dispose:counter");
        }
        [SunderService]
        public sealed partial class ReaderService(ICounterService counter)
        { public Task<Reply> ReadAsync() => counter.NextAsync(); }
        public sealed record Reply { public required long Number { get; init; } }
        """;
}
