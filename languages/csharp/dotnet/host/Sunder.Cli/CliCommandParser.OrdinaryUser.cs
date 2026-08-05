using System.CommandLine;

namespace Sunder.Cli;

internal sealed partial class CliCommandParser
{
    private void AddRuntimeLifecycleCommand(Command runtime, string name, RuntimeLifecycleAction action)
        => Bind(
            Leaf(runtime, name, $"{char.ToUpperInvariant(name[0])}{name[1..]} the local Runtime and wait for completion."),
            $"runtime {name}",
            _ => new RuntimeLifecycleCommand(action));

    private void AddPackageStatusCommand(Command package)
    {
        var status = Leaf(package, "status", "Show installed and current Runtime-session status for a package.");
        var packageId = PackageIdArgument();
        status.Arguments.Add(packageId);
        Bind(status, "package status", result => new PackageStatusCommand(result.GetRequiredValue(packageId)));
    }

    private void BuildPackageUpdate(Command package)
    {
        var update = Leaf(package, "update", "Update one installed package or all installed packages.");
        var packageId = PackageIdArgument(required: false);
        var all = Flag("--all", "Update all installed packages.");
        var prerelease = Flag("--include-prerelease", "Include prerelease versions.");
        var registryOrigin = ValueOption("--registry-origin", "Only update packages recorded from this Registry origin.", "url");
        update.Arguments.Add(packageId);
        update.Options.Add(all);
        update.Options.Add(prerelease);
        update.Options.Add(registryOrigin);
        update.Validators.Add(result =>
        {
            var hasPackageId = result.GetResult(packageId)?.Tokens.Count > 0;
            if (result.GetValue(all) == hasPackageId)
                result.AddError("Specify exactly one package id or '--all'.");
        });
        Bind(update, "package update", result => new UpdatePackagesCommand(
            result.GetValue(packageId),
            result.GetValue(prerelease),
            result.GetValue(registryOrigin)));
    }

