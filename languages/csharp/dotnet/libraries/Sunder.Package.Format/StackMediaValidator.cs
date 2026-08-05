namespace Sunder.Package.Format;

internal static class StackMediaValidator
{
    private const long MaxMediaSize = 10L * 1024L * 1024L;
    private const int MaxMediaDimension = 8192;
    private static readonly HashSet<string> AllowedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/gif",
        "image/jpeg",
        "image/png",
        "image/webp",
    };

    public static void Validate(
        IReadOnlyList<SunderStackMediaManifest>? mediaItems,
        string stagingPath,
        ICollection<string> errors)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var media in mediaItems ?? [])
        {
            if (media is null)
            {
                errors.Add("Stack media entry is null.");
                continue;
            }
            var label = string.IsNullOrWhiteSpace(media.FileName) ? media.Path ?? "unknown" : media.FileName;
            if (string.IsNullOrWhiteSpace(media.Path))
            {
                errors.Add($"Stack media '{label}' is missing path.");
                continue;
            }

            if (!StackArchivePathValidator.TryParse(media.Path, "media path", errors, out var path))
            {
                continue;
            }

            var normalizedPath = path.ToString();
            if (!normalizedPath.StartsWith(SunderStackFormat.MediaPayloadRoot, StringComparison.Ordinal))
            {
                errors.Add($"Stack media '{label}' path '{media.Path}' must be under {SunderStackFormat.MediaPayloadRoot}.");
            }
            else if (!seenPaths.Add(normalizedPath))
            {
                errors.Add($"Stack media path '{normalizedPath}' is declared more than once.");
            }

            var filePath = SunderArchive.ResolveFile(stagingPath, path);
            if (!File.Exists(filePath))
            {
                errors.Add($"Stack media '{label}' path '{media.Path}' was not found in the Stack archive.");
                continue;
            }

            var fileLength = new FileInfo(filePath).Length;
            if (fileLength <= 0)
            {
                errors.Add($"Stack media '{label}' is empty.");
                continue;
            }
            else if (fileLength > MaxMediaSize)
            {
                errors.Add($"Stack media '{label}' must be 10 MB or smaller.");
                continue;
            }

            if (media.Size is null or <= 0)
            {
                errors.Add($"Stack media '{label}' must declare size.");
            }
            else if (media.Size != fileLength)
            {
                errors.Add($"Stack media '{label}' size mismatch.");
            }

            if (string.IsNullOrWhiteSpace(media.FileName) || media.FileName != Path.GetFileName(media.FileName))
            {
                errors.Add($"Stack media '{label}' must declare a fileName without path separators.");
            }

            if (string.IsNullOrWhiteSpace(media.ContentType) || !AllowedMediaTypes.Contains(media.ContentType))
            {
                errors.Add($"Stack media '{label}' must be a PNG, JPEG, WebP, or GIF image.");
            }
            else if (!ImageFileInspector.TryRead(filePath, out var image, out var imageError))
            {
                errors.Add($"Stack media '{label}' is invalid: {imageError}.");
            }
            else if (!string.Equals(media.ContentType, image.ContentType, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Stack media '{label}' declares contentType '{media.ContentType}' but contains '{image.ContentType}'.");
            }
            else if (!ImageFileInspector.ExtensionMatches(media.FileName!, image.ContentType)
                     || !ImageFileInspector.ExtensionMatches(normalizedPath, image.ContentType))
            {
                errors.Add($"Stack media '{label}' file extension does not match its '{image.ContentType}' signature.");
            }
            else if (image.Width > MaxMediaDimension || image.Height > MaxMediaDimension)
            {
                errors.Add($"Stack media '{label}' dimensions must not exceed {MaxMediaDimension}x{MaxMediaDimension} pixels.");
            }

            if (media.SortOrder is < 0)
            {
                errors.Add($"Stack media '{label}' sortOrder must not be negative.");
            }

            if (media.AltText?.Length > 500)
            {
                errors.Add($"Stack media '{label}' altText must be 500 characters or shorter.");
            }
        }
    }
}
