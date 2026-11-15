using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

// Real App image/OS peer, no desktop or package-view construction. This gate
// qualifies exported artifact materialization and supervised module activation;
// ordinary Program.Main/native UI and installed acceptance have separate owners.
internal static class StartupHook
{
    public static void Initialize()
    {
        var config = Environment.GetEnvironmentVariable("SUNDER_TEST_AGENT_FAMILY_APP_CONFIG");
        if (string.IsNullOrWhiteSpace(config)) return;
        Environment.SetEnvironmentVariable("SUNDER_TEST_AGENT_FAMILY_APP_CONFIG", null);
        var code = 1;
        try { RunAsync(config).GetAwaiter().GetResult(); code = 0; }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            foreach (var entry in AppSessionLog.Snapshot())
                Console.Error.WriteLine(entry.Message + Environment.NewLine + entry.ExceptionText);
        }
        Environment.Exit(code); // Never fall through to normal startup or live state.
    }

    private static async Task RunAsync(string configPath)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(configPath, deadline.Token));
        var config = document.RootElement;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string Read(string name) => config.GetProperty(name).GetString()!;
        var connection = RuntimeConnectionInfoStore.Load(Read("connectionPath")) ?? throw new InvalidDataException("Missing private family test connection.");
        using var gateway = new RuntimeClientTransport(() => connection, RuntimeIpcHttpMessageHandlerFactory.Create(connection.IpcEndpoint!));
        using var direct = new AppRuntimeProcessTransport(gateway);
        await using var policy = new AppProcessPolicySupervisor(direct, () => Environment.Exit(79));
        var package = config.GetProperty("package").Deserialize<ActivePackageDescriptor>(json)!;
        var snapshot = config.GetProperty("snapshot").Deserialize<PackageUiSnapshotDescriptor>(json)!;
        if (snapshot.SourceKind != PackageSourceKind.Dev || package.PackageId != "younics.ai.agent")
            throw new InvalidDataException("Family App activation requires the exact development owner.");
        await using (var host = new PackageViewHostService(new AppPackageViewRegistry(), [], [], [], Read("sessionRoot"),
            getRuntimeConnectionInfo: direct.Transport.GetConnectionInfo,
            downloadPackageUiSnapshotAsync: async (requested, destination, token) =>
            {
                if (requested != snapshot) throw new InvalidDataException("Unexpected App artifact request.");
                await using var source = File.OpenRead(Read("snapshotPath"));
                await source.CopyToAsync(destination, token);
            }, uiDispatcher: new ImmediateUiDispatcher(), packagePolicy: policy, runtimeStateRootPath: Read("runtimeRoot")))
        {
            await host.ApplyPackageDeltaAsync([package], [snapshot], cancellationToken: deadline.Token);
            var active = host.FilterEnabledPackages([package]).Single();
            if (host.LoadedPackageCount != 1 || !active.Views.Any(view => view.ViewId == "younics.ai.agent.chat"))
                throw new InvalidDataException("The exported owner artifact did not activate its reviewed App contribution.");
            Console.WriteLine("agent-family-app: materialized-and-activated");
            var input = new byte[1];
            if (await Console.OpenStandardInput().ReadAsync(input, deadline.Token) != 0)
                throw new InvalidDataException("Unexpected family App retirement command.");
        }
        Console.WriteLine("agent-family-app: retired");
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task InvokeAsync(Func<Task> action) => action();
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }
}
