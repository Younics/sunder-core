namespace Sunder.Cli;

internal abstract record CliCommand;

internal sealed record VersionCommand : CliCommand;
internal sealed record ShowConfigCommand : CliCommand;
internal sealed record RuntimeStatusCommand : CliCommand;
internal sealed record RuntimeLifecycleCommand(RuntimeLifecycleAction Action) : CliCommand;
internal enum RuntimeResetScope
{
    All,
    StartupCache,
}

internal sealed record RuntimeResetCommand(
    bool Yes,
    RuntimeResetScope Scope = RuntimeResetScope.All) : CliCommand;
internal sealed record ListExecutionRuntimesCommand : CliCommand;
internal sealed record ShowExecutionRuntimeCommand(CliExecutionRuntimeIdentity Identity) : CliCommand;
internal sealed record EnsureExecutionRuntimeCommand(
    string RuntimeId,
    string VersionRange,
    string ModuleAbi,
    string? RegistryOrigin) : CliCommand;
internal sealed record ImportExecutionRuntimeCommand(string File) : CliCommand;
internal sealed record VerifyExecutionRuntimeCommand(CliExecutionRuntimeIdentity Identity) : CliCommand;
internal sealed record RepairExecutionRuntimeCommand(CliExecutionRuntimeIdentity Identity) : CliCommand;
internal sealed record GarbageCollectExecutionRuntimesCommand : CliCommand;
internal sealed record AuthLoginCommand : CliCommand;
internal sealed record AuthStatusCommand : CliCommand;
internal sealed record AuthLogoutCommand : CliCommand;
internal sealed record SearchPackagesCommand(string? Query, int Skip, int Take) : CliCommand;
internal sealed record PackageInfoCommand(string PackageId, string? Version) : CliCommand;
internal sealed record ListInstalledCommand : CliCommand;
internal sealed record PackageStatusCommand(string PackageId) : CliCommand;
internal sealed record InstallRegistryPackageCommand(
    string PackageId,
    string? Version,
    string? Tag,
    bool AllowDowngrade,
    bool Reinstall,
    bool ApproveSensitiveCapabilities = false,
    bool DryRun = false) : CliCommand;
internal sealed record InstallLocalPackageCommand(string File, bool AllowDowngrade, bool Reinstall, bool ApproveSensitiveCapabilities = false) : CliCommand;
internal sealed record UpdatePackagesCommand(
    string? PackageId,
    bool IncludePrerelease,
    string? RegistryOrigin,
    bool ApproveSensitiveCapabilities = false,
    bool DryRun = false) : CliCommand;
internal sealed record AdoptPackageSourceCommand(
    string PackageId,
    string RegistryOrigin,
    string? Tag,
    string? Version,
    bool IncludePrerelease,
    bool AllowDowngrade,
    bool DryRun,
    bool ApproveSensitiveCapabilities = false) : CliCommand;
internal sealed record SetPackageEnabledCommand(string PackageId, bool Enabled) : CliCommand;
internal sealed record UninstallPackageCommand(string PackageId, bool AllowCascade, bool DryRun) : CliCommand;
internal sealed record ShowPackageConfigSchemaCommand(string PackageId) : CliCommand;
internal sealed record ListPackageConfigCommand(string PackageId) : CliCommand;
internal sealed record GetPackageConfigCommand(string PackageId, string Key) : CliCommand;
internal sealed record SetPackageConfigCommand(string PackageId, string Key, string Value) : CliCommand;
internal sealed record UnsetPackageConfigCommand(string PackageId, string Key) : CliCommand;
internal sealed record PackageSecretStatusCommand(string PackageId) : CliCommand;
internal sealed record SetPackageSecretCommand(
    string PackageId,
    string Key,
    PackageSecretSource Source,
    string? EnvironmentVariable) : CliCommand;
internal sealed record UnsetPackageSecretCommand(string PackageId, string Key) : CliCommand;
internal sealed record PackageAuthStatusCommand(string PackageId) : CliCommand;
internal sealed record PackageAuthLoginCommand(string PackageId) : CliCommand;
internal sealed record PackageAuthLogoutCommand(string PackageId) : CliCommand;
internal sealed record ValidatePackageCommand(string File, string? ApiOutput = null) : CliCommand;
internal sealed record RunDevPackageCommand(string Folder, bool Watch) : CliCommand;
internal sealed record PrepareDevPackageImportsCommand(string Folder, string? RegistryOrigin,
    IReadOnlyList<string>? CandidateArchives = null, string? PlatformCatalogPath = null) : CliCommand;
internal sealed record SmokeDevPackageCommand(string Folder) : CliCommand;
internal sealed record PublishPackageCommand(
    string File,
    bool? SetLatest,
    bool DevLocal,
    RegistryCredentialSource CredentialSource = RegistryCredentialSource.Runtime) : CliCommand;
internal sealed record SetYankCommand(string PackageId, string Version, bool IsYanked) : CliCommand;
internal sealed record SetDeprecationCommand(string PackageId, string Version, string? Message) : CliCommand;
internal sealed record ListDistTagsCommand(string PackageId) : CliCommand;
internal sealed record SetDistTagCommand(string PackageId, string Tag, string? Version) : CliCommand;
internal sealed record SearchStacksCommand(string? Query, int Skip, int Take) : CliCommand;
internal sealed record StackInfoCommand(string StackId) : CliCommand;
internal sealed record DownloadStackCommand(string StackId, string? Output, bool Force) : CliCommand;
internal sealed record ImportStackCommand(string File) : CliCommand;
internal sealed record ExportStackCommand(
    string StackId,
    string? Name,
    string? Summary,
    string? Output,
    bool IncludeAll,
    bool Force) : CliCommand;
