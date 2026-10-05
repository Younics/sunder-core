using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Package.Build.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderPackageRoleProjectionTests
{
    [Fact]
    public async Task Project_CompilesMixedSourceIntoSeparateRoleAssembliesWithoutImplementationLeakage()
    {
        const string source = """
            global using BackendOnly;
            using Backend = BackendOnly;
            using WorkerMarker = BackendOnly.Marker;
            using static BackendOnly.Marker;
            using Sunder.Sdk.Packaging;
            using Sunder.Sdk.Rpc;
            using System.Threading.Tasks;
            namespace Example;
            [SunderRpcService]
            public sealed partial class NotesService
            {
                [SunderRpcMethod] public Task<Note> ReadAsync() => Task.FromResult(new Note { Text = Storage.Read() });
            }
            internal static class Storage { public static string Read() => WorkerMarker.Read(); }
            public sealed record Note { public required string Text { get; init; } }
            [SunderPackageView("example.notes", "Notes", ViewModel = typeof(NotesViewModel))]
            public sealed class NotesView : Avalonia.Controls.UserControl;
            public sealed class NotesViewModel(INotesService notes) { public Task<Note> ReadAsync() => notes.ReadAsync(); }
            """;
        var paths = References();
        var input = new SunderRpcCSharpCompilerInput("example.notes", "1.0.0", [new("mixed.cs", source),
            new("backend.cs", "namespace BackendOnly { internal static class Marker { public static string Read() => \"backend\"; } }"),
            new("metadata.cs", "[assembly: System.Reflection.AssemblyMetadata(\"fixture\", \"retained\")]")], paths, AssemblyName: "Example.Notes");
        var analysis = SunderPackageAuthoringCompiler.Analyze(input);
        Assert.True(analysis.Success, Errors(analysis));
        var app = SunderPackageRoleProjection.Project(analysis, SunderPackageRoleMembership.App);
        var worker = SunderPackageRoleProjection.Project(analysis, SunderPackageRoleMembership.Worker);
        Assert.Equal(2, app.Count); Assert.Equal(3, worker.Count);
        var appSource = Assert.Single(app, value => value.Path == "mixed.cs").Text;
        Assert.Equal(source.Count(value => value == '\n'), appSource.Count(value => value == '\n'));
        Assert.Equal(source.IndexOf("public sealed class NotesViewModel", StringComparison.Ordinal), appSource.IndexOf("public sealed class NotesViewModel", StringComparison.Ordinal));
        Assert.DoesNotContain("class NotesService", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("class Storage", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("BackendOnly", appSource, StringComparison.Ordinal);
        Assert.Contains("using System.Threading.Tasks;", appSource, StringComparison.Ordinal);
        Assert.Contains("global using BackendOnly;", Assert.Single(worker, value => value.Path == "mixed.cs").Text, StringComparison.Ordinal);
        Assert.DoesNotContain("class NotesView", Assert.Single(worker, value => value.Path == "mixed.cs").Text, StringComparison.Ordinal);
        var contract = Assert.Single(analysis.Compilation.Contracts);
        var clients = SunderRpcCSharpServiceApiEmitter.EmitAppClients(analysis.Compilation.Contracts, input.PackageId, input.PackageVersion);
        var load = new AssemblyLoadContext("single-project-role-projection", isCollectible: true);
        try
        {
            var appAssembly = Emit("Example.Notes.App", app.Concat(analysis.GeneratedSources.Where(value => value.Path != "authoring.g.cs")).Concat(clients).Append(SunderPackageCompositionEmitter.EmitApp(analysis)),
                paths.Where(path => Path.GetFileName(path) != "Sunder.Sdk.Worker.dll"));
            var workerAssembly = Emit("Example.Notes", worker.Concat(analysis.GeneratedSources).Append(SunderPackageCompositionEmitter.EmitWorker(analysis)), paths.Where(path => !Path.GetFileName(path).StartsWith("Avalonia", StringComparison.Ordinal)));
            Assert.NotNull(appAssembly.GetType("Example.NotesViewModel")); Assert.Null(appAssembly.GetType("Example.NotesService")); Assert.Null(appAssembly.GetType("Example.Storage"));
            Assert.NotNull(workerAssembly.GetType("Example.NotesService")); Assert.Null(workerAssembly.GetType("Example.NotesView"));
            Assert.NotNull(workerAssembly.GetType("BackendOnly.Marker")); Assert.Null(appAssembly.GetType("BackendOnly.Marker"));
            Assert.NotSame(appAssembly.GetType("Example.Note"), workerAssembly.GetType("Example.Note"));
            var appModule = Assert.IsAssignableFrom<ISunderAppPackageModule>(Activator.CreateInstance(appAssembly.GetType("Sunder.Generated.Package.AppModule", true)!)!);
            Assert.NotNull(workerAssembly.GetType("Sunder.Generated.Package.WorkerEntryPoint", true)!.GetMethod("Main"));
            Assert.DoesNotContain(appAssembly.GetReferencedAssemblies(), value => value.Name is "Example.Notes" or "Sunder.Sdk.Worker");
            Assert.DoesNotContain(workerAssembly.GetReferencedAssemblies(), value => value.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
            Assert.Contains(appAssembly.CustomAttributes, value => value.AttributeType == typeof(System.Reflection.AssemblyMetadataAttribute)
                && (string?)value.ConstructorArguments[1].Value == "retained");
            var service = Activator.CreateInstance(workerAssembly.GetType("Example.NotesService", true)!)!;
            var task = (Task)service.GetType().GetMethod("ReadAsync")!.Invoke(service, null)!;
            await task;
            var note = task.GetType().GetProperty("Result")!.GetValue(task)!;
            Assert.Equal("backend", note.GetType().GetProperty("Text")!.GetValue(note));
            var registration = (SunderWorkerProviderRegistration)service.GetType().GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(service, null)!;
            var rpc = new RoleRpc(contract.Descriptor, registration);
            var context = DispatchProxy.Create<IPackageContext, MetadataOnlyContext>();
            var collection = new ServiceCollection(); collection.AddSingleton<ISunderRpcClient>(rpc);
            appModule.ConfigureAppServices(collection, context);
            await using var appServices = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            var client = appServices.GetRequiredService(appAssembly.GetType("Example.INotesService", true)!);
            await using var lifetime = (IAsyncDisposable)client;
            var viewModel = appServices.GetRequiredService(appAssembly.GetType("Example.NotesViewModel", true)!);
            var remote = (Task)viewModel.GetType().GetMethod("ReadAsync")!.Invoke(viewModel, null)!;
            await remote;
            var remoteNote = remote.GetType().GetProperty("Result")!.GetValue(remote)!;
            Assert.Same(appAssembly, remoteNote.GetType().Assembly);
            Assert.Equal("backend", remoteNote.GetType().GetProperty("Text")!.GetValue(remoteNote));
            Assert.Equal(rpc.Created, rpc.Disposed);
            rpc.InvalidateAfterCall = true;
            var stale = (Task)viewModel.GetType().GetMethod("ReadAsync")!.Invoke(viewModel, null)!;
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => stale)).Error.Kind);
            rpc.InvalidateAfterCall = false; rpc.Block = true;
            var blocked = (Task)viewModel.GetType().GetMethod("ReadAsync")!.Invoke(viewModel, null)!;
            await rpc.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopped = lifetime.DisposeAsync().AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
            await stopped.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(rpc.Created, rpc.Disposed);
            var configureWorker = workerAssembly.GetType("Sunder.Generated.Package.WorkerEntryPoint", true)!
                .GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic, [typeof(IPackageContext), typeof(ISunderRpcClient)])!;
            var configured = (SunderWorkerOptions)configureWorker.Invoke(null, [context, rpc])!;
            Assert.Null(configured.Composition);
            try
            {
                var generatedProvider = Assert.Single(configured.Providers);
                Assert.Equal(registration.ProviderId, generatedProvider.ProviderId);
                var generatedReply = await generatedProvider.Handler.InvokeUnaryAsync(null!, "notes", "read", JsonSerializer.SerializeToElement(new { }));
                Assert.Equal("backend", generatedReply.GetProperty("text").GetString());
            }
            finally { await configured.OnShutdown!(ShutdownReason(), CancellationToken.None); }
        }
        finally { load.Unload(); }

        System.Reflection.Assembly Emit(string name, IEnumerable<SunderRpcCSharpSourceFile> files, IEnumerable<string> references)
        {
            var compilation = CSharpCompilation.Create(name,
                files.Select(file => CSharpSyntaxTree.ParseText(file.Text, new CSharpParseOptions(LanguageVersion.CSharp14), file.Path)),
                references.Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var stream = new MemoryStream();
            var emitted = compilation.Emit(stream);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            stream.Position = 0; return load.LoadFromStream(stream);
        }
    }

    [Fact]
    public void Project_RejectsFailedPlansAndCombinedRoleRequests()
    {
        var invalid = SunderPackageAuthoringCompiler.Analyze(new("example.invalid", "1.0.0", [new("bad.cs", "public class Broken {")], References()));
        Assert.Throws<InvalidOperationException>(() => SunderPackageRoleProjection.Project(invalid, SunderPackageRoleMembership.App));
        var valid = SunderPackageAuthoringCompiler.Analyze(new("example.valid", "1.0.0", [new("good.cs", "public class Valid { }")], References()));
        Assert.True(valid.Success, Errors(valid));
        Assert.Throws<ArgumentOutOfRangeException>(() => SunderPackageRoleProjection.Project(valid, SunderPackageRoleMembership.Shared));
    }

    private static string[] References() => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Concat([typeof(SunderPackageCodeRoleAttribute).Assembly.Location, typeof(SunderWorker).Assembly.Location, typeof(IAvaloniaPackageContributionRegistry).Assembly.Location,
            typeof(Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions).Assembly.Location,
            typeof(Control).Assembly.Location, typeof(Avalonia.AvaloniaObject).Assembly.Location]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static string Errors(SunderPackageAuthoringAnalysis value) => string.Join(Environment.NewLine,
        value.Compilation.Diagnostics.Concat(value.RolePlan.Diagnostics).Select(error => error.Message));

    public class MetadataOnlyContext : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("Composition registration must not access package storage or execute package context operations.");
    }

    private sealed class RoleRpc(SunderRpcContractDescriptor descriptor, SunderWorkerProviderRegistration registration) : ISunderRpcClient
    {
        internal SunderRpcProviderSnapshot Provider = new("example.notes", "1.0.0", registration.ProviderId, descriptor.ContractId,
            descriptor.Version, descriptor.Sha256, descriptor.AbiSha256, Guid.NewGuid(), 1, 1, new("role-endpoint"), 1, SunderRpcProviderState.Active);
        internal int Created;
        internal int Disposed;
        internal bool InvalidateAfterCall;
        internal bool Block;
        internal bool MalformedStream;
        internal int StreamsDisposed;
        internal SunderRpcException? Failure;
        internal JsonElement? UnaryResponse;
        internal int UnaryCalls;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Created++; return ValueTask.FromResult<ISunderRpcCallScope>(new Scope(this)); }
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Use the owned scope.");
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Discovery must use the owned scope.");
        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Use the owned scope.");
        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private sealed class Scope(RoleRpc owner) : ISunderRpcCallScope
        {
            public DateTimeOffset DeadlineUtc => DateTimeOffset.UtcNow.AddMinutes(1);
            public ValueTask DisposeAsync() { owner.Disposed++; return ValueTask.CompletedTask; }
            public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
            { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<SunderRpcProviderSnapshot?>(owner.Provider); }
            public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
            { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(new SunderRpcCatalogSnapshot(1, 1, [owner.Provider])); }
            public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
            public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public async ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            {
                owner.UnaryCalls++;
                if (owner.Failure is { } error) throw error;
                if (owner.Block) { owner.Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
                var value = owner.UnaryResponse ?? await owner.InvokeHandler(serviceId, methodId, request, cancellationToken);
                if (owner.InvalidateAfterCall) owner.Provider = owner.Provider with { ActivationEpoch = owner.Provider.ActivationEpoch + 1 };
                return value;
            }
            public async IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                try
                {
                    if (owner.Failure is { } error) throw error;
                    await foreach (var value in owner.StreamHandler(serviceId, methodId, request, cancellationToken).WithCancellation(cancellationToken))
                    {
                        if (owner.InvalidateAfterCall) owner.Provider = owner.Provider with { ActivationEpoch = owner.Provider.ActivationEpoch + 1 };
                        yield return owner.MalformedStream ? JsonSerializer.SerializeToElement(new { unexpected = true }) : value;
                    }
                }
                finally { owner.StreamsDisposed++; }
            }
            public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string path, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
        private ValueTask<JsonElement> InvokeHandler(string service, string method, JsonElement request, CancellationToken token)
            => registration.Handler.InvokeUnaryAsync(null!, service, method, request, token);
        private IAsyncEnumerable<JsonElement> StreamHandler(string service, string method, JsonElement request, CancellationToken token)
            => registration.Handler.InvokeServerStreamAsync(null!, service, method, request, token);
    }
}
