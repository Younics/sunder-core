using Sunder.Package.Format;

namespace Sunder.Cli;

internal sealed class ArchiveValidationService
{
    public Task<SunderPackageArchiveValidationResult> ValidatePackageAsync(string path, CancellationToken token)
        => ValidateAsync(
            path,
            "package",
            (source, staging) => SunderPackageArchiveInspector.ExtractAndValidateAsync(source, staging, token));

    public Task<SunderStackArchiveValidationResult> ValidateStackAsync(string path, CancellationToken token)
        => ValidateAsync(
            path,
            "stack",
            (source, staging) => SunderStackArchiveInspector.ExtractAndValidateAsync(source, staging, token));

    private static async Task<T> ValidateAsync<T>(string path, string kind, Func<string, string, Task<T>> validate)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException($"{char.ToUpperInvariant(kind[0])}{kind[1..]} file '{fullPath}' was not found.", fullPath);
        var stagingPath = Path.Combine(Path.GetTempPath(), "sunder-cli-validate", kind, Guid.NewGuid().ToString("N"));
        try
        {
            return await validate(fullPath, stagingPath).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true);
            }
            catch
            {
                // Temporary validation cleanup is best effort.
            }
        }
    }
}