internal sealed record PublishStackCommand(
    string File,
    bool DevLocal,
    RegistryCredentialSource CredentialSource = RegistryCredentialSource.Runtime) : CliCommand;
internal sealed record SetStackVisibilityCommand(string StackId, bool Listed) : CliCommand;
internal sealed record OpenStackCommand(string StackId) : CliCommand;
internal sealed record ValidateStackCommand(string File) : CliCommand;

internal enum RuntimeLifecycleAction
{
    Start,
    Stop,
    Restart,
}

internal enum PackageSecretSource
{
    Environment,
    StandardInput,
}

internal sealed record CliExecutionRuntimeIdentity(
    string RuntimeId,
    string Version,
    int PackagingRevision,
    string Rid,
    string ModuleAbi);

[Flags]
internal enum CliConfigurationRequirements
{
    None = 0,
    RegistryApi = 1,
    RegistryWeb = 2,
    Runtime = 4,
}

[Flags]
internal enum CliClientRequirements
{
    None = 0,
    Runtime = 1,
    Registry = 2,
    Host = 4,
}

internal sealed record CliCommandPlan(
    CliConfigurationRequirements Configuration,
    CliClientRequirements Clients,
    bool UseRequestTimeout = true)
{
    public static CliCommandPlan For(CliCommand command) => command switch
    {
        VersionCommand => new(CliConfigurationRequirements.None, CliClientRequirements.None, UseRequestTimeout: false),
        ShowConfigCommand => new(
            CliConfigurationRequirements.RegistryApi | CliConfigurationRequirements.RegistryWeb | CliConfigurationRequirements.Runtime,
            CliClientRequirements.None,
            UseRequestTimeout: false),
        ValidatePackageCommand or ValidateStackCommand => new(
            CliConfigurationRequirements.None,
            CliClientRequirements.None,
            UseRequestTimeout: false),
        RunDevPackageCommand => new(
            CliConfigurationRequirements.Runtime,
            CliClientRequirements.Host | CliClientRequirements.Runtime,
            UseRequestTimeout: false),
        SmokeDevPackageCommand or PrepareDevPackageImportsCommand => new(
            CliConfigurationRequirements.Runtime,
            CliClientRequirements.Host | CliClientRequirements.Runtime),
        RuntimeStatusCommand => new(
            CliConfigurationRequirements.Runtime,
            CliClientRequirements.Host | CliClientRequirements.Runtime),
        RuntimeLifecycleCommand => new(
            CliConfigurationRequirements.Runtime,
            CliClientRequirements.Host),
        RuntimeResetCommand or ListExecutionRuntimesCommand or ShowExecutionRuntimeCommand
            or ImportExecutionRuntimeCommand or VerifyExecutionRuntimeCommand or RepairExecutionRuntimeCommand
            or GarbageCollectExecutionRuntimesCommand
            or ListInstalledCommand or PackageStatusCommand or InstallLocalPackageCommand
            or UpdatePackagesCommand or AdoptPackageSourceCommand
            or SetPackageEnabledCommand or UninstallPackageCommand
            or ShowPackageConfigSchemaCommand or ListPackageConfigCommand or GetPackageConfigCommand
            or SetPackageConfigCommand or UnsetPackageConfigCommand
            or PackageSecretStatusCommand or SetPackageSecretCommand or UnsetPackageSecretCommand
            or PackageAuthStatusCommand or PackageAuthLoginCommand or PackageAuthLogoutCommand
            or ImportStackCommand or ExportStackCommand => new(
            CliConfigurationRequirements.Runtime,
            CliClientRequirements.Runtime),
        EnsureExecutionRuntimeCommand => new(
            CliConfigurationRequirements.RegistryApi | CliConfigurationRequirements.Runtime,
            CliClientRequirements.Runtime),
        InstallRegistryPackageCommand => new(
            CliConfigurationRequirements.RegistryApi | CliConfigurationRequirements.Runtime,
            CliClientRequirements.Runtime),
        AuthLoginCommand => new(
            CliConfigurationRequirements.RegistryApi | CliConfigurationRequirements.RegistryWeb | CliConfigurationRequirements.Runtime,
            CliClientRequirements.Runtime),
        PublishPackageCommand { DevLocal: false, CredentialSource: not RegistryCredentialSource.Runtime }
            or PublishStackCommand { DevLocal: false, CredentialSource: not RegistryCredentialSource.Runtime } => new(
                CliConfigurationRequirements.RegistryApi,
                CliClientRequirements.Registry),
        AuthStatusCommand or AuthLogoutCommand or SetYankCommand or SetDeprecationCommand or SetDistTagCommand
            or PublishPackageCommand { DevLocal: false } or PublishStackCommand { DevLocal: false }
            or SetStackVisibilityCommand => new(
                CliConfigurationRequirements.RegistryApi | CliConfigurationRequirements.Runtime,
                CliClientRequirements.Runtime),
        SearchPackagesCommand or PackageInfoCommand or SearchStacksCommand or StackInfoCommand
            or DownloadStackCommand or OpenStackCommand or ListDistTagsCommand => new(
                CliConfigurationRequirements.RegistryApi,
                CliClientRequirements.Registry),
        PublishPackageCommand { DevLocal: true } or PublishStackCommand { DevLocal: true } => new(
            CliConfigurationRequirements.RegistryApi,
            CliClientRequirements.Registry),
        _ => throw new InvalidOperationException($"No execution plan is registered for {command.GetType().Name}."),
    };
}

internal sealed record CliOptionOverrides(
    string? RegistryApiUrl,
    string? RegistryWebUrl,
    string? RuntimeUrl,
    TimeSpan? RequestTimeout);

internal sealed record CliInvocation(
    CliCommand Command,
    string CommandPath,
    bool Json,
    CliOptionOverrides Overrides);
