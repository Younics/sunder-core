namespace Sunder.Package.Format;

public sealed record SunderArchiveExtractionOptions
{
    public static SunderArchiveExtractionOptions Default { get; } = new();

    public int MaxEntries { get; init; } = 4096;

    public long MaxEntryUncompressedBytes { get; init; } = 256L * 1024 * 1024;

    public long MaxTotalUncompressedBytes { get; init; } = 1024L * 1024 * 1024;

    public double MaxCompressionRatio { get; init; } = 200;

    public int MaxPathLength { get; init; } = 240;

    public int MaxPathDepth { get; init; } = 32;

    public int MaxMetadataJsonBytes { get; init; } = 1024 * 1024;

    public CancellationToken CancellationToken { get; init; }

    internal void Validate()
    {
        if (MaxEntries <= 0
            || MaxEntryUncompressedBytes <= 0
            || MaxTotalUncompressedBytes <= 0
            || MaxCompressionRatio < 1
            || double.IsNaN(MaxCompressionRatio)
            || double.IsInfinity(MaxCompressionRatio)
            || MaxPathLength <= 0
            || MaxPathDepth <= 0
            || MaxMetadataJsonBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SunderArchiveExtractionOptions), "All archive limits must be positive and the compression ratio must be finite and at least 1.");
        }
    }
}
