using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Bounded, untrusted accessibility labels from one already open development view. This is not desktop or shell authority.</summary>
/// <param name="SnapshotId">Opaque, short-lived identity owned by this development session.</param>
/// <param name="PackageId">The exact development package.</param>
/// <param name="ViewId">The package view, never a settings or shell surface.</param>
/// <param name="Revision">The applied source observed before inspecting this exact App activation.</param>
/// <param name="Elements">Bounded accessible elements. Text input values and protected contents are not returned.</param>
/// <param name="WasTruncated">Whether element, text, or traversal bounds omitted information.</param>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentViewsV1)]
public sealed record PackageDevelopmentViewSnapshot(string SnapshotId, string PackageId, string ViewId,
    PackageDevelopmentRevision Revision, IReadOnlyList<PackageDevelopmentViewElement> Elements, bool WasTruncated);

/// <summary>One opaque element reference, valid only in its original snapshot and exact view. Names are untrusted package content.</summary>
/// <param name="Reference">Opaque element identity; not an automation ID, path, selector or coordinate.</param>
/// <param name="Role">The accessibility control role.</param>
/// <param name="Name">A bounded label, never an input value.</param>
/// <param name="IsEnabled">Whether the element was enabled when inspected.</param>
/// <param name="Actions">Supported actions: invoke, set-text, toggle, scroll-up or scroll-down. An empty list grants no input.</param>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentViewsV1)]
public sealed record PackageDevelopmentViewElement(string Reference, string Role, string Name, bool IsEnabled, IReadOnlyList<string> Actions)
{
    /// <summary>Requested bounded semantic state, never protected input contents.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentFeedbackV1)]
    public IReadOnlyDictionary<string, string> State { get; init; } = new Dictionary<string, string>();
}

/// <summary>One authorized UI input against an inspected element. No script, selector, clipboard, shortcut or screen position is accepted.</summary>
/// <param name="OperationId">A fresh canonical GUID in N format; retain it to resolve a lost response without replay.</param>
/// <param name="SnapshotId">The exact snapshot which supplied the element reference.</param>
/// <param name="ElementReference">The selected opaque element reference.</param>
/// <param name="Action">One of the selected element's advertised actions.</param>
/// <param name="Text">At most 4,096 characters, only for set-text; otherwise null.</param>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentViewsV1)]
public sealed record PackageDevelopmentViewInput(string OperationId, string SnapshotId, string ElementReference, string Action, string? Text = null);

/// <summary>Recorded input dispatch, not proof of an application effect. Inspect again to observe state; never automatically replay an unknown action.</summary>
/// <param name="OperationId">The original input operation identity.</param>
/// <param name="State">Dispatched or unknown. Invalid/stale requests are rejected before admission.</param>
/// <param name="OutcomeUnknown">Whether an exception or retirement prevented confirmation after input dispatch began.</param>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentViewsV1)]
public sealed record PackageDevelopmentViewInputResult(string OperationId, string State, bool OutcomeUnknown);
