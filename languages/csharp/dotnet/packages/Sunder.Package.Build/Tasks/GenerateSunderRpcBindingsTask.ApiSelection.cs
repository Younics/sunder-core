using Sunder.Package.Build.Rpc.Authoring;

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
        else
        {
            inputs.ExpectAbsent(path);
            foreach (var evidence in BootstrapEvidencePaths(generatedDirectory)) inputs.ExpectAbsent(evidence);
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
            allowBootstrap: baseline is null, validateBaseline: baseline is not null, forceAuthoring: true, inputSnapshot: inputs,
            allowUnboundImportedProviderKeys: providerKey is null ? null : new HashSet<string>(StringComparer.Ordinal) { providerKey },
            validateSelection: compilation =>
            {
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
            if (baseline is null) VerifyFreshAuthoringEvidence(projectDirectory, generatedDirectory);
        }
    }

    private static IEnumerable<string> BootstrapEvidencePaths(string generatedDirectory)
        => [Path.Combine(generatedDirectory, SunderRpcAuthoringDocuments.AuthoringReceiptFileName),
            Path.Combine(generatedDirectory, SunderRpcAuthoringDocuments.PackageExportFileName), GetOwnershipMarkerPath(generatedDirectory)];

    private static void VerifyFreshAuthoringEvidence(string projectDirectory, string generatedDirectory)
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
            GeneratedOutputPathSafety.EnsureRegularDirectory(directory.Path, "initial authoring evidence root");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory.Path))
            {
                if (++count > 32_768 || directory.Depth > 32)
                    throw new InvalidDataException("rpc-authoring.source-limit-exceeded: Initial authoring evidence discovery exceeds its bound.");
                var name = Path.GetFileName(entry);
                if (name is SunderRpcAuthoringDocuments.AuthoringReceiptFileName or SunderRpcAuthoringDocuments.PackageExportFileName
                    || name == GeneratedDirectoryName + ValidateSunderDevOutputPathTask.MarkerFileName)
                    throw new InvalidDataException("rpc-authoring.evidence-mismatch: The existing owned-contract baseline is required. Restore it before configuring imports or accepting source with prior generated evidence.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0 && name is not (".git" or "node_modules"))
                    pending.Push((entry, directory.Depth + 1));
            }
        }
    }
}
