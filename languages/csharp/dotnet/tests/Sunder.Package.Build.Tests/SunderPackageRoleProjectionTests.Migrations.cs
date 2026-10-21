using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Sunder.Package.Build.Authoring;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderPackageRoleProjectionTests
{
    [Fact]
    public async Task GeneratedStorageMigrations_SnapshotPlansAndPrepareInDependencyOrderWithoutLifecycleComposition()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Storage;
            using Sunder.Sdk.Worker;
            namespace Example;
            public sealed class Migrations : ISunderWorkerStorageMigrationProvider,
                ISunderWorkerStorageMigrationPreparation, System.IDisposable
            {
                public static List<string> Events { get; } = [];
                public static SunderWorkerMigrationPreparationContext? Seen { get; private set; }
                public static List<PackageStorageKeyMigration> Rules { get; } =
                    [PackageStorageKeyMigration.Exact("legacy.state", "current.state")];
                public Migrations() => Events.Add("constructed");
                public IReadOnlyList<PackageStorageKeyMigration> StateMigrations => Rules;
                public IReadOnlyList<PackageStorageKeyMigration> SecretMigrations =>
                    [PackageStorageKeyMigration.Exact("legacy.secret", "current.secret")];
                public ValueTask PrepareStorageMigrationsAsync(SunderWorkerMigrationPreparationContext context,
                    CancellationToken token = default)
                { Seen = context; Events.Add("prepare:plans"); return default; }
                public void Dispose() => Events.Add("disposed");
            }
            public sealed class ADependentPreparation(Migrations migrations) : ISunderWorkerStorageMigrationPreparation
            {
                public ValueTask PrepareStorageMigrationsAsync(SunderWorkerMigrationPreparationContext context,
                    CancellationToken token = default)
                { _ = migrations; Migrations.Events.Add("prepare:dependent"); return default; }
            }
            """;
        var analysis = SunderPackageCompositionPlannerTests.Analyze(source);
        Assert.True(analysis.Success, Errors(analysis));
        var load = new AssemblyLoadContext("generated-storage-migrations", isCollectible: true);
        try
        {
            var worker = EmitCompositionRole(load, analysis, SunderPackageRoleMembership.Worker);
            var options = Configure(worker, DispatchProxy.Create<ISunderRpcClient, MetadataOnlyContext>());
            Assert.Null(options.Composition);
            Assert.Null(options.OnActivated);
            var migrationType = worker.GetType("Example.Migrations", throwOnError: true)!;
            var events = (List<string>)migrationType.GetProperty("Events")!.GetValue(null)!;
            Assert.Equal(["constructed"], events);
            Assert.Single(options.StateMigrations);
            Assert.Single(options.SecretMigrations);
            ((System.Collections.IList)migrationType.GetProperty("Rules")!.GetValue(null)!).Clear();
            Assert.Single(options.StateMigrations);
            // This emitter-level test only forwards the preparation context. The real
            // read-only Host barrier is exercised by WorkerV1RuntimeTests.
            var context = (SunderWorkerMigrationPreparationContext)RuntimeHelpers.GetUninitializedObject(
                typeof(SunderWorkerMigrationPreparationContext));
            try
            {
                await options.OnBeforeStorageMigrations!(context, CancellationToken.None);
                Assert.Same(context, migrationType.GetProperty("Seen")!.GetValue(null));
                Assert.Equal(["constructed", "prepare:plans", "prepare:dependent"], events);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    options.OnBeforeStorageMigrations(context, new CancellationToken(canceled: true)).AsTask());
                Assert.Equal(3, events.Count);
            }
            finally { await options.OnShutdown!(ShutdownReason(), CancellationToken.None); }
            await options.OnShutdown!(ShutdownReason(), CancellationToken.None);
            Assert.Equal(["constructed", "prepare:plans", "prepare:dependent", "disposed"], events);
        }
        finally { load.Unload(); }
    }
}
