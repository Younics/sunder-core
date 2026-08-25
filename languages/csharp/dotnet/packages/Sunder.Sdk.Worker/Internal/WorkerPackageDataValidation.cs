using Sunder.Sdk.Rpc;
using Sunder.Sdk.Storage;

namespace Sunder.Sdk.Worker.Internal;

internal static class WorkerPackageDataValidation
{
    internal static void ValidateKey(string key, string parameterName)
    {
        if (!PackageStorageValidation.IsValidKey(key))
        {
            throw new ArgumentException(
                $"Package storage keys must be portable ASCII tokens of at most {PackageStorageValidation.MaximumKeyLength} characters.",
                parameterName);
        }
    }

    internal static void ValidatePrefix(string? prefix, string parameterName)
    {
        if (prefix is not null && prefix.Length > 0 && !PackageStorageValidation.IsValidKey(prefix))
        {
            throw new ArgumentException(
                $"Package storage key prefixes must be empty or portable ASCII tokens of at most {PackageStorageValidation.MaximumKeyLength} characters.",
                parameterName);
        }
    }

    internal static void ValidateValue(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!PackageStorageValidation.IsValidValue(value))
        {
            throw new ArgumentException(
                $"Package storage values cannot exceed {PackageStorageValidation.MaximumValueUtf8Bytes} UTF-8 bytes.",
                parameterName);
        }
    }

    internal static void ValidatePath(string relativePath, string parameterName)
    {
        if (!PackageStorageValidation.IsValidRelativePath(relativePath))
        {
            throw new ArgumentException(
                "Package file paths must be relative, portable, non-empty, free of traversal, and within the declared length limits.",
                parameterName);
        }
    }

    internal static void ValidateWorkspacePath(string relativePath, string parameterName)
    {
        if (!PackageStorageValidation.IsValidRelativePath(relativePath))
        {
            throw new ArgumentException(
                "Package workspace paths must be portable, non-empty, relative, and free of traversal.",
                parameterName);
        }
    }

    internal static async Task<T> TranslateValidationAsync<T>(Task<T> operation, string parameterName)
    {
        try
        {
            return await operation.ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Validation)
        {
            throw new ArgumentException(exception.Error.Message, parameterName, exception);
        }
    }

    internal static async Task TranslateValidationAsync(Task operation, string parameterName)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Validation)
        {
            throw new ArgumentException(exception.Error.Message, parameterName, exception);
        }
    }
}
