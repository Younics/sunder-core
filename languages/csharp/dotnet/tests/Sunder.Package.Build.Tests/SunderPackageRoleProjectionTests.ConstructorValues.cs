using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Build.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderPackageRoleProjectionTests
{
    [Fact]
    public async Task ConstructorValues_ExecuteTypedAppWorkerRoundTripsWithIndependentDomainRecords()
    {
        // Positional profile fields/defaults mirror the genuine domain shape that
        // first exposed the source-value boundary; this remains a standalone Core test.
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Packaging;
            using Sunder.Sdk.Services;
            using Sunder.Generated.Packages.Example_Notes_Services;
            namespace Example;
            [SunderService]
            public sealed partial class NotesService
            {
                public Task<AgentProfileRecord?> EchoAsync(AgentProfileRecord? profile = null, CancellationToken token = default)
                { token.ThrowIfCancellationRequested(); return Task.FromResult(profile is null ? null : profile with { }); }
                public Task<DateTimeOffset?> TimestampAsync(DateTimeOffset? value = null) => Task.FromResult(value);
                public Task<IReadOnlyList<DateTimeOffset?>?> TimestampsAsync(IReadOnlyList<DateTimeOffset?>? values = null) => Task.FromResult(values);
                public async IAsyncEnumerable<AgentProfileRecord> WatchAsync(AgentProfileRecord profile)
                { await Task.Yield(); yield return profile with { }; }
            }
            public sealed record AgentProfileRecord(string ProfileId, string DisplayName, string? Description, string? Instructions,
                string? ChatProviderId, string? ChatModelId, string? EmbeddingProviderId, string? EmbeddingModelId,
                DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc,
                IReadOnlyList<Binding>? ModelBindings = null, IReadOnlyList<Assignment>? SelectableCapabilityAssignments = null,
                string? BehaviorLoopId = null, string? BehaviorLoopSourceId = null, string? BehaviorLoopSettingsJson = null, bool IsInternal = false);
            public sealed record Binding(string ProviderId, string ModelId, DateTimeOffset UpdatedAtUtc);
            public sealed record Assignment(string CapabilityId, string? SettingsJson = null);
            [SunderPackageCodeRole(SunderPackageCodeRole.App)]
            public static class AppProbe
            {
                public static async Task RoundTrip(INotesService service)
                {
                    var now = new DateTimeOffset(2026, 10, 6, 12, 34, 56, TimeSpan.FromMinutes(345)).AddTicks(1234567);
                    var profile = new AgentProfileRecord("profile", "Example", null, null, "provider", "model", null, null,
                        now, now, [new Binding("provider", "model", now)], [new Assignment("tool")]);
                    var reply = await service.EchoAsync(profile);
                    if (reply is null || ReferenceEquals(profile, reply) || reply.ProfileId != profile.ProfileId || reply.Description is not null
                        || reply.IsInternal || reply.CreatedAtUtc.Ticks != now.Ticks || reply.CreatedAtUtc.Offset != now.Offset
                        || reply.ModelBindings![0].UpdatedAtUtc.Ticks != now.Ticks || reply.ModelBindings[0].UpdatedAtUtc.Offset != now.Offset
                        || reply.SelectableCapabilityAssignments![0].SettingsJson is not null) throw new Exception("Profile value changed across roles.");
                    if (await service.EchoAsync() is not null || await service.TimestampAsync() is not null || await service.TimestampsAsync() is not null)
                        throw new Exception("A nullable/default root changed across roles.");
                    var timestamp = await service.TimestampAsync(now);
                    var list = await service.TimestampsAsync(new DateTimeOffset?[] { null, now });
                    if (timestamp!.Value.Ticks != now.Ticks || timestamp.Value.Offset != now.Offset || list![0] is not null
                        || list[1]!.Value.Ticks != now.Ticks || list[1]!.Value.Offset != now.Offset) throw new Exception("Timestamp precision or offset changed.");
                    var count = 0;
                    await foreach (var item in service.WatchAsync(profile))
                    { count++; if (item.CreatedAtUtc.Offset != now.Offset || item.CreatedAtUtc.Ticks != now.Ticks) throw new Exception("Stream value changed."); }
                    if (count != 1) throw new Exception("Stream shape changed.");
                }
            }
            """;
        var input = new SunderRpcCSharpCompilerInput("example.notes", "1.0.0", [new("profile.cs", source)], References(), AssemblyName: "Example.Profile");
        var analysis = SunderPackageAuthoringCompiler.Analyze(input);
        Assert.True(analysis.Success, Errors(analysis));
        var load = new AssemblyLoadContext("constructor-value-role-projection", isCollectible: true);
        try
        {
            var worker = Emit("Example.Profile", SunderPackageRoleProjection.Project(analysis, SunderPackageRoleMembership.Worker)
                .Concat(analysis.GeneratedSources).Append(SunderPackageCompositionEmitter.EmitWorker(analysis)));
            var app = Emit("Example.Profile.App", SunderPackageRoleProjection.Project(analysis, SunderPackageRoleMembership.App)
                .Concat(analysis.GeneratedSources.Where(file => file.Path != "authoring.g.cs"))
                .Concat(SunderRpcCSharpServiceApiEmitter.EmitAppClients(analysis.Compilation.Contracts, input.PackageId, input.PackageVersion))
                .Append(SunderPackageCompositionEmitter.EmitApp(analysis)));
            Assert.Null(app.GetType("Example.NotesService"));
            Assert.NotSame(worker.GetType("Example.AgentProfileRecord"), app.GetType("Example.AgentProfileRecord"));
            Assert.DoesNotContain(app.GetReferencedAssemblies(), reference => reference.Name is "Example.Profile" or "Sunder.Sdk.Worker");
            var context = DispatchProxy.Create<IPackageContext, MetadataOnlyContext>();
            var workerCreate = worker.GetType("Sunder.Generated.Package.WorkerEntryPoint")!.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(IPackageContext), typeof(ISunderRpcClient)])!;
            var configured = (SunderWorkerOptions)workerCreate.Invoke(null, [context, DispatchProxy.Create<ISunderRpcClient, MetadataOnlyContext>()])!;
            try
            {
                var rpc = new RoleRpc(Assert.Single(analysis.Compilation.Contracts).Descriptor, Assert.Single(configured.Providers)) { AllowCatalogDiscovery = true };
                var appModule = (ISunderAppPackageModule)Activator.CreateInstance(app.GetType("Sunder.Generated.Package.AppModule")!)!;
                var services = new ServiceCollection(); services.AddSingleton<ISunderRpcClient>(rpc);
                appModule.ConfigureAppServices(services, context);
                await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
                var api = provider.GetRequiredService(app.GetType("Sunder.Generated.Packages.Example_Notes_Services.INotesService")!);
                await (Task)app.GetType("Example.AppProbe")!.GetMethod("RoundTrip")!.Invoke(null, [api])!;
                Assert.Equal(6, rpc.UnaryCalls); Assert.Equal(1, rpc.StreamsDisposed);
                Assert.Equal(rpc.Created, rpc.Disposed);
            }
            finally { await configured.OnShutdown!(ShutdownReason(), CancellationToken.None); }
        }
        finally { load.Unload(); }

        Assembly Emit(string name, IEnumerable<SunderRpcCSharpSourceFile> files)
        {
            var compilation = CSharpCompilation.Create(name,
                files.Select(file => CSharpSyntaxTree.ParseText(file.Text, new CSharpParseOptions(LanguageVersion.CSharp14), file.Path)),
                References().Where(path => !name.EndsWith(".App", StringComparison.Ordinal) || Path.GetFileName(path) != "Sunder.Sdk.Worker.dll")
                    .Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var buffer = new MemoryStream(); var result = compilation.Emit(buffer);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            buffer.Position = 0; return load.LoadFromStream(buffer);
        }
    }
}
