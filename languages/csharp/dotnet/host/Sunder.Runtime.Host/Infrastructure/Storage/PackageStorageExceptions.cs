namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal class PackageStorageException : IOException
{
    public PackageStorageException(string message)
        : base(message)
    {
    }

    public PackageStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal class PackageStorageCorruptionException : PackageStorageException
{
    public PackageStorageCorruptionException(string message, Exception? innerException = null)
        : base(message, innerException ?? new InvalidDataException(message))
    {
    }
}

internal sealed class PackageStorageRecoveryRequiredException : PackageStorageCorruptionException
{
    public PackageStorageRecoveryRequiredException(
        string message,
        string quarantinePath,
        Exception? innerException = null)
        : base(message, innerException)
    {
        QuarantinePath = quarantinePath;
    }

    public string QuarantinePath { get; }
}

internal sealed class PackageStorageKeyUnavailableException : PackageStorageException
{
    public PackageStorageKeyUnavailableException(string message)
        : base(message)
    {
    }

    public PackageStorageKeyUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class PackageStorageNotSupportedException : NotSupportedException
{
    public PackageStorageNotSupportedException(string message)
        : base(message)
    {
    }

    public PackageStorageNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
