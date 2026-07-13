using Sunder.Sdk.Packaging;

namespace Sunder.Cli;

internal static class CliCommandParser
{
    private const int MaximumPageSize = 100;
    public static CliInvocation Parse(IReadOnlyList<string> arguments)
    {
        var args = arguments.ToList();
        var help = TakeFlag(args, "--help") | TakeFlag(args, "-h");
        var options = CliOptions.Parse(args);
        if (help || args.Count == 0)
        {
            return new(options, new HelpCommand());
        }

        var family = TakeFirst(args).ToLowerInvariant();
        var command = family switch
        {
            "system" or "runtime" => ParseSystem(args),
            "auth" => ParseAuth(args),
            "search" => ParseSearchPackages(args),
            "info" => ParsePackageInfo(args),
            "list" => NoArgs(args, new ListInstalledCommand(), "sunder list"),
            "install" => ParseInstall(args),
            "update" => ParseUpdate(args),
            "publish" => ParsePublishPackage(args),
            "yank" => ParseYank(args, true),
            "unyank" => ParseYank(args, false),
            "deprecate" => ParseDeprecation(args, false),
            "undeprecate" => ParseDeprecation(args, true),
            "dist-tag" => ParseDistTag(args),
            "validate" => new ValidatePackageCommand(One(args, "sunder validate <package.sunderpkg>")),
            "package" => ParsePackage(args),
            "stack" or "stacks" => ParseStack(args),
            _ => throw Usage($"Unknown command '{family}'.")
        };
        return new(options, command);
    }

    private static CliCommand ParseSystem(List<string> args)
    {
        if (args.Count == 1 && string.Equals(args[0], "status", StringComparison.OrdinalIgnoreCase))
        {
            return new SystemStatusCommand();
        }
        if (args.Count > 0 && string.Equals(args[0], "reset", StringComparison.OrdinalIgnoreCase))
        {
            args.RemoveAt(0);
            var yes = TakeFlag(args, "--yes");
            EnsureEmpty(args, "sunder runtime reset --yes");
            if (!yes)
            {
                throw Usage("Runtime reset is destructive. Re-run as 'sunder runtime reset --yes'.");
            }

            return new RuntimeResetCommand(yes);
        }
        throw Usage("Usage: sunder runtime <status|reset --yes>");
    }

    private static CliCommand ParseAuth(List<string> args)
        => args.Count == 1 ? args[0].ToLowerInvariant() switch
        {
            "login" => new AuthLoginCommand(),
            "status" => new AuthStatusCommand(),
            "logout" => new AuthLogoutCommand(),
            _ => throw Usage("Usage: sunder auth <login|status|logout>")
        } : throw Usage("Usage: sunder auth <login|status|logout>");

    private static CliCommand ParseSearchPackages(List<string> args)
    {
        var skip = TakeInt(args, "--skip", 0);
        var take = Math.Clamp(TakeInt(args, "--take", 20), 1, MaximumPageSize);
        return new SearchPackagesCommand(ZeroOrOne(args, "sunder search [query] [--skip <count>] [--take <count>]"), skip, take);
    }

    private static CliCommand ParsePackageInfo(List<string> args)
    {
        var version = TakeOption(args, "--version");
        return new PackageInfoCommand(
            PackageIdArgument(One(args, "sunder info <package-id> [--version <version>]")),
            version is null ? null : SemanticVersionArgument(version));
    }

    private static CliCommand ParseInstall(List<string> args)
    {
        var file = TakeOption(args, "--file");
        var version = TakeOption(args, "--version");
        var explicitTag = TakeOption(args, "--tag");
        var allowDowngrade = TakeFlag(args, "--allow-downgrade");
        var reinstall = TakeFlag(args, "--reinstall");
        if (version is not null && explicitTag is not null)
        {
            throw Usage("Use either '--version' or '--tag', not both.");
        }
        if (file is not null)
        {
            if (version is not null || explicitTag is not null || args.Count != 0)
            {
                throw Usage("Usage: sunder install --file <package.sunderpkg> [--allow-downgrade] [--reinstall]");
            }
            return new InstallLocalPackageCommand(file, allowDowngrade, reinstall);
        }
        return new InstallRegistryPackageCommand(
            PackageIdArgument(One(args, "sunder install <package-id> [--version <version>|--tag <tag>] [--allow-downgrade] [--reinstall]")),
            version is null ? null : SemanticVersionArgument(version),
            version is null ? explicitTag ?? "latest" : null,
            allowDowngrade,
            reinstall);
    }

