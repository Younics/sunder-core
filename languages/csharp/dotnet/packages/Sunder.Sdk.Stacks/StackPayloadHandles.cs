using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Stacks;

/// <summary>Provides repeatable stream access to one exported Stack payload file without exposing a filesystem path.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackExportPayloadHandle
{
    /// <summary>Creates an exported payload handle.</summary>
    public StackExportPayloadHandle(
        string relativePath,
        Func<CancellationToken, ValueTask<Stream>> openReadAsync,
        long? length = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(openReadAsync);
        if (length is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
        RelativePath = relativePath;
        OpenReadAsync = openReadAsync;
        Length = length;
    }

    /// <summary>Gets the fragment-relative portable path.</summary>
    public string RelativePath { get; }
    /// <summary>Gets the factory for a fresh readable stream.</summary>
    public Func<CancellationToken, ValueTask<Stream>> OpenReadAsync { get; }
    /// <summary>Gets the known byte length, or <see langword="null"/> when unknown.</summary>
    public long? Length { get; }
}

/// <summary>Provides repeatable read-only access to one imported Stack payload file without exposing extraction paths.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackImportPayloadHandle
{
    /// <summary>Creates an imported payload handle.</summary>
    public StackImportPayloadHandle(
        string relativePath,
        Func<CancellationToken, ValueTask<Stream>> openReadAsync,
        long length)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(openReadAsync);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        RelativePath = relativePath;
        OpenReadAsync = openReadAsync;
        Length = length;
    }

    /// <summary>Gets the fragment-relative portable path.</summary>
    public string RelativePath { get; }
    /// <summary>Gets the factory for a fresh readable stream.</summary>
    public Func<CancellationToken, ValueTask<Stream>> OpenReadAsync { get; }
    /// <summary>Gets the validated byte length.</summary>
    public long Length { get; }
}
