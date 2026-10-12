using System.CommandLine;

namespace Sunder.Cli;

internal sealed partial class CliCommandParser
{
    private void BuildRuntimeCommands()
    {
        var runtime = Group(Root, "runtime", "Inspect and manage the local Sunder Runtime.");
        Bind(Leaf(runtime, "status", "Show authenticated Runtime status."), "runtime status", _ => new RuntimeStatusCommand());
        AddRuntimeLifecycleCommand(runtime, "start", RuntimeLifecycleAction.Start);
        AddRuntimeLifecycleCommand(runtime, "stop", RuntimeLifecycleAction.Stop);
        AddRuntimeLifecycleCommand(runtime, "restart", RuntimeLifecycleAction.Restart);

        var reset = Leaf(runtime, "reset", "Drain the Runtime and delete validated local Runtime V1 state.");
        var yes = Flag("--yes", "Confirm the destructive reset.");
        var resetScope = ValueOption(
            "--scope",
            "Limit the reset to one reviewed state scope. Supported: startup-cache.",
            "SCOPE");
        reset.Options.Add(yes);
        reset.Options.Add(resetScope);
        resetScope.Validators.Add(result =>
        {
            if (result.Tokens.Count == 1
                && !string.Equals(result.Tokens[0].Value, "startup-cache", StringComparison.Ordinal))
            {
                result.AddError("Option '--scope' supports only 'startup-cache'.");
            }
        });
        reset.Validators.Add(result =>
        {
            if (!result.GetValue(yes)) result.AddError("Runtime reset is destructive. Specify '--yes' to continue.");
        });
        Bind(reset, "runtime reset", result => new RuntimeResetCommand(
            result.GetValue(yes),
            result.GetValue(resetScope) is null ? RuntimeResetScope.All : RuntimeResetScope.StartupCache));
        BuildExecutionRuntimeCommands(runtime);
    }

    private void AddDevPackageRuntimeCommands(Command package)
    {
        var prepare = Leaf(package, "prepare-imports", "Validate exact local or Registry-backed imports for an unpublished development package.");
        var prepareFolder = RequiredTextArgument("folder", "Canonical exploded development package folder.");
        var registry = ValueOption("--registry", "Explicit HTTPS Registry origin for published imports; omit for local evidence.", "origin");
        var candidates = new Option<string[]>("--candidate")
        {
            Description = "Exact local .sunderpkg candidate; repeat for each selected package.",
            HelpName = "archive",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false,
        };
        var catalog = ValueOption("--platform-catalog", "Exact local Core platform catalog.", "path");
        prepare.Arguments.Add(prepareFolder); prepare.Options.Add(registry); prepare.Options.Add(candidates); prepare.Options.Add(catalog);
        prepare.Validators.Add(result =>
        {
            var paths = result.GetValue(candidates) ?? [];
            if (paths.Length > 128 || paths.Any(string.IsNullOrWhiteSpace)) result.AddError("Supply at most 128 nonempty candidate paths.");
            if (result.GetValue(registry) is not null && (paths.Length > 0 || result.GetValue(catalog) is not null))
                result.AddError("Select local --candidate/--platform-catalog inputs or --registry, not both.");
        });
        Bind(prepare, "dev package prepare-imports", result => new PrepareDevPackageImportsCommand(
            result.GetRequiredValue(prepareFolder), result.GetValue(registry),
            result.GetValue(candidates) is { Length: > 0 } paths ? paths : null, result.GetValue(catalog)));
        var run = Leaf(package, "run", "Run a canonical development package folder through the managed Runtime.");
        var runFolder = RequiredTextArgument("folder", "Canonical exploded development package folder.");
        var watch = Flag("--watch", "Watch the folder and reload the package after stable changes.");
        run.Arguments.Add(runFolder);
        run.Options.Add(watch);
        Bind(run, "dev package run", result => new RunDevPackageCommand(
            result.GetRequiredValue(runFolder),
            result.GetValue(watch)));

        var smoke = Leaf(package, "smoke", "Load and release a development package through the managed Runtime.");
        var smokeFolder = RequiredTextArgument("folder", "Canonical exploded development package folder.");
        smoke.Arguments.Add(smokeFolder);
        Bind(smoke, "dev package smoke", result => new SmokeDevPackageCommand(result.GetRequiredValue(smokeFolder)));
    }

