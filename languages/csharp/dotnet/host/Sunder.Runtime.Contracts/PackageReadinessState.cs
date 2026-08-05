namespace Sunder.Runtime.Contracts;

public enum PackageReadinessState
{
    Unknown = 0,
    Ready = 1,
    NeedsConfiguration = 2,
    Degraded = 3,
    Failed = 4,
    Disabled = 5,
}
