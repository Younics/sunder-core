namespace Sunder.Cli;

internal sealed class CliCommandDispatcher
{
    private readonly SystemCommandHandler _system;
    private readonly RuntimeResetCommandHandler _runtimeReset;
    private readonly PackageCommandHandler _packages;
    private readonly RegistryBrowseCommandHandler _browse;
    private readonly RegistryAuthCommandHandler _auth;
    private readonly RegistryManagementCommandHandler _management;
    private readonly ValidationCommandHandler _validation;
    private readonly DeveloperPublishCommandHandler _publish;
    private readonly StackCommandHandler _stacks;

    public CliCommandDispatcher(
        ICliRuntimeClient runtime,
        IRegistryClient registry,
        CliOutput output,
        ICliProgress progress,
        IBrowserLauncher browser,
        CliOptions options)
    {
        var archives = new ArchiveValidationService();
        _system = new(runtime, output);
        _runtimeReset = new(runtime, output, options.RuntimeUrl);
        _packages = new(runtime, output, progress, options, archives);
        _browse = new(registry, output);
        _auth = new(runtime, output, browser, options);
        _management = new(runtime, registry, output, options);
        _validation = new(archives, output);
        _publish = new(runtime, registry, archives, output, progress, options);
        _stacks = new(runtime, runtime, registry, archives, output, progress, options);
    }

    public Task<int> ExecuteAsync(CliCommand command, CancellationToken token) => command switch
    {
        SystemStatusCommand value => _system.ExecuteAsync(value, token),
        RuntimeResetCommand value => _runtimeReset.ExecuteAsync(value, token),
        ListInstalledCommand value => _packages.ExecuteAsync(value, token),
        InstallRegistryPackageCommand value => _packages.ExecuteAsync(value, token),
        InstallLocalPackageCommand value => _packages.ExecuteAsync(value, token),
        UpdatePackagesCommand value => _packages.ExecuteAsync(value, token),
        SearchPackagesCommand value => _browse.ExecuteAsync(value, token),
        PackageInfoCommand value => _browse.ExecuteAsync(value, token),
        AuthLoginCommand value => _auth.ExecuteAsync(value, token),
        AuthStatusCommand value => _auth.ExecuteAsync(value, token),
        AuthLogoutCommand value => _auth.ExecuteAsync(value, token),
        SetYankCommand value => _management.ExecuteAsync(value, token),
        SetDeprecationCommand value => _management.ExecuteAsync(value, token),
        ListDistTagsCommand value => _management.ExecuteAsync(value, token),
        SetDistTagCommand value => _management.ExecuteAsync(value, token),
        ValidatePackageCommand value => _validation.ExecuteAsync(value, token),
        PublishPackageCommand value => _publish.ExecuteAsync(value, token),
        SearchStacksCommand value => _stacks.ExecuteAsync(value, token),
        StackInfoCommand value => _stacks.ExecuteAsync(value, token),
        DownloadStackCommand value => _stacks.ExecuteAsync(value, token),
        PublishStackCommand value => _stacks.ExecuteAsync(value, token),
        UpdateStackCommand value => _stacks.ExecuteAsync(value, token),
        DeleteStackCommand value => _stacks.ExecuteAsync(value, token),
        UseStackCommand value => _stacks.ExecuteAsync(value, token),
        ValidateStackCommand value => _stacks.ExecuteAsync(value, token),
        _ => throw new InvalidOperationException($"No handler is registered for {command.GetType().Name}.")
    };
}
