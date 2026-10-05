using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Package.Build.Authoring;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Format;
using static Sunder.Package.Build.Authoring.SunderPrebuiltRoleInputs;

namespace Sunder.Package.Build.Tasks;

public sealed partial class ManageSunderPackageRoleInputsTask
{
    private ITaskItem Export()
    {
        var input = CapturedInputs.Single(); var frame = Frame(input);
        var leaf = Full(DevPackagePath); var parent = Path.GetDirectoryName(leaf)!;
        if (Path.GetFileName(leaf) != "sunder-dev") throw Invalid("The native input must be a canonical sunder-dev leaf.");
        var output = Path.Combine(parent, FileName); var digest = output + ".sha256";
        var planRoot = Path.GetDirectoryName(Full(RolePlanPath))!;
        var planItem = new TaskItem(planRoot); planItem.SetMetadata("DiscoveryRoot", Path.GetDirectoryName(planRoot)!);
        var planLease = new ManageSunderRpcGeneratedOutputLockTask { BuildEngine = BuildEngine, Action = "Acquire", ProjectDirectory = ProjectDirectory, OutputDirectories = [planItem] };
        if (!planLease.Execute()) throw Invalid("Could not acquire the native role-plan lease.");
        try
        {
            using var lease = GeneratedOutputLock.Acquire([GeneratedOutputLock.TargetLeafDiscoveryKey(parent, leaf)], [leaf, output, digest], cancellationToken: _cancellation.Token);
            RequireHash(RolePlanSha256);
            var planBytes = SunderRpcDescriptorFileReader.ReadStableBytes(RolePlanPath, 32 * 1024 * 1024, "native role plan");
            if (Hash(planBytes) != RolePlanSha256) throw Invalid("Native role plan changed.");
            using var document = JsonDocument.Parse(planBytes); var plan = document.RootElement;
            // Qualification includes the reviewed tree's discovery envelope. Bind
            // it here to actual compiled inputs, rather than putting unrelated
            // tree membership into the role snapshot's compilation identity.
            var compiledPaths = plan.GetProperty("sourceFiles").EnumerateArray()
                .Select(source => Full(Path.GetFullPath(source.GetProperty("path").GetString()!, ProjectDirectory))).ToHashSet(StringComparer.Ordinal);
            if (frame.Items.Where(item => item.Kind == "compile").Any(item => !compiledPaths.Contains(Child(input.GetMetadata("SourceRoot"), item.Identity))))
                throw Invalid("Compiled source membership omits a captured authored input.");
            foreach (var source in plan.GetProperty("sourceFiles").EnumerateArray())
            {
                var path = Path.GetFullPath(source.GetProperty("path").GetString()!, ProjectDirectory);
                var sourceBytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, 4 * 1024 * 1024, "compiled authored source");
                var start = sourceBytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
                if (Hash(sourceBytes[start..]) != source.GetProperty("sha256").GetString()) throw Invalid("Compiled source differs from the captured reviewed source.");
            }
            foreach (var resource in plan.GetProperty("resources").EnumerateArray())
                if (File(resource.GetProperty("sourcePath").GetString()!, "resource", 64 * 1024 * 1024, _cancellation.Token).Sha256 != resource.GetProperty("sha256").GetString())
                    throw Invalid("Compiled resource differs from current reviewed input.");
            var defines = frame.Properties["DefineConstants"].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            if (!defines.SequenceEqual(plan.GetProperty("defineConstants").EnumerateArray().Select(value => value.GetString()), StringComparer.Ordinal)) throw Invalid("Role-plan compiler constants differ from the captured frame.");
            var roles = new List<SunderPrebuiltRole>();
            foreach (var role in plan.GetProperty("hasApp").GetBoolean() ? new[] { "worker", "app" } : ["worker"])
            {
                var reader = new ReadSunderPackageRoleInputsTask { BuildEngine = BuildEngine, PlanPath = RolePlanPath, ExpectedSha256 = RolePlanSha256,
                    ProjectIdentity = frame.ProjectIdentity, Configuration = frame.Configuration, TargetFramework = frame.TargetFramework,
                    RuntimeIdentifier = frame.RuntimeIdentifier, PackageVersion = frame.PackageVersion, Role = role, OutputLockLeaseId = planLease.LeaseId };
                if (!reader.Execute()) throw Invalid("Native role inputs no longer match their compiled plan.");
                var dependencies = plan.GetProperty(role + "Dependencies");
                var generated = plan.GetProperty(role + "Compile").EnumerateArray()
                    .Concat(dependencies.ValueKind == JsonValueKind.Null ? [] : dependencies.GetProperty("files").EnumerateArray())
                    .Concat(plan.GetProperty("resources").EnumerateArray().Where(value => value.GetProperty("roles").GetString() is "shared" || value.GetProperty("roles").GetString() == role)
                        ).GroupBy(value => value.GetProperty("path").GetString()!, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group =>
                    {
                        var hashes = group.Select(value => value.GetProperty("sha256").GetString()!).Distinct(StringComparer.Ordinal).ToArray();
                        if (hashes.Length != 1) throw Invalid("Conflicting native generated-input hashes.");
                        return NativeFile(Child(planRoot, group.Key), group.Key, hashes[0]);
                    }).ToArray();
                var receipt = System.IO.File.ReadAllText(reader.AuthoringReceipts.Single().ItemSpec, Encoding.UTF8);
                roles.Add(new(role, dependencies.ValueKind == JsonValueKind.Null ? [] : dependencies.GetProperty("libraries").EnumerateArray().Select(value => value.GetString()!).ToArray(),
                    generated, References(role + "References"), References(role + "RuntimeReferences"), receipt));
            }
            var files = Tree(leaf, source: false, _cancellation.Token);
            var evidence = new SunderPrebuiltEvidence(1, frame, new(plan.GetProperty("inputFingerprint").GetString()!, RolePlanSha256, roles.Count == 2, roles.ToArray()), "sunder-dev", files);
            _ = ValidateEvidence(evidence, input, leaf, requireFrozen: false);
            var bytes = Write(evidence); var hash = Hash(bytes);
            var stage = output + ".stage-" + Guid.NewGuid().ToString("N"); var digestStage = digest + ".stage-" + Guid.NewGuid().ToString("N");
            try
            {
                GeneratedOutputTransaction.RecoverAndCleanup(output, digest);
                if (!System.IO.File.Exists(output) || !SunderRpcDescriptorFileReader.ReadStableBytes(output, MaximumDocumentBytes, "role-input evidence").AsSpan().SequenceEqual(bytes)
                    || !System.IO.File.Exists(digest) || Encoding.UTF8.GetString(SunderRpcDescriptorFileReader.ReadStableBytes(digest, 65, "role-input digest")) != hash + "\n")
                {
                    System.IO.File.WriteAllBytes(stage, bytes); System.IO.File.WriteAllText(digestStage, hash + "\n", new UTF8Encoding(false));
                    VerifyCapture(input); Equal(files, Tree(leaf, source: false, _cancellation.Token), "Native payload");
                    _cancellation.Token.ThrowIfCancellationRequested();
                    GeneratedOutputTransaction.Commit(new(output, stage), new(digest, digestStage));
                }
            }
            finally { if (System.IO.File.Exists(stage)) System.IO.File.Delete(stage); if (System.IO.File.Exists(digestStage)) System.IO.File.Delete(digestStage); }
            var result = new TaskItem(output); result.SetMetadata("Sha256", hash); result.SetMetadata("RuntimeIdentifier", frame.RuntimeIdentifier);
            Log.LogMessage(MessageImportance.High, "Qualified role inputs: {0} sha256={1}", output, hash);
            return result;

            SunderPrebuiltFile[] References(string name) => plan.GetProperty(name).EnumerateArray()
                .Select(value => NativeFile(value.GetProperty("path").GetString()!, value.GetProperty("name").GetString()!, value.GetProperty("sha256").GetString()!))
                .OrderBy(value => value.Path, StringComparer.Ordinal).ToArray();

            SunderPrebuiltFile NativeFile(string path, string identity, string expectedHash)
            {
                var file = File(path, identity, 64 * 1024 * 1024, _cancellation.Token);
                if (file.Sha256 != expectedHash) throw Invalid("Native input changed after role-plan validation.");
                return file;
            }
        }
        finally
        {
            var release = new ManageSunderRpcGeneratedOutputLockTask { BuildEngine = BuildEngine, Action = "Release", ProjectDirectory = ProjectDirectory, LeaseId = planLease.LeaseId };
            if (!release.Execute()) throw Invalid("Could not release the native role-plan lease.");
        }
    }

    private ITaskItem Validate()
    {
        var input = CapturedInputs.Single(); var frame = Frame(input);
        var selectionPath = Full(SelectionFile);
        var selectionBytes = SunderRpcDescriptorFileReader.ReadStableBytes(selectionPath, 32768, "prebuilt selection");
        var selection = Read<SunderPrebuiltSelection>(selectionBytes, 32768);
        ValidateSelection(selection);
        var selected = selection.Inputs.SingleOrDefault(value => value.RuntimeIdentifier == frame.RuntimeIdentifier)
            ?? throw Invalid("The requested exact RID has no qualified native input.");
        var path = Child(Path.GetDirectoryName(selectionPath)!, selected.Path);
        var leaf = Path.Combine(Path.GetDirectoryName(path)!, "sunder-dev");
        using var lease = GeneratedOutputLock.Acquire([GeneratedOutputLock.TargetLeafDiscoveryKey(Path.GetDirectoryName(path)!, leaf)], [path, leaf, selectionPath], cancellationToken: _cancellation.Token);
        var evidence = ReadEvidence(path, selected.Sha256);
        _ = ValidateEvidence(evidence, input, leaf, requireFrozen: true);
        var result = new TaskItem(input) { ItemSpec = path };
        result.SetMetadata("EvidenceSha256", selected.Sha256); result.SetMetadata("SelectionFile", selectionPath);
        result.SetMetadata("SelectionSha256", Hash(selectionBytes)); result.SetMetadata("RuntimeIdentifier", frame.RuntimeIdentifier);
        return result;
    }

    private void Pack()
    {
        if (ValidatedInputs.Length is < 1 or > 6 || ExpectedRuntimeIdentifiers.Length is < 1 or > 6
            || ExpectedRuntimeIdentifiers.Distinct(StringComparer.Ordinal).Count() != ExpectedRuntimeIdentifiers.Length
            || !ExpectedRuntimeIdentifiers.Order(StringComparer.Ordinal).SequenceEqual(ValidatedInputs.Select(input => input.GetMetadata("RuntimeIdentifier")).Order(StringComparer.Ordinal)))
            throw Invalid("Prebuilt fan-in requires exactly one complete native input for every selected RID.");
        var selectionPath = Full(SelectionFile);
        var selectionBytes = SunderRpcDescriptorFileReader.ReadStableBytes(selectionPath, 32768, "prebuilt selection");
        var selection = Read<SunderPrebuiltSelection>(selectionBytes, 32768); ValidateSelection(selection);
        if (!selection.Inputs.Select(input => input.RuntimeIdentifier).Order(StringComparer.Ordinal).SequenceEqual(ExpectedRuntimeIdentifiers.Order(StringComparer.Ordinal)))
            throw Invalid("Prebuilt selection contains missing or extra RIDs.");
        var leafPaths = ValidatedInputs.Select(input => Path.Combine(Path.GetDirectoryName(Full(input.ItemSpec))!, "sunder-dev")).ToArray();
        var keys = leafPaths.Select(leaf => GeneratedOutputLock.TargetLeafDiscoveryKey(Path.GetDirectoryName(leaf)!, leaf));
        var authoredPaths = ValidatedInputs.SelectMany(input => new[] { Child(input.GetMetadata("SourceRoot"), Frame(input).OwnedBaseline), Child(input.GetMetadata("SourceRoot"), Frame(input).ContractLock) });
        BeforeInputLeaseAcquisition?.Invoke();
        using var leases = GeneratedOutputLock.Acquire(keys, leafPaths.Concat(ValidatedInputs.Select(input => input.ItemSpec)).Concat(authoredPaths).Append(selectionPath), cancellationToken: _cancellation.Token);
        var manifests = VerifyAll();
        var stage = Path.Combine(Path.GetTempPath(), "sunder-prebuilt-pack-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            var leaves = leafPaths.Select(path => { var item = new TaskItem(path); item.SetMetadata("DiscoveryRoot", Path.GetDirectoryName(path)!); return (ITaskItem)item; }).ToArray();
            var aggregate = new AggregateSunderPackageTask { BuildEngine = BuildEngine, TargetLeaves = leaves,
                OutputPath = Path.Combine(stage, "sunder-dev"), OutputDiscoveryRoot = stage, TargetDirectory = stage,
                ProjectDirectory = ProjectDirectory, ExpectedPackageVersion = PackageVersion, InputsAlreadyLeased = true, CancellationToken = _cancellation.Token };
            if (!aggregate.Execute()) throw Invalid("Prebuilt composition failed; the prior archive was retained.");
            var pack = new PackSunderPackageTask { BuildEngine = BuildEngine, DevPackagePath = aggregate.NormalizedOutputPath,
                DevPackageDiscoveryRoot = stage, PackageOutputPath = PackageOutputPath, ProjectDirectory = ProjectDirectory,
                QualifiedPrebuiltValidation = manifest =>
                {
                    _ = VerifyAll();
                    PackSunderPackageTask.RequireMatchingProjection(manifests[0], manifest);
                }, BeforeArchivePublication = BeforeArchivePublication, CancellationToken = _cancellation.Token };
            if (!pack.Execute()) throw Invalid("Prebuilt archive publication failed; the prior archive was retained.");
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }

        SunderPackageManifest[] VerifyAll()
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            if (!SunderRpcDescriptorFileReader.ReadStableBytes(selectionPath, 32768, "prebuilt selection").AsSpan().SequenceEqual(selectionBytes)) throw Invalid("Prebuilt selection changed while waiting/packing.");
            var output = new List<SunderPackageManifest>();
            foreach (var input in ValidatedInputs)
            {
                if (input.GetMetadata("SelectionSha256") != Hash(selectionBytes) || Full(input.GetMetadata("SelectionFile")) != selectionPath) throw Invalid("Mixed selection frames.");
                var selected = selection.Inputs.Single(value => value.RuntimeIdentifier == input.GetMetadata("RuntimeIdentifier"));
                if (Full(input.ItemSpec) != Full(Child(Path.GetDirectoryName(selectionPath)!, selected.Path)) || input.GetMetadata("EvidenceSha256") != selected.Sha256) throw Invalid("A validated artifact locator changed.");
                var evidence = ReadEvidence(input.ItemSpec, selected.Sha256);
                output.Add(ValidateEvidence(evidence, input, Path.Combine(Path.GetDirectoryName(input.ItemSpec)!, "sunder-dev"), requireFrozen: true));
            }
            return output.ToArray();
        }
    }

    private SunderPackageManifest ValidateEvidence(SunderPrebuiltEvidence evidence, ITaskItem input, string leaf, bool requireFrozen)
    {
        if (evidence.SchemaVersion != 1 || evidence.Leaf != "sunder-dev" || evidence.RolePlan is null || evidence.Inputs is null
            || evidence.Files is null || evidence.Files.Length is < 1 or > MaximumFiles) throw Invalid("Unsupported or incomplete role-input evidence.");
        var frame = Frame(input);
        var missingInputs = evidence.Inputs.ExternalInputs.Select(file => file.Path).Except(frame.ExternalInputs.Select(file => file.Path), StringComparer.Ordinal).ToArray();
        var extraInputs = frame.ExternalInputs.Select(file => file.Path).Except(evidence.Inputs.ExternalInputs.Select(file => file.Path), StringComparer.Ordinal).ToArray();
        if (missingInputs.Length != 0 || extraInputs.Length != 0) throw Invalid("External input membership differs: missing=" + string.Join(",", missingInputs.Take(5)) + "; extra=" + string.Join(",", extraInputs.Take(5)));
        Equal(frame, evidence.Inputs, "Reviewed authoring inputs"); VerifyCapture(input);
        RequireHash(evidence.RolePlan.NativePlanSha256); RequireHash(evidence.RolePlan.InputFingerprint);
        if (evidence.RolePlan.Roles is not { Length: >= 1 and <= 2 } || evidence.RolePlan.Roles.Any(role => role is null))
            throw Invalid("Invalid native role set.");
        var names = evidence.RolePlan.Roles.Select(role => role.Role).ToArray();
        if (!names.SequenceEqual(evidence.RolePlan.HasApp ? new[] { "worker", "app" } : ["worker"])) throw Invalid("The complete Worker/App role set is required.");
        Equal(evidence.Files, Tree(leaf, source: false, _cancellation.Token), "Native leaf bytes/membership");
        var validation = SunderPackageArchiveInspector.ValidateExtractedPackageAsync(leaf, _cancellation.Token).GetAwaiter().GetResult();
        if (!validation.Success) throw Invalid("Canonical native leaf validation failed: " + string.Join(" | ", validation.Errors));
        var manifest = validation.Manifest!;
        if (manifest.Id != frame.PackageId || manifest.Version != frame.PackageVersion
            || manifest.Targets!.Any(target => target!.Rid != frame.RuntimeIdentifier || target.TargetFramework != frame.TargetFramework)
            || !manifest.Targets!.Select(target => target!.Role!).Order(StringComparer.Ordinal).SequenceEqual((evidence.RolePlan.HasApp ? new[] { "app", "runtime" } : ["runtime"])))
            throw Invalid("The native package/version/framework/RID/role closure differs from its source frame.");
        var baseline = SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(Child(input.GetMetadata("SourceRoot"), frame.OwnedBaseline));
        if (baseline.Sha256 != frame.OwnedBaselineSha256 || baseline.PackageId != frame.PackageId) throw Invalid("Owned baseline changed.");
        var projections = evidence.RolePlan.Roles.Select(role =>
        {
            ValidateFiles(role.GeneratedInputs, MaximumFiles, requireNonempty: true);
            ValidateFiles(role.References, 4096, requireNonempty: false);
            ValidateFiles(role.RuntimeReferences, 4096, requireNonempty: false);
            if (role.Libraries is null || role.Libraries.Length > 8192 || role.Libraries.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 1024)
                || role.Libraries.Distinct(StringComparer.OrdinalIgnoreCase).Count() != role.Libraries.Length) throw Invalid("Invalid role library closure.");
            using var receipt = JsonDocument.Parse(role.Receipt);
            var appReceipt = receipt.RootElement.TryGetProperty("role", out var receiptRole);
            if ((role.Role == "app") != appReceipt || appReceipt && receiptRole.GetString() != "app") throw Invalid("Mixed Worker/App receipt roles.");
            if (receipt.RootElement.GetProperty("inputFingerprint").GetString() != evidence.RolePlan.InputFingerprint) throw Invalid("Mixed role-plan/receipt source frames.");
            return SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(Encoding.UTF8.GetBytes(role.Receipt), frame.ContractLockSha256, frame.PackageId, frame.PackageVersion);
        }).ToArray();
        var owned = projections[0].Manifest.ContractBundles!.Where(bundle => bundle!.Source == "owned").Select(bundle => bundle!.ContractId!).Order(StringComparer.Ordinal);
        if (!owned.SequenceEqual(baseline.Contracts.Values.Where(contract => contract.Current is not null).Select(contract => contract.ContractId).Order(StringComparer.Ordinal))) throw Invalid("Owned API membership differs from the reviewed baseline.");
        foreach (var (key, contract) in baseline.Contracts.Where(pair => pair.Value.Current is not null))
        {
            if (requireFrozen && contract.Current!.State is not ("release-frozen" or "published")) throw Invalid("Prebuilt packing requires frozen/published owned APIs.");
            if (!projections[0].Contracts.TryGetValue(key, out var descriptor) || !descriptor.GetCanonicalUtf8().AsSpan().SequenceEqual(contract.Current!.Descriptor.GetCanonicalUtf8())) throw Invalid("Owned descriptor/ABI identity differs from the reviewed baseline.");
            var bundle = projections[0].Manifest.ContractBundles!.Single(value => value!.ContractId == descriptor.ContractId)!;
            if (bundle.Exposure != contract.Current!.Exposure.ToString().ToLowerInvariant()) throw Invalid("Owned API exposure differs from the reviewed baseline.");
        }
        if (evidence.RolePlan.HasApp)
        {
            var workerReceipt = JsonNode.Parse(evidence.RolePlan.Roles[0].Receipt)!;
            var appReceipt = JsonNode.Parse(evidence.RolePlan.Roles[1].Receipt)!;
            var appContracts = new JsonArray(workerReceipt["contracts"]!.AsArray()
                .Where(contract => baseline.Contracts.TryGetValue(contract!["contractKey"]!.GetValue<string>(), out var ownedContract) && ownedContract.Current is not null)
                .Select(contract => { var copy = contract!.DeepClone().AsObject(); copy.Remove("runtimeUse"); return (JsonNode)copy; }).ToArray());
            if (!JsonNode.DeepEquals(appContracts, appReceipt["contracts"])) throw Invalid("App receipt does not project the Worker's complete owned API and App-use policy.");
        }
        var worker = projections[0].Manifest;
        var expected = new SunderPackageManifest { Id = worker.Id, Version = worker.Version, ContractBundles = worker.ContractBundles,
            Provides = worker.Provides, UsesContracts = projections.SelectMany(projection => projection.Manifest.UsesContracts ?? []).ToArray(),
            PackageArtifactImports = worker.PackageArtifactImports, PackageApiImports = worker.PackageApiImports,
            ProviderImports = worker.ProviderImports, PlatformImports = worker.PlatformImports };
        PackSunderPackageTask.RequireMatchingProjection(expected, manifest);
        return expected;

        static void ValidateFiles(SunderPrebuiltFile[] files, int maximum, bool requireNonempty)
        {
            if (files is null || files.Length > maximum || requireNonempty && files.Length == 0) throw Invalid("Invalid role-input file count.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); string? previous = null; long total = 0;
            foreach (var file in files)
            {
                if (file is null || !ArchiveRelativePath.TryParse(file.Path, 1024, 32, out _, out _) || !seen.Add(file.Path)
                    || previous is not null && string.CompareOrdinal(previous, file.Path) >= 0 || file.Bytes < 0
                    || file.Bytes > 64 * 1024 * 1024 || (total += file.Bytes) > MaximumSourceBytes) throw Invalid("Invalid role-input path/order/size closure.");
                RequireHash(file.Sha256); previous = file.Path;
            }
        }
    }

    private static SunderPrebuiltEvidence ReadEvidence(string path, string hash)
    {
        RequireHash(hash); var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, MaximumDocumentBytes, "qualified role-input evidence");
        if (Hash(bytes) != hash) throw Invalid("Role-input evidence differs from the explicitly selected SHA-256.");
        return Read<SunderPrebuiltEvidence>(bytes);
    }
    private static void ValidateSelection(SunderPrebuiltSelection selection)
    {
        if (selection.SchemaVersion != 1 || selection.Inputs is null || selection.Inputs.Length is < 1 or > 6 || selection.Inputs.Any(input => input is null)
            || selection.Inputs.Select(input => input.RuntimeIdentifier).Distinct(StringComparer.Ordinal).Count() != selection.Inputs.Length
            || selection.Inputs.Select(input => input.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selection.Inputs.Length) throw Invalid("Invalid prebuilt input selection.");
        foreach (var input in selection.Inputs)
        {
            if (!Rids.Contains(input.RuntimeIdentifier) || !ArchiveRelativePath.TryParse(input.Path, 1024, 32, out _, out _) || Path.GetFileName(input.Path) != FileName) throw Invalid("Invalid exact-RID artifact locator.");
            RequireHash(input.Sha256);
        }
    }
}