    private static CliCommand ParseUpdate(List<string> args)
    {
        var all = TakeFlag(args, "--all");
        var prerelease = TakeFlag(args, "--include-prerelease");
        var packageId = ZeroOrOne(args, "sunder update [package-id|--all] [--include-prerelease]");
        if (all && packageId is not null)
        {
            throw Usage("Usage: sunder update [package-id|--all] [--include-prerelease]");
        }
        return new UpdatePackagesCommand(packageId is null ? null : PackageIdArgument(packageId), all, prerelease);
    }

    private static CliCommand ParsePublishPackage(List<string> args)
    {
        var file = RequiredOption(args, "--file", "sunder publish --file <package.sunderpkg> [--no-latest] [--dev-local]");
        var command = new PublishPackageCommand(file, !TakeFlag(args, "--no-latest"), TakeFlag(args, "--dev-local"));
        EnsureEmpty(args, "sunder publish --file <package.sunderpkg> [--no-latest] [--dev-local]");
        return command;
    }

    private static CliCommand ParseYank(List<string> args, bool value)
    {
        EnsureCount(args, 2, $"sunder {(value ? "yank" : "unyank")} <package-id> <version>");
        return new SetYankCommand(PackageIdArgument(args[0]), SemanticVersionArgument(args[1]), value);
    }

    private static CliCommand ParseDeprecation(List<string> args, bool clear)
    {
        var message = clear ? null : TakeOption(args, "--message");
        EnsureCount(args, 2, clear
            ? "sunder undeprecate <package-id> <version>"
            : "sunder deprecate <package-id> <version> --message <message>");
        if (!clear && string.IsNullOrWhiteSpace(message))
        {
            throw Usage("Usage: sunder deprecate <package-id> <version> --message <message>");
        }
        return new SetDeprecationCommand(PackageIdArgument(args[0]), SemanticVersionArgument(args[1]), message);
    }

    private static CliCommand ParseDistTag(List<string> args)
    {
        if (args.Count == 0) throw Usage("Usage: sunder dist-tag <list|set|delete> ...");
        var action = TakeFirst(args).ToLowerInvariant();
        return action switch
        {
            "list" => new ListDistTagsCommand(PackageIdArgument(One(args, "sunder dist-tag list <package-id>"))),
            "set" => ParseSetTag(args),
            "delete" or "rm" => ParseDeleteTag(args),
            _ => throw Usage("Usage: sunder dist-tag <list|set|delete> ...")
        };
    }

    private static CliCommand ParseSetTag(List<string> args)
    {
        EnsureCount(args, 3, "sunder dist-tag set <package-id> <tag> <version>");
        return new SetDistTagCommand(PackageIdArgument(args[0]), args[1], SemanticVersionArgument(args[2]));
    }

    private static CliCommand ParseDeleteTag(List<string> args)
    {
        EnsureCount(args, 2, "sunder dist-tag delete <package-id> <tag>");
        return new SetDistTagCommand(PackageIdArgument(args[0]), args[1], null);
    }

    private static CliCommand ParsePackage(List<string> args)
    {
        if (args.Count > 0 && string.Equals(TakeFirst(args), "validate", StringComparison.OrdinalIgnoreCase))
        {
            return new ValidatePackageCommand(One(args, "sunder package validate <package.sunderpkg>"));
        }
        throw Usage("Usage: sunder package validate <package.sunderpkg>");
    }

    private static CliCommand ParseStack(List<string> args)
    {
        if (args.Count == 0) throw Usage("Usage: sunder stack <search|info|download|publish|update|delete|use|inspect|validate> ...");
        return TakeFirst(args).ToLowerInvariant() switch
        {
            "search" => ParseStackSearch(args),
            "info" => new StackInfoCommand(One(args, "sunder stack info <stack-id>")),
            "download" => ParseStackDownload(args),
            "publish" => ParseStackPublish(args),
            "update" => ParseStackUpdate(args),
            "delete" or "rm" => new DeleteStackCommand(One(args, "sunder stack delete <stack-id>")),
            "use" => new UseStackCommand(One(args, "sunder stack use <stack-id>")),
            "inspect" or "validate" => new ValidateStackCommand(One(args, "sunder stack inspect <stack.sunderstack>")),
            _ => throw Usage("Usage: sunder stack <search|info|download|publish|update|delete|use|inspect|validate> ...")
        };
    }

