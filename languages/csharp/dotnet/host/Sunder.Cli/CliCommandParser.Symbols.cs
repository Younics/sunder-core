using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using Sunder.Sdk.Packaging;

namespace Sunder.Cli;

internal sealed partial class CliCommandParser
{
    private void AddPagedSearch(
        Command parent,
        string name,
        string description,
        string path,
        Func<string?, int, int, CliCommand> factory)
    {
        var search = Leaf(parent, name, description);
        var query = new Argument<string?>("query")
        {
            Description = "Optional search query.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var skip = new Option<int>("--skip")
        {
            Description = "Number of results to skip.",
            HelpName = "count",
            DefaultValueFactory = _ => 0,
        };
        var take = new Option<int>("--take")
        {
            Description = $"Number of results to return (1-{MaximumPageSize}).",
            HelpName = "count",
            DefaultValueFactory = _ => 20,
        };
        RejectRepeated(skip);
        RejectRepeated(take);
        skip.Validators.Add(result =>
        {
            if (result.Tokens.Count == 1
                && int.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                && value < 0)
            {
                result.AddError("Option '--skip' must be a non-negative integer.");
            }
        });
        take.Validators.Add(result =>
        {
            if (result.Tokens.Count == 1
                && int.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                && value is < 1 or > MaximumPageSize)
            {
                result.AddError($"Option '--take' must be between 1 and {MaximumPageSize}.");
            }
        });
        search.Arguments.Add(query);
        search.Options.Add(skip);
        search.Options.Add(take);
        Bind(search, path, result => factory(result.GetValue(query), result.GetValue(skip), result.GetValue(take)));
    }

    private Command Group(Command parent, string name, string description)
    {
        var command = CreateCommand(name, description);
        parent.Subcommands.Add(command);
        _helpPaths.Add(command, JoinPath(_helpPaths[parent], name));
        return command;
    }

    private Command Leaf(Command parent, string name, string description)
    {
        var command = CreateCommand(name, description);
        parent.Subcommands.Add(command);
        _helpPaths.Add(command, JoinPath(_helpPaths[parent], name));
        return command;
    }

    private void Bind(Command command, string path, Func<ParseResult, CliCommand> factory)
    {
        _bindings.Add(command, factory);
        _paths.Add(command, path);
    }

    private static Command CreateCommand(string name, string description)
    {
        var command = new Command(name, description) { TreatUnmatchedTokensAsErrors = true };
        command.SetAction(_ => 0);
        return command;
    }

    private static Argument<string> PackageIdArgument(bool required = true)
    {
        var argument = new Argument<string>("package-id")
        {
            Description = "Canonical lowercase package id.",
            Arity = required ? ArgumentArity.ExactlyOne : ArgumentArity.ZeroOrOne,
            CustomParser = result =>
            {
                if (result.Tokens.Count == 0) return null!;
                var value = result.Tokens[0].Value;
                if (!PackageId.TryParse(value, out _))
                    result.AddError($"Package id '{value}' must be a lowercase dot-separated ASCII id of at most {PackageId.MaximumLength} characters.");
                return value;
            },
        };
        return argument;
    }

    private static Argument<string> SemanticVersionArgument()
    {
        var argument = RequiredTextArgument("version", "Strict SemVer 2.0 package version.");
        argument.Validators.Add(result => ValidateSemanticVersion(result.Tokens, result.AddError));
        return argument;
    }

    private static Option<string?> SemanticVersionOption(string name, string description)
    {
        var option = ValueOption(name, description, "version");
        option.Validators.Add(result => ValidateSemanticVersion(result.Tokens, result.AddError));
        return option;
    }

    private static void ValidateSemanticVersion(IReadOnlyList<Token> tokens, Action<string> addError)
    {
        if (tokens.Count == 0) return;
        var value = tokens[0].Value;
        if (!SemanticVersion.TryParse(value, out _))
            addError($"Version '{value}' must be strict SemVer 2.0 and at most {SemanticVersion.MaximumLength} characters.");
    }

    private static Argument<string> RequiredTextArgument(string name, string description)
    {
        var argument = new Argument<string>(name) { Description = description };
        argument.Validators.Add(result =>
        {
            if (result.Tokens.Count == 1 && string.IsNullOrWhiteSpace(result.Tokens[0].Value))
                result.AddError($"Argument '<{name}>' cannot be empty.");
        });
        return argument;
    }

    private static Option<string?> RequiredValueOption(string name, string description, string helpName)
    {
        var option = ValueOption(name, description, helpName);
        option.Required = true;
        return option;
    }

    private static Option<string?> ValueOption(
        string name,
        string description,
        string helpName,
        bool recursive = false,
        string[]? aliases = null)
    {
        var option = new Option<string?>(name, aliases ?? [])
        {
            Description = description,
            HelpName = helpName,
            Recursive = recursive,
            Arity = ArgumentArity.ExactlyOne,
        };
        RejectRepeated(option);
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count == 1 && string.IsNullOrWhiteSpace(result.Tokens[0].Value))
                result.AddError($"Option '{name}' cannot be empty.");
        });
        return option;
    }

    private static Option<bool> Flag(string name, string description, bool recursive = false)
    {
        var option = new Option<bool>(name)
        {
            Description = description,
            Recursive = recursive,
            Arity = ArgumentArity.Zero,
        };
        RejectRepeated(option);
        return option;
    }

    private static void RejectRepeated(Option option)
    {
        option.Validators.Add(result =>
        {
            if (result.IdentifierTokenCount > 1)
                result.AddError($"Option '{option.Name}' may only be specified once.");
        });
    }

    private IReadOnlyList<string> FindUnknownOptions(IReadOnlyList<string> arguments, Command selected)
    {
        var allowed = selected.Options.Concat(Root.Options.Where(option => option.Recursive))
            .Distinct()
            .SelectMany(option => option.Aliases.Prepend(option.Name).Select(alias => (Alias: alias, Option: option)))
            .ToDictionary(item => item.Alias, item => item.Option, StringComparer.Ordinal);
        var errors = new List<string>();
        var consumesNext = false;
        foreach (var token in arguments)
        {
            if (token == "--") break;
            if (consumesNext)
            {
                consumesNext = false;
                continue;
            }
            if (!token.StartsWith("-", StringComparison.Ordinal) || token == "-") continue;

            if (allowed.TryGetValue(token, out var exact))
            {
                consumesNext = exact.Arity.MaximumNumberOfValues > 0;
                continue;
            }
            if (allowed.Any(pair =>
                    token.StartsWith(pair.Key + "=", StringComparison.Ordinal)
                    || token.StartsWith(pair.Key + ":", StringComparison.Ordinal)
                    || pair.Key.Length == 2 && pair.Key[0] == '-' && token.StartsWith(pair.Key, StringComparison.Ordinal)
                       && pair.Value.Arity.MaximumNumberOfValues > 0))
            {
                continue;
            }

            errors.Add($"Unrecognized option '{token}'. Use '--' before argument values that begin with '-'.");
        }
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string JoinPath(string parent, string name)
        => string.IsNullOrEmpty(parent) ? name : $"{parent} {name}";
}
