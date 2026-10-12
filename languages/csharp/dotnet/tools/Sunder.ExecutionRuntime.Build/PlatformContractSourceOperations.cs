using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.ExecutionRuntime.Build;

// Core engineering only: ordinary source and retained baseline evidence, never package assembly loading or signing.
internal sealed class PlatformContractSourceOperations
{
    internal const string StackKey = "stack-contributor-rpc-service-adapter";
    internal const string StackServiceType = "Sunder.Sdk.Stacks.StackContributorRpcServiceAdapter";
    internal const int MaximumSourceFiles = 64;
    internal const int MaximumSourceBytes = 4_194_304;
    internal const int MaximumSourceDirectories = 128;
    internal const int MaximumSourceDepth = 16;
    internal const int MaximumInputFiles = 1024;
    private const int MaximumDescriptorJsonDepth = 256;
    private const string StackId = "sunder.stack.contributor";
    private const string InitialStackVersion = "2.0.0";
    private readonly string _coreRoot;
    private readonly string _sourceRoot;
    private readonly string[] _sourcePaths;
    private readonly string[] _references;
    private readonly SunderRpcCSharpSourceFile[] _sources;
    private readonly SunderRpcInputSnapshot _inputs = new();
    private readonly SunderRpcOwnedPlatformBaseline? _baseline;
    private readonly CancellationToken _cancellationToken;
    private readonly Action? _duringInputVerification;
    private readonly CoreStackTransportRequest? _buildContext;

    internal PlatformContractSourceOperations(string coreRoot, CancellationToken cancellationToken = default,
        Action? duringInputVerification = null, CoreStackTransportRequest? buildContext = null)
    {
        _buildContext = buildContext;
        _cancellationToken = cancellationToken;
        _duringInputVerification = duringInputVerification;
        cancellationToken.ThrowIfCancellationRequested();
        _coreRoot = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(coreRoot);
        GeneratedOutputPathSafety.EnsureRegularDirectory(_coreRoot, "Core root");
        _ = _inputs.Read(Path.Combine(_coreRoot, "Sunder.Core.slnx"), 1024 * 1024, "Core solution marker");
        var baselinePath = GenerateSunderRpcBindingsTask.ResolveOwnedBaselinePath(_coreRoot,
            Path.Combine(_coreRoot, "eng", "platform-contracts", SunderRpcAuthoringDocuments.OwnedContractsFileName));
        if (GeneratedOutputPathSafety.PathExists(baselinePath))
        {
            _baseline = SunderRpcAuthoringDocuments.ReadOwnedPlatformBaseline(baselinePath);
            _inputs.Record(baselinePath, _baseline.ExactBytes);
        }
        else _inputs.ExpectAbsent(baselinePath);

        _sourceRoot = Path.Combine(_coreRoot, "languages", "csharp", "dotnet", "packages", "Sunder.Sdk.Stacks");
        _sourcePaths = SourcePaths();
        if (buildContext is not null && _sourcePaths.Except(buildContext.SourcePaths, StringComparer.Ordinal).Any())
            throw new InvalidDataException("The evaluated Core Compile request must include the entire ordinary Stack source inventory; omissions are not permitted.");
        var sources = new List<SunderRpcCSharpSourceFile>();
        var remaining = MaximumSourceBytes;
        foreach (var path in buildContext?.SourcePaths ?? _sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = _inputs.Read(path, remaining, "ordinary Core platform source");
            remaining -= bytes.Length;
            var text = new UTF8Encoding(false, true).GetString(bytes);
            sources.Add(new(path, text.StartsWith('\uFEFF') ? text[1..] : text));
        }
        if (buildContext is null) sources.Add(new("CorePlatform.GlobalUsings.g.cs", """
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Net.Http;
            global using System.Threading;
            global using System.Threading.Tasks;
            """));
        _sources = sources.ToArray();
        _references = buildContext?.ReferencePaths.ToArray() ?? ReferencePaths();
        foreach (var reference in _references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = _inputs.Read(reference, 64 * 1024 * 1024, "trusted Core compiler reference");
        }
    }

