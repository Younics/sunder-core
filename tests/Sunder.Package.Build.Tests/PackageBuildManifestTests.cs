using System.Collections;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Microsoft.Build.Framework;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Build.Tasks;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Configuration;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Theming;
using Xunit;
using MSBuildTaskItem = Microsoft.Build.Utilities.TaskItem;

[assembly: SunderPackage(
    Id = "test.package.build.fixture",
    Name = "Package Build Fixture",
    Summary = "Exercises generated manifest compatibility metadata.")]

[assembly: SunderPackageDependency(
    PackageId = "test.dependency",
    VersionRange = ">=1.0.0")]

namespace Sunder.Package.Build.Tests;

public sealed class PackageBuildManifestTests
{
    [Fact]
    public void GenerateManifest_InfersSdkCompatibilityMetadata()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);

        var success = task.Execute();

        Assert.True(success, string.Join(Environment.NewLine, buildEngine.Errors));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("sdkApiVersion").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("sdkPackageVersion").GetString()));
        var capabilities = ReadCapabilities(root);
        AssertContainsCapabilities(
            capabilities,
            SunderSdkCapabilities.CoreV1,
            SunderSdkCapabilities.PackagingV1,
            SunderSdkCapabilities.ContributionsV1,
            SunderSdkCapabilities.ViewsV1,
            SunderSdkCapabilities.SettingsViewsV1,
            SunderSdkCapabilities.WorkspacesV1,
            SunderSdkCapabilities.BackgroundServicesV1,
            SunderSdkCapabilities.ExtensionsV1,
            SunderSdkCapabilities.ConfigurationSchemaV1,
            SunderSdkCapabilities.ConfigurationValuesV1,
            SunderSdkCapabilities.StorageV1,
            SunderSdkCapabilities.SecretsV1,
            SunderSdkCapabilities.LoggingV1,
            SunderSdkCapabilities.NotificationsV1,
            SunderSdkCapabilities.ShellViewV1,
            SunderSdkCapabilities.CallbacksV1,
            SunderSdkCapabilities.AuthV1,
            SunderSdkCapabilities.ExtensionChangesV1,
            SunderSdkCapabilities.ThemingV1);
    }

    [Fact]
    public void GenerateManifest_IncludesManualSdkCapabilityOverrides()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        task.SdkCapabilities = [new MSBuildTaskItem("custom.dynamic.v1")];

        var success = task.Execute();

        Assert.True(success, string.Join(Environment.NewLine, buildEngine.Errors));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var capabilities = ReadCapabilities(document.RootElement);
        Assert.Contains("custom.dynamic.v1", capabilities);
    }

    private static GenerateSunderPackageManifestTask CreateTask(string manifestPath, IBuildEngine buildEngine)
        => new()
        {
            BuildEngine = buildEngine,
            TargetAssemblyPath = Assembly.GetExecutingAssembly().Location,
            ManifestOutputPath = manifestPath,
            EntryAssembly = Path.GetFileName(Assembly.GetExecutingAssembly().Location),
            PackageVersion = "1.2.3",
            ProjectDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            TargetFramework = "net10.0",
        };

    private static HashSet<string> ReadCapabilities(JsonElement root)
        => root.GetProperty("requiredSdkCapabilities")
            .EnumerateArray()
            .Select(static capability => capability.GetString()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void AssertContainsCapabilities(HashSet<string> actual, params string[] expected)
    {
        foreach (var capability in expected)
        {
            Assert.Contains(capability, actual);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-package-build-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestBuildEngine : IBuildEngine
    {
        public List<string> Errors { get; } = [];

        public bool ContinueOnError => false;

        public int LineNumberOfTaskNode => 0;

        public int ColumnNumberOfTaskNode => 0;

        public string ProjectFileOfTaskNode => string.Empty;

        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs)
            => true;

        public void LogCustomEvent(CustomBuildEventArgs e)
        {
        }

        public void LogErrorEvent(BuildErrorEventArgs e)
            => Errors.Add(e.Message ?? string.Empty);

        public void LogMessageEvent(BuildMessageEventArgs e)
        {
        }

        public void LogWarningEvent(BuildWarningEventArgs e)
        {
        }
    }
}

public sealed class FixturePackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        _ = context.Configuration;
        _ = context.Storage.State;
        _ = context.Secrets;
        _ = context.Logging.Events;
        services.AddSingleton<IPackageCallbackHandler, FixtureAuthHandler>();
        services.AddSingleton<IPackageAuthHandler, FixtureAuthHandler>();
        services.AddSingleton<IPackageNotificationService, NullPackageNotificationService>();
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterPackageView<FixtureView>(new PackageViewRegistration("fixture.view", "Fixture View"));
        registry.RegisterPackageViewFactory<FixtureWorkspaceFactory>(new PackageViewRegistration("fixture.workspace", "Fixture Workspace"));
        registry.RegisterSettingsView<FixtureSettingsView>();
        registry.RegisterSettingsViewFactory<FixtureWorkspaceFactory>();
        registry.RegisterBackgroundService<FixtureBackgroundService>();
        registry.RegisterExtension(new PackageExtensionPoint<IFixtureExtension>("fixture.extension"), new FixtureExtension());
        registry.RegisterConfigurationSchema(new PackageConfigurationSchema(
            "test.package.build.fixture",
            "Package Build Fixture",
            null,
            [new PackageConfigurationSection(
                "general",
                "General",
                null,
                [new PackageConfigurationField("enabled", "Enabled", PackageConfigurationFieldKind.Boolean)])]));
    }
}

