namespace Sunder.Cli;

internal sealed class CliTimeoutException : Exception
{
    public CliTimeoutException(string operation, TimeSpan timeout, Exception innerException)
        : base($"{operation} timed out after {FormatTimeout(timeout)}. Use --timeout <duration> to increase it.", innerException)
    {
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }

    private static string FormatTimeout(TimeSpan timeout)
    {
        if (timeout.TotalSeconds >= 60 && Math.Abs(timeout.TotalSeconds % 60) < 0.001)
        {
            return FormattableString.Invariant($"{timeout.TotalMinutes:0}m");
        }

        return FormattableString.Invariant($"{timeout.TotalSeconds:0.###}s");
    }
}
