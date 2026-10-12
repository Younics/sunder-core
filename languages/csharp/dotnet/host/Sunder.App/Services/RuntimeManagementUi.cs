using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal static class RuntimeManagementUi
{
    public static string Family(string id) => id switch
    {
        "sunder.runtime.dotnet" => ".NET",
        "sunder.runtime.node" => "Node.js",
        _ => id,
    };

    public static string Identity(RuntimeExecutionRuntimeIdentity identity)
        => $"{Family(identity.RuntimeId)} {identity.VendorVersion}";

    public static string Resolution(RuntimeExecutionRuntimeResolution resolution)
        => Identity(resolution.Artifact.Identity);

    public static string Source(RuntimeExecutionRuntimeIdentity identity)
        => identity.SourceId == ConfiguredOfficialSourceId.DotNet ? "Microsoft .NET" : "Node.js official distribution";

    public static bool IsFingerprint(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsStale(string? code) => code?.Contains("stale", StringComparison.OrdinalIgnoreCase) == true;

    public static string Error(string message, string? code)
        => IsStale(code) ? "This preview changed or expired. Check again and review the new confirmation. " + message
            : string.IsNullOrWhiteSpace(code) ? message : $"{message} ({code})";

    public static RuntimeExecutionRuntimePlanConfirmation Confirm(RuntimeExecutionRuntimePlanIdentity plan)
    {
        if (string.IsNullOrWhiteSpace(plan.PlanId) || !IsFingerprint(plan.Fingerprint) || plan.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("This preview expired or is invalid. Check again and review a fresh confirmation.");
        return new(plan.PlanId, plan.Fingerprint);
    }
}
