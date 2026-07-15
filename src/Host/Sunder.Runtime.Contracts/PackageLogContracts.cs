using System.Text.Json.Serialization;

namespace Sunder.Runtime.Contracts;

public enum RuntimePackageLogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
}

public sealed record PackageLogEntryDescriptor(
    long SequenceId,
    string PackageId,
    DateTimeOffset Timestamp,
    RuntimePackageLogLevel Level,
    string Category,
    string Message,
    bool Truncated);

public sealed record PackageLogSnapshot(
    long SequenceId,
    IReadOnlyList<PackageLogEntryDescriptor> Entries,
    [property: JsonPropertyOrder(2)] bool HistoryGap)
{
    private IReadOnlyList<PackageLogEntryDescriptor> _entries = RuntimeContractCollections.Freeze(Entries);

    [JsonPropertyOrder(1)]
    public IReadOnlyList<PackageLogEntryDescriptor> Entries
    {
        get => _entries;
        init => _entries = RuntimeContractCollections.Freeze(value);
    }
}
