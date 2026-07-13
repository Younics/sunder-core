namespace Sunder.Package.Format;

public static class SunderArchive
{
    public static Task ExtractAtomicAsync(
        string archivePath,
        string destinationPath,
        SunderArchiveExtractionOptions? options = null)
        => SunderArchiveExtractor.ExtractAtomicAsync(archivePath, destinationPath, options);

    public static void ExtractAtomic(
        string archivePath,
        string destinationPath,
        SunderArchiveExtractionOptions? options = null)
        => SunderArchiveExtractor.ExtractAtomic(archivePath, destinationPath, options);

    public static Task WriteDeterministicAsync(
        string sourceDirectory,
        string archivePath,
        CancellationToken cancellationToken = default)
        => SunderArchiveDeterministicWriter.WriteAsync(sourceDirectory, archivePath, cancellationToken);

    public static Task WriteDeterministicAsync(
        string sourceDirectory,
        string archivePath,
        SunderArchiveExtractionOptions options)
        => SunderArchiveDeterministicWriter.WriteAsync(sourceDirectory, archivePath, options);

    public static void WriteDeterministic(string sourceDirectory, string archivePath)
        => SunderArchiveDeterministicWriter.Write(sourceDirectory, archivePath);

    public static IReadOnlyList<(ArchiveRelativePath Path, string FullPath)> EnumerateFiles(string rootPath)
        => SunderArchiveFileSystem.EnumerateFiles(rootPath);

    public static string ResolveFile(string rootPath, ArchiveRelativePath relativePath)
        => SunderArchiveFileSystem.ResolveFile(rootPath, relativePath);

    public static Task<string> ReadMetadataJsonAsync(
        string rootPath,
        ArchiveRelativePath relativePath,
        int maxBytes,
        CancellationToken cancellationToken)
        => SunderArchiveMetadataReader.ReadJsonAsync(rootPath, relativePath, maxBytes, cancellationToken);
}
