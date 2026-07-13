using System.Diagnostics;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Services;

var startupOptions = RuntimeHostStartupOptions.Parse(args);
if (startupOptions.WaitForDebugger)
{
    while (!Debugger.IsAttached)
    {
        Thread.Sleep(100);
    }
}

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
{
    throw new InvalidOperationException(
        "Configured Kestrel endpoints are not supported by the local Runtime. Configure one loopback URL with --urls or ASPNETCORE_URLS instead.");
}

var listenUrls = RuntimeListenUrlValidator.ParseAndValidate(
    builder.Configuration["urls"],
    startupOptions.DevelopmentAllowNonLoopbackRuntimeListen);
builder.WebHost.UseUrls(listenUrls.ToArray());

var packagePaths = new RuntimePackagePaths();
Environment.SetEnvironmentVariable(RuntimeLocalState.StateRootEnvironmentVariable, null);
RuntimeLocalState.Validate(packagePaths.RootPath);
using var runtimeRootLease = RuntimeRootLease.Acquire(packagePaths);
RuntimeLocalState.EnsureInitialized(packagePaths.RootPath);

var bearerToken = Environment.GetEnvironmentVariable("SUNDER_RUNTIME_BEARER_TOKEN");
Environment.SetEnvironmentVariable("SUNDER_RUNTIME_BEARER_TOKEN", null);
if (string.IsNullOrWhiteSpace(bearerToken))
{
    bearerToken = RuntimeBearerToken.Create();
}

var connectionInfoPath = Environment.GetEnvironmentVariable("SUNDER_RUNTIME_CONNECTION_FILE");
Environment.SetEnvironmentVariable("SUNDER_RUNTIME_CONNECTION_FILE", null);
var connectionInfo = new RuntimeConnectionInfo(new Uri(listenUrls[0]), bearerToken);
RuntimeConnectionInfoStore.Save(connectionInfo, connectionInfoPath);

try
{
    builder.Services.AddRuntimeHostServices(
        packagePaths,
        new RuntimeBearerTokenValidator(bearerToken));

    var app = builder.Build();
    app.UseMiddleware<RuntimeProblemDetailsMiddleware>();
    app.UseMiddleware<RuntimeBearerAuthenticationMiddleware>();

    var startedAtUtc = DateTimeOffset.UtcNow;
    app.MapRuntimeHandshakeEndpoint();
    var api = app.MapGroup("/api/v1");
    api.MapSystemEndpoints(startedAtUtc)
        .MapPackageSessionEndpoints()
        .MapContentTransferEndpoints()
        .MapPackageDataEndpoints()
        .MapPackageRuntimeOperationEndpoints()
        .MapPackageSettingsEndpoints()
        .MapPackageCallbackEndpoints()
        .MapPackageAuthEndpoints()
        .MapPackageFaultEndpoints()
        .MapInstalledPackageEndpoints()
        .MapStackEndpoints()
        .MapRuntimeStreamEndpoints();
    api.MapRegistryEndpoints();

    var packageSessionService = app.Services.GetRequiredService<PackageSessionLifecycleService>();
    var installedPackageService = app.Services.GetRequiredService<InstalledPackageLifecycleService>();
    var devPackageWatchService = app.Services.GetRequiredService<DevPackageWatchService>();
    var packageLogStreamService = app.Services.GetRequiredService<PackageLogStreamService>();
    var runtimeEventStreamService = app.Services.GetRequiredService<RuntimeEventStreamService>();
    var runtimeSessionOwner = app.Services.GetRequiredService<RuntimeSessionOwner>();
    try
    {
        packageLogStreamService.Start();
        await installedPackageService.InitializeAsync();
        if (startupOptions.DevPackageFolders.Count > 0)
        {
            var startupLoad = await packageSessionService.LoadStartupDevPackagesAsync(startupOptions.DevPackageFolders);
            if (!startupLoad.Success)
            {
                var details = startupLoad.Errors.Count > 0
                    ? string.Join(Environment.NewLine, startupLoad.Errors)
                    : startupLoad.Message ?? "The Runtime rejected the startup dev packages.";
                throw new InvalidOperationException($"Failed to load startup dev packages:{Environment.NewLine}{details}");
            }
        }
        else
        {
            await installedPackageService.LoadInstalledPackagesAsync();
        }

        await app.RunAsync();
    }
    finally
    {
        await runtimeSessionOwner.Callbacks.ShutdownAsync();
        await devPackageWatchService.DisposeAsync();
        await installedPackageService.ShutdownAsync(packageSessionService);
        await packageLogStreamService.DisposeAsync();
        runtimeEventStreamService.Complete();
    }
}
finally
{
    RuntimeConnectionInfoStore.DeleteIfMatches(connectionInfo, connectionInfoPath);
}
