namespace Sunder.App.Services;

internal static class RuntimeReconnectBackoff
{
    private const double MinimumJitter = 0.8;
    private const double JitterRange = 0.4;

    public static TimeSpan GetDelay(int consecutiveFailures)
    {
        var exponent = Math.Clamp(consecutiveFailures, 0, 5);
        var baseMilliseconds = Math.Min(8000, 250 * (1 << exponent));
        var jitter = MinimumJitter + (Random.Shared.NextDouble() * JitterRange);
        return TimeSpan.FromMilliseconds(baseMilliseconds * jitter);
    }
}
