using Sunder.Package.Build.Rpc.Authoring;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sunder.Package.Build.Tasks;

public sealed partial class GenerateSunderRpcBindingsTask
{
    private static IEnumerable<string> SelectionProviders(Sunder.Package.Build.Rpc.Authoring.CSharp.SunderRpcCSharpCompilationResult compilation)
        => compilation.ImportedProviders.Select(provider => string.Join('\0', provider.Surface.Key, provider.Provider.ProviderId,
            provider.Provider.ServiceId, provider.Provider.Audience.ToString())).Order(StringComparer.Ordinal);

    // A genuinely new project first configures its dependency graph. Its complete
    // source may already name several not-yet-selected provider interfaces. Import
    // materialization is checked now; first Accept checks the complete source graph.
    // Accepted projects always use the normal semantic/generator fixed point.
    internal void ValidateApiSelection(string operation, string? providerKey)
    {
        if (!TryResolveConfiguration(out var projectDirectory, out _, out var generatedDirectory, out _))
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: Package API source inputs could not be resolved.");
        var path = ResolveOwnedBaselinePath(projectDirectory, OwnedContractsPath);
        var baseline = GeneratedOutputPathSafety.PathExists(path) ? SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(path) : null;
        VerifyBootstrap();
        var inputs = new SunderRpcInputSnapshot();
        if (baseline is not null) inputs.Record(path, baseline.ExactBytes);
        else inputs.ExpectAbsent(path);
        if (baseline is null && !HasPriorAuthoringEvidence(projectDirectory, generatedDirectory, publicOnly: false))
        {
            foreach (var evidence in BootstrapEvidencePaths(generatedDirectory))
                if (File.Exists(evidence)) _ = inputs.Read(evidence, 67_108_864, "prior private authoring evidence"); else inputs.ExpectAbsent(evidence);
            ValidateSourceDocumentDimensions();
            var sources = ReadSourceFiles(projectDirectory, inputs)
                ?? throw new InvalidDataException("rpc-authoring.evidence-mismatch: Initial dependency source inputs could not be captured.");
            if (sources.Count > 512 || sources.Sum(source => (long)Utf8NoBom.GetByteCount(source.Text)) > 4_194_304)
                throw new InvalidDataException("rpc-authoring.source-limit-exceeded: Initial dependency source inputs exceed the authoring budget.");
            var lockPath = ResolveProjectFile(projectDirectory, ContractLockPath, SunderRpcAuthoringDocuments.ContractLockFileName);
            var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(lockPath, AllowDevelopmentRegistryOrigin);
            inputs.Record(lockPath, contractLock.ExactBytes);
            _ = Sunder.Package.Build.Rpc.Authoring.CSharp.SunderRpcCSharpImportPrepass.Prepare(contractLock,
                string.IsNullOrWhiteSpace(ContractCacheRoot) ? SunderRpcContractCache.DefaultRoot : Path.GetFullPath(ContractCacheRoot, projectDirectory));
            inputs.Verify();
            VerifyBootstrap();
            return;
        }
        var plan = BuildSourceAuthoringPlan(projectDirectory, generatedDirectory, baseline,
            allowBootstrap: baseline is null, validateBaseline: operation != "MigrateConsumerRoles", forceAuthoring: true, inputSnapshot: inputs,
            allowUnboundImportedProviderKeys: providerKey is null ? null : new HashSet<string>(StringComparer.Ordinal) { providerKey },
            validateSelection: compilation =>
            {
                if (operation == "MigrateConsumerRoles" && baseline is not null) SunderRpcAuthoringDocuments.ValidatePublicCompiledContracts(baseline, compilation.Contracts);
                if (operation == "Unimplement" && compilation.ImportedProviders.Any(provider => provider.Surface.Key == providerKey))
                    throw new InvalidDataException("package-api.remove-still-referenced: Remove local provider bindings before unimplementing this contract.");
            });
        if (plan is null)
            throw new InvalidDataException(operation == "PlatformUnimplement"
                ? "platform-api.remove-still-referenced: Remove provider bindings and resolve source diagnostics before unimplementing."
                : "rpc-authoring.evidence-mismatch: Proposed package API source generation failed; the committed lock is unchanged.");
        inputs.Verify();
        VerifyExpectedBaseline(projectDirectory, path, baseline?.ExactBytes);
        VerifyBootstrap();

        void VerifyBootstrap()
        {
            if (baseline is null) VerifyNoLostPublicBaseline(projectDirectory, generatedDirectory);
        }
    }

