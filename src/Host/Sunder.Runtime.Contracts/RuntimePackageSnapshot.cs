namespace Sunder.Runtime.Contracts;

public enum RuntimeBootstrapState
{
    Starting = 0,
    Ready = 1,
    Failed = 2,
    ShuttingDown = 3,
}

public sealed record RuntimePackageSnapshot(
    Guid RuntimeInstanceId,
    long SessionGeneration,
    long EventSequence,
    RuntimeBootstrapState BootstrapState,
    IReadOnlyList<ActivePackageDescriptor> ActivePackages,
    IReadOnlyList<SessionPackageDescriptor> SessionPackages,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public IReadOnlyList<ActivePackageDescriptor> ActivePackages { get; }
        = Freeze(ActivePackages.Select(package => package with { Views = Freeze(package.Views) }));

    public IReadOnlyList<SessionPackageDescriptor> SessionPackages { get; }
        = Freeze(SessionPackages.Select(package => package with { Views = Freeze(package.Views) }));

    public IReadOnlyList<string> Warnings { get; } = Freeze(Warnings);

    public IReadOnlyList<string> Errors { get; } = Freeze(Errors);

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values)
        => Array.AsReadOnly(values.ToArray());
}
