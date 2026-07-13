using Sunder.Runtime.Client;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

internal interface ICredentialCommandRunner
{
    CommandResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        TimeSpan timeout);
}

internal sealed class BoundedCredentialProcessAdapter : ICredentialCommandRunner
{
    public CommandResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        TimeSpan timeout)
    {
        CapturedProcessResult result;
        try
        {
            result = CapturedProcessRunner.Run(fileName, arguments, standardInput, timeout);
        }
        catch (IOException exception) when (exception.Message == $"Could not start process '{fileName}'.")
        {
            throw new IOException($"Could not start credential provider command '{fileName}'.", exception);
        }

        if (result.TimedOut)
        {
            throw new PackageStorageKeyUnavailableException(
                $"Credential provider command '{fileName}' timed out.");
        }

        if (result.OutputTruncated)
        {
            throw new PackageStorageKeyUnavailableException(
                $"Credential provider command '{fileName}' exceeded the output limit.");
        }

        return new CommandResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }
}

internal static class CommandRunner
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private static readonly ICredentialCommandRunner Adapter = new BoundedCredentialProcessAdapter();

    internal static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                // Ignore malformed or inaccessible PATH entries.
            }
        }

        return null;
    }

    internal static CommandResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        TimeSpan? timeout = null) =>
        Adapter.Run(fileName, arguments, standardInput, timeout ?? CommandTimeout);
}