    private void BuildExecutionRuntimeCommands(Command runtime)
    {
        var settings = Leaf(runtime, "settings", "Read or change Host-owned automatic runtime updates (security refresh is always active).");
        var automatic = ValueOption("--automatic-updates", "Set automatic binary updates to true or false.", "boolean");
        settings.Options.Add(automatic);
        Bind(settings, "runtime settings", result => new ExecutionRuntimeSettingsCommand(result.GetValue(automatic) is { } value
            ? bool.TryParse(value, out var enabled) ? enabled : throw new CliUsageException("--automatic-updates requires true or false.") : null));

        var update = Leaf(runtime, "update", "Preview compatible Supported runtime updates; prepare/apply without stopping work by default.");
        var updateYes = Flag("--yes", "Confirm download and the displayed runtime transition.");
        var restart = Flag("--restart", "Explicitly restart the displayed affected package graph; requires --yes.");
        update.Options.Add(updateYes);
        update.Options.Add(restart);
        update.Validators.Add(result => { if (result.GetValue(restart) && !result.GetValue(updateYes)) result.AddError("--restart requires --yes."); });
        Bind(update, "runtime update", result => new UpdateExecutionRuntimesCommand(result.GetValue(updateYes), result.GetValue(restart)));

        var install = Leaf(runtime, "install", "Preview/install a runtime retained until explicit uninstall.");
        var installId = RequiredTextArgument("runtime-id", "Runtime family.");
        var installRange = RequiredTextArgument("version-range", "Bounded compatible version range.");
        var installAbi = RequiredTextArgument("module-abi", "Runtime module ABI.");
        var installYes = Flag("--yes", "Confirm the exact resolved runtime download and installation.");
        install.Arguments.Add(installId); install.Arguments.Add(installRange); install.Arguments.Add(installAbi);
        install.Options.Add(installYes);
        Bind(install, "runtime install", result => new InstallExecutionRuntimeCommand(result.GetRequiredValue(installId), result.GetRequiredValue(installRange),
            result.GetRequiredValue(installAbi), result.GetValue(installYes)));
        var removeYes = Flag("--yes", "Confirm removal only if Host proves the runtime unreferenced and not running.");
        AddExecutionRuntimeIdentityCommand(runtime, "remove", "Uninstall an exact runtime with authoritative usage checks.",
            static identity => new RemoveExecutionRuntimeCommand(identity), command =>
            {
                command.Options.Add(removeYes);
                command.Validators.Add(result => { if (!result.GetValue(removeYes)) result.AddError("runtime remove requires --yes; use runtime info to inspect references."); });
            });
        Bind(
            Leaf(runtime, "list", "List installed execution runtimes and health/lifecycle state."),
            "runtime list",
            _ => new ListExecutionRuntimesCommand());

        AddExecutionRuntimeIdentityCommand(
            runtime,
            "info",
            "Show one execution runtime and its dependent package references.",
            static identity => new ShowExecutionRuntimeCommand(identity));

        var ensure = Leaf(runtime, "ensure", "Resolve and install a trusted execution runtime for the current RID.");
        var ensureRuntimeId = RequiredTextArgument(
            "runtime-id",
            "Execution runtime id, such as sunder.runtime.dotnet or sunder.runtime.node.");
        var versionRange = RequiredTextArgument("version-range", "Strict bounded runtime version range.");
        var moduleAbi = RequiredTextArgument("module-abi", "Required module ABI.");
        ensure.Arguments.Add(ensureRuntimeId);
        ensure.Arguments.Add(versionRange);
        ensure.Arguments.Add(moduleAbi);
        Bind(ensure, "runtime ensure", result => new EnsureExecutionRuntimeCommand(
            result.GetRequiredValue(ensureRuntimeId),
            result.GetRequiredValue(versionRange),
            result.GetRequiredValue(moduleAbi)));

        AddExecutionRuntimeIdentityCommand(
            runtime,
            "verify",
            "Deeply verify one installed execution runtime.",
            static identity => new VerifyExecutionRuntimeCommand(identity));

        var yes = Flag("--yes", "Confirm that dependent Workers may be stopped during repair.");
        AddExecutionRuntimeIdentityCommand(
            runtime,
            "repair",
            "Repair one unhealthy execution runtime from its immutable cached archive.",
            static identity => new RepairExecutionRuntimeCommand(identity),
            command =>
            {
                command.Options.Add(yes);
                command.Validators.Add(result =>
                {
                    if (!result.GetValue(yes))
                        result.AddError("Execution runtime repair may stop dependent Workers. Specify '--yes' to continue.");
                });
            });

        Bind(
            Leaf(runtime, "gc", "Request lease-safe execution runtime garbage collection."),
            "runtime gc",
            _ => new GarbageCollectExecutionRuntimesCommand());
    }

