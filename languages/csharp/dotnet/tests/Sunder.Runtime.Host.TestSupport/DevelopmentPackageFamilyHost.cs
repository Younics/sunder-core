using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Host.TestSupport;

// Real local development composition: original archives/catalog, production
// import admission, official execution runtimes, native App IPC and no Registry
// publication identity. Shared by the Host producer and App artifact consumer.
internal sealed class DevelopmentPackageFamilyHost : IAsyncDisposable
{
    private readonly TemporaryDirectoryScope _temporary;
    private readonly WebApplication _app;
    private readonly Dictionary<(int Id, long Birth), Process> _observed = [];
    private readonly HashSet<string> _workerFolders = new(StringComparer.Ordinal);

    private DevelopmentPackageFamilyHost(TemporaryDirectoryScope temporary, WebApplication app, RuntimePackagePaths paths,
        RuntimeConnectionInfo connection)
    {
        _temporary = temporary;
        _app = app;
        Paths = paths;
        Connection = connection;
    }

    internal string RootPath => _temporary.RootPath;
    internal RuntimePackagePaths Paths { get; }
    internal RuntimeConnectionInfo Connection { get; }
    internal IServiceProvider Services => _app.Services;
    internal RuntimeSessionOwner Owner => Services.GetRequiredService<RuntimeSessionOwner>();
    internal PackageSessionLifecycleService Sessions => Services.GetRequiredService<PackageSessionLifecycleService>();
    internal RuntimePackageUiService Ui => Services.GetRequiredService<RuntimePackageUiService>();
    internal Dictionary<string, string> Folders { get; } = new(StringComparer.Ordinal);

    internal static async Task<DevelopmentPackageFamilyHost> CreateAsync(IReadOnlyDictionary<string, string> archives,
        string catalog, IReadOnlyList<string> selectedPackageIds, CancellationToken token)
    {
        var temporary = TemporaryDirectoryScope.Create("sunder-development-family", new(3, (path, _) =>
        {
            if (!ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(path))
                throw new IOException("Could not reclaim the exact development-family fixture root: " + path);
            return ValueTask.CompletedTask;
        }));
        var paths = new RuntimePackagePaths(temporary.GetPath("runtime"));
        RuntimeLocalState.EnsureInitialized(paths.RootPath);
        var endpoint = new RuntimeIpcEndpoint(OperatingSystem.IsWindows() ? RuntimeIpcTransportKind.NamedPipe : RuntimeIpcTransportKind.UnixSocket,
            OperatingSystem.IsWindows() ? "sunder-family-" + Guid.NewGuid().ToString("N")
                : Path.Combine(Path.GetTempPath(), "sf-" + Guid.NewGuid().ToString("N")[..12] + ".sock"));
        var bearer = Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateSlimBuilder();
        RuntimeSupervisedTransport.Configure(builder.WebHost, endpoint);
        builder.Services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator(bearer));
        builder.Services.AddSingleton(new RuntimeLifecyclePolicyOptions
        {
            PackageWorkerStartupTimeout = TimeSpan.FromMinutes(8),
            PackageRuntimeGenerationActivationTimeout = TimeSpan.FromMinutes(2),
        });
        var app = builder.Build();
        app.UseMiddleware<RuntimeProblemDetailsMiddleware>();
        app.UseMiddleware<RuntimeBearerAuthenticationMiddleware>();
        app.MapRuntimeHandshakeEndpoint();
        app.MapGroup("/api/v1").MapRuntimeRpcEndpoints().MapAppPolicyEndpoints().MapContentTransferEndpoints();
        var fixture = new DevelopmentPackageFamilyHost(temporary, app, paths,
            new(RuntimeIpcEndpoint.LogicalRuntimeUrl, bearer) { IpcEndpoint = endpoint });
        try
        {
            await ServicesInitializeAsync();
            var requirements = new HashSet<RuntimeExecutionRuntimeRequirement>();
            foreach (var packageId in selectedPackageIds)
            {
                var folder = temporary.GetPath("sources", packageId);
                var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archives[packageId], folder, token);
                Assert.True(validation.Success, string.Join(" | ", validation.Errors));
                Assert.Equal(packageId, validation.Manifest!.Id);
                var required = (validation.Manifest.PackageArtifactImports ?? []).Select(value => value!.PackageId!).ToHashSet(StringComparer.Ordinal);
                await app.Services.GetRequiredService<DevelopmentImportAuthorityPreparation>().PrepareAsync(new(folder)
                {
                    CandidateArchives = required.Order(StringComparer.Ordinal).Select(id => archives[id]).ToArray(),
                    PlatformCatalogPath = DevelopmentImportAuthorityStore.Platforms(validation.Manifest).Length == 0 ? null : catalog,
                }, token);
                fixture.Folders.Add(packageId, folder);
                var target = Assert.Single(validation.Manifest.Targets!, target => target!.Role == "runtime"
                    && target.Rid == PackageTargetSelection.GetCurrentRuntimeIdentifier())!;
                Assert.NotNull(target.ExecutionRuntime);
                requirements.Add(new(target.ExecutionRuntime.Id!, target.ExecutionRuntime.VersionRange!, target.ExecutionRuntime.ModuleAbi!));
            }
            foreach (var requirement in requirements)
            {
                // Use the production official-runtime acquisition path, never the
                // ambient-SDK test resolver or synthesized installed bindings.
                var result = await app.Services.GetRequiredService<ExecutionRuntimeManagementService>().EnsureAsync(new(requirement), token);
                Assert.True(result.Success, result.ErrorCode + ": " + result.Message);
            }
            return fixture;

