using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Package.Build.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderPackageRoleProjectionTests
{
    [Fact]
    public async Task GeneratedTypedStream_PreservesCancellationErrorsRetirementAndEnumeratorOwnership()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Packaging;
            using Sunder.Sdk.Rpc;
            namespace Example;
            [SunderRpcService]
            public sealed partial class NotesService
            {
                [SunderRpcMethod]
                public async IAsyncEnumerable<Note> WatchAsync(WatchRequest request, [EnumeratorCancellation] CancellationToken token = default)
                {
                    yield return new Note { Text = "first" };
                    if (request.Block) await Task.Delay(Timeout.Infinite, token);
                    yield return new Note { Text = "second" };
                }
            }
            public sealed record WatchRequest { public required bool Block { get; init; } }
            public sealed record Note { public required string Text { get; init; } }
            [SunderPackageCodeRole(SunderPackageCodeRole.App)]
            public sealed class NotesViewModel(INotesService service) { public INotesService Service { get; } = service; }
            """;
        var references = References();
        var input = new SunderRpcCSharpCompilerInput("example.notes", "1.0.0", [new("stream.cs", source)], references, AssemblyName: "Example.Streams");
        var analysis = SunderPackageAuthoringCompiler.Analyze(input);
        Assert.True(analysis.Success, Errors(analysis));
        var load = new AssemblyLoadContext("single-project-typed-stream", isCollectible: true);
        try
        {
            var worker = Emit("Example.Streams", SunderPackageRoleProjection.Project(analysis, SunderPackageRoleMembership.Worker).Concat(analysis.GeneratedSources));
            var app = Emit("Example.Streams.App", SunderPackageRoleProjection.Project(analysis, SunderPackageRoleMembership.App)
                .Concat(analysis.GeneratedSources.Where(file => file.Path != "authoring.g.cs"))
                .Concat(SunderRpcCSharpServiceApiEmitter.EmitAppClients(analysis.Compilation.Contracts, input.PackageId, input.PackageVersion)));
            var service = Activator.CreateInstance(worker.GetType("Example.NotesService", true)!)!;
            var registration = (SunderWorkerProviderRegistration)service.GetType().GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(service, null)!;
            var rpc = new RoleRpc(Assert.Single(analysis.Compilation.Contracts).Descriptor, registration);
            var api = app.GetType("Example.INotesService", true)!;
            var clientType = app.GetTypes().Single(type => !type.IsInterface && api.IsAssignableFrom(type));
            var client = Activator.CreateInstance(clientType, rpc)!;
            await using var lifetime = (IAsyncDisposable)client;

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await using var cursor = Stream(block: false, cancellation.Token).GetAsyncEnumerator();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cursor.MoveNextAsync().AsTask());
                Assert.Equal(0, rpc.Created);
            }
            await using (var cursor = Stream(block: false).GetAsyncEnumerator())
            {
                Assert.True(await cursor.MoveNextAsync()); Assert.Equal("first", Text(cursor.Current));
                Assert.True(await cursor.MoveNextAsync()); Assert.Equal("second", Text(cursor.Current));
                Assert.False(await cursor.MoveNextAsync());
            }
            Assert.Equal(1, rpc.StreamsDisposed); Assert.Equal(rpc.Created, rpc.Disposed);

            // Disposal while suspended at a yielded item releases the provider's
            // enumerator and the same call scope, without requiring another pull.
            await using (var cursor = Stream(block: true).GetAsyncEnumerator()) Assert.True(await cursor.MoveNextAsync());
            Assert.Equal(2, rpc.StreamsDisposed); Assert.Equal(rpc.Created, rpc.Disposed);

            using (var cancellation = new CancellationTokenSource())
            {
                await using var cursor = Stream(block: true).GetAsyncEnumerator(cancellation.Token);
                Assert.True(await cursor.MoveNextAsync());
                var blocked = cursor.MoveNextAsync().AsTask(); Assert.False(blocked.IsCompleted);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
            }
            Assert.Equal(rpc.Created, rpc.Disposed);

            await using (var cursor = Stream(block: false).GetAsyncEnumerator())
            {
                Assert.True(await cursor.MoveNextAsync()); rpc.InvalidateAfterCall = true;
                var stale = await Assert.ThrowsAsync<SunderRpcException>(() => cursor.MoveNextAsync().AsTask());
                Assert.Equal(SunderRpcErrorKind.StaleEndpoint, stale.Error.Kind);
            }
            rpc.InvalidateAfterCall = false;
            rpc.Failure = new(new(SunderRpcErrorKind.Unavailable, "fixture.unavailable", "Unavailable."));
            await using (var cursor = Stream(block: false).GetAsyncEnumerator())
                Assert.Same(rpc.Failure, await Assert.ThrowsAsync<SunderRpcException>(() => cursor.MoveNextAsync().AsTask()));
            rpc.Failure = null; rpc.MalformedStream = true;
            await using (var cursor = Stream(block: false).GetAsyncEnumerator())
                await Assert.ThrowsAsync<JsonException>(() => cursor.MoveNextAsync().AsTask());
            rpc.MalformedStream = false;
            Assert.Equal(rpc.Created, rpc.Disposed);

            await using (var cursor = Stream(block: true).GetAsyncEnumerator())
            {
                Assert.True(await cursor.MoveNextAsync());
                var blocked = cursor.MoveNextAsync().AsTask();
                var stopped = lifetime.DisposeAsync().AsTask();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
                await stopped.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.Equal(rpc.Created, rpc.Disposed);
            Assert.Equal(rpc.Created, rpc.StreamsDisposed);
            await using var rejected = Stream(block: false).GetAsyncEnumerator();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => rejected.MoveNextAsync().AsTask());

            IAsyncEnumerable<object> Stream(bool block, CancellationToken token = default)
            {
                var request = Activator.CreateInstance(app.GetType("Example.WatchRequest", true)!)!;
                request.GetType().GetProperty("Block")!.SetValue(request, block);
                return Assert.IsAssignableFrom<IAsyncEnumerable<object>>(api.GetMethod("WatchAsync")!.Invoke(client, [request, token]));
            }
        }
        finally { load.Unload(); }

        Assembly Emit(string name, IEnumerable<SunderRpcCSharpSourceFile> files)
        {
            var compilation = CSharpCompilation.Create(name,
                files.Select(file => CSharpSyntaxTree.ParseText(file.Text, new CSharpParseOptions(LanguageVersion.CSharp14), file.Path)),
                references.Select(path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var buffer = new MemoryStream(); var result = compilation.Emit(buffer);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            buffer.Position = 0; return load.LoadFromStream(buffer);
        }
        static string? Text(object value) => (string?)value.GetType().GetProperty("Text")!.GetValue(value);
    }
}
