using Microsoft.Build.Framework;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;

namespace Sunder.Package.Build.Tasks;

public sealed partial class GenerateSunderRpcBindingsTask
{
    public string ContractOperation { get; set; } = string.Empty;

    public string ContractKey { get; set; } = string.Empty;

    public string ContractCurrentVersion { get; set; } = string.Empty;

    public string ContractNextVersion { get; set; } = string.Empty;

    public bool AcknowledgeBreakingChange { get; set; }

    private bool ExecuteOwnedContractOperation()
    {
        try
        {
            if (ContractOperation is not ("Check" or "Accept" or "Freeze"))
                throw new InvalidDataException("ContractOperation must be Check, Accept, or Freeze. Publication evidence cannot be manufactured offline.");
            if (GenerationMode != "Combined" || ExpectedPackageProjectExports.Length != 0)
                throw new InvalidDataException("Owned contract commands require the canonical source project, not a project export/import or RID implementation invocation.");
            if (ContractOperation != "Accept" && (ContractCurrentVersion.Length != 0 || ContractNextVersion.Length != 0 || AcknowledgeBreakingChange))
                throw new InvalidDataException("Contract old/new versions and breaking-change acknowledgement are valid only for AcceptSunderContracts.");
            if (ContractOperation == "Check" && ContractKey.Length != 0)
                throw new InvalidDataException("CheckSunderContracts checks the complete owned contract set; it does not accept SunderContractKey.");
            if (!TryResolveConfiguration(out var projectDirectory, out _, out var generatedDirectory, out _)) return false;
            var path = ResolveOwnedBaselinePath(projectDirectory, OwnedContractsPath);
            var baseline = GeneratedOutputPathSafety.PathExists(path)
                ? SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(path)
                : null;
            if (baseline is null && (ContractOperation != "Accept" || ContractCurrentVersion.Length != 0 || AcknowledgeBreakingChange
                || BootstrapEvidencePaths(generatedDirectory).Any(GeneratedOutputPathSafety.PathExists)))
            {
                throw new InvalidDataException("The existing owned-contract baseline is required. Restore it before checking, freezing, or accepting an existing contract release.");
            }
            if (baseline is null) VerifyFreshAuthoringEvidence(projectDirectory, generatedDirectory);

            // This is the generation pipeline's in-memory plan, never its RPC-tree publisher.
            var candidate = PrepareOwnedContractCandidate(baseline, allowBootstrap: baseline is null, validateBaseline: ContractOperation != "Accept");
            if (candidate is null) return false;
            if (ContractOperation == "Check")
            {
                candidate.Inputs.Verify();
                VerifyExpectedBaseline(projectDirectory, path, baseline!.ExactBytes);
                Log.LogMessage(MessageImportance.High, "Owned Sunder contracts match source; no files changed.");
                return true;
            }

            var proposal = ContractOperation == "Accept"
                ? SunderRpcAuthoringDocuments.PlanOwnedBaselineAcceptance(candidate.Baseline, candidate.Contracts,
                    Optional(ContractKey), Optional(ContractCurrentVersion), Optional(ContractNextVersion), AcknowledgeBreakingChange)
                : SunderRpcAuthoringDocuments.PlanOwnedBaselineFreeze(candidate.Baseline, candidate.Contracts, PackageVersion.Trim(), Optional(ContractKey));
            var proposedBaseline = SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(proposal);
            // Acceptance can allocate new contract versions. Compile their generated bindings and
            // registrations too, rather than committing a baseline that the next Build cannot use.
            if (ContractOperation == "Accept")
            {
                var verified = PrepareOwnedContractCandidate(proposedBaseline);
                if (verified is null) return false;
                candidate.Inputs.Merge(verified.Inputs, requireSameFiles: true);
            }
            if (baseline is null)
                foreach (var evidence in BootstrapEvidencePaths(generatedDirectory)) candidate.Inputs.ExpectAbsent(evidence);
            var changed = CommitOwnedBaseline(projectDirectory, path, baseline?.ExactBytes, proposal, candidate.Inputs,
                beforeCommit: baseline is null ? () => VerifyFreshAuthoringEvidence(projectDirectory, generatedDirectory) : null);
            Log.LogMessage(MessageImportance.High, changed
                ? $"{ContractOperation}SunderContracts updated '{path}'. Run Build to regenerate RPC outputs."
                : "Owned Sunder contracts are unchanged; no files changed.");
            return true;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: false);
            return false;
        }