    private void BuildPackageSourceCommands(Command package)
    {
        var source = Group(package, "source", "Inspect or deliberately adopt package source ownership.");
        var adopt = Leaf(source, "adopt", "Adopt a Registry source and explicit version-selection policy for an installed package.");
        var packageId = PackageIdArgument();
        var registryOrigin = ValueOption("--registry-origin", "Registry origin to adopt.", "url");
        var version = SemanticVersionOption("--version", "Pin the package to one exact Registry version.");
        var tag = ValueOption("--tag", "Follow one Registry dist tag.", "tag");
        var prerelease = Flag("--include-prerelease", "Include prerelease versions when following a tag.");
        var allowDowngrade = Flag("--allow-downgrade", "Allow the adopted policy to select an older version.");
        var yes = Flag("--yes", "Confirm replacement and source adoption.");
        var dryRun = Flag("--dry-run", "Resolve and show the adoption plan without changing installed state.");
        adopt.Arguments.Add(packageId);
        adopt.Options.Add(registryOrigin);
        adopt.Options.Add(version);
        adopt.Options.Add(tag);
        adopt.Options.Add(prerelease);
        adopt.Options.Add(allowDowngrade);
        adopt.Options.Add(yes);
        adopt.Options.Add(dryRun);
        adopt.Validators.Add(result =>
        {
            var origin = result.GetResult(registryOrigin)?.Tokens.FirstOrDefault()?.Value;
            var exactVersion = result.GetResult(version)?.Tokens.FirstOrDefault()?.Value;
            var selectedTag = result.GetResult(tag)?.Tokens.FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(origin))
                result.AddError("Option '--registry-origin <url>' is required.");
            if ((exactVersion is null) == (selectedTag is null))
                result.AddError("Specify exactly one of '--version' or '--tag'.");
            if (exactVersion is not null && result.GetValue(prerelease))
                result.AddError("Option '--include-prerelease' is only valid with '--tag'.");
            if (result.GetValue(yes) == result.GetValue(dryRun))
                result.AddError("Specify exactly one of '--yes' or '--dry-run'.");
        });
        Bind(adopt, "package source adopt", result => new AdoptPackageSourceCommand(
            result.GetRequiredValue(packageId),
            result.GetValue(registryOrigin)!,
            result.GetValue(tag),
            result.GetValue(version),
            result.GetValue(prerelease),
            result.GetValue(allowDowngrade),
            result.GetValue(dryRun)));
    }

    private void BuildPackageConfigCommands(Command package)
    {
        var config = Group(package, "config", "Inspect and manage declared non-secret package configuration.");
        AddPackageIdLeaf(config, "schema", "Show the package's declared configuration schema.",
            (id, _) => new ShowPackageConfigSchemaCommand(id));
        AddPackageIdLeaf(config, "list", "List declared non-secret configuration and effective values.",
            (id, _) => new ListPackageConfigCommand(id));
        AddPackageKeyLeaf(config, "get", "Show one declared non-secret configuration value.",
            (id, key, _) => new GetPackageConfigCommand(id, key));

        var set = Leaf(config, "set", "Set one declared non-secret configuration value.");
        var setPackageId = PackageIdArgument();
        var setKey = RequiredTextArgument("key", "Declared configuration key.");
        var setValue = RequiredTextArgument("value", "Configuration value. Use '--' before option-shaped values.");
        set.Arguments.Add(setPackageId);
        set.Arguments.Add(setKey);
        set.Arguments.Add(setValue);
        Bind(set, "package config set", result => new SetPackageConfigCommand(
            result.GetRequiredValue(setPackageId),
            result.GetRequiredValue(setKey),
            result.GetRequiredValue(setValue)));

        AddPackageKeyLeaf(config, "unset", "Remove one stored non-secret configuration value.",
            (id, key, _) => new UnsetPackageConfigCommand(id, key));
    }

    private void BuildPackageSecretCommands(Command package)
    {
        var secret = Group(package, "secret", "Inspect presence and safely manage declared package secrets.");
        AddPackageIdLeaf(secret, "status", "Show whether declared package secrets are configured without reading values.",
            (id, _) => new PackageSecretStatusCommand(id));

        var set = Leaf(secret, "set", "Set a declared package secret from an environment variable or redirected stdin.");
        var packageId = PackageIdArgument();
        var key = RequiredTextArgument("key", "Declared secret key.");
        var fromEnvironment = ValueOption("--from-env", "Read the value from this environment variable.", "name");
        var stdin = Flag("--stdin", "Read the value from redirected standard input.");
        set.Arguments.Add(packageId);
        set.Arguments.Add(key);
        set.Options.Add(fromEnvironment);
        set.Options.Add(stdin);
        set.Validators.Add(result =>
        {
            if ((result.GetValue(fromEnvironment) is not null) == result.GetValue(stdin))
                result.AddError("Specify exactly one of '--from-env <name>' or '--stdin'.");
        });
        Bind(set, "package secret set", result => new SetPackageSecretCommand(
            result.GetRequiredValue(packageId),
            result.GetRequiredValue(key),
            result.GetValue(stdin) ? PackageSecretSource.StandardInput : PackageSecretSource.Environment,
            result.GetValue(fromEnvironment)));

        AddPackageKeyLeaf(secret, "unset", "Remove one stored package secret without reading it.",
            (id, secretKey, _) => new UnsetPackageSecretCommand(id, secretKey));
    }

    private void BuildPackageAuthCommands(Command package)
    {
        var auth = Group(package, "auth", "Manage a package's declared browser authentication capability.");
        AddPackageIdLeaf(auth, "status", "Show package authentication status.",
            (id, _) => new PackageAuthStatusCommand(id));
        AddPackageIdLeaf(auth, "login", "Authorize a package in a browser and wait for completion.",
            (id, _) => new PackageAuthLoginCommand(id));
        AddPackageIdLeaf(auth, "logout", "Disconnect package authentication.",
            (id, _) => new PackageAuthLogoutCommand(id));
    }

    private void AddStackImportCommand(Command stack)
    {
        var import = Leaf(stack, "import", "Validate and preview a local Stack import without applying changes.");
        var file = RequiredValueOption("--file", "Local .sunderstack archive.", "path");
        var dryRun = Flag("--dry-run", "Preview default selections without changing package state.");
        import.Options.Add(file);
        import.Options.Add(dryRun);
        import.Validators.Add(result =>
        {
            if (!result.GetValue(dryRun))
                result.AddError("Stack import apply is not available in the noninteractive CLI. Specify '--dry-run'.");
        });
        Bind(import, "stack import", result => new ImportStackCommand(result.GetRequiredValue(file)!));
    }

    private void AddStackExportCommand(Command stack)
    {
        var export = Leaf(stack, "export", "Export default-selected Runtime Stack content to a local archive.");
        var stackId = RequiredTextArgument("stack-id", "Lowercase Stack id.");
        var name = ValueOption("--name", "Stack display name. Defaults to the Stack id.", "name");
        var summary = ValueOption("--summary", "Optional Stack summary.", "text");
        var output = ValueOption("--output", "Exact destination file.", "path", aliases: ["-o"]);
        var all = Flag("--all", "Export all discovered items and details instead of defaults.");
        var force = Flag("--force", "Replace an existing destination file.");
        export.Arguments.Add(stackId);
        export.Options.Add(name);
        export.Options.Add(summary);
        export.Options.Add(output);
        export.Options.Add(all);
        export.Options.Add(force);
        Bind(export, "stack export", result => new ExportStackCommand(
            result.GetRequiredValue(stackId),
            result.GetValue(name),
            result.GetValue(summary),
            result.GetValue(output),
            result.GetValue(all),
            result.GetValue(force)));
    }

    private void AddPackageIdLeaf(
        Command parent,
        string name,
        string description,
        Func<string, ParseResult, CliCommand> factory)
    {
        var command = Leaf(parent, name, description);
        var packageId = PackageIdArgument();
        command.Arguments.Add(packageId);
        Bind(command, $"{_helpPaths[parent]} {name}", result => factory(result.GetRequiredValue(packageId), result));
    }

    private void AddPackageKeyLeaf(
        Command parent,
        string name,
        string description,
        Func<string, string, ParseResult, CliCommand> factory)
    {
        var command = Leaf(parent, name, description);
        var packageId = PackageIdArgument();
        var key = RequiredTextArgument("key", "Declared configuration key.");
        command.Arguments.Add(packageId);
        command.Arguments.Add(key);
        Bind(command, $"{_helpPaths[parent]} {name}", result => factory(
            result.GetRequiredValue(packageId),
            result.GetRequiredValue(key),
            result));
    }
}
