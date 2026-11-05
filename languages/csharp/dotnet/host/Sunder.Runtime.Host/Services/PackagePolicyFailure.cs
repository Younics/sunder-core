using System.ComponentModel;
using System.Diagnostics;

namespace Sunder.Runtime.Host.Services;

internal static class PackagePolicyFailure
{
    internal static string Describe(Exception error)
    {
        var failures = new List<string>();
        for (var depth = 0; error is not null && depth < 4; depth++, error = error.InnerException!)
        {
            // Exception messages can contain response bodies, URLs or local paths.
            // Retain native codes and source operations, never those messages.
            var operation = new StackTrace(error, fNeedFileInfo: false).GetFrames()
                .Select(frame => frame.GetMethod())
                .FirstOrDefault(method => method?.DeclaringType?.Assembly == typeof(PackagePolicyFailure).Assembly);
            var type = error.GetType().Name;
            var site = operation is null ? "unknown" : $"{operation.DeclaringType!.Name}.{operation.Name}";
            failures.Add($"{type[..Math.Min(type.Length, 80)]}(hresult=0x{error.HResult:x8},native={(error as Win32Exception)?.NativeErrorCode},operation={site[..Math.Min(site.Length, 160)]})");
        }
        return string.Join(" <- ", failures);
    }
}
