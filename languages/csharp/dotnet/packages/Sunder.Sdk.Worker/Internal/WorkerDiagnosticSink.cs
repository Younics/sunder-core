using System.Text;

namespace Sunder.Sdk.Worker.Internal;

internal sealed class WorkerDiagnosticSink
{
    private readonly TextWriter _writer;
    private readonly object _gate = new();
    private readonly int _maximumCharacters;

    public WorkerDiagnosticSink(TextWriter writer, int maximumCharacters)
    {
        _writer = writer;
        _maximumCharacters = maximumCharacters;
    }

    public void Write(string message, Exception? exception = null)
        => Write(_writer, _gate, _maximumCharacters, message, exception);

    public static void Write(
        TextWriter writer,
        object gate,
        int maximumCharacters,
        string message,
        Exception? exception)
    {
        var sanitized = exception is null
            ? Sanitize(message, maximumCharacters)
            : SanitizeSegments(
                maximumCharacters,
                message,
                ": ",
                exception.GetType().Name,
                ": ",
                exception.Message);
        if (sanitized.Length == 0) return;
        try
        {
            lock (gate)
            {
                writer.WriteLine(sanitized);
                writer.Flush();
            }
        }
        catch
        {
            // Diagnostics must never corrupt or interrupt the protocol channel.
        }
    }

    internal static string Sanitize(string value, int maximumCharacters)
        => SanitizeSegments(maximumCharacters, value);

    private static string SanitizeSegments(int maximumCharacters, params string[] values)
    {
        var builder = new StringBuilder(maximumCharacters);
        var truncated = false;
        foreach (var value in values)
        {
            foreach (var character in value)
            {
                if (builder.Length == maximumCharacters)
                {
                    truncated = true;
                    break;
                }
                builder.Append(
                    char.IsControl(character) && character != '\t' || character is '\u2028' or '\u2029'
                        ? ' '
                        : character);
            }
            if (truncated) break;
        }

        var result = builder.ToString().Trim();
        if (!truncated) return result;
        const string suffix = " [truncated]";
        if (maximumCharacters <= suffix.Length) return suffix[..maximumCharacters];
        return result[..Math.Min(result.Length, maximumCharacters - suffix.Length)] + suffix;
    }
}
