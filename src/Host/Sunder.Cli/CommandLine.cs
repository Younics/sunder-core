namespace Sunder.Cli;

internal static class CommandLine
{
    public static bool ConsumeFlag(List<string> args, string name)
    {
        var index = args.FindIndex(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        args.RemoveAt(index);
        return true;
    }

    public static string? ConsumeOption(List<string> args, string name)
    {
        var index = args.FindIndex(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException($"Option '{name}' requires a value.");
        }

        var value = args[index + 1];
        args.RemoveAt(index + 1);
        args.RemoveAt(index);
        return value;
    }

    public static int ConsumeInt32Option(List<string> args, string name, int defaultValue)
    {
        var value = ConsumeOption(args, name);
        if (value is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var parsed) || parsed < 0)
        {
            throw new ArgumentException($"Option '{name}' must be a non-negative integer.");
        }

        return parsed;
    }

    public static string RequireSingleArgument(List<string> args, string usage)
    {
        if (args.Count != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            throw new ArgumentException(usage);
        }

        return args[0];
    }

    public static void EnsureNoExtraArguments(List<string> args, string usage)
    {
        if (args.Count > 0)
        {
            throw new ArgumentException(usage);
        }
    }
}
