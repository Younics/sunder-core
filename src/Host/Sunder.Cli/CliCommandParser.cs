using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Globalization;

namespace Sunder.Cli;

internal sealed partial class CliCommandParser
{
    private const int MaximumPageSize = 100;
    private readonly Dictionary<Command, Func<ParseResult, CliCommand>> _bindings = [];
    private readonly Dictionary<Command, string> _paths = [];
    private readonly Dictionary<Command, string> _helpPaths = [];
    private readonly HelpOption _helpOption = new("--help", ["-h"])
    {
        Description = "Show help and usage information.",
        Recursive = true,
    };
    private readonly Option<bool> _jsonOption = Flag("--json", "Emit one versioned JSON result.", recursive: true);
    private readonly Option<string?> _registryApiUrlOption = ValueOption("--registry-api-url", "Registry API base URL.", "url", recursive: true);
    private readonly Option<string?> _registryWebUrlOption = ValueOption("--registry-web-url", "Registry web URL used for browser sign-in.", "url", recursive: true);
    private readonly Option<string?> _runtimeUrlOption = ValueOption("--runtime-url", "Authenticated local Runtime base URL.", "url", recursive: true);
    private readonly Option<TimeSpan?> _timeoutOption;
    private readonly ParserConfiguration _parserConfiguration = new()
    {
        EnablePosixBundling = false,
        ResponseFileTokenReplacer = null,
    };

    public CliCommandParser()
    {
        _timeoutOption = new Option<TimeSpan?>("--timeout")
        {
            Description = "Request timeout, such as 15m, 900s, 900, or 00:15:00.",
            HelpName = "duration",
            Recursive = true,
            Arity = ArgumentArity.ExactlyOne,
            CustomParser = result =>
            {
                if (result.Tokens.Count != 1)
                {
                    result.AddError("Option '--timeout' requires one value.");
                    return null;
                }

                if (CliOptions.TryParseTimeout(result.Tokens[0].Value, out var timeout, out var error))
                {
                    return timeout;
                }

                result.AddError(error!);
                return null;
            },
        };
        RejectRepeated(_timeoutOption);

        Root = CreateCommand("sunder", "Manage the Sunder Runtime, packages, Stacks, Registry, and development artifacts.");
        _helpPaths.Add(Root, string.Empty);
        Root.Options.Add(_helpOption);
        Root.Options.Add(_jsonOption);
        Root.Options.Add(_registryApiUrlOption);
        Root.Options.Add(_registryWebUrlOption);
        Root.Options.Add(_runtimeUrlOption);
        Root.Options.Add(_timeoutOption);

        BuildRuntimeCommands();
        BuildPackageCommands();
        BuildStackCommands();
        BuildRegistryCommands();
        BuildDeveloperCommands();
        BuildConfigCommands();
        Bind(Leaf(Root, "version", "Show the Sunder CLI version."), "version", _ => new VersionCommand());
    }

    public Command Root { get; }

    public CliCommandParseResult Parse(IReadOnlyList<string> arguments)
    {
        var parseResult = Root.Parse(arguments, _parserConfiguration);
        var selected = parseResult.CommandResult.Command;
        var json = parseResult.GetResult(_jsonOption) is OptionResult { Implicit: false };
        var helpRequested = parseResult.GetResult(_helpOption) is OptionResult { Implicit: false };
        if (helpRequested || arguments.Count == 0)
        {
            return new CliCommandParseResult(null, [], selected, ShowHelp: true, json);
        }

        var unknownOptions = FindUnknownOptions(arguments, selected);
        if (unknownOptions.Count > 0)
        {
            return new CliCommandParseResult(null, unknownOptions, selected, ShowHelp: false, json);
        }

        var errors = parseResult.Errors
            .Select(error => error.Message)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (errors.Length > 0)
        {
            return new CliCommandParseResult(null, errors, selected, ShowHelp: false, json);
        }
        if (!_bindings.ContainsKey(selected))
        {
            return new CliCommandParseResult(null, [], selected, ShowHelp: true, json);
        }

        var command = _bindings[selected](parseResult);
        var invocation = new CliInvocation(
            command,
            _paths[selected],
            json,
            new CliOptionOverrides(
                parseResult.GetValue(_registryApiUrlOption),
                parseResult.GetValue(_registryWebUrlOption),
                parseResult.GetValue(_runtimeUrlOption),
                parseResult.GetValue(_timeoutOption)));
        return new CliCommandParseResult(invocation, [], selected, ShowHelp: false, json);
    }

