namespace Sunder.Cli;

internal abstract record CliCommand;

internal sealed record HelpCommand : CliCommand;
internal sealed record SystemStatusCommand : CliCommand;
internal sealed record RuntimeResetCommand(bool Yes) : CliCommand;
internal sealed record AuthLoginCommand : CliCommand;
internal sealed record AuthStatusCommand : CliCommand;
internal sealed record AuthLogoutCommand : CliCommand;
internal sealed record SearchPackagesCommand(string? Query, int Skip, int Take) : CliCommand;
internal sealed record PackageInfoCommand(string PackageId, string? Version) : CliCommand;
internal sealed record ListInstalledCommand : CliCommand;
internal sealed record InstallRegistryPackageCommand(string PackageId, string? Version, string? Tag, bool AllowDowngrade, bool Reinstall) : CliCommand;
internal sealed record InstallLocalPackageCommand(string File, bool AllowDowngrade, bool Reinstall) : CliCommand;
internal sealed record UpdatePackagesCommand(string? PackageId, bool IncludePrerelease) : CliCommand;
internal sealed record ValidatePackageCommand(string File) : CliCommand;
internal sealed record PublishPackageCommand(string File, bool SetLatest, bool DevLocal) : CliCommand;
internal sealed record SetYankCommand(string PackageId, string Version, bool IsYanked) : CliCommand;
internal sealed record SetDeprecationCommand(string PackageId, string Version, string? Message) : CliCommand;
internal sealed record ListDistTagsCommand(string PackageId) : CliCommand;
internal sealed record SetDistTagCommand(string PackageId, string Tag, string? Version) : CliCommand;
internal sealed record SearchStacksCommand(string? Query, int Skip, int Take) : CliCommand;
internal sealed record StackInfoCommand(string StackId) : CliCommand;
internal sealed record DownloadStackCommand(string StackId, string? Output, bool Force) : CliCommand;
internal sealed record PublishStackCommand(string File, bool DevLocal) : CliCommand;
internal sealed record UpdateStackCommand(string StackId, string File) : CliCommand;
internal sealed record DeleteStackCommand(string StackId) : CliCommand;
internal sealed record UseStackCommand(string StackId) : CliCommand;
internal sealed record ValidateStackCommand(string File) : CliCommand;

internal sealed record CliInvocation(CliCommand Command, bool Json, CliOptions? Options);
