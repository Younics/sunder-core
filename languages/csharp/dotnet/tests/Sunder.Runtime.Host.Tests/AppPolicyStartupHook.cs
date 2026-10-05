using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Rpc;

// Test-only startup hook compiled outside every package and product payload. It
// exercises the production shell controller in the actual App image before Main,
// so native process identity is real and the test does not need a desktop window.
internal static class StartupHook
{
    public static void Initialize()
    {
        var path = Environment.GetEnvironmentVariable("SUNDER_TEST_APP_POLICY_CONFIG");
        if (string.IsNullOrWhiteSpace(path)) return;
        Environment.SetEnvironmentVariable("SUNDER_TEST_APP_POLICY_CONFIG", null);
        RunAsync(path).GetAwaiter().GetResult();
    }

    private static async Task RunAsync(string path)
    {
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        var config = document.RootElement;
        var resultPath = config.GetProperty("resultPath").GetString()!;
        try
        {
            var connection = RuntimeConnectionInfoStore.Load(config.GetProperty("connectionPath").GetString())
                ?? throw new InvalidDataException("Missing native test connection.");
            using var gateway = new RuntimeClientTransport(() => connection, RuntimeIpcHttpMessageHandlerFactory.Create(connection.IpcEndpoint!));
            using var direct = new AppRuntimeProcessTransport(gateway);
            await using var policy = new AppProcessPolicySupervisor(direct, () => Environment.Exit(0));
            var snapshot = config.GetProperty("snapshot").Deserialize<PackageUiSnapshotDescriptor>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await policy.RequireLoadAsync(snapshot, deadline.Token);
            var root = config.GetProperty("projectionPath").GetString()!;
            var manifestHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "manifest", "sunder-package.json"), deadline.Token)));
            var stamp = new ActivePackageWebStamp(snapshot.PackageId, snapshot.PolicySubject!.Version, manifestHash,
                snapshot.Target, snapshot.SessionGeneration, Guid.NewGuid());
            await using var rpc = await new RuntimeAppWebRpcClientFactory(direct.Transport.GetConnectionInfo).CreateAsync(stamp, deadline.Token);
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, snapshot.Target.EntryPoint.Replace('/', Path.DirectorySeparatorChar)));
            var method = assembly.GetType("Example.Generated.App.UsesGeneratedClient", throwOnError: true)!.GetMethod("InvokeAsync", BindingFlags.Public | BindingFlags.Static)!;
            var response = await (Task<string>)method.Invoke(null, new object[] { (ISunderRpcClient)rpc, "native-policy", deadline.Token })!;
            await WriteResultAsync(resultPath, new
            {
                success = true,
                response,
                assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(value => value.GetName().Name).Order(StringComparer.Ordinal).ToArray(),
            });
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        catch (Exception error)
        {
            await WriteResultAsync(resultPath, new { success = false, error = error.ToString() });
            Environment.Exit(78);
        }
    }

    private static async Task WriteResultAsync<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(value));
        File.Move(temporary, path, overwrite: true);
    }
}
