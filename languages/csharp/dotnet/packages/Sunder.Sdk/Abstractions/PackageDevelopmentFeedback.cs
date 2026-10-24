using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>A package view available to an exact development owner. Names are untrusted package content.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentFeedbackV1)]
public sealed record PackageDevelopmentView(string ViewId, string Name, bool IsOpen)
{
    /// <summary>Whether this open view's current platform supports package capture. Geometry is revalidated at capture.</summary>
    public bool CanCapture { get; init; }
}

/// <summary>A bounded semantic query, never a selector, script or desktop coordinate.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentFeedbackV1)]
public sealed record PackageDevelopmentViewQuery
{
    /// <summary>Case-insensitive accessible role filter.</summary>
    public string? Role { get; init; }
    /// <summary>Literal, case-insensitive accessible name filter.</summary>
    public string? Name { get; init; }
    /// <summary>Literal, case-insensitive displayed text filter.</summary>
    public string? Text { get; init; }
    /// <summary>Original snapshot for Within or ElementReference.</summary>
    public string? SnapshotId { get; init; }
    /// <summary>Limit traversal to this previously inspected container.</summary>
    public string? Within { get; init; }
    /// <summary>Read the state of this exact previously inspected element.</summary>
    public string? ElementReference { get; init; }
    /// <summary>Explicitly read an ordinary editable value. Protected values are never returned.</summary>
    public bool IncludeValue { get; init; }
    /// <summary>Maximum results, from one through 50. Default 20.</summary>
    public int Limit { get; init; } = 20;
    /// <summary>Continue an unchanged query's retained page.</summary>
    public string? Cursor { get; init; }

    /// <summary>Rejects malformed intent before accessing a view.</summary>
    public void Validate()
    {
        if (Limit is < 1 or > 50 || Role?.Length > 32 || Name?.Length > 256 || Text?.Length > 256 || Cursor?.Length > 128
            || SnapshotId is not null && !Guid.TryParseExact(SnapshotId, "N", out _)
            || Within is not null && !Guid.TryParseExact(Within, "N", out _)
            || ElementReference is not null && !Guid.TryParseExact(ElementReference, "N", out _)
            || (Within is not null || ElementReference is not null) && SnapshotId is null
            || Within is not null && ElementReference is not null || SnapshotId is not null && Within is null && ElementReference is null)
            throw new ArgumentException("Use bounded literal view filters and original inspected references.");
    }
}

/// <summary>One query page. Partial means traversal or backend coverage was incomplete, not that a match is absent.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentFeedbackV1)]
public sealed record PackageDevelopmentViewPage(PackageDevelopmentViewSnapshot Snapshot, bool Partial, string? NextCursor);
