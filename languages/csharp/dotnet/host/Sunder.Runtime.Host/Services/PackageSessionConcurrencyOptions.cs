namespace Sunder.Runtime.Host.Services;

internal sealed record PackageSessionConcurrencyOptions
{
    public const int DefaultMaximumConcurrency = 2;
    public const int MinimumConcurrency = 1;
    public const int MaximumConcurrency = 8;

    public int MaximumPreparationConcurrency { get; init; } = DefaultMaximumConcurrency;

    public int MaximumWorkerStartupConcurrency { get; init; } = DefaultMaximumConcurrency;

    public void Validate()
    {
        if (MaximumPreparationConcurrency is < MinimumConcurrency or > MaximumConcurrency)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPreparationConcurrency));
        }
        if (MaximumWorkerStartupConcurrency is < MinimumConcurrency or > MaximumConcurrency)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumWorkerStartupConcurrency));
        }
    }
}
