using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Logging;

namespace Sunder.Sdk.Abstractions;

/// <summary>A bounded query over retained package diagnostics. Bounds apply after filtering; entries are returned oldest first.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentLogQueriesV1)]
public sealed record PackageDevelopmentLogQuery
{
    /// <summary>Inclusive lower time bound, with an explicit offset.</summary>
    public DateTimeOffset? Since { get; init; }
    /// <summary>Exclusive upper time bound, with an explicit offset.</summary>
    public DateTimeOffset? Until { get; init; }
    /// <summary>Minimum severity to include.</summary>
    public PackageLogLevel MinimumLevel { get; init; } = PackageLogLevel.Information;
    /// <summary>Observation source: all, runtime or app.</summary>
    public string Source { get; init; } = "all";
    /// <summary>Maximum entries, from 1 through 100.</summary>
    public int MaximumEntries { get; init; } = 20;
    /// <summary>Maximum encoded response bytes, from 1,024 through 32,768.</summary>
    public int MaximumBytes { get; init; } = 16 * 1024;
    /// <summary>Opaque continuation from a previous query for the same package and filters.</summary>
    public string? Cursor { get; init; }
    /// <summary>Literal case-insensitive message filter applied before page selection.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentFeedbackV1)]
    public string? Contains { get; init; }

    /// <summary>Rejects invalid intent before accessing diagnostics.</summary>
    public void Validate()
    {
        if (Since is { } since && Until is { } until && since >= until)
            throw new ArgumentException("The log time range must have since before until.");
        if (!Enum.IsDefined(MinimumLevel) || Source is not ("all" or "runtime" or "app"))
            throw new ArgumentException("Select a valid log severity and source (all, runtime or app).");
        if (MaximumEntries is < 1 or > 100 || MaximumBytes is < 1024 or > 32768 || Cursor?.Length > 2048 || Contains?.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries), "Log queries require 1–100 entries, 1–32 KiB and a bounded cursor.");
    }
}
