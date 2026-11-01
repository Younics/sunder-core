using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Avalonia.Theming;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Storage;


[assembly: SunderPackage(
    Id = "test.package.build.fixture",
    Name = "Package Build Fixture",
    Summary = "Exercises generated manifest compatibility metadata.")]

[assembly: SunderPackageDependency(
    PackageId = "test.dependency",
    VersionRange = ">=$(PackageVersion) <$(NextPackageMajorVersion).0.0")]

namespace Sunder.Package.Build.Tests;


public sealed class FixturePackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        _ = context.Settings;
        _ = context.Storage.State;
        _ = context.Secrets;
        _ = context.Logging.Events;
        services.AddSingleton<IPackageCallbackHandler, FixtureAuthHandler>();
        services.AddSingleton<IPackageAuthHandler, FixtureAuthHandler>();
        services.AddSingleton<IPackageNotificationService, NullPackageNotificationService>();
        services.AddSingleton<FixtureStackContributor>();
        services.AddSingleton(new PackageSettingsSchema(
            null,
            [new PackageSettingsSection(
                "general",
                "General",
                null,
                [new PackageSettingsField("enabled", "Enabled", PackageSettingsFieldKind.Boolean)])]));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        _ = Type.GetType("Sunder.Sdk.Callbacks.PackageCallbackStartContext, Sunder.Sdk");
        registry.RegisterPackageView<FixtureView>(new PackageViewRegistration("fixture.view", "Fixture View"));
        registry.RegisterSettingsView<FixtureSettingsView>();
    }
}


public sealed class FixtureView : UserControl;


public sealed class FixtureSettingsView : UserControl;


public sealed class FixtureStackContributor : IPackageStackExporter, IPackageStackImporter
{
    public string ContributorId => "test.package.build.fixture.stacks";

    public string DisplayName => "Fixture Stacks";

    public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
        StackExportDiscoveryContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<StackExportItemDescriptor>>(
            [new StackExportItemDescriptor("fixture", "Fixture", "fixture")]);

    public ValueTask<StackExportContribution> ExportAsync(
        StackExportRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new StackExportContribution([], [], []));

    public ValueTask<StackImportPreview> PreviewImportAsync(
        StackImportPreviewRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new StackImportPreview([], [], [], []));

    public ValueTask<StackImportResult> ImportAsync(
        StackImportRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new StackImportResult(StackImportOutcome.Completed, [], new Dictionary<string, string>(), [], []));
}


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


public sealed class FixtureShellViewConsumer(IPackageShellViewService shellViewService)
{
    public IPackageShellViewService ShellViewService { get; } = shellViewService;

    public IReadOnlyList<string> ThemeKeys => SunderThemeKeys.BrushKeys;
}


public sealed class FixturePreparedNavigationConsumer(IPackageViewNavigationPreparationTarget target)
{
    public IPackageViewNavigationPreparationTarget Target { get; } = target;
}


public sealed class FixturePackageActivationCapabilities(IPackageRoleLocalWorkspace roleLocalWorkspace)
{
    public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; } = roleLocalWorkspace;
}


public sealed class FixtureNewSdkCapabilities(IPackageStorageKeyMigrator storageKeyMigrator)
{
    public IPackageStorageKeyMigrator StorageKeyMigrator { get; } = storageKeyMigrator;

    public string StorageKey { get; } = PackageStorageKeyFactory.Create("fixture", 1, "key");

    public PackageStorageKeyMigration StorageMigration { get; }
        = PackageStorageKeyMigration.Exact("legacy", "fixture.v1.key");

}