            async Task ServicesInitializeAsync()
            {
                await app.Services.GetRequiredService<ExecutionRuntimeStore>().InitializeAsync(token);
                await app.Services.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync(token);
                await app.StartAsync(token);
                RuntimeSupervisedTransport.RestrictEndpoint(endpoint);
            }
        }
        catch (Exception failure)
        {
            try { await fixture.DisposeAsync(); }
            catch (Exception cleanup) { throw new AggregateException("Development-family preparation and cleanup failed.", failure, cleanup); }
            throw;
        }
    }

    internal async Task LoadAsync(CancellationToken token)
    {
        var result = await Sessions.LoadStartupDevPackagesAsync(Folders.Values.ToArray(), token);
        CaptureWorkers();
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Empty(await Services.GetRequiredService<InstalledPackageLifecycleService>().GetInstalledAsync(token));
        Assert.Equal(Folders.Count, Owner.State.ActiveSession.LoadedPackageMap.Count);
        Assert.All(Owner.State.ActiveSession.LoadedPackageMap.Values, package =>
        {
            Assert.Equal(PackageSourceKind.Dev, package.Source.Kind);
            Assert.True(package.Source.DevelopmentImports!.IsLocal);
            Assert.Null(package.Source.DevelopmentImports.RegistryOrigin);
            Assert.NotNull(package.ExecutionRuntimeBinding);
        });
        Owner.MarkReady();
    }

    internal Process[] CaptureWorkers()
    {
        var processes = new List<Process>();
        foreach (var package in Owner.State.ActiveSession.LoadedPackageMap.Values)
        {
            var worker = Assert.IsType<ProcessRuntimeWorker>(package.Worker);
            var actual = Assert.IsType<Process>(typeof(ProcessRuntimeWorker).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker));
            Assert.False(actual.HasExited);
            var identity = (actual.Id, actual.StartTime.ToUniversalTime().Ticks);
            if (!_observed.TryGetValue(identity, out var observed))
            {
                observed = Process.GetProcessById(actual.Id);
                _ = observed.SafeHandle; // Hold the native identity across retirement/PID reuse.
                Assert.Equal(actual.StartTime.ToUniversalTime(), observed.StartTime.ToUniversalTime());
                _observed.Add(identity, observed);
            }
            processes.Add(observed);
            _workerFolders.Add(worker.WorkerTemporaryPath);
        }
        return processes.ToArray();
    }

    internal static async Task AssertStoppedAsync(IEnumerable<Process> processes)
    {
        foreach (var process in processes.Distinct())
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            try { CaptureWorkers(); }
            finally { await Services.GetRequiredService<InstalledPackageLifecycleService>().ShutdownAsync(Sessions, default); }
        }
        finally
        {
            try { await _app.StopAsync(); }
            finally
            {
                try { await _app.DisposeAsync(); }
                finally
                {
                    try
                    {
                        await AssertStoppedAsync(_observed.Values);
                        Assert.All(_workerFolders, path => Assert.False(Directory.Exists(path), "Worker temporary directory survived retirement: " + path));
                    }
                    finally
                    {
                        foreach (var process in _observed.Values) process.Dispose();
                        if (Connection.IpcEndpoint!.Kind == RuntimeIpcTransportKind.UnixSocket) File.Delete(Connection.IpcEndpoint.Address);
                        await _temporary.DisposeAsync();
                    }
                }
            }
        }
    }
}