        static string? Optional(string value) => value.Length == 0 ? null : value;
    }

    internal OwnedContractCandidate? PrepareOwnedContractCandidate(
        SunderRpcOwnedPackageBaseline? baseline,
        bool allowBootstrap = false,
        bool validateBaseline = true)
    {
        var projectDirectory = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(ProjectDirectory);
        GeneratedOutputPathSafety.EnsureRegularDirectory(projectDirectory, "Sunder RPC project directory");
        var generatedDirectory = Path.Combine(Path.GetFullPath(IntermediateOutputPath, projectDirectory), GeneratedDirectoryName);
        var inputs = new SunderRpcInputSnapshot();
        var plan = BuildSourceAuthoringPlan(projectDirectory, generatedDirectory, baseline, allowBootstrap, validateBaseline, forceAuthoring: true, inputSnapshot: inputs);
        return plan is null ? null : new(plan.Baseline!, plan.Contracts, plan.LockSha256, plan.Projection!, inputs);
    }

    internal static string ResolveOwnedBaselinePath(string projectDirectory, string configuredPath)
    {
        var path = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(
            ResolveProjectFile(projectDirectory, configuredPath, SunderRpcAuthoringDocuments.OwnedContractsFileName));
        GeneratedOutputPathSafety.EnsureRegularDirectory(projectDirectory, "owned-contract project root");
        if (!GeneratedOutputPathSafety.IsDescendantOf(path, projectDirectory))
            throw new InvalidDataException("The owned-contract baseline must be a single file below the canonical project root.");
        GeneratedOutputPathSafety.EnsureRegularDirectory(Path.GetDirectoryName(path)!, "owned-contract baseline parent");
        GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "owned-contract baseline");
        if (GeneratedOutputPathSafety.PathExists(path))
            GeneratedOutputPathSafety.EnsureRegularFile(path, "owned-contract baseline");
        return path;
    }

    internal static void VerifyExpectedBaseline(string projectDirectory, string path, byte[]? expected)
    {
        _ = ResolveOwnedBaselinePath(projectDirectory, path);
        var exists = GeneratedOutputPathSafety.PathExists(path);
        if (expected is null ? exists : !exists || !SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(path).ExactBytes.AsSpan().SequenceEqual(expected))
            throw new IOException("The owned-contract baseline changed after planning; no baseline was written. Re-run the command against the current file.");
    }

    internal static bool CommitOwnedBaseline(
        string projectDirectory, string path, byte[]? expected, byte[] proposal, SunderRpcInputSnapshot inputs, Action? beforeCommit = null)
        => CommitOwnedBaselineCore(projectDirectory, path, expected, proposal,
            bytes => SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(bytes).ExactBytes,
            baselinePath => SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(baselinePath).ExactBytes,
            inputs.Verify, _ => beforeCommit?.Invoke());

    internal static bool CommitOwnedPlatformBaseline(
        string coreRoot, byte[]? expected, byte[] proposal, Action verifyInputs, Action<string>? beforeCommit = null,
        CancellationToken cancellationToken = default)
        => CommitOwnedBaselineCore(coreRoot,
            Path.Combine(coreRoot, "eng", "platform-contracts", SunderRpcAuthoringDocuments.OwnedContractsFileName),
            expected, proposal,
            bytes => SunderRpcAuthoringDocuments.ParseOwnedPlatformBaseline(bytes).ExactBytes,
            baselinePath => SunderRpcAuthoringDocuments.ReadOwnedPlatformBaseline(baselinePath).ExactBytes,
            verifyInputs, beforeCommit, cancellationToken);

    internal static bool CommitOwnedPublication(string projectDirectory, string path, byte[] expected, byte[] proposal, Action verifyInputs, CancellationToken token)
        => CommitOwnedBaselineCore(projectDirectory, path, expected, proposal,
            bytes => SunderRpcAuthoringDocuments.ParseOwnedPackageBaseline(bytes).ExactBytes,
            baselinePath => SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(baselinePath).ExactBytes, verifyInputs, null, token);

    private static bool CommitOwnedBaselineCore(
        string projectDirectory, string path, byte[]? expected, byte[] proposal,
        Func<byte[], byte[]> parse, Func<string, byte[]> read, Action verifyInputs, Action<string>? beforeCommit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = ResolveOwnedBaselinePath(projectDirectory, path);
        _ = parse(proposal);
        using var baselineLock = GeneratedOutputLock.Acquire([path], cancellationToken: cancellationToken);
        verifyInputs();
        VerifyExpected();
        cancellationToken.ThrowIfCancellationRequested();
        if (expected is not null && expected.AsSpan().SequenceEqual(proposal)) return false;

        var stage = Path.Combine(Path.GetDirectoryName(path)!, ".sunder-contract-stage-" + Guid.NewGuid().ToString("N"));
        var created = false;
        try
        {
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(proposal);
                stream.Flush(flushToDisk: true);
            }
            beforeCommit?.Invoke(stage);
            if (!read(stage).AsSpan().SequenceEqual(proposal))
                throw new IOException("The staged owned-contract baseline changed before publication.");
            verifyInputs();
            VerifyExpected();
            if (!SunderRpcDescriptorFileReader.ReadStableBytes(stage, proposal.Length, "owned-contract stage")
                .AsSpan().SequenceEqual(proposal))
                throw new IOException("The staged owned-contract baseline changed during input verification.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stage, path, overwrite: expected is not null);
            created = false;
            return true;
        }
        finally
        {
            // Never discover or recursively clean stages in a source tree. Only this invocation's
            // successfully created, still byte-identical regular stage file is eligible for cleanup.
            if (created)
            {
                try
                {
                    GeneratedOutputPathSafety.EnsureRegularFile(stage, "owned-contract stage");
                    if (SunderRpcDescriptorFileReader.ReadStableBytes(stage, proposal.Length, "owned-contract stage")
                        .AsSpan().SequenceEqual(proposal)) File.Delete(stage);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
        }

        void VerifyExpected()
        {
            _ = ResolveOwnedBaselinePath(projectDirectory, path);
            var exists = GeneratedOutputPathSafety.PathExists(path);
            if (expected is null ? exists : !exists || !read(path).AsSpan().SequenceEqual(expected))
                throw new IOException("The owned-contract baseline changed after planning; no baseline was written. Re-run the command against the current file.");
        }
    }

    internal sealed record OwnedContractCandidate(
        SunderRpcOwnedPackageBaseline Baseline,
        IReadOnlyList<SunderRpcCSharpCompiledContract> Contracts,
        string LockSha256,
        SunderRpcAuthoringReceiptProjection Projection,
        SunderRpcInputSnapshot Inputs);
}
