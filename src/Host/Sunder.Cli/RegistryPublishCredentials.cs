namespace Sunder.Cli;

internal enum RegistryCredentialSource
{
    Runtime = 0,
    Environment = 1,
    StandardInput = 2,
}

internal static class RegistryCredentialSourceParser
{
    public static bool TryParse(string? value, out RegistryCredentialSource source)
    {
        source = value?.Trim().ToLowerInvariant() switch
        {
            "runtime" => RegistryCredentialSource.Runtime,
            "environment" => RegistryCredentialSource.Environment,
            "stdin" => RegistryCredentialSource.StandardInput,
            _ => (RegistryCredentialSource)(-1),
        };
        return Enum.IsDefined(source);
    }
}

internal sealed class RegistryPublishCredential : IDisposable
{
    public const string EnvironmentVariable = "SUNDER_REGISTRY_PUBLISH_TOKEN";
    public const string Prefix = "sunder_pub_v1_";
    public const int MaximumLength = 512;

    private string _value;

    public RegistryPublishCredential(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new CliAuthenticationException("A Registry publish credential is required.");
        if (value.Length > MaximumLength
            || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || value.Any(char.IsWhiteSpace)
            || value.Any(character => character is < '!' or > '~'))
        {
            throw new CliAuthenticationException("The supplied Registry publish credential is invalid.");
        }
        _value = value;
    }

    public string Value
        => _value.Length > 0
            ? _value
            : throw new ObjectDisposedException(nameof(RegistryPublishCredential));

    public void Dispose() => _value = string.Empty;

    public override string ToString() => "[REDACTED]";
}

internal interface IRegistryPublishCredentialReader
{
    ValueTask<RegistryPublishCredential> ReadAsync(
        RegistryCredentialSource source,
        CancellationToken cancellationToken);
}

internal sealed class RegistryPublishCredentialReader(
    TextReader standardInput,
    Func<bool> isInputRedirected,
    Func<string, string?>? getEnvironmentVariable = null,
    Action<string, string?>? setEnvironmentVariable = null)
    : IRegistryPublishCredentialReader
{
    private readonly Func<string, string?> _getEnvironmentVariable
        = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    private readonly Action<string, string?> _setEnvironmentVariable
        = setEnvironmentVariable ?? Environment.SetEnvironmentVariable;

    public ValueTask<RegistryPublishCredential> ReadAsync(
        RegistryCredentialSource source,
        CancellationToken cancellationToken)
        => source switch
        {
            RegistryCredentialSource.Environment => ValueTask.FromResult(ReadEnvironment()),
            RegistryCredentialSource.StandardInput => ReadStandardInputAsync(cancellationToken),
            _ => ValueTask.FromException<RegistryPublishCredential>(
                new InvalidOperationException("Runtime-managed publication does not read an automation credential.")),
        };

    private RegistryPublishCredential ReadEnvironment()
    {
        var value = _getEnvironmentVariable(RegistryPublishCredential.EnvironmentVariable);
        _setEnvironmentVariable(RegistryPublishCredential.EnvironmentVariable, null);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CliAuthenticationException(
                $"Environment variable {RegistryPublishCredential.EnvironmentVariable} is not set.");
        }
        return new RegistryPublishCredential(value);
    }

    private async ValueTask<RegistryPublishCredential> ReadStandardInputAsync(
        CancellationToken cancellationToken)
    {
        if (!isInputRedirected())
            throw new CliUsageException("Credential source 'stdin' requires redirected standard input.");

        var buffer = new char[RegistryPublishCredential.MaximumLength + 3];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await standardInput.ReadAsync(buffer.AsMemory(count), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        var value = new string(buffer, 0, count);
        value = value.EndsWith("\r\n", StringComparison.Ordinal)
            ? value[..^2]
            : value.EndsWith('\n')
                ? value[..^1]
                : value;
        if (value.Length > RegistryPublishCredential.MaximumLength)
            throw new CliAuthenticationException("The Registry publish credential exceeds the maximum length.");
        return new RegistryPublishCredential(value);
    }
}
