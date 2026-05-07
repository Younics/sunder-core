namespace Sunder.Cli;

internal static class ConsoleOutput
{
    public static void WriteInfo(string message) => Console.WriteLine(message);

    public static void WriteSuccess(string message)
    {
        var currentColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ForegroundColor = currentColor;
    }

    public static void WriteWarning(string message)
    {
        var currentColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Error.WriteLine(message);
        Console.ForegroundColor = currentColor;
    }

    public static void WriteError(string message)
    {
        var currentColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(message);
        Console.ForegroundColor = currentColor;
    }
}
