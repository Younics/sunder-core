using System.Text;

namespace Sunder.Cli;

internal sealed class PackageSecretValue : IDisposable
{
    public const int MaximumUtf8Bytes = 1024 * 1024;
    private string? _value;

    public PackageSecretValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Encoding.UTF8.GetByteCount(value) > MaximumUtf8Bytes)
            throw new CliUsageException("The package secret exceeds the Runtime value limit.");
        _value = value;
    }

    public string Value
        => _value ?? throw new ObjectDisposedException(nameof(PackageSecretValue));

    public void Dispose() => _value = null;

    public override string ToString() => "[REDACTED]";
}

internal interface IPackageSecretValueReader
{
    ValueTask<PackageSecretValue> ReadAsync(
        PackageSecretSource source,
        string? environmentVariable,
        CancellationToken cancellationToken);
}

internal sealed class PackageSecretValueReader(
    TextReader standardInput,
    Func<bool> isInputRedirected,
    Func<string, string?>? getEnvironmentVariable = null) : IPackageSecretValueReader
{
    private readonly Func<string, string?> _getEnvironmentVariable
        = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

    public ValueTask<PackageSecretValue> ReadAsync(
        PackageSecretSource source,
        string? environmentVariable,
        CancellationToken cancellationToken)
        => source switch
        {
            PackageSecretSource.Environment => ValueTask.FromResult(ReadEnvironment(environmentVariable)),
            PackageSecretSource.StandardInput => ReadStandardInputAsync(cancellationToken),
            _ => ValueTask.FromException<PackageSecretValue>(new CliUsageException("The package secret source is invalid.")),
        };

    private PackageSecretValue ReadEnvironment(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new CliUsageException("Option '--from-env' requires an environment variable name.");
        var value = _getEnvironmentVariable(name);
        if (value is null)
            throw new CliUsageException($"Environment variable '{name}' is not set.");
        return new PackageSecretValue(value);
    }

    private async ValueTask<PackageSecretValue> ReadStandardInputAsync(CancellationToken cancellationToken)
    {
        if (!isInputRedirected())
            throw new CliUsageException("Option '--stdin' requires redirected standard input.");

        var builder = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var read = await standardInput.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            builder.Append(buffer, 0, read);
            if (builder.Length > PackageSecretValue.MaximumUtf8Bytes + 2)
                throw new CliUsageException("The package secret exceeds the Runtime value limit.");
        }

        if (builder.Length > 0 && builder[^1] == '\n')
        {
            builder.Length--;
            if (builder.Length > 0 && builder[^1] == '\r') builder.Length--;
        }
        return new PackageSecretValue(builder.ToString());
    }
}
