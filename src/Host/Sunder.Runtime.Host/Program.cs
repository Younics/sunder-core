using System.Diagnostics;
using Sunder.Protocol;
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
builder.Services.AddSingleton<RuntimePackagePaths>();
builder.Services.AddSingleton<InstalledPackageStore>();
builder.Services.AddSingleton<SunderPackageArchiveInstaller>();
builder.Services.AddSingleton<RuntimePackageSessionService>();
builder.Services.AddSingleton<PackageAuthCallbackServer>();

var app = builder.Build();

var startedAtUtc = DateTimeOffset.UtcNow;

app.MapSystemEndpoints(startedAtUtc)
    .MapPackageSessionEndpoints()
    .MapPackageConfigurationEndpoints()
    .MapPackageAuthEndpoints()
    .MapPackageFaultEndpoints()
    .MapInstalledPackageEndpoints();

var packageSessionService = app.Services.GetRequiredService<RuntimePackageSessionService>();
if (startupOptions.DevPackageFolders.Count > 0)
{
    await packageSessionService.LoadPackageLifecycleAsync(new PackageLifecycleLoadRequest(
        startupOptions.DevPackageFolders
            .Select(folder => new PackageSessionLoadRequest(PackageSourceKind.Dev, folder))
            .ToArray(),
        PackageLifecycleOverlayOwner.Startup));
}
else
{
    await packageSessionService.LoadInstalledPackagesAsync();
}

app.Run();
