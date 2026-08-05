using System.Globalization;
using System.Reflection;

namespace Sunder.Cli;

internal sealed class CliCommandDispatcher(
    ICliHostClient? host,
    ICliRuntimeClient? runtime,
    IRegistryClient? registry,
    CliOutput output,
    ICliProgress progress,
    IBrowserLauncher browser,
    CliOptions options,
    IRegistryPublishCredentialReader credentials,
    IPackageSecretValueReader secretValues)
{
    private readonly ArchiveValidationService _archives = new();

    public Task<int> ExecuteAsync(CliCommand command, CancellationToken token) => command switch
    {
        VersionCommand => Task.FromResult(ShowVersion()),
        ShowConfigCommand => Task.FromResult(ShowConfig()),
        RuntimeStatusCommand value => new RuntimeStatusCommandHandler(RequireHost(), RequireRuntime(), output).ExecuteAsync(value, token),
        RuntimeLifecycleCommand value => new RuntimeLifecycleCommandHandler(RequireHost(), output, progress).ExecuteAsync(value, token),
        RuntimeResetCommand value => new RuntimeResetCommandHandler(RequireRuntime(), output, options.RequireRuntimeUrl()).ExecuteAsync(value, token),
        ListInstalledCommand value => PackageHandler().ExecuteAsync(value, token),
        PackageStatusCommand value => PackageHandler().ExecuteAsync(value, token),
        InstallRegistryPackageCommand value => PackageHandler().ExecuteAsync(value, token),
        InstallLocalPackageCommand value => PackageHandler().ExecuteAsync(value, token),
        UpdatePackagesCommand value => PackageHandler().ExecuteAsync(value, token),
        AdoptPackageSourceCommand value => PackageHandler().ExecuteAsync(value, token),
        SetPackageEnabledCommand value => PackageHandler().ExecuteAsync(value, token),
        UninstallPackageCommand value => PackageHandler().ExecuteAsync(value, token),
        ShowPackageConfigSchemaCommand value => SettingsHandler().ExecuteAsync(value, token),
        ListPackageConfigCommand value => SettingsHandler().ExecuteAsync(value, token),
        GetPackageConfigCommand value => SettingsHandler().ExecuteAsync(value, token),
        SetPackageConfigCommand value => SettingsHandler().ExecuteAsync(value, token),
        UnsetPackageConfigCommand value => SettingsHandler().ExecuteAsync(value, token),
        PackageSecretStatusCommand value => SettingsHandler().ExecuteAsync(value, token),
        SetPackageSecretCommand value => SettingsHandler().ExecuteAsync(value, token),
        UnsetPackageSecretCommand value => SettingsHandler().ExecuteAsync(value, token),
        PackageAuthStatusCommand value => PackageAuthHandler().ExecuteAsync(value, token),
        PackageAuthLoginCommand value => PackageAuthHandler().ExecuteAsync(value, token),
        PackageAuthLogoutCommand value => PackageAuthHandler().ExecuteAsync(value, token),
        SearchPackagesCommand value => new RegistryBrowseCommandHandler(RequireRegistry(), output).ExecuteAsync(value, token),
        PackageInfoCommand value => new RegistryBrowseCommandHandler(RequireRegistry(), output).ExecuteAsync(value, token),
        AuthLoginCommand value => AuthHandler().ExecuteAsync(value, token),
        AuthStatusCommand value => AuthHandler().ExecuteAsync(value, token),
        AuthLogoutCommand value => AuthHandler().ExecuteAsync(value, token),
        SetYankCommand value => ManagementHandler().ExecuteAsync(value, token),
        SetDeprecationCommand value => ManagementHandler().ExecuteAsync(value, token),
        ListDistTagsCommand value => ManagementHandler().ExecuteAsync(value, token),
        SetDistTagCommand value => ManagementHandler().ExecuteAsync(value, token),
        ValidatePackageCommand value => new ValidationCommandHandler(_archives, output).ExecuteAsync(value, token),
        PublishPackageCommand value => PublishHandler().ExecuteAsync(value, token),
        SearchStacksCommand value => StackHandler().ExecuteAsync(value, token),
        StackInfoCommand value => StackHandler().ExecuteAsync(value, token),
        DownloadStackCommand value => StackHandler().ExecuteAsync(value, token),
        PublishStackCommand value => StackHandler().ExecuteAsync(value, token),
        DeleteStackCommand value => StackHandler().ExecuteAsync(value, token),
        OpenStackCommand value => StackHandler().ExecuteAsync(value, token),
        ImportStackCommand value => RuntimeStackHandler().ExecuteAsync(value, token),
        ExportStackCommand value => RuntimeStackHandler().ExecuteAsync(value, token),
        ValidateStackCommand value => StackHandler().ExecuteAsync(value, token),
        _ => throw new InvalidOperationException($"No handler is registered for {command.GetType().Name}."),
    };

    private int ShowVersion()
    {
        var assembly = typeof(CliCommandDispatcher).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "unknown";
        output.Data(new { version });
        output.Line(version);
        return CliExitCodes.Success;
    }

    private int ShowConfig()
    {
        var data = new
        {
            registry = new
            {
                apiUrl = options.RequireRegistryApiUrl().AbsoluteUri,
                webUrl = options.RequireRegistryWebUrl().AbsoluteUri,
            },
            runtime = new { url = options.RequireRuntimeUrl().AbsoluteUri },
            requestTimeout = options.RequestTimeout.ToString("c", CultureInfo.InvariantCulture),
        };
        output.Data(data);
        output.Line($"Registry API: {data.registry.apiUrl}");
        output.Line($"Registry web: {data.registry.webUrl}");
        output.Line($"Runtime: {data.runtime.url}");
        output.Line($"Request timeout: {data.requestTimeout}");
        return CliExitCodes.Success;
    }

    private PackageCommandHandler PackageHandler()
        => new(RequireRuntime(), output, progress, options, _archives);

    private PackageSettingsCommandHandler SettingsHandler()
        => new(RequireRuntime(), output, secretValues);

    private PackageAuthCommandHandler PackageAuthHandler()
        => new(RequireRuntime(), output, browser);

    private RegistryAuthCommandHandler AuthHandler()
        => new(RequireRuntime(), output, browser, options);

    private RegistryManagementCommandHandler ManagementHandler()
        => new(runtime, registry, output, options);

    private DeveloperPublishCommandHandler PublishHandler()
        => new(runtime, registry, _archives, output, progress, options, credentials);

    private StackCommandHandler StackHandler()
        => new(runtime, runtime, registry, _archives, output, progress, browser, options, credentials);

    private RuntimeStackCommandHandler RuntimeStackHandler()
        => new(RequireRuntime(), _archives, output, progress);

    private ICliHostClient RequireHost()
        => host ?? throw new InvalidOperationException("The selected command was not provisioned with a Host client.");

    private ICliRuntimeClient RequireRuntime()
        => runtime ?? throw new InvalidOperationException("The selected command was not provisioned with a Runtime client.");

    private IRegistryClient RequireRegistry()
        => registry ?? throw new InvalidOperationException("The selected command was not provisioned with a Registry client.");
}
