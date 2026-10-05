using Sunder.Runtime.LocalState;

namespace Sunder.Runtime.Host.Services;

internal sealed record PolicyClockReading(string BootSessionId, string Source, long Frequency, long Ticks, string? VirtualMachineGeneration = null)
{
    public bool SameEpoch(PolicyClockReading other) => BootSessionId == other.BootSessionId && Source == other.Source
        && Frequency == other.Frequency && VirtualMachineGeneration == other.VirtualMachineGeneration;
}

internal interface IPolicyContinuousClock
{
    PolicyClockReading Read();
}

// Stopwatch/TimeProvider timestamps are not uniformly suspend-aware. Wall time is
// never a fallback for an unavailable native clock or boot-session identity.
internal sealed class PolicyContinuousClock : IPolicyContinuousClock
{
    public PolicyClockReading Read()
    {
        var value = RuntimeContinuousClock.Read();
        return new(value.BootSessionId, value.Source, value.Frequency, value.Ticks);
    }
}
