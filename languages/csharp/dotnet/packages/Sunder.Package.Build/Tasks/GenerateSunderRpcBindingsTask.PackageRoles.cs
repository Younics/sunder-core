using System.Security.Cryptography;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sunder.Package.Build.Authoring;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;

namespace Sunder.Package.Build.Tasks;

public sealed partial class GenerateSunderRpcBindingsTask
{
    private const string RolePlanPath = "package-role-plan.v1.json";

    private bool AddPackageRoleSources(List<SourceGeneratedFile> files, string directory, string projectDirectory,
        SunderRpcCSharpCompilationResult compilation, IReadOnlyList<SunderRpcCSharpSourceFile> sources,
        IReadOnlyList<SunderRpcCSharpSourceFile> generated, string inputFingerprint,
        IReadOnlyList<SunderPackageResourceInput> resourceInputs, IReadOnlyList<SunderRpcCSharpSourceFile> frameworkSources,
        IReadOnlyList<SunderPackageFileEvidence> frameworkInputs, IReadOnlyList<SunderFrameworkSourceGenerator> roleGenerators,
        SunderRpcContractLock contractLock, SunderRpcCSharpImportPrepassResult imports, IReadOnlySet<string> presentRoles, SunderRpcInputSnapshot inputSnapshot)
    {
        var paths = sources.Select(source => source.Path).ToHashSet(StringComparer.Ordinal);
        var sourceCompilation = compilation.SourceCompilation!;
        var markup = SunderPackageResourcePlanner.Markup(sourceCompilation, resourceInputs);
        var plan = SunderPackageRolePlanner.Plan(sourceCompilation, sourceCompilation.SyntaxTrees.Where(tree => paths.Contains(tree.FilePath)).ToArray(),
            compilation.Contracts.SelectMany(contract => contract.SourceTypes).Select(value => value.SourceType), markup.Types, compilation.TrustedImportedModelTypes);
        if (!plan.Success) { LogCompilerDiagnostics(plan.Diagnostics); return false; }
        var analysis = SunderPackageAuthoringCompiler.Complete(new(compilation, plan, generated) { OrdinarySourcePaths = paths });
        if (!analysis.Success) { LogCompilerDiagnostics(analysis.RolePlan.Diagnostics); return false; }
        var resources = SunderPackageResourcePlanner.Plan(analysis, resourceInputs, RootNamespace.Length == 0 ? AssemblyName : RootNamespace);
        foreach (var resource in resources)
            AddSourceGeneratedFile(files, directory, resource.SnapshotPath, resourceInputs.Single(input => input.Kind == resource.Kind && input.SourcePath == resource.SourcePath).Contents,
                false, SourceOutputScope.Implementation);
        var hasApp = plan.Views.Count != 0 || plan.Types.Any(type => type.Roles == SunderPackageRoleMembership.App);
        if (!hasApp && resources.Any(resource => resource.Roles == SunderPackageRoleMembership.App))
            throw new InvalidDataException("package-authoring.resource-role-conflict: App resources require an App source role.");
        var workerReceipt = files.Single(file => file.RelativeOutputPath == SunderRpcAuthoringDocuments.AuthoringReceiptFileName);
        using var receiptDocument = JsonDocument.Parse(workerReceipt.Contents);
        var lockSha256 = receiptDocument.RootElement.GetProperty("lockSha256").GetString()!;
        if (hasApp)
        {
            var appReceipt = SunderRpcAuthoringDocuments.WriteAuthoringReceipt(GetProducerVersion(), inputFingerprint,
                contractLock, compilation.ResolvedPackageId, PackageVersion.Trim(), compilation, imports, "app", presentRoles);
            _ = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(appReceipt, lockSha256, compilation.ResolvedPackageId, PackageVersion.Trim());
            AddSourceGeneratedFile(files, directory, SunderRpcAuthoringDocuments.AppAuthoringReceiptFileName, appReceipt, false, SourceOutputScope.Implementation);
        }
        var references = new Dictionary<SunderPackageRoleMembership, SunderPackageRoleReference[]>();
        var runtimeReferences = new Dictionary<SunderPackageRoleMembership, SunderPackageRoleReference[]>();
        var semanticAssemblies = new Dictionary<SunderPackageRoleMembership, IReadOnlySet<string>>();
        foreach (var role in hasApp ? new[] { SunderPackageRoleMembership.Worker, SunderPackageRoleMembership.App } : [SunderPackageRoleMembership.Worker])
        {
            var prefix = role == SunderPackageRoleMembership.App ? "roles/app/" : "roles/worker/";
            if (role == SunderPackageRoleMembership.App)
                foreach (var source in frameworkSources)
                    AddSourceGeneratedFile(files, directory, prefix + source.Path, Utf8NoBom.GetBytes(source.Text), false, SourceOutputScope.Implementation);
            var projected = SunderPackageRoleProjection.Project(analysis, role);
            foreach (var source in projected)
            {
                var relative = Path.GetRelativePath(projectDirectory, source.Path).Replace('\\', '/');
                var name = Convert.ToHexStringLower(SHA256.HashData(Utf8NoBom.GetBytes(relative)));
                // Repository editorconfig commonly marks every obj/**/*.cs file as
                // generated. Preserve the authored compilation's nullable context
                // explicitly, even though these snapshots retain a plain .cs suffix.
                var text = (NullableEnabled ? "#nullable enable\n" : "#nullable disable\n")
                    + "#line 1 " + Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(source.Path, quote: true) + "\n" + source.Text;
                AddSourceGeneratedFile(files, directory, prefix + "source/" + name + ".cs", Utf8NoBom.GetBytes(text), false, SourceOutputScope.Implementation);
            }
            var composition = role == SunderPackageRoleMembership.App ? SunderPackageCompositionEmitter.EmitApp(analysis) : SunderPackageCompositionEmitter.EmitWorker(analysis);
            AddSourceGeneratedFile(files, directory, prefix + composition.Path, Utf8NoBom.GetBytes(composition.Text), false, SourceOutputScope.Implementation);
            if (role == SunderPackageRoleMembership.App)
                foreach (var source in SunderRpcCSharpServiceApiEmitter.EmitAppClients(compilation.Contracts, compilation.ResolvedPackageId, PackageVersion.Trim()))
                    AddSourceGeneratedFile(files, directory, prefix + source.Path, Utf8NoBom.GetBytes(source.Text), false, SourceOutputScope.Implementation);
            else
                foreach (var source in SunderPackageCompositionEmitter.EmitWorkerClients(analysis, compilation.ResolvedPackageId, PackageVersion.Trim()))
                    AddSourceGeneratedFile(files, directory, prefix + source.Path, Utf8NoBom.GetBytes(source.Text), false, SourceOutputScope.Implementation);

            var roleFiles = files.Where(file => file.RelativeOutputPath.StartsWith(prefix, StringComparison.Ordinal)
                || file.Compile && (role == SunderPackageRoleMembership.Worker || file.RelativeOutputPath != "authoring.g.cs"));
            var parse = (CSharpParseOptions)sourceCompilation.SyntaxTrees.First().Options;
            var roleCompilation = CSharpCompilation.Create(role == SunderPackageRoleMembership.App ? AssemblyName + ".App" : AssemblyName,
                roleFiles.Select(file => CSharpSyntaxTree.ParseText(StrictUtf8.GetString(file.Contents), parse, file.OutputPath)),
                sourceCompilation.References, sourceCompilation.Options.WithOutputKind(role == SunderPackageRoleMembership.App ? OutputKind.DynamicallyLinkedLibrary : OutputKind.ConsoleApplication));
            var roleGeneratedSources = roleGenerators.SelectMany(generator => generator.Generate(roleCompilation)).ToArray();
            foreach (var source in roleGeneratedSources)
            {
                var path = prefix + source.Path;
                AddSourceGeneratedFile(files, directory, path, Utf8NoBom.GetBytes(source.Text), false, SourceOutputScope.Implementation);
                roleCompilation = roleCompilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(source.Text, parse, Path.Combine(directory, path)));
            }
            var errors = roleCompilation.GetDiagnostics().Where(error => error.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0)
            {
                foreach (var error in errors) Log.LogError("package-authoring.role-compilation-invalid: " + error);
                return false;
            }
            roleCompilation = RemoveUnusedRoleUsings(roleCompilation, files, prefix, out var bodyAssemblies);
            semanticAssemblies[role] = bodyAssemblies;
            references[role] = RoleReferences(roleCompilation);
            runtimeReferences[role] = RoleRuntimeReferences(roleCompilation);
        }
        var dependencies = new Dictionary<SunderPackageRoleMembership, SunderPackageDependencyPlan>();
        if (ProjectAssetsFile.Length != 0)
        {
            var planner = new SunderPackageDependencyPlanner(ProjectAssetsFile, ProjectFile, ProjectExtensionsPath, TargetFramework, RuntimeIdentifier,
                PackageReferences, ProjectReferences, ProjectImports, ReferencePaths, RuntimeReferencePaths);
            foreach (var (role, bodyAssemblies) in semanticAssemblies)
                planner.UseAssemblies(role, bodyAssemblies.Concat(runtimeReferences[role].Select(reference => reference.Name))
                    .Concat(role == SunderPackageRoleMembership.App ? markup.Assemblies : []), ReferencePaths.Concat(RuntimeReferencePaths).ToArray());
            planner.Complete();
            foreach (var role in semanticAssemblies.Keys)
            {
                var dependency = planner.Project(role);
                dependencies.Add(role, dependency);
                var prefix = role == SunderPackageRoleMembership.App ? "roles/app/restore/" : "roles/worker/restore/";
                foreach (var file in dependency.Files) AddSourceGeneratedFile(files, directory, prefix + file.Path, file.Contents, false, SourceOutputScope.Implementation);
            }
            planner.VerifyInputs();
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            compilationCacheRoot = Path.Combine(Path.GetDirectoryName(directory)!, "sunder-package-compilations"),
            packageId = compilation.ResolvedPackageId,
            projectIdentity = ProjectIdentity.Trim(),
            configuration = Configuration.Trim(),
            targetFramework = TargetFramework.Trim(),
            runtimeIdentifier = RuntimeIdentifier.Trim(),
            packageVersion = PackageVersion.Trim(),
            lockSha256,
            defineConstants = DefineConstants.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            sourceSha256 = sources.Select(SourceHash).Order(StringComparer.Ordinal).ToArray(),
            sourceFiles = sources.OrderBy(source => source.Path, StringComparer.Ordinal).Select(source => new
            {
                path = Path.GetRelativePath(projectDirectory, source.Path).Replace('\\', '/'),
                sha256 = SourceHash(source),
            }).ToArray(),
            inputFingerprint,
            assemblyName = AssemblyName,
            hasApp,
            resources = resources.Select(resource => new
            {
                kind = resource.Kind, sourcePath = resource.SourcePath, path = resource.SnapshotPath, sha256 = resource.Sha256,
                roles = resource.Roles.ToString().ToLowerInvariant(), metadata = resource.Metadata,
            }).ToArray(),
            frameworkInputs,
            frameworkProperties = GeneratorProperties.OrderBy(property => property.ItemSpec, StringComparer.Ordinal).ToDictionary(property => property.ItemSpec, property => property.GetMetadata("Value")),
            frameworkGenerators = frameworkInputs.Select(input => Path.GetFileName(input.Path)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            types = plan.Types.Select(type => new
            {
                metadataName = type.MetadataName,
                roles = type.Roles.ToString().ToLowerInvariant(),
                sources = type.SourcePaths.Select(path => Path.GetRelativePath(projectDirectory, path).Replace('\\', '/')).ToArray(),
                portableValue = type.IsPortableValue,
                explicitRole = type.HasExplicitRole,
            }).ToArray(),
            views = plan.Views,
            composition = new
            {
                worker = Construction(analysis.Composition!.Worker),
                app = Construction(analysis.Composition.App),
            },
            workerCompile = CompileFiles("roles/worker/", app: false),
            appCompile = hasApp ? CompileFiles("roles/app/", app: true) : [],
            workerReferences = references[SunderPackageRoleMembership.Worker],
            appReferences = hasApp ? references[SunderPackageRoleMembership.App] : [],
            workerRuntimeReferences = runtimeReferences[SunderPackageRoleMembership.Worker],
            appRuntimeReferences = hasApp ? runtimeReferences[SunderPackageRoleMembership.App] : [],
            workerReceipt = Receipt(SunderRpcAuthoringDocuments.AuthoringReceiptFileName),
            appReceipt = hasApp ? Receipt(SunderRpcAuthoringDocuments.AppAuthoringReceiptFileName) : null,
            workerDependencies = Dependency(SunderPackageRoleMembership.Worker),
            appDependencies = hasApp ? Dependency(SunderPackageRoleMembership.App) : null,
        });
        AddSourceGeneratedFile(files, directory, RolePlanPath, bytes, false, SourceOutputScope.Implementation);
        return true;

