using Microsoft.Build.Framework;

namespace Sunder.Package.Build.Tasks;

/// <summary>Stabilizes SDK-generated runtime metadata before role outputs are qualified.</summary>
public sealed class NormalizeSunderRuntimeMetadataTask : Microsoft.Build.Utilities.Task
{
    [Required] public ITaskItem[] Files { get; set; } = [];

    public override bool Execute()
    {
        try
        {
            foreach (var file in Files)
            {
                GeneratedOutputPathSafety.EnsureRegularFile(file.ItemSpec, "generated runtime metadata");
                var bytes = File.ReadAllBytes(file.ItemSpec);
                var length = 0;
                for (var index = 0; index < bytes.Length; index++)
                {
                    // JSON string newlines are escaped. Change only the SDK's raw
                    // CRLF whitespace, retaining all values, ordering and encoding.
                    if (bytes[index] == '\r' && index + 1 < bytes.Length && bytes[index + 1] == '\n') continue;
                    bytes[length++] = bytes[index];
                }
                if (length != bytes.Length) File.WriteAllBytes(file.ItemSpec, bytes.AsSpan(0, length));
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.LogError($"Could not normalize generated runtime metadata: {error.Message}");
            return false;
        }
    }
}