    public string FormatHelp(Command command)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var path = GetPath(command);
        var arguments = string.IsNullOrEmpty(path)
            ? new[] { "--help" }
            : [.. path.Split(' ', StringSplitOptions.RemoveEmptyEntries), "--help"];
        var result = Root.Parse(arguments, _parserConfiguration);
        result.Invoke(new InvocationConfiguration
        {
            EnableDefaultExceptionHandler = false,
            Output = writer,
            Error = writer,
        });
        return writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
    }

    public string GetPath(Command command) => _helpPaths[command];

    private void BuildRuntimeCommands()
    {
        var runtime = Group(Root, "runtime", "Inspect and manage the local Sunder Runtime.");
        Bind(Leaf(runtime, "status", "Show authenticated Runtime status."), "runtime status", _ => new RuntimeStatusCommand());
        AddRuntimeLifecycleCommand(runtime, "start", RuntimeLifecycleAction.Start);
        AddRuntimeLifecycleCommand(runtime, "stop", RuntimeLifecycleAction.Stop);
        AddRuntimeLifecycleCommand(runtime, "restart", RuntimeLifecycleAction.Restart);

        var reset = Leaf(runtime, "reset", "Drain the Runtime and delete validated local Runtime V1 state.");
        var yes = Flag("--yes", "Confirm the destructive reset.");
        reset.Options.Add(yes);
        reset.Validators.Add(result =>
        {
            if (!result.GetValue(yes)) result.AddError("Runtime reset is destructive. Specify '--yes' to continue.");
        });
        Bind(reset, "runtime reset", result => new RuntimeResetCommand(result.GetValue(yes)));
    }

    private void BuildPackageCommands()
    {
        var package = Group(Root, "package", "Browse, install, list, and update packages.");
        AddPagedSearch(package, "search", "Search Registry packages.", "package search", (query, skip, take) => new SearchPackagesCommand(query, skip, take));

        var info = Leaf(package, "info", "Show Registry package details.");
        var infoId = PackageIdArgument();
        var infoVersion = SemanticVersionOption("--version", "Show one exact package version.");
        info.Arguments.Add(infoId);
        info.Options.Add(infoVersion);
        Bind(info, "package info", result => new PackageInfoCommand(result.GetRequiredValue(infoId), result.GetValue(infoVersion)));

        Bind(Leaf(package, "list", "List packages installed in the local Runtime."), "package list", _ => new ListInstalledCommand());
        AddPackageStatusCommand(package);
        BuildPackageInstall(package);
        BuildPackageUpdate(package);
        BuildPackageSourceCommands(package);
        AddPackageEnabledCommand(package, "enable", enabled: true);
        AddPackageEnabledCommand(package, "disable", enabled: false);
        BuildPackageUninstall(package);
        BuildPackageConfigCommands(package);
        BuildPackageSecretCommands(package);
        BuildPackageAuthCommands(package);
    }

    private void BuildPackageInstall(Command package)
    {
        var install = Leaf(package, "install", "Install a Registry package or a local package archive.");
        var packageId = PackageIdArgument(required: false);
        var file = ValueOption("--file", "Install a local .sunderpkg archive.", "path");
        var version = SemanticVersionOption("--version", "Install one exact Registry version.");
        var tag = ValueOption("--tag", "Install a Registry dist tag. Defaults to latest.", "tag");
        var allowDowngrade = Flag("--allow-downgrade", "Allow replacing a newer installed version.");
        var reinstall = Flag("--reinstall", "Allow reinstalling the same version.");
        install.Arguments.Add(packageId);
        install.Options.Add(file);
        install.Options.Add(version);
        install.Options.Add(tag);
        install.Options.Add(allowDowngrade);
        install.Options.Add(reinstall);
        install.Validators.Add(result =>
        {
            var id = result.GetResult(packageId)?.Tokens.FirstOrDefault()?.Value;
            var path = result.GetResult(file)?.Tokens.FirstOrDefault()?.Value;
            var exactVersion = result.GetResult(version)?.Tokens.FirstOrDefault()?.Value;
            var explicitTag = result.GetResult(tag)?.Tokens.FirstOrDefault()?.Value;
            if ((id is null) == (path is null))
                result.AddError("Specify exactly one package id or '--file <path>'.");
            if (exactVersion is not null && explicitTag is not null)
                result.AddError("Options '--version' and '--tag' cannot be used together.");
            if (path is not null && (exactVersion is not null || explicitTag is not null))
                result.AddError("Options '--version' and '--tag' are only valid for Registry installs.");
        });
        Bind(install, "package install", result =>
        {
            var path = result.GetValue(file);
            if (path is not null)
                return new InstallLocalPackageCommand(path, result.GetValue(allowDowngrade), result.GetValue(reinstall));
            var exactVersion = result.GetValue(version);
            return new InstallRegistryPackageCommand(
                result.GetRequiredValue(packageId),
                exactVersion,
                exactVersion is null ? result.GetValue(tag) ?? "latest" : null,
                result.GetValue(allowDowngrade),
                result.GetValue(reinstall));
        });
    }

    private void AddPackageEnabledCommand(Command package, string name, bool enabled)
    {
        var command = Leaf(package, name, enabled
            ? "Enable an installed package."
            : "Disable an installed package.");
        var packageId = PackageIdArgument();
        command.Arguments.Add(packageId);
        Bind(command, $"package {name}", result => new SetPackageEnabledCommand(
            result.GetRequiredValue(packageId),
            enabled));
    }

    private void BuildPackageUninstall(Command package)
    {
        var uninstall = Leaf(package, "uninstall", "Plan or uninstall an installed package.");
        var packageId = PackageIdArgument();
        var cascade = Flag("--cascade", "Allow removal of installed packages that depend on this package.");
        var yes = Flag("--yes", "Apply the exact Runtime-generated uninstall plan.");
        var dryRun = Flag("--dry-run", "Show the uninstall plan without changing installed state.");
        uninstall.Arguments.Add(packageId);
        uninstall.Options.Add(cascade);
        uninstall.Options.Add(yes);
        uninstall.Options.Add(dryRun);
        uninstall.Validators.Add(result =>
        {
            if (result.GetValue(yes) == result.GetValue(dryRun))
                result.AddError("Specify exactly one of '--yes' or '--dry-run'.");
            if (result.GetValue(dryRun) && result.GetValue(cascade))
                result.AddError("Option '--cascade' is only valid when applying an uninstall with '--yes'.");
        });
        Bind(uninstall, "package uninstall", result => new UninstallPackageCommand(
            result.GetRequiredValue(packageId),
            result.GetValue(cascade),
            result.GetValue(dryRun)));
    }

    private void BuildStackCommands()
    {
        var stack = Group(Root, "stack", "Browse, download, and open Sunder Stacks.");
        AddPagedSearch(stack, "search", "Search Registry Stacks.", "stack search", (query, skip, take) => new SearchStacksCommand(query, skip, take));
        AddStackImportCommand(stack);
        AddStackExportCommand(stack);

        var info = Leaf(stack, "info", "Show Registry Stack details.");
        var infoId = RequiredTextArgument("stack-id", "Registry Stack id.");
        info.Arguments.Add(infoId);
        Bind(info, "stack info", result => new StackInfoCommand(result.GetRequiredValue(infoId)));

        var download = Leaf(stack, "download", "Download and verify a Registry Stack archive.");
        var downloadId = RequiredTextArgument("stack-id", "Registry Stack id.");
        var output = ValueOption("--output", "Exact destination file.", "path", aliases: ["-o"]);
        var force = Flag("--force", "Replace an existing destination file.");
        download.Arguments.Add(downloadId);
        download.Options.Add(output);
        download.Options.Add(force);
        Bind(download, "stack download", result => new DownloadStackCommand(
            result.GetRequiredValue(downloadId), result.GetValue(output), result.GetValue(force)));

        var open = Leaf(stack, "open", "Open a Registry Stack in Sunder App.");
        var openId = RequiredTextArgument("stack-id", "Registry Stack id.");
        open.Arguments.Add(openId);
        Bind(open, "stack open", result => new OpenStackCommand(result.GetRequiredValue(openId)));
    }

    private void BuildRegistryCommands()
    {
        var registry = Group(Root, "registry", "Sign in and perform remote Registry management operations.");
        var auth = Group(registry, "auth", "Manage the Runtime-owned human Registry credential.");
        Bind(Leaf(auth, "login", "Sign in to the configured Registry in a browser."), "registry auth login", _ => new AuthLoginCommand());
        Bind(Leaf(auth, "status", "Show Registry sign-in status."), "registry auth status", _ => new AuthStatusCommand());
        Bind(Leaf(auth, "logout", "Revoke and remove the configured Registry credential."), "registry auth logout", _ => new AuthLogoutCommand());

        BuildRegistryPackageCommands(registry);
        BuildRegistryStackCommands(registry);
    }

    private void BuildRegistryPackageCommands(Command registry)
    {
        var package = Group(registry, "package", "Publish and manage remote Registry packages.");
        AddPackagePublish(package, "registry package publish", devLocal: false);

        AddYankCommand(package, "yank", isYanked: true);
        AddYankCommand(package, "unyank", isYanked: false);
        AddDeprecationCommand(package, "deprecate", clear: false);
        AddDeprecationCommand(package, "undeprecate", clear: true);

        var tag = Group(package, "tag", "List and manage package dist tags.");
        var list = Leaf(tag, "list", "List package dist tags.");
        var listId = PackageIdArgument();
        list.Arguments.Add(listId);
        Bind(list, "registry package tag list", result => new ListDistTagsCommand(result.GetRequiredValue(listId)));

        var set = Leaf(tag, "set", "Set a package dist tag to an exact version.");
        var setId = PackageIdArgument();
        var setTag = RequiredTextArgument("tag", "Dist tag.");
        var setVersion = SemanticVersionArgument();
        set.Arguments.Add(setId);
        set.Arguments.Add(setTag);
        set.Arguments.Add(setVersion);
        Bind(set, "registry package tag set", result => new SetDistTagCommand(
            result.GetRequiredValue(setId), result.GetRequiredValue(setTag), result.GetRequiredValue(setVersion)));

        var delete = Leaf(tag, "delete", "Delete a package dist tag.");
        var deleteId = PackageIdArgument();
        var deleteTag = RequiredTextArgument("tag", "Dist tag.");
        delete.Arguments.Add(deleteId);
        delete.Arguments.Add(deleteTag);
        Bind(delete, "registry package tag delete", result => new SetDistTagCommand(
            result.GetRequiredValue(deleteId), result.GetRequiredValue(deleteTag), null));
    }

    private void BuildRegistryStackCommands(Command registry)
    {
        var stack = Group(registry, "stack", "Publish and delete remote Registry Stacks.");
        AddStackPublish(stack, "registry stack publish", devLocal: false);

        var delete = Leaf(stack, "delete", "Delete a remote Registry Stack.");
        var deleteId = RequiredTextArgument("stack-id", "Registry Stack id.");
        delete.Arguments.Add(deleteId);
        Bind(delete, "registry stack delete", result => new DeleteStackCommand(result.GetRequiredValue(deleteId)));
    }

    private void BuildDeveloperCommands()
    {
        var dev = Group(Root, "dev", "Validate local artifacts and use development-only services.");
        var package = Group(dev, "package", "Work with local package artifacts.");
        var validate = Leaf(package, "validate", "Validate and inspect a local .sunderpkg archive.");
        var packageFile = RequiredTextArgument("file", "Local .sunderpkg archive.");
        validate.Arguments.Add(packageFile);
        Bind(validate, "dev package validate", result => new ValidatePackageCommand(result.GetRequiredValue(packageFile)));

        var stack = Group(dev, "stack", "Work with local Stack artifacts.");
        var inspect = Leaf(stack, "inspect", "Validate and inspect a local .sunderstack archive.");
        var stackFile = RequiredTextArgument("file", "Local .sunderstack archive.");
        inspect.Arguments.Add(stackFile);
        Bind(inspect, "dev stack inspect", result => new ValidateStackCommand(result.GetRequiredValue(stackFile)));

        var registry = Group(dev, "registry", "Use loopback-only development Registry operations.");
        var registryPackage = Group(registry, "package", "Publish local packages to a development Registry.");
        AddPackagePublish(registryPackage, "dev registry package publish-local", devLocal: true, commandName: "publish-local");
        var registryStack = Group(registry, "stack", "Publish local Stacks to a development Registry.");
        AddStackPublish(registryStack, "dev registry stack publish-local", devLocal: true, commandName: "publish-local");
    }

    private void BuildConfigCommands()
    {
        var config = Group(Root, "config", "Inspect effective Sunder CLI endpoint configuration.");
        Bind(Leaf(config, "show", "Show effective Registry, Runtime, and timeout settings."), "config show", _ => new ShowConfigCommand());
    }

    private void AddPackagePublish(Command parent, string path, bool devLocal, string commandName = "publish")
    {
        var publish = Leaf(parent, commandName, devLocal
            ? "Publish a package to a loopback development Registry."
            : "Publish a package with Runtime-managed human auth or a scoped automation credential.");
        var file = RequiredValueOption("--file", "Local .sunderpkg archive.", "path");
        var noLatest = Flag("--no-latest", "Do not set or promote the latest dist tag.");
        var setLatest = Flag("--set-latest", "Set or promote the latest dist tag, including for a prerelease.");
        publish.Options.Add(file);
        publish.Options.Add(noLatest);
        publish.Options.Add(setLatest);
        Option<string?>? credentialSource = null;
        if (!devLocal)
        {
            credentialSource = ValueOption(
                "--credential-source",
                "Credential source: runtime, environment, or stdin. Defaults to runtime.",
                "source");
            credentialSource.Validators.Add(result => ValidateCredentialSource(result.Tokens, result.AddError));
            publish.Options.Add(credentialSource);
        }
        publish.Validators.Add(result =>
        {
            if (result.GetValue(noLatest) && result.GetValue(setLatest))
                result.AddError("Options '--set-latest' and '--no-latest' cannot be used together.");
        });
        Bind(publish, path, result => new PublishPackageCommand(
            result.GetRequiredValue(file)!,
            result.GetValue(setLatest) ? true : result.GetValue(noLatest) ? false : null,
            devLocal,
            devLocal ? RegistryCredentialSource.Runtime : ParseCredentialSource(result.GetValue(credentialSource!))));
    }

    private void AddStackPublish(Command parent, string path, bool devLocal, string commandName = "publish")
    {
        var publish = Leaf(parent, commandName, devLocal
            ? "Publish a Stack to a loopback development Registry."
            : "Publish a Stack with Runtime-managed human auth or a scoped automation credential.");
        var file = RequiredValueOption("--file", "Local .sunderstack archive.", "path");
        publish.Options.Add(file);
        Option<string?>? credentialSource = null;
        if (!devLocal)
        {
            credentialSource = ValueOption(
                "--credential-source",
                "Credential source: runtime, environment, or stdin. Defaults to runtime.",
                "source");
            credentialSource.Validators.Add(result => ValidateCredentialSource(result.Tokens, result.AddError));
            publish.Options.Add(credentialSource);
        }
        Bind(publish, path, result => new PublishStackCommand(
            result.GetRequiredValue(file)!,
            devLocal,
            devLocal ? RegistryCredentialSource.Runtime : ParseCredentialSource(result.GetValue(credentialSource!))));
    }

    private static void ValidateCredentialSource(IReadOnlyList<Token> tokens, Action<string> addError)
    {
        if (tokens.Count == 0) return;
        if (!RegistryCredentialSourceParser.TryParse(tokens[0].Value, out _))
            addError("Option '--credential-source' must be one of: runtime, environment, stdin.");
    }

    private static RegistryCredentialSource ParseCredentialSource(string? value)
        => value is null
            ? RegistryCredentialSource.Runtime
            : RegistryCredentialSourceParser.TryParse(value, out var source)
                ? source
                : throw new CliUsageException("Invalid Registry credential source.");

    private void AddYankCommand(Command package, string name, bool isYanked)
    {
        var command = Leaf(package, name, isYanked ? "Yank an exact package version." : "Restore a yanked package version.");
        var packageId = PackageIdArgument();
        var version = SemanticVersionArgument();
        command.Arguments.Add(packageId);
        command.Arguments.Add(version);
        Bind(command, $"registry package {name}", result => new SetYankCommand(
            result.GetRequiredValue(packageId), result.GetRequiredValue(version), isYanked));
    }

    private void AddDeprecationCommand(Command package, string name, bool clear)
    {
        var command = Leaf(package, name, clear ? "Clear a package version deprecation." : "Deprecate an exact package version.");
        var packageId = PackageIdArgument();
        var version = SemanticVersionArgument();
        command.Arguments.Add(packageId);
        command.Arguments.Add(version);
        Option<string?>? message = null;
        if (!clear)
        {
            message = RequiredValueOption("--message", "Deprecation message.", "text");
            command.Options.Add(message);
        }
        Bind(command, $"registry package {name}", result => new SetDeprecationCommand(
            result.GetRequiredValue(packageId), result.GetRequiredValue(version), message is null ? null : result.GetRequiredValue(message)));
    }

}

internal sealed record CliCommandParseResult(
    CliInvocation? Invocation,
    IReadOnlyList<string> Errors,
    Command HelpScope,
    bool ShowHelp,
    bool Json);
