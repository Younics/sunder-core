using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sunder.App.Composition;
using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

// Run inside the real App executable so native process admission is unchanged.
// Compose the normal shell directly: default startup's CLI installation and
// update/provisioning work are deliberately outside this fixture's authority.
internal static class StartupHook
{
    public static void Initialize()
    {
        var config = Environment.GetEnvironmentVariable("SUNDER_TEST_DEVELOPMENT_CAPTURE_CONFIG");
        if (string.IsNullOrEmpty(config)) return;
        Environment.SetEnvironmentVariable("SUNDER_TEST_DEVELOPMENT_CAPTURE_CONFIG", null);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("The App entry point must supply the original STA thread; the fixture must not repair its apartment.");
        Environment.Exit(Start(config));
    }

    private static int Start(string config) => AppBuilder.Configure<CaptureApplication>().UsePlatformDetect().AfterSetup(builder =>
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)builder.Instance!.ApplicationLifetime!;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.Startup += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            try { await RunAsync(desktop, config).WaitAsync(TimeSpan.FromSeconds(90)); desktop.Shutdown(0); }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                ReportProfileLocks();
                foreach (var entry in AppSessionLog.Snapshot())
                    Console.Error.WriteLine(entry.Message + Environment.NewLine + entry.ExceptionText);
                desktop.Shutdown(1);
            }
        });
    }).StartWithClassicDesktopLifetime([]);

    private static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, string config)
    {
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(config));
        string Read(string name) => document.RootElement.GetProperty(name).GetString()!;
        var gateway = RuntimeConnectionInfoStore.Load(Read("gatewayPath"))!;
        var worker = RuntimeConnectionInfoStore.Load(Read("workerPath"))!;
        using var transport = new RuntimeClientTransport(() => gateway);
        using var processTransport = new AppRuntimeProcessTransport(transport, _ => worker);
        await using var policy = new AppProcessPolicySupervisor(processTransport,
            () => Dispatcher.UIThread.Post(() => desktop.Shutdown(2)));
        var resources = new AppPackageResourceAssemblyRegistry();
        var registrations = SunderAppComposition.CreateServices(Application.Current!,
            new AppStartupOptions { RuntimeUrl = gateway.RuntimeUrl, HasExplicitRuntimeUrl = true }, resources);
        registrations.RemoveAll<RuntimeConnectionState>();
        registrations.AddSingleton(new RuntimeConnectionState(gateway.RuntimeUrl, _ => gateway));
        registrations.RemoveAll<RuntimeClientTransport>();
        registrations.AddSingleton(transport);
        registrations.RemoveAll<PackageViewHostServiceFactory>();
        registrations.AddSingleton(provider => new PackageViewHostServiceFactory(
            provider.GetRequiredService<AppPackageShellViewService>(), provider.GetRequiredService<AppPackageSettingsNavigationService>(),
            processTransport.Transport, provider.GetRequiredService<NotificationCenterService>(), provider.GetRequiredService<BackgroundTaskQueueService>(),
            resources, AvaloniaUiDispatcher.Instance)
        { PackagePolicy = policy, DeveloperServices = provider.GetRequiredService<AppPackageDeveloperServices>() });
        await using var services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var client = new RuntimeApiClient(transport);
        var snapshot = await client.GetRuntimePackageSnapshotAsync();
        var sources = await client.GetActivePackageUiSnapshotsAsync(AppPackageTargetEnvironment.CurrentRid);
        Require(snapshot.ActivePackages.Count == 1 && sources.Single().SourceKind == PackageSourceKind.Installed,
            "The developer tool was not installed by the Runtime.");
        await using var host = await services.GetRequiredService<PackageViewHostServiceFactory>().CreateForPackagesAsync([], []);
        var state = services.GetRequiredService<ShellState>();
        var shell = services.GetRequiredService<IShellCompositionService>().Compose([], state, null, [], [], ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot);
        await using var subscription = new RuntimeEventSubscriptionService(services.GetRequiredService<IRuntimeApiClientFactory>(),
            services.GetRequiredService<DeveloperLogService>());
        using var launcher = services.GetRequiredService<WindowLauncherFactory>().Create(host, subscription);
        var (window, model) = services.GetRequiredService<MainWindowFactory>().Create(launcher, shell, host, null, deferInitialHostedViews: false);
        desktop.MainWindow = window;
        window.Width = 900;
        window.Height = 600;
        window.Show();
        using var browserTracking = ContentControl.ContentProperty.Changed.AddClassHandler<AppWebPackageViewControl>(
            (view, args) =>
            {
                if (args.NewValue is not Control) return;
                view.Browser.NavigationCompleted += (_, _) =>
                {
                    if (view.Browser.NativeBrowserProcessId is { } pid) WindowsWebViewBrowserProcess.Record(pid);
                };
            });
        try
        {
            await launcher.ApplyPackageLifecycleSnapshotAsync(snapshot, sources, null, default);
            await subscription.StartAsync(launcher, snapshot, sources);
            subscription.ReleasePresentation();
            Require(await model.OpenPackageViewPanelAsync("example.generated.developer"), "The shell did not open the installed developer tool.");
            var view = host.GetOrCreateView("example.generated.developer")!;
            var method = view.GetType().GetMethod("CaptureAsync", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException("The installed SDK fixture did not expose its capture action.");
            await ((Task)method.Invoke(view, [Read("development"), Read("output")])!);
            var whole = await File.ReadAllBytesAsync(Path.Combine(Read("output"), "whole.png"));
            var dimensions = AppDevelopmentViewOperations.ValidatePng(whole);
            Require(Math.Max(dimensions.Width, dimensions.Height) == 128, "The SDK image did not obey its bounded edge.");
            var pixels = WindowsWebViewCaptureProcessProbe.Pixels(whole);
            Require(WindowsWebViewCaptureProcessProbe.HasColor(pixels, 10, 180, 30)
                && WindowsWebViewCaptureProcessProbe.HasColor(pixels, 20, 40, 220), "The SDK image omitted the native page pixels.");
            Require(!WindowsWebViewCaptureProcessProbe.HasColor(pixels, 255, 0, 0), "The SDK image included the neighboring installed tool.");
            var crop = await File.ReadAllBytesAsync(Path.Combine(Read("output"), "selected.png"));
            Require(AppDevelopmentViewOperations.ValidatePng(crop) == (100, 80), "The SDK selection changed the native element bounds.");
            Require(WindowsWebViewCaptureProcessProbe.EveryColor(WindowsWebViewCaptureProcessProbe.Pixels(crop), 20, 40, 220),
                "The SDK selection included pixels outside its native element.");
            Require((await client.GetRuntimePackageSnapshotAsync()).ActivePackages.Count == 1, "The SDK owner did not unload its development package.");
            Require(host.BrowserSessionCount == 0,
                "The SDK unload retained an active, retiring or cached package browser profile.");
            // Shared-engine files are reclaimed at App shutdown, not by deleting
            // a live sibling's UDF or pretending that moving roots proved exit.
            Require(window.IsVisible, "The native cleanup check ran after the App window closed.");
            Console.WriteLine("installed-consumer-capture: native-shell; runtime-installed; sdk-owned; package-only; selected-crop; retired-session-rejected");
        }
        finally
        {
            await subscription.DisposeAsync();
            await launcher.CancelBackgroundTasksAsync();
            model.Dispose();
            window.CloseForShutdown();
            window.DataContext = null;
            launcher.CloseForShutdown();
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static void ReportProfileLocks()
    {
        var root = AppLocalState.GetPath("webview-data");
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "lockfile", SearchOption.AllDirectories))
        {
            var key = new System.Text.StringBuilder(33);
            if (RmStartSession(out var session, 0, key) != 0) continue;
            try
            {
                var registered = RmRegisterResources(session, 1, [file], 0, 0, 0, 0);
                uint needed = 0, count = 0, reasons = 0;
                var result = RmGetList(session, out needed, ref count, null, ref reasons);
                var owners = new RestartManagerProcess[needed];
                count = needed;
                if (result == 234) result = RmGetList(session, out needed, ref count, owners, ref reasons);
                Console.Error.WriteLine($"installed-consumer: lock owners; registered={registered}; result={result}; file={file}");
                if (result == 0) foreach (var process in owners.Take((int)count))
                    Console.Error.WriteLine($"installed-consumer: lock owner; pid={process.Process.ProcessId}; app={process.Name}");
            }
            finally { RmEndSession(session); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RestartManagerIdentity { internal uint ProcessId; internal System.Runtime.InteropServices.ComTypes.FILETIME Started; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RestartManagerProcess
    {
        internal RestartManagerIdentity Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string Service;
        internal uint ApplicationType, Status, SessionId;
        [MarshalAs(UnmanagedType.Bool)] internal bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint session, uint flags, System.Text.StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint session, uint fileCount, string[] files, uint appCount, nint apps, uint serviceCount, nint services);
    [DllImport("rstrtmgr.dll")] private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RestartManagerProcess[]? processes, ref uint reasons);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);

    private sealed class CaptureApplication : Sunder.App.App
    {
        public CaptureApplication() { }
        // Inherit the real App resources, not default startup's installation work.
        public override void OnFrameworkInitializationCompleted() { }
    }
}
