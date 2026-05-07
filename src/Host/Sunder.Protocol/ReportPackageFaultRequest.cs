namespace Sunder.Protocol;

public sealed record ReportPackageFaultRequest(
    PackageFailureOrigin Origin,
    string Message);