    private static IEnumerable<string> BootstrapEvidencePaths(string generatedDirectory)
        => [Path.Combine(generatedDirectory, SunderRpcAuthoringDocuments.AuthoringReceiptFileName),
            Path.Combine(generatedDirectory, SunderRpcAuthoringDocuments.PackageExportFileName), GetOwnershipMarkerPath(generatedDirectory)];

    private static void VerifyFreshAuthoringEvidence(string projectDirectory, string generatedDirectory)
    {
        if (HasPriorAuthoringEvidence(projectDirectory, generatedDirectory, publicOnly: false)) throw MissingBaseline();
    }

    internal static void VerifyNoLostPublicBaseline(string projectDirectory, string generatedDirectory, Action<string>? beforeDirectoryRead = null)
    {
        if (HasPriorAuthoringEvidence(projectDirectory, generatedDirectory, publicOnly: true, beforeDirectoryRead)) throw MissingBaseline();
    }

    private static InvalidDataException MissingBaseline() => new("rpc-authoring.evidence-mismatch: The existing owned-contract baseline is required. Restore it before configuring imports or accepting source with prior public generated evidence.");

    private static bool HasPriorAuthoringEvidence(string projectDirectory, string generatedDirectory, bool publicOnly, Action<string>? beforeDirectoryRead = null)
    {
        // Canonical builds publish below configuration/RID-specific analysis and
        // immutable handoff roots. Checking only the current neutral output would
        // allow a deleted baseline to re-bootstrap by selecting another build frame.
        var roots = new[] { projectDirectory, Path.GetDirectoryName(generatedDirectory)! }.Distinct(PathComparer);
        var visited = new HashSet<string>(PathComparer);
        var pending = new Stack<(string Path, int Depth)>(roots.Select(root => (root, 0)));
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            if (!visited.Add(directory.Path) || !Directory.Exists(directory.Path)) continue;
            beforeDirectoryRead?.Invoke(directory.Path);
            string[] entries;
            try
            {
                GeneratedOutputPathSafety.EnsureRegularDirectory(directory.Path, "initial authoring evidence root");
                entries = Directory.EnumerateFileSystemEntries(directory.Path).Take(32_769 - count).ToArray();
            }
            catch (IOException) when (!GeneratedOutputPathSafety.PathExists(directory.Path))
            {
                // Output-lock transition candidates and cleanable directories may
                // disappear after discovery. Existing/replaced/unsafe paths still fail.
                continue;
            }
            foreach (var entry in entries)
            {
                if (++count > 32_768 || directory.Depth > 32)
                    throw new InvalidDataException("rpc-authoring.source-limit-exceeded: Initial authoring evidence discovery exceeds its bound.");
                var name = Path.GetFileName(entry);
                if ((name is SunderRpcAuthoringDocuments.AuthoringReceiptFileName or SunderRpcAuthoringDocuments.PackageExportFileName
                    || name == GeneratedDirectoryName + ValidateSunderDevOutputPathTask.MarkerFileName)
                    && (!publicOnly || HasPublicOrUnknownEvidence(entry)))
                    return true;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (IOException) when (!GeneratedOutputPathSafety.PathExists(entry)) { continue; }
                if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0 && name is not (".git" or "node_modules"))
                    pending.Push((entry, directory.Depth + 1));
            }
        }
        return false;
    }

    private static bool HasPublicOrUnknownEvidence(string path)
    {
        var name = Path.GetFileName(path);
        if (name == GeneratedDirectoryName + ValidateSunderDevOutputPathTask.MarkerFileName)
        {
            var root = path[..^ValidateSunderDevOutputPathTask.MarkerFileName.Length];
            var proof = new[] { SunderRpcAuthoringDocuments.AuthoringReceiptFileName, SunderRpcAuthoringDocuments.PackageExportFileName }
                .Select(file => Path.Combine(root, file)).Where(File.Exists).ToArray();
            return proof.Length != 0 ? proof.Any(HasPublicOrUnknownEvidence) : !IsVerifiedEditorOnlyOutput(root);
        }
        var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, 67_108_864, "retained authoring evidence");
        using var document = Sunder.Package.Format.SunderRpcAuthoringJson.ParseRegistryMetadata(bytes, 67_108_864);
        var rootElement = document.RootElement;
        var contracts = rootElement.GetProperty("contracts").EnumerateArray().ToArray();
        if (contracts.Length > 128) throw new InvalidDataException("rpc-authoring.source-limit-exceeded: Prior contract evidence exceeds its bound.");
        var owned = name == SunderRpcAuthoringDocuments.PackageExportFileName ? null
            : rootElement.GetProperty("bundles").EnumerateArray().Where(bundle => bundle.GetProperty("source").GetString() == "owned")
                .Select(bundle => bundle.GetProperty("contractKey").GetString()).ToHashSet(StringComparer.Ordinal);
        return contracts.Any(contract => (owned is null || owned.Contains(contract.GetProperty("contractKey").GetString()))
            && contract.GetProperty("exposure").GetString() != "package");
    }

    private static bool IsVerifiedEditorOnlyOutput(string root)
    {
        var parent = new DirectoryInfo(Path.GetDirectoryName(root)!);
        var immutable = parent.Parent?.Name == "sunder-editor-snapshots" && parent.Parent.Parent?.Name == "sunder-design-time";
        if (parent.Name != "sunder-design-time" && !immutable) return false;
        var path = Path.Combine(root, "design-time-only.json");
        if (!File.Exists(path) || !HasValidOwnershipMarker(GetOwnershipMarkerPath(root))) return false;
        try
        {
            var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, 67_108_864, "editor-only evidence");
            if (immutable && Convert.ToHexStringLower(SHA256.HashData(bytes)) != parent.Name) return false;
            using var document = Sunder.Package.Format.SunderRpcAuthoringJson.ParseRegistryMetadata(bytes, 67_108_864);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 8
                || value.GetProperty("schemaVersion").GetInt32() != 1 || value.GetProperty("purpose").GetString() != "editor-imports-only"
                || value.GetProperty("artifactEligible").ValueKind != JsonValueKind.False
                || new[] { "projectIdentity", "configuration", "targetFramework" }.Any(name => value.GetProperty(name).GetString() is not { Length: > 0 and <= 256 })
                || !HashText(value.GetProperty("lockSha256").GetString())) return false;
            var sources = value.GetProperty("generatedSources");
            if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() is < 1 or > 4096) return false;
            var expected = new Dictionary<string, string>(PathComparer);
            foreach (var source in sources.EnumerateArray())
            {
                if (source.ValueKind != JsonValueKind.Object || source.EnumerateObject().Count() != 2
                    || source.GetProperty("path").GetString() is not { } relative
                    || !TryNormalizeRelativeOutputFile(relative, out var normalized, out _) || relative != normalized.Replace(Path.DirectorySeparatorChar, '/')
                    || source.GetProperty("sha256").GetString() is not { } hash || !HashText(hash)
                    || !expected.TryAdd(relative, hash)) return false;
            }
            var pending = new Stack<string>(); pending.Push(root); var count = 0; long totalBytes = bytes.Length;
            while (pending.TryPop(out var directory))
            {
                GeneratedOutputPathSafety.EnsureRegularDirectory(directory, "editor-only output");
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (++count > 8192) return false;
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) return false;
                    if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                    var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                    if (relative == "design-time-only.json") continue;
                    if (!expected.Remove(relative, out var hash)) return false;
                    var content = SunderRpcDescriptorFileReader.ReadStableBytes(entry, 67_108_864, "editor-only generated source");
                    totalBytes += content.Length;
                    if (totalBytes > 67_108_864 || Convert.ToHexStringLower(SHA256.HashData(content)) != hash) return false;
                }
            }
            return expected.Count == 0 && SunderRpcDescriptorFileReader.ReadStableBytes(path, 67_108_864, "editor-only evidence").AsSpan().SequenceEqual(bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return false; // Unproven editor metadata never dismisses an unknown marker.
        }

        static bool HashText(string? text) => text is { Length: 64 } && text.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}