public sealed class FixtureView : UserControl;

public sealed class FixtureSettingsView : UserControl;

public sealed class FixtureWorkspaceFactory : IPackageWorkspaceFactory
{
    public Control CreateRootView(IServiceProvider services)
        => new FixtureView();
}

public sealed class FixtureBackgroundService : IPackageBackgroundService
{
    public Task StartAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public interface IFixtureExtension;

public sealed class FixtureExtension : IFixtureExtension;

public sealed class FixtureAuthHandler : IPackageAuthHandler
{
    public ValueTask<PackageAuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new PackageAuthStatus(
            "test.package.build.fixture",
            PackageAuthStatusKind.NotConnected,
            "Not connected.",
            CanAuthorize: true,
            CanDisconnect: false));

    public Task<PackageAuthSessionStartResult?> StartAuthorizationAsync(PackageAuthSessionStartContext context, CancellationToken cancellationToken = default)
        => Task.FromResult<PackageAuthSessionStartResult?>(new PackageAuthSessionStartResult(
            "test.package.build.fixture",
            context.AuthSessionId,
            PackageAuthFlowKind.Browser,
            "https://example.test/auth",
            "Authorize."));

    public Task<PackageAuthStatus> CompleteAuthorizationAsync(PackageAuthSessionCompletionContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(new PackageAuthStatus(
            "test.package.build.fixture",
            PackageAuthStatusKind.Connected,
            "Connected.",
            CanAuthorize: false,
            CanDisconnect: true));

    public Task<PackageAuthStatus> DisconnectAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new PackageAuthStatus(
            "test.package.build.fixture",
            PackageAuthStatusKind.NotConnected,
            "Disconnected.",
            CanAuthorize: true,
            CanDisconnect: false));
}

public sealed class FixtureExtensionCatalogObserver(IPackageExtensionCatalog extensionCatalog)
{
    private readonly IPackageExtensionCatalogMonitor? _monitor = extensionCatalog as IPackageExtensionCatalogMonitor;

    public bool HasMonitor => _monitor is not null;
}

public sealed class FixtureShellViewConsumer(IPackageShellViewService shellViewService)
{
    public IPackageShellViewService ShellViewService { get; } = shellViewService;

    public IReadOnlyList<string> ThemeKeys => SunderThemeKeys.BrushKeys;
}