    internal void Check()
    {
        var baseline = RequireBaseline();
        SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, Compile(baseline));
        VerifyInputs();
    }

    internal string GenerateTransport()
    {
        var baseline = RequireBaseline();
        var (contracts, transport) = CompileWithTransport(baseline);
        SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, contracts);
        VerifyInputs();
        return transport;
    }

    internal string GenerateHostTransport()
    {
        var baseline = RequireBaseline();
        var contracts = Compile(baseline);
        SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, contracts);
        var source = contracts.Single(contract => contract.ServiceType.ToDisplayString() == StackServiceType);
        var domain = source.ServiceType.ContainingAssembly.GetTypeByMetadataName("Sunder.Sdk.Stacks.IPackageStackContributor")
            ?? throw new InvalidDataException("Core Stack source lacks its domain interface.");
        var contract = new SunderRpcCSharpImportedContract(StackId, "Sunder.Runtime.Host.StackTransport", source.Descriptor);
        var adapter = SunderRpcCSharpSourceCompiler.CompileCorePlatformAdapter(domain, contract);
        var wire = SunderRpcCSharpGenerator.Generate(source.Descriptor, contract.GeneratedLogicalName, SunderRpcCSharpAccessibility.Internal);
        // The ordinary generator emits a file-scoped namespace. Keep it in its own
        // block when composing the role-local domain client and shared schema.
        var declaration = "namespace " + contract.GeneratedLogicalName + ";";
        if (!wire.Contains(declaration, StringComparison.Ordinal)) throw new InvalidDataException("Core Stack wire namespace differs from its requested role.");
        wire = wire.Replace(declaration, "namespace " + contract.GeneratedLogicalName + "\n{", StringComparison.Ordinal) + "\n}\n";
        var generated = wire + SunderRpcCSharpImplementationEmitter.EmitCorePlatformClient(adapter);
        VerifyInputs();
        return generated;
    }

    internal bool Accept(string? key, string? oldVersion, string? nextVersion, bool acknowledgeBreaking,
        Action<string>? beforePublication = null)
    {
        if (_baseline is null && (key != StackKey || oldVersion is not null
            || nextVersion != InitialStackVersion || acknowledgeBreaking))
            throw new InvalidDataException($"Initial Core baseline acceptance requires --key {StackKey} --next-version 2.0.0, without an old version or breaking acknowledgement.");
        var contracts = Compile(_baseline);
        if (_baseline is null)
        {
            var descriptor = contracts.Count == 1 && contracts[0].ContractKey == StackKey
                && contracts[0].Exposure == SunderServiceExposure.Public ? contracts[0].Descriptor : null;
            if (descriptor is null || descriptor.ContractId != StackId || descriptor.Version != InitialStackVersion
                || descriptor.DescriptorVersion != 1 || descriptor.SchemaProfile != "sunder.rpc.values.v1"
                || descriptor.Sha256 != "38c8f49840f951aec29398d3641ff2306c906aee12ea80c2277db734bce49898"
                || descriptor.AbiSha256 != "6ca6d6385c720290548592de890f3e3ceb86f3d999167f4e80c6afb15f8a02d8")
                throw new InvalidDataException("Actual Core Stack source does not match the reviewed Stack 2.0.0 migration tuple; no baseline was written.");
        }
        var proposal = SunderRpcAuthoringDocuments.PlanOwnedPlatformBaselineAcceptance(
            _baseline, contracts, key, oldVersion, nextVersion, acknowledgeBreaking);
        var proposed = SunderRpcAuthoringDocuments.ParseOwnedPlatformBaseline(proposal);
        // Recompile the entire source at proposed identities, not just a rewritten descriptor version.
        SunderRpcAuthoringDocuments.ValidateCompiledContracts(proposed, Compile(proposed));
        return GenerateSunderRpcBindingsTask.CommitOwnedPlatformBaseline(
            _coreRoot, _baseline?.ExactBytes, proposal, VerifyInputs, beforePublication, cancellationToken: _cancellationToken);
    }

    internal bool Freeze(string catalogRelease, string? key, Action<string>? beforePublication = null)
    {
        var baseline = RequireBaseline();
        ValidateRelease(catalogRelease, requireFrozen: false);
        var proposal = SunderRpcAuthoringDocuments.PlanOwnedPlatformBaselineFreeze(
            baseline, Compile(baseline), catalogRelease, key);
        return GenerateSunderRpcBindingsTask.CommitOwnedPlatformBaseline(
            _coreRoot, baseline.ExactBytes, proposal, VerifyInputs, beforePublication, cancellationToken: _cancellationToken);
    }

    internal async Task WriteCatalogAsync(string catalogRelease, string outputPath,
        Action<string>? beforePublication = null)
    {
        var baseline = RequireBaseline();
        ValidateRelease(catalogRelease, requireFrozen: true);
        var contracts = Compile(baseline);
        SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, contracts);
        var bytes = SunderPlatformContractCatalog.Create(
            catalogRelease, contracts.Select(static contract => contract.Descriptor).ToArray());
        var destination = EngineeringFileOperations.NormalizePath(outputPath);
        // Conservatively protect case aliases even on case-sensitive macOS/Windows volumes.
        if (destination.StartsWith(Path.Combine(_coreRoot, "languages") + Path.DirectorySeparatorChar,
                OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Platform catalog output must be outside Core language source trees.");
        var parent = EngineeringFileOperations.EnsureDirectory(Path.GetDirectoryName(destination)!);
        var parentCreated = Directory.GetCreationTimeUtc(parent);
        CheckDestination();
        _cancellationToken.ThrowIfCancellationRequested();
        using var lease = GeneratedOutputLock.Acquire([destination], cancellationToken: _cancellationToken);
        VerifyInputs();
        CheckDestination();
        var stage = Path.Combine(parent, $".{Path.GetFileName(destination)}.catalog-{Guid.NewGuid():N}");
        EngineeringFileOperations.FileStamp? owned = null;
        try
        {
            await using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, _cancellationToken);
                stream.Flush(flushToDisk: true);
                owned = EngineeringFileOperations.ObserveFile(stage);
            }
            beforePublication?.Invoke(stage);
            var staged = await EngineeringFileOperations.ReadStableAsync(stage, bytes.Length, _cancellationToken);
            if (!staged.AsSpan().SequenceEqual(bytes) || EngineeringFileOperations.ObserveFile(stage) != owned)
                throw new IOException("Platform catalog stage changed before publication.");
            VerifyInputs();
            CheckDestination();
            var finalBytes = await EngineeringFileOperations.ReadStableAsync(stage, bytes.Length, _cancellationToken);
            if (!finalBytes.AsSpan().SequenceEqual(bytes) || EngineeringFileOperations.ObserveFile(stage) != owned)
                throw new IOException("Platform catalog stage changed during input verification.");
            _cancellationToken.ThrowIfCancellationRequested();
            File.Move(stage, destination, overwrite: false);
            owned = null;
        }
        finally
        {
            EngineeringFileOperations.TryDeleteOwnedFile(stage, owned, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }

        void CheckDestination()
        {
            EngineeringFileOperations.EnsureDirectory(parent);
            if (Directory.GetCreationTimeUtc(parent) != parentCreated)
                throw new IOException("Platform catalog output parent changed after planning.");
            EngineeringFileOperations.EnsureAbsent(destination);
        }
    }

    internal async Task<bool> ImportPublicationAsync(string catalogPath, string registryOrigin, Action<string>? beforePublication = null,
        HttpClient? registryClient = null)
    {
        SunderRpcAuthoringDocuments.RequireCanonicalRegistryOrigin(registryOrigin, "Core publication", allowDevelopmentRegistryOrigin: false);
        var baseline = RequireBaseline(); SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, Compile(baseline));
        var bytes = _inputs.Read(Path.GetFullPath(catalogPath), 67_108_864, "retained platform catalog");
        var catalog = SunderPlatformContractCatalog.Verify(bytes);
        using var ownedClient = registryClient is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan } : null;
        var publishedAt = await new SunderRpcContractRestore(registryClient ?? ownedClient!).VerifyPlatformPublicationAsync(registryOrigin, catalog, _cancellationToken);
        var proposal = SunderRpcAuthoringDocuments.PlanPlatformPublication(baseline, catalog, registryOrigin, publishedAt);
        return GenerateSunderRpcBindingsTask.CommitOwnedPlatformBaseline(_coreRoot, baseline.ExactBytes, proposal, VerifyInputs, beforePublication, _cancellationToken);
    }

    private SunderRpcOwnedPlatformBaseline RequireBaseline()
        => _baseline ?? throw new InvalidDataException("The Core platform baseline is required; restore it or explicitly accept the reviewed initial Stack source.");

    private void ValidateRelease(string catalogRelease, bool requireFrozen)
    {
        if (!SemanticVersion.TryParse(catalogRelease, out var release) || release.Prerelease is not null)
            throw new InvalidDataException("A Core platform catalog release must be an exact stable semantic version.");
        foreach (var contract in RequireBaseline().Contracts.Values)
        {
            if (contract.Current is { } current)
            {
                if (requireFrozen && current.State is not ("release-frozen" or "published"))
                    throw new InvalidDataException("Catalog export requires all current source contracts to be release-frozen or published, never draft.");
                if (current.State != "draft") CheckRelease(current, historical: false);
            }
            foreach (var historical in contract.History) CheckRelease(historical, historical: true);
        }

        void CheckRelease(SunderRpcOwnedContractSnapshot snapshot, bool historical)
        {
            var issued = snapshot.SnapshotJson.GetProperty("catalogRelease").GetProperty("releaseVersion").GetString()!;
            var issuedVersion = SemanticVersion.Parse(issued);
            if (issuedVersion.Prerelease is not null)
                throw new InvalidDataException("Frozen Core platform catalog release evidence must be stable.");
            var comparison = release.ComparePrecedenceTo(issuedVersion);
            if (comparison < 0 || comparison == 0 && (historical || catalogRelease != issued))
                throw new InvalidDataException("Catalog release cannot precede frozen evidence or reuse a replaced release; unchanged issued contracts may be reused in later catalogs.");
        }
    }

    private IReadOnlyList<SunderRpcCSharpCompiledContract> Compile(SunderRpcOwnedPlatformBaseline? baseline)
        => CompileWithTransport(baseline).Contracts;

    private (IReadOnlyList<SunderRpcCSharpCompiledContract> Contracts, string Transport) CompileWithTransport(
        SunderRpcOwnedPlatformBaseline? baseline)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var identities = baseline is not null ? SunderRpcAuthoringDocuments.CompilerIdentities(baseline)
            : new Dictionary<string, SunderRpcCSharpOwnedContractIdentity>(StringComparer.Ordinal)
            { [StackKey] = new(StackId, InitialStackVersion) };
        // Lifecycle commands use a fixed Release context; builds must validate their actual evaluated context.
        var result = CompileTransport(new("sunder.core", InitialStackVersion, _sources, _references,
            DefineConstants: _buildContext?.DefineConstants ?? ["TRACE", "NET", "NET10_0", "NETCOREAPP", "NET5_0_OR_GREATER", "NET6_0_OR_GREATER",
                "NET7_0_OR_GREATER", "NET8_0_OR_GREATER", "NET9_0_OR_GREATER", "NET10_0_OR_GREATER",
                "NETCOREAPP1_0_OR_GREATER", "NETCOREAPP1_1_OR_GREATER", "NETCOREAPP2_0_OR_GREATER",
                "NETCOREAPP2_1_OR_GREATER", "NETCOREAPP2_2_OR_GREATER", "NETCOREAPP3_0_OR_GREATER", "NETCOREAPP3_1_OR_GREATER"],
            OwnedContractsByContractKey: identities,
            NullableContext: _buildContext?.NullableContext ?? NullableContextOptions.Enable));
        _cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    internal static (IReadOnlyList<SunderRpcCSharpCompiledContract> Contracts, string Transport) CompileTransport(
        SunderRpcCSharpCompilerInput input)
    {
        input = input with { ServiceHandlerFactoryTypes = new HashSet<string>(StringComparer.Ordinal) { StackServiceType } };
        var contracts = Derive(input);
        var transport = Emit(contracts);
        var resolved = Derive(input with
        {
            SupplementalSources = [new("CoreStackTransport.g.cs", transport)],
            RequireGeneratedMembersResolved = true,
        });
        if (transport != Emit(resolved) || contracts.Count != resolved.Count
            || contracts.Zip(resolved).Any(pair => pair.First.ContractKey != pair.Second.ContractKey
                || !pair.First.Descriptor.GetCanonicalUtf8().AsSpan().SequenceEqual(pair.Second.Descriptor.GetCanonicalUtf8())))
            throw new InvalidDataException("Core platform generated transport and all source contracts must reach a two-pass fixed point.");
        return (resolved, transport);

        static IReadOnlyList<SunderRpcCSharpCompiledContract> Derive(SunderRpcCSharpCompilerInput context)
        {
            var result = new SunderRpcCSharpSourceCompiler().Compile(context);
            if (!result.Success)
                throw new InvalidDataException("Core platform source compilation failed: " + string.Join(Environment.NewLine, result.Diagnostics));
            return result.Contracts.Select(contract => contract with { Descriptor = WithDialect(contract.Descriptor) }).ToArray();
        }

        static string Emit(IReadOnlyList<SunderRpcCSharpCompiledContract> compiled)
            => SunderRpcCSharpImplementationEmitter.EmitServiceHandler(compiled.SingleOrDefault(contract =>
                contract.ServiceType.ToDisplayString() == StackServiceType)
                ?? throw new InvalidDataException("Core transport requires the actual Stack service adapter contract."));
    }

    internal static SunderRpcContractDescriptor WithDialect(SunderRpcContractDescriptor descriptor)
    {
        var root = JsonNode.Parse(descriptor.GetCanonicalUtf8(),
            documentOptions: new JsonDocumentOptions { MaxDepth = MaximumDescriptorJsonDepth })!.AsObject();
        root["$schema"] = SunderRpcProtocol.JsonSchemaDialect;
        return SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(
            root.ToJsonString(new JsonSerializerOptions { MaxDepth = MaximumDescriptorJsonDepth })));
    }

    internal void VerifyInputs()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        // Test observation never replaces the byte and directory membership checks.
        _duringInputVerification?.Invoke();
        _inputs.Verify();
        if (!_sourcePaths.SequenceEqual(SourcePaths(), StringComparer.Ordinal))
            throw new IOException("The exact ordinary Core platform source input set changed after planning.");
        _cancellationToken.ThrowIfCancellationRequested();
    }

    private string[] SourcePaths()
    {
        var paths = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((_sourceRoot, 0));
        var directories = 1;
        var files = 0;
        while (pending.TryPop(out var directory))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            GeneratedOutputPathSafety.EnsureRegularDirectory(directory.Path, "ordinary Core Stack source directory");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory.Path, "*", SearchOption.TopDirectoryOnly))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "Core platform source input");
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new IOException($"Core platform source input '{path}' must not be a link or device.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (++directories > MaximumSourceDirectories)
                        throw new InvalidDataException($"Core platform source exceeds the {MaximumSourceDirectories}-directory limit.");
                    var name = Path.GetFileName(path);
                    // Fixed Core SDK layout: **/*.cs minus root bin/**, obj/** and **/.*/**.
                    // Generated, nested bin/obj and filesystem Hidden flags are not SDK exclusions.
                    // Builds also snapshot the evaluated Compile set; lifecycle commands use this fixed layout.
                    if (name.StartsWith('.') || directory.Depth == 0
                        && (name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("obj", StringComparison.OrdinalIgnoreCase))) continue;
                    if (directory.Depth >= MaximumSourceDepth)
                        throw new InvalidDataException($"Core platform source exceeds the directory depth limit of {MaximumSourceDepth}.");
                    pending.Push((path, directory.Depth + 1));
                }
                else
                {
                    if (++files > MaximumInputFiles)
                        throw new InvalidDataException($"Core platform source exceeds the {MaximumInputFiles}-file traversal limit.");
                    // Treat .cs/.CS identically on every OS; case must not hide a compilation input.
                    if (!Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                    GeneratedOutputPathSafety.EnsureRegularFile(path, "ordinary Core platform source");
                    if (paths.Count >= MaximumSourceFiles)
                        throw new InvalidDataException($"Core platform source exceeds the {MaximumSourceFiles} C# source file limit.");
                    paths.Add(path);
                }
            }
        }
        if (paths.Count == 0) throw new InvalidDataException("Core platform source requires ordinary C# source files.");
        return paths.Order(StringComparer.Ordinal).ToArray();
    }

    internal static string[] ReferencePaths()
    {
        // Never use the test/application TPA inventory or a compiled Sunder.Sdk.Stacks assembly.
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string[] names = ["System.Private.CoreLib", "System.Runtime", "System.Runtime.Extensions", "System.Collections",
            "System.Linq", "System.ObjectModel", "System.Threading", "System.Threading.Tasks", "System.Memory",
            "System.Text.Json", "System.Net.Http"];
        return names.Select(name => Path.Combine(runtime, name + ".dll"))
            .Append(typeof(SunderServiceAttribute).Assembly.Location)
            .Select(GeneratedOutputPathSafety.NormalizeTrustedSystemAliases).ToArray();
    }
}