    private static CliCommand ParseStackSearch(List<string> args)
    {
        var skip = TakeInt(args, "--skip", 0);
        var take = Math.Clamp(TakeInt(args, "--take", 20), 1, MaximumPageSize);
        return new SearchStacksCommand(ZeroOrOne(args, "sunder stack search [query] [--skip <count>] [--take <count>]"), skip, take);
    }

    private static CliCommand ParseStackDownload(List<string> args)
    {
        var output = TakeOption(args, "--output") ?? TakeOption(args, "-o");
        return new DownloadStackCommand(One(args, "sunder stack download <stack-id> [--output <path>]"), output);
    }

    private static CliCommand ParseStackPublish(List<string> args)
    {
        var file = RequiredOption(args, "--file", "sunder stack publish --file <stack.sunderstack> [--dev-local]");
        var result = new PublishStackCommand(file, TakeFlag(args, "--dev-local"));
        EnsureEmpty(args, "sunder stack publish --file <stack.sunderstack> [--dev-local]");
        return result;
    }

    private static CliCommand ParseStackUpdate(List<string> args)
    {
        var file = RequiredOption(args, "--file", "sunder stack update <stack-id> --file <stack.sunderstack>");
        return new UpdateStackCommand(One(args, "sunder stack update <stack-id> --file <stack.sunderstack>"), file);
    }

    private static T NoArgs<T>(List<string> args, T command, string usage) where T : CliCommand
    {
        EnsureEmpty(args, usage);
        return command;
    }

    private static string TakeFirst(List<string> args)
    {
        var value = args[0];
        args.RemoveAt(0);
        return value;
    }

    private static bool TakeFlag(List<string> args, string name)
    {
        var matches = args.Select((value, index) => (value, index)).Where(item => string.Equals(item.value, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw Usage($"Option '{name}' may only be specified once.");
        if (matches.Length == 0) return false;
        args.RemoveAt(matches[0].index);
        return true;
    }

    private static string? TakeOption(List<string> args, string name)
    {
        var indexes = args.Select((value, index) => (value, index)).Where(item => string.Equals(item.value, name, StringComparison.OrdinalIgnoreCase)).Select(item => item.index).ToArray();
        if (indexes.Length > 1) throw Usage($"Option '{name}' may only be specified once.");
        if (indexes.Length == 0) return null;
        var index = indexes[0];
        if (index + 1 >= args.Count || args[index + 1].StartsWith("-", StringComparison.Ordinal)) throw Usage($"Option '{name}' requires a value.");
        var value = args[index + 1];
        args.RemoveRange(index, 2);
        return value;
    }

    private static string RequiredOption(List<string> args, string name, string usage)
        => TakeOption(args, name) ?? throw Usage($"Usage: {usage}");

    private static int TakeInt(List<string> args, string name, int fallback)
    {
        var value = TakeOption(args, name);
        if (value is null) return fallback;
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var result) || result < 0)
            throw Usage($"Option '{name}' must be a non-negative integer.");
        return result;
    }

    private static string One(List<string> args, string usage)
    {
        EnsureCount(args, 1, usage);
        return args[0];
    }

    private static string? ZeroOrOne(List<string> args, string usage)
    {
        if (args.Count > 1) throw Usage($"Usage: {usage}");
        return args.Count == 0 ? null : args[0];
    }

    private static void EnsureEmpty(List<string> args, string usage) => EnsureCount(args, 0, usage);

    private static void EnsureCount(List<string> args, int count, string usage)
    {
        if (args.Count != count || args.Any(string.IsNullOrWhiteSpace)) throw Usage($"Usage: {usage}");
    }

    private static string PackageIdArgument(string value)
        => PackageId.TryParse(value, out _)
            ? value
            : throw Usage($"Package id '{value}' must be a lowercase dot-separated ASCII id of at most {PackageId.MaximumLength} characters.");

    private static string SemanticVersionArgument(string value)
        => SemanticVersion.TryParse(value, out _)
            ? value
            : throw Usage($"Version '{value}' must be strict SemVer 2.0 and at most {SemanticVersion.MaximumLength} characters.");

    private static CliUsageException Usage(string message) => new(message);
}
