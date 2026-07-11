namespace Sunder.Runtime.Contracts;

public sealed record ReportPackageFaultRequest(
    PackageFailureOrigin Origin,
    string Message,
    long GenerationId);