    private void AddExecutionRuntimeIdentityCommand(
        Command parent,
        string name,
        string description,
        Func<CliExecutionRuntimeIdentity, CliCommand> factory,
        Action<Command>? configure = null)
    {
        var command = Leaf(parent, name, description);
        var sourceId = RequiredTextArgument("source-id", "Official source id: dotnet or node.");
        var runtimeId = RequiredTextArgument("runtime-id", "Execution runtime id.");
        var version = RequiredTextArgument("vendor-version", "Actual upstream SemVer runtime version.");
        version.Validators.Add(result => ValidateSemanticVersion(result.Tokens, result.AddError));
        var rid = RequiredTextArgument("rid", "Exact runtime identifier (RID).");
        var moduleAbi = RequiredTextArgument("module-abi", "Exact module ABI.");
        var checksumAlgorithm = RequiredTextArgument("checksum-algorithm", "Vendor checksum algorithm: sha512 or sha256.");
        var checksumDigest = RequiredTextArgument("checksum-digest", "Vendor checksum digest.");
        command.Arguments.Add(sourceId);
        command.Arguments.Add(runtimeId);
        command.Arguments.Add(version);
        command.Arguments.Add(rid);
        command.Arguments.Add(moduleAbi);
        command.Arguments.Add(checksumAlgorithm);
        command.Arguments.Add(checksumDigest);
        configure?.Invoke(command);
        Bind(command, $"runtime {name}", result => factory(new CliExecutionRuntimeIdentity(
            ParseSourceId(result.GetRequiredValue(sourceId)),
            result.GetRequiredValue(runtimeId),
            result.GetRequiredValue(version),
            result.GetRequiredValue(rid),
            result.GetRequiredValue(moduleAbi),
            ParseChecksumAlgorithm(result.GetRequiredValue(checksumAlgorithm)),
            result.GetRequiredValue(checksumDigest))));
    }

    private static Sunder.Runtime.Contracts.ConfiguredOfficialSourceId ParseSourceId(string value)
        => value switch
        {
            "dotnet" => Sunder.Runtime.Contracts.ConfiguredOfficialSourceId.DotNet,
            "node" => Sunder.Runtime.Contracts.ConfiguredOfficialSourceId.Node,
            _ => throw new CliUsageException("Source id must be 'dotnet' or 'node'."),
        };

    private static Sunder.Runtime.Contracts.RuntimeExecutionRuntimeChecksumAlgorithm ParseChecksumAlgorithm(string value)
        => value switch
        {
            "sha512" => Sunder.Runtime.Contracts.RuntimeExecutionRuntimeChecksumAlgorithm.Sha512,
            "sha256" => Sunder.Runtime.Contracts.RuntimeExecutionRuntimeChecksumAlgorithm.Sha256,
            _ => throw new CliUsageException("Checksum algorithm must be 'sha512' or 'sha256'."),
        };

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
        var approveSensitiveCapabilities = Flag(
            "--approve-sensitive-capabilities",
            "Explicitly approve exact capability escalations shown by Runtime preflight.");
        var yes = Flag("--yes", "Apply the exact Runtime-generated update preflight.");
        var dryRun = Flag("--dry-run", "Show package/runtime bytes and conflicts without applying changes.");
        update.Arguments.Add(packageId);
        update.Options.Add(all);
        update.Options.Add(prerelease);
        update.Options.Add(registryOrigin);
        update.Options.Add(approveSensitiveCapabilities);
        update.Options.Add(yes);
        update.Options.Add(dryRun);
        update.Validators.Add(result =>
        {
            var hasPackageId = result.GetResult(packageId)?.Tokens.Count > 0;
            if (result.GetValue(all) == hasPackageId)
                result.AddError("Specify exactly one package id or '--all'.");
            if (result.GetValue(yes) == result.GetValue(dryRun))
                result.AddError("Package update requires exactly one of '--yes' or '--dry-run'.");
            if (result.GetValue(dryRun) && result.GetValue(approveSensitiveCapabilities))
                result.AddError("Option '--approve-sensitive-capabilities' is only valid when applying updates with '--yes'.");
        });
        Bind(update, "package update", result => new UpdatePackagesCommand(
            result.GetValue(packageId),
            result.GetValue(prerelease),
            result.GetValue(registryOrigin),
            result.GetValue(approveSensitiveCapabilities),
            result.GetValue(dryRun)));
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
        var approveSensitiveCapabilities = Flag(
            "--approve-sensitive-capabilities",
            "Explicitly approve exact capability escalations shown by Runtime preflight.");
        adopt.Arguments.Add(packageId);
        adopt.Options.Add(registryOrigin);
        adopt.Options.Add(version);
        adopt.Options.Add(tag);
        adopt.Options.Add(prerelease);
        adopt.Options.Add(allowDowngrade);
        adopt.Options.Add(yes);
        adopt.Options.Add(dryRun);
        adopt.Options.Add(approveSensitiveCapabilities);
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
            result.GetValue(dryRun),
            result.GetValue(approveSensitiveCapabilities)));
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
