namespace Sunder.Package.Format;

public static class SunderExecutionRuntimeFormat
{
    private static readonly IReadOnlyList<string> RuntimeIdentifiers = Array.AsReadOnly(
        ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]);

    public const int CurrentSchemaVersion = 1;
    public const int CurrentContentIndexVersion = 1;
    public const int CurrentNativeAttestationVersion = 1;
    public const string Extension = ".sunderruntime";
    public const string ManifestPath = "manifest/sunder-runtime.json";
    public const string ContentIndexPath = "manifest/content-index.json";
    public const string SignaturePath = "manifest/sunder-runtime.sig";
    public const string NativeAttestationPath = "payload/provenance/native-build-attestation.v1.json";
    public const string PayloadRoot = "payload/";
    public const string WorkerProtocol = "sunder.worker.v1";
    public const string SigningAlgorithm = "ed25519";
    public const string DotNetRuntimeId = SunderPackageFormat.DotNetExecutionRuntimeId;
    public const string NodeRuntimeId = SunderPackageFormat.NodeExecutionRuntimeId;
    public const string DotNetModuleAbi = SunderPackageFormat.DotNetWorkerModuleAbi;
    public const string NodeModuleAbi = SunderPackageFormat.NodeWorkerModuleAbi;
    public const string PackageEntryPointPlaceholder = "{package.entryPoint}";
    public const string DotNetExecArgument = "exec";
    public const string ReleaseSignatureDomain = "sunder.execution-runtime.v1\0";
    public const string NativeAttestationSignatureDomain = "sunder.execution-runtime.native-attestation.v1\0";
    public const string LauncherSmokePassed = "passed";
    public const string PlatformCheckPassed = "passed";
    public const string PlatformCheckNotApplicable = "not-applicable";

    public const long MaxArchiveBytes = 1024L * 1024 * 1024;
    public const int MaxArchiveEntries = 8192;
    public const int MaxContentIndexEntries = MaxArchiveEntries - 3;
    public const long MaxEntryUncompressedBytes = 1024L * 1024 * 1024;
    public const long MaxTotalUncompressedBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxMetadataBytes = 4 * 1024 * 1024;
    public const double MaxCompressionRatio = 200;
    public const int MaxArchivePathLength = 240;
    public const int MaxArchivePathDepth = 32;
    public const int MaxPackagingRevision = 1_000_000;
    public const int MaxLaunchArguments = 16;
    public const int MaxLaunchArgumentLength = 512;
    public const int MaxLaunchArgumentsTotalLength = 4096;
    public const int MaxLicenses = 64;
    public const int MaxLicenseLength = 128;
    public const int MaxKeyIdLength = 128;
    public const int MaxIdentityLength = 128;
    public const int MaxUpstreamNameLength = 128;
    public const int MaxSourceUriLength = 2048;
    public const int MaxWorkflowValueLength = 256;
    public const int Ed25519PublicKeyBytes = 32;
    public const int Ed25519SignatureBytes = 64;

    public static IReadOnlyList<string> SupportedRuntimeIdentifiers => RuntimeIdentifiers;

    internal static SunderArchiveExtractionOptions CreateExtractionOptions(CancellationToken cancellationToken)
        => new()
        {
            MaxEntries = MaxArchiveEntries,
            MaxEntryUncompressedBytes = MaxEntryUncompressedBytes,
            MaxTotalUncompressedBytes = MaxTotalUncompressedBytes,
            MaxCompressionRatio = MaxCompressionRatio,
            MaxPathLength = MaxArchivePathLength,
            MaxPathDepth = MaxArchivePathDepth,
            MaxMetadataJsonBytes = MaxMetadataBytes,
            CancellationToken = cancellationToken,
        };

    internal static bool IsPayloadPath(string path)
        => path.StartsWith(PayloadRoot, StringComparison.Ordinal)
           && path.Length > PayloadRoot.Length
           && ArchiveRelativePath.TryParse(
               path,
               MaxArchivePathLength,
               MaxArchivePathDepth,
               out _,
               out _);

    internal static bool IsMetadataPath(string path)
        => path is ManifestPath or ContentIndexPath or SignaturePath;

}