        string SourceHash(SunderRpcCSharpSourceFile source) => inputSnapshot.CapturedSha256(source.Path);

        object? Dependency(SunderPackageRoleMembership role)
        {
            if (!dependencies.TryGetValue(role, out var dependency)) return null;
            var prefix = role == SunderPackageRoleMembership.App ? "roles/app/restore/" : "roles/worker/restore/";
            return new
            {
                libraries = dependency.Libraries, excludedProjects = dependency.ExcludedProjects,
                compilePaths = dependency.CompilePaths, runtimePaths = dependency.RuntimePaths,
                inputs = dependency.Inputs,
                files = dependency.Files.Select(file => new { path = prefix + file.Path, sha256 = Convert.ToHexStringLower(SHA256.HashData(file.Contents)) }).ToArray(),
                projectAssetsFile = prefix + "project.assets.json", projectExtensionsPath = prefix,
            };
        }

        object[] Construction(IReadOnlyList<SunderPackageServiceConstruction> entries) => entries.Select(entry => (object)new
        {
            type = entry.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), lifetime = entry.Transient ? "transient" : "singleton", lifecycle = entry.Lifecycle, activation = entry.Activation,
            arguments = entry.Arguments.Select(argument => new { name = argument.Parameter.Name, serviceType = argument.ServiceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), deferred = argument.Deferred }).ToArray(),
        }).ToArray();

        object Receipt(string path) => new { path, sha256 = Convert.ToHexStringLower(SHA256.HashData(files.Single(file => file.RelativeOutputPath == path).Contents)) };

        object[] CompileFiles(string prefix, bool app) => files.Where(file => file.RelativeOutputPath.EndsWith(".cs", StringComparison.Ordinal)
                && (file.RelativeOutputPath.StartsWith(prefix, StringComparison.Ordinal) || file.Compile && (!app || file.RelativeOutputPath != "authoring.g.cs")))
            .OrderBy(file => file.RelativeOutputPath, StringComparer.Ordinal)
            .Select(file => (object)new { path = file.RelativeOutputPath, sha256 = Convert.ToHexStringLower(SHA256.HashData(file.Contents)) }).ToArray();
    }

    private void PopulatePackageRoleOutputs(SourceAuthoringPlan plan)
    {
        var rolePlan = plan.Files.SingleOrDefault(file => file.RelativeOutputPath == RolePlanPath);
        if (rolePlan is null) return;
        using var document = JsonDocument.Parse(rolePlan.Contents);
        WorkerCompileItems = Items("roles/worker/", app: false);
        AppCompileItems = document.RootElement.GetProperty("hasApp").GetBoolean() ? Items("roles/app/", app: true) : [];
        var item = new TaskItem(rolePlan.OutputPath);
        item.SetMetadata("Sha256", Convert.ToHexStringLower(SHA256.HashData(rolePlan.Contents)));
        item.SetMetadata("InputFingerprint", document.RootElement.GetProperty("inputFingerprint").GetString()!);
        item.SetMetadata("HasApp", document.RootElement.GetProperty("hasApp").GetBoolean() ? "true" : "false");
        item.SetMetadata("AssemblyName", AssemblyName);
        item.SetMetadata("GeneratedDirectory", Path.GetDirectoryName(rolePlan.OutputPath)!);
        item.SetMetadata("IntermediateOutputPath", Path.GetDirectoryName(Path.GetDirectoryName(rolePlan.OutputPath)!)!);
        var snapshot = PublishImmutableRolePlan(plan, Path.GetDirectoryName(rolePlan.OutputPath)!);
        item.SetMetadata("SnapshotPath", Path.Combine(snapshot, RolePlanPath));
        item.SetMetadata("SnapshotGeneratedDirectory", snapshot);
        item.SetMetadata("SnapshotIntermediateOutputPath", Path.GetDirectoryName(snapshot)!);
        item.SetMetadata("WorkerCompilationRoot", SunderRoleCompilationSnapshot.Publish(document.RootElement, snapshot, "worker"));
        if (document.RootElement.GetProperty("hasApp").GetBoolean())
            item.SetMetadata("AppCompilationRoot", SunderRoleCompilationSnapshot.Publish(document.RootElement, snapshot, "app"));
        item.SetMetadata("HasDependencyPlan", document.RootElement.GetProperty("workerDependencies").ValueKind == JsonValueKind.Null ? "false" : "true");
        PackageRolePlans = [item];

        ITaskItem[] Items(string prefix, bool app) => plan.Files
            .Where(file => file.RelativeOutputPath.EndsWith(".cs", StringComparison.Ordinal)
                && (file.RelativeOutputPath.StartsWith(prefix, StringComparison.Ordinal) || file.Compile && (!app || file.RelativeOutputPath != "authoring.g.cs")))
            .OrderBy(file => file.RelativeOutputPath, StringComparer.Ordinal)
            .Select(file => (ITaskItem)new TaskItem(file.OutputPath)).ToArray();
    }

    private static SunderPackageRoleReference[] RoleReferences(CSharpCompilation compilation)
    {
        // Close over assembly metadata as well as directly used symbols. A dependency
        // used inside an external assembly still belongs to that assembly's role.
        var selected = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
        var pending = new Queue<IAssemblySymbol>(compilation.GetUsedAssemblyReferences()
            .Select(reference => compilation.GetAssemblyOrModuleSymbol(reference)).OfType<IAssemblySymbol>());
        while (pending.TryDequeue(out var assembly))
        {
            if (!selected.Add(assembly)) continue;
            foreach (var dependency in assembly.Modules.SelectMany(module => module.ReferencedAssemblySymbols)) pending.Enqueue(dependency);
        }
        return compilation.References.Where(reference => compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly && selected.Contains(assembly))
            .Cast<PortableExecutableReference>().OrderBy(reference => reference.FilePath, StringComparer.Ordinal)
            .Select(reference => new SunderPackageRoleReference(reference.FilePath!,
                ((IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(reference)!).Identity.Name,
                Convert.ToHexStringLower(SHA256.HashData(SunderRpcDescriptorFileReader.ReadStableBytes(reference.FilePath!, int.MaxValue, "package role reference"))))).ToArray();
    }

    private SunderPackageRoleReference[] RoleRuntimeReferences(CSharpCompilation compilation)
    {
        // Namespace/extension lookup can mark a compile reference used even when
        // the emitted role has no dependency on it. Runtime closure starts with the
        // isolated role's actual AssemblyRefs, then follows external metadata.
        using var emitted = new MemoryStream();
        // Reference-only emission omits dependencies used exclusively in method
        // bodies. Inspect the complete isolated role image, never a mixed payload.
        var result = compilation.Emit(emitted);
        if (!result.Success) throw new InvalidDataException("package-authoring.role-compilation-invalid: " + string.Join(Environment.NewLine, result.Diagnostics));
        emitted.Position = 0;
        using var roleReader = new PEReader(emitted);
        var pending = new Queue<string>(Dependencies(roleReader.GetMetadataReader()));
        var available = new Dictionary<string, (string Path, string Sha256, string[] Dependencies)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in RuntimeReferencePaths.Select(item => Path.GetFullPath(item.ItemSpec))
                     .Concat(compilation.References.OfType<PortableExecutableReference>().Select(reference => reference.FilePath!))
                     .Where(path => Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, int.MaxValue, "package runtime reference");
            if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z') continue;
            using var reader = new PEReader(new MemoryStream(bytes, writable: false));
            if (!reader.HasMetadata) continue;
            var metadata = reader.GetMetadataReader();
            if (!metadata.IsAssembly) continue;
            var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            // Prefer the evaluated runtime asset over a reference assembly.
            available.TryAdd(name, (path, Convert.ToHexStringLower(SHA256.HashData(bytes)), Dependencies(metadata)));
        }
        var selected = new SortedDictionary<string, SunderPackageRoleReference>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var name))
        {
            if (selected.ContainsKey(name) || !available.TryGetValue(name, out var reference)) continue;
            selected.Add(name, new(reference.Path, name, reference.Sha256));
            foreach (var dependency in reference.Dependencies) pending.Enqueue(dependency);
        }
        return selected.Values.ToArray();

        static string[] Dependencies(MetadataReader metadata) => metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
    }

    private static CSharpCompilation RemoveUnusedRoleUsings(CSharpCompilation compilation, List<SourceGeneratedFile> files, string prefix,
        out IReadOnlySet<string> semanticAssemblies)
    {
        // Compiler hidden unused-import diagnostics can be suppressed for generated
        // trees/#line mappings. Use bound body symbols instead; namespace imports
        // themselves must not keep the other role's entire dependency alive.
        var authored = files.Select((file, index) => (file, index))
            .Where(item => item.file.RelativeOutputPath.StartsWith(prefix + "source/", StringComparison.Ordinal))
            .ToDictionary(item => item.file.OutputPath, item => item.index, StringComparer.Ordinal);
        var result = SunderPackageRoleUsingPruner.Prune(compilation, authored.Keys.ToHashSet(StringComparer.Ordinal));
        semanticAssemblies = result.SemanticAssemblies;
        foreach (var (path, updated) in result.UpdatedSources)
        {
            var index = authored[path];
            files[index] = files[index] with { Contents = Utf8NoBom.GetBytes(updated) };
        }
        return result.Compilation;
    }
}
