namespace Sunder.Runtime.Contracts;

internal static class RuntimeRequestSizeLimits
{
    internal const long DefaultMaxRequestBodyBytes = 16L * 1024 * 1024;
    internal const long PackageUploadMaxRequestBodyBytes = 512L * 1024 * 1024;
    internal const long StackUploadMaxRequestBodyBytes = 256L * 1024 * 1024;
    internal const long StackMediaUploadMaxRequestBodyBytes = 64L * 1024 * 1024;
    internal const long ExecutionRuntimeUploadMaxRequestBodyBytes = 1024L * 1024 * 1024;
}
