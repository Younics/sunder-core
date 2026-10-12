using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Build.Tasks;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class GenerateSunderRpcBindingsTaskTests
{
    private static readonly JsonSerializerOptions DeterministicJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void Execute_GeneratesAndAtomicallyPublishesImplementationFirstOutputs()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "Program.cs");
            File.WriteAllText(sourcePath, ImplementationFirstSource);
            var references = CompilationReferences();
            var candidate = new SunderRpcCSharpSourceCompiler().Compile(new(
                string.Empty,
                "7.6.5",
                [new(sourcePath, ImplementationFirstSource)],
                references));
            Assert.True(candidate.Success, string.Join(Environment.NewLine, candidate.Diagnostics));
            WriteSourceAuthoringState(root, Assert.Single(candidate.Contracts).Descriptor);
            var intermediateOutputPath = Path.Combine(root, "obj", "Debug", "net10.0");
            var first = CreateSourceTask(root, intermediateOutputPath, sourcePath, references);

            Assert.True(first.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)first.BuildEngine).Errors));
            Assert.Contains(first.CompileItems, item => Path.GetFileName(item.ItemSpec) == "authoring.g.cs");
            Assert.Contains(first.CompileItems, item => Path.GetFileName(item.ItemSpec) == "imports.g.cs");
            Assert.Contains(first.CompileItems, item => item.ItemSpec.EndsWith(
                Path.Combine("bindings", "greeting.g.cs"),
                PathComparison));
            var authoringPath = first.CompileItems.Single(item => Path.GetFileName(item.ItemSpec) == "authoring.g.cs").ItemSpec;
            Assert.Contains("CreateSunderRpcProviderRegistration", File.ReadAllText(authoringPath), StringComparison.Ordinal);

            var snapshot = Assert.Single(first.BindingSnapshots);
            Assert.Equal("example.generated.greeting", snapshot.GetMetadata("ContractId"));
            Assert.Equal("1.0.0", snapshot.GetMetadata("ContractVersion"));
            Assert.Equal(snapshot.ItemSpec, snapshot.GetMetadata("SourceDescriptorPath"));
            var bundle = Assert.Single(first.ContractBundles);
            Assert.Equal(snapshot.ItemSpec, bundle.ItemSpec);
            Assert.Equal("contracts/greeting.rpc.json", bundle.GetMetadata("DescriptorPath"));
            var provider = Assert.Single(first.RpcProviders);
            Assert.Equal("example.generated.greeting.provider", provider.ItemSpec);
            Assert.Equal("runtime", provider.GetMetadata("Role"));
            Assert.Equal("public", provider.GetMetadata("Audience"));

            var exportPath = Assert.Single(first.PackageProjectExports).ItemSpec;
            var receiptItem = Assert.Single(first.AuthoringReceipts);
            var receiptPath = receiptItem.ItemSpec;
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(receiptPath))).ToLowerInvariant(), receiptItem.GetMetadata("Sha256"));
            Assert.Equal("example.generated", receiptItem.GetMetadata("PackageId"));
            Assert.Equal("7.6.5", receiptItem.GetMetadata("PackageVersion"));
            var lockSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                Path.Combine(root, SunderRpcAuthoringDocuments.ContractLockFileName)))).ToLowerInvariant();
            using (var export = JsonDocument.Parse(File.ReadAllBytes(exportPath)))
            {
                Assert.Equal("example.generated", export.RootElement.GetProperty("packageId").GetString());
                Assert.Equal(lockSha256, export.RootElement.GetProperty("lockSha256").GetString());
                var contract = Assert.Single(export.RootElement.GetProperty("contracts").EnumerateArray());
                Assert.Equal("owned", contract.GetProperty("source").GetString());
                Assert.Equal("example.generated", contract.GetProperty("authorityId").GetString());
            }
            using (var receipt = JsonDocument.Parse(File.ReadAllBytes(receiptPath)))
            {
                Assert.Equal(64, receipt.RootElement.GetProperty("inputFingerprint").GetString()!.Length);
                Assert.Equal(lockSha256, receipt.RootElement.GetProperty("lockSha256").GetString());
                Assert.Equal(lockSha256, receiptItem.GetMetadata("LockSha256"));
                Assert.Single(receipt.RootElement.GetProperty("contracts").EnumerateArray());
                Assert.Single(receipt.RootElement.GetProperty("bundles").EnumerateArray());
                Assert.Single(receipt.RootElement.GetProperty("providers").EnumerateArray());
                Assert.Single(receipt.RootElement.GetProperty("generatedBindings").EnumerateArray());
            }

            var generatedDirectory = Path.Combine(intermediateOutputPath, GenerateSunderRpcBindingsTask.GeneratedDirectoryName);
            var markerPath = GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(generatedDirectory);
            var before = ReadOwnedSet(generatedDirectory);
            var oldTimestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            foreach (var path in Directory.EnumerateFiles(generatedDirectory, "*", SearchOption.AllDirectories))
            {
                File.SetLastWriteTimeUtc(path, oldTimestamp);
            }
            File.SetLastWriteTimeUtc(markerPath, oldTimestamp);
            Directory.SetLastWriteTimeUtc(generatedDirectory, oldTimestamp);
            var timestamps = ReadOwnedTimestamps(generatedDirectory);

            var second = CreateSourceTask(root, intermediateOutputPath, sourcePath, references);
            Assert.True(second.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)second.BuildEngine).Errors));
            Assert.Equal(before, ReadOwnedSet(generatedDirectory));
            Assert.Equal(timestamps, ReadOwnedTimestamps(generatedDirectory));

            var baselinePath = Path.Combine(root, "sunder-contracts.owned.json");
            var baselineBytes = File.ReadAllBytes(baselinePath);
            var originalReceipt = File.ReadAllBytes(receiptPath);
            var renamed = CreateSourceTask(root, intermediateOutputPath, sourcePath, references);
            renamed.AssemblyName = "Example.ActualAssembly";
            Assert.True(renamed.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)renamed.BuildEngine).Errors));
            Assert.NotEqual(originalReceipt, File.ReadAllBytes(receiptPath));
            Assert.Equal(baselineBytes, File.ReadAllBytes(baselinePath));
            Assert.True(first.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)first.BuildEngine).Errors));
            Assert.Equal(before, ReadOwnedSet(generatedDirectory));
            File.AppendAllText(baselinePath, "\n");
            var noncanonical = CreateSourceTask(root, intermediateOutputPath, sourcePath, references);
            Assert.False(noncanonical.Execute());
            Assert.Contains(((TestBuildEngine)noncanonical.BuildEngine).Errors, error =>
                error.Contains("deterministic outer JSON form", StringComparison.Ordinal));
            Assert.Equal(before, ReadOwnedSet(generatedDirectory));
            File.WriteAllBytes(baselinePath, baselineBytes);

            File.WriteAllText(
                sourcePath,
                ImplementationFirstSource.Replace(
                    "    [SunderMethod]",
                    "    public SunderWorkerProviderRegistration CreateSunderRpcProviderRegistration() => throw null!;\n    [SunderMethod]",
                    StringComparison.Ordinal));
            var failed = CreateSourceTask(root, intermediateOutputPath, sourcePath, references);
            Assert.False(failed.Execute());
            Assert.Contains(((TestBuildEngine)failed.BuildEngine).Errors, error =>
                error.Contains("rpc-authoring.generated-name-collision", StringComparison.Ordinal));
            Assert.Equal(before, ReadOwnedSet(generatedDirectory));
            Assert.Empty(failed.CompileItems);
            Assert.Empty(failed.ContractBundles);
            Assert.Empty(failed.RpcProviders);

            File.WriteAllText(
                sourcePath,
                ImplementationFirstSource.Replace(
                    "[SunderService(SunderServiceExposure.Public)]\n[SunderAppUse(Required = true)]",
                    string.Empty,
                    StringComparison.Ordinal));
            var removed = CreateSourceTask(root, intermediateOutputPath, sourcePath, references);
            Assert.False(removed.Execute());
            Assert.Contains(((TestBuildEngine)removed.BuildEngine).Errors, error =>
                error.Contains("owned baseline has 1 active contract", StringComparison.Ordinal));
            Assert.Equal(before, ReadOwnedSet(generatedDirectory));

            File.WriteAllText(sourcePath, ImplementationFirstSource);
            var invalidIdentity = CreateSourceTask(root, intermediateOutputPath, sourcePath, references);
            invalidIdentity.ProjectIdentity = "Implementation+FirstRpc";
            Assert.False(invalidIdentity.Execute());
            Assert.Contains(((TestBuildEngine)invalidIdentity.BuildEngine).Errors, error =>
                error.Contains("portable project identity", StringComparison.Ordinal));
            Assert.Equal(before, ReadOwnedSet(generatedDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("4d2c20ba9591c04449ee4fe1e48fbbd1664642807e039a6984245317a9735c1d")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Execute_ProjectImportCopiesVerifiedSnapshotsAndGeneratesAppLocalClients(string? producerLockSha256)
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "Program.cs");
            File.WriteAllText(sourcePath, ImplementationFirstSource);
            var references = CompilationReferences();
            var candidate = new SunderRpcCSharpSourceCompiler().Compile(new(
                string.Empty,
                "7.6.5",
                [new(sourcePath, ImplementationFirstSource)],
                references));
            Assert.True(candidate.Success, string.Join(Environment.NewLine, candidate.Diagnostics));
            WriteSourceAuthoringState(root, Assert.Single(candidate.Contracts).Descriptor);

            var exportTask = CreateSourceTask(
                root,
                Path.Combine(root, "obj", "Debug", "net10.0"),
                sourcePath,
                references);
            exportTask.GenerationMode = "ProjectExport";
            Assert.True(
                exportTask.Execute(),
                string.Join(Environment.NewLine, ((TestBuildEngine)exportTask.BuildEngine).Errors));

            Directory.CreateDirectory(Path.Combine(root, "App"));
            var exportItem = Assert.Single(exportTask.PackageProjectExports);
            var exportPath = exportItem.ItemSpec;
            using var produced = JsonDocument.Parse(File.ReadAllBytes(exportPath));
            var producerVersion = produced.RootElement.GetProperty("producer").GetProperty("version").GetString()!;
            var lockSha256 = producerLockSha256 ?? produced.RootElement.GetProperty("lockSha256").GetString()!;
            if (producerLockSha256 is not null)
            {
                // Exercise nonempty/arbitrary lock hashes without enabling imported source generation.
                var bytes = SunderRpcAuthoringDocuments.WritePackageExport(
                    producerVersion,
                    exportTask.ProjectIdentity,
                    exportTask.Configuration,
                    exportTask.TargetFramework,
                    produced.RootElement.GetProperty("inputFingerprint").GetString()!,
                    producerLockSha256,
                    "example.generated",
                    "7.6.5",
                    candidate.Contracts);
                File.WriteAllBytes(exportPath, bytes);
                exportItem.SetMetadata("Sha256", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            }
            var projectExport = SunderRpcAuthoringDocuments.ReadPackageProjectExport(
                exportPath,
                producerVersion,
                exportTask.ProjectIdentity,
                exportTask.Configuration,
                exportTask.TargetFramework,
                "example.generated", "7.6.5");
            Assert.Equal(lockSha256, projectExport.LockSha256);
            Assert.Equal(exportItem.GetMetadata("Sha256"), SunderRpcAuthoringDocuments.ValidatePackageProjectExport(
                exportPath,
                producerVersion,
                exportTask.ProjectIdentity,
                exportTask.Configuration,
                exportTask.TargetFramework,
                "example.generated",
                "7.6.5",
                lockSha256,
                candidate.Contracts));
            var sharedIntermediate = Path.GetDirectoryName(Path.GetDirectoryName(exportPath)!)!;
            var overlappingIntermediate = OperatingSystem.IsMacOS()
                                          && sharedIntermediate.StartsWith("/private/var/", StringComparison.Ordinal)
                ? sharedIntermediate["/private".Length..]
                : sharedIntermediate;
            var overlappingImport = new GenerateSunderRpcBindingsTask
            {
                BuildEngine = new TestBuildEngine(),
                GenerationMode = "ProjectImport",
                ExpectedPackageProjectExports = [Assert.Single(exportTask.PackageProjectExports)],
                PackageVersion = "7.6.5",
                ProjectIdentity = "ImplementationFirstRpc.App",
                Configuration = "Debug",
                TargetFramework = "net10.0-app",
                ProjectDirectory = Path.Combine(root, "App"),
                IntermediateOutputPath = overlappingIntermediate,
            };
            Assert.False(overlappingImport.Execute());
            Assert.Contains(((TestBuildEngine)overlappingImport.BuildEngine).Errors, error =>
                error.Contains("distinct producer and local", StringComparison.Ordinal));
            Assert.True(File.Exists(exportPath));

            var appIntermediate = Path.Combine(root, "App", "obj", "Debug", "net10.0");
            var importTask = new GenerateSunderRpcBindingsTask
            {
                BuildEngine = new TestBuildEngine(),
                GenerationMode = "ProjectImport",
                ExpectedPackageProjectExports = [Assert.Single(exportTask.PackageProjectExports)],
                PackageVersion = "7.6.5",
                ProjectIdentity = "ImplementationFirstRpc.App",
                Configuration = "Debug",
                TargetFramework = "net10.0-app",
                ProjectDirectory = Path.Combine(root, "App"),
                IntermediateOutputPath = appIntermediate,
            };

            Assert.True(
                importTask.Execute(),
                string.Join(Environment.NewLine, ((TestBuildEngine)importTask.BuildEngine).Errors));
            Assert.DoesNotContain(importTask.CompileItems, item => Path.GetFileName(item.ItemSpec) == "authoring.g.cs");
            Assert.Contains(importTask.CompileItems, item => Path.GetFileName(item.ItemSpec) == "imports.g.cs");
            var bindingPath = importTask.CompileItems.Single(item => item.ItemSpec.EndsWith(
                Path.Combine("bindings", "imports", "example.generated.greeting.g.cs"),
                PathComparison)).ItemSpec;
            var binding = File.ReadAllText(bindingPath);
            Assert.Contains("namespace Sunder.Generated.Packages.Example_Generated_Internal.CExample_Generated_Greeting;", binding, StringComparison.Ordinal);
            Assert.Contains("internal sealed class GreetingClient", binding, StringComparison.Ordinal);
            Assert.DoesNotContain("Sunder.Sdk.Worker", binding, StringComparison.Ordinal);

            var snapshot = Assert.Single(importTask.BindingSnapshots);
            Assert.Equal("example.generated.greeting", snapshot.GetMetadata("ContractId"));
            Assert.Equal(snapshot.ItemSpec, Assert.Single(importTask.ContractBundles).ItemSpec);
            Assert.Empty(importTask.RpcProviders);
            Assert.Empty(importTask.PackageProjectExports);
            var use = Assert.Single(importTask.RpcUses);
            Assert.Equal("example.generated.greeting", use.ItemSpec);
            Assert.Equal("app", use.GetMetadata("Role"));
            Assert.Equal("true", use.GetMetadata("Required"));
            Assert.Equal("discover;invoke", use.GetMetadata("Actions"));

            var receiptPath = Assert.Single(importTask.AuthoringReceipts).ItemSpec;
            using var receipt = JsonDocument.Parse(File.ReadAllBytes(receiptPath));
            Assert.Equal("example.generated", receipt.RootElement.GetProperty("packageId").GetString());
            Assert.Equal(lockSha256, receipt.RootElement.GetProperty("lockSha256").GetString());
            Assert.Single(receipt.RootElement.GetProperty("contracts").EnumerateArray());
            var importedBundle = Assert.Single(receipt.RootElement.GetProperty("bundles").EnumerateArray());
            Assert.Equal("owned", importedBundle.GetProperty("source").GetString());
            Assert.Equal("example.generated", importedBundle.GetProperty("authorityId").GetString());
            Assert.Empty(receipt.RootElement.GetProperty("providers").EnumerateArray());
            Assert.Single(receipt.RootElement.GetProperty("uses").EnumerateArray());
            Assert.Single(receipt.RootElement.GetProperty("generatedBindings").EnumerateArray());
            foreach (var name in new[]
                     {
                         "packageArtifacts", "packageArtifactImports", "packageApiImports", "providerImports",
                         "platformImports", "generatedPackageFacades",
                     })
            {
                Assert.Empty(receipt.RootElement.GetProperty(name).EnumerateArray());
            }

            var generatedDirectory = Path.Combine(
                appIntermediate,
                GenerateSunderRpcBindingsTask.GeneratedDirectoryName);
            var before = ReadOwnedSet(generatedDirectory);
            var timestamps = ReadOwnedTimestamps(generatedDirectory);
            File.WriteAllText(exportPath, File.ReadAllText(exportPath).Replace(
                $"\"lockSha256\": \"{lockSha256}\"",
                $"\"lockSha256\": \"{new string('f', 64)}\"",
                StringComparison.Ordinal));
            var failed = new GenerateSunderRpcBindingsTask
            {
                BuildEngine = new TestBuildEngine(),
                GenerationMode = "ProjectImport",
                ExpectedPackageProjectExports = [Assert.Single(exportTask.PackageProjectExports)],
                PackageVersion = "7.6.5",
                ProjectIdentity = "ImplementationFirstRpc.App",
                Configuration = "Debug",
                TargetFramework = "net10.0-app",
                ProjectDirectory = Path.Combine(root, "App"),
                IntermediateOutputPath = appIntermediate,
            };
            Assert.False(failed.Execute());
            Assert.Contains(((TestBuildEngine)failed.BuildEngine).Errors, error =>
                error.Contains("changed after its producing target completed", StringComparison.Ordinal));
            Assert.Equal(before, ReadOwnedSet(generatedDirectory));
            Assert.Equal(timestamps, ReadOwnedTimestamps(generatedDirectory));
            Assert.Empty(failed.CompileItems);
            Assert.Empty(failed.FileWrites);
            Assert.Empty(failed.BindingSnapshots);
            Assert.Empty(failed.ContractBundles);
            Assert.Empty(failed.RpcUses);
            Assert.Empty(failed.AuthoringReceipts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("missing", "missing or unsupported members")]
    [InlineData("null", "deterministic outer JSON form")]
    [InlineData("empty", "not lowercase SHA-256 text")]
    [InlineData("short", "not lowercase SHA-256 text")]
    [InlineData("uppercase", "not lowercase SHA-256 text")]
    [InlineData("nonhex", "not lowercase SHA-256 text")]
    [InlineData("out-of-order", "not in deterministic schema order")]
    public void ReadPackageProjectExport_RequiresLockSha256InSchemaOrder(string mutation, string expectedError)
    {
        var root = CreateTempDirectory();
        try
        {
            var lockSha256 = new string('b', 64);
            var contents = Encoding.UTF8.GetString(SunderRpcAuthoringDocuments.WritePackageExport(
                "1.0.0", "ImplementationFirstRpc", "Debug", "net10.0", new string('a', 64),
                lockSha256, "example.generated", "7.6.5", []));
            var lockLine = $"  \"lockSha256\": \"{lockSha256}\",\n";
            const string packageLine = "  \"packageId\": \"example.generated\",\n";
            contents = mutation switch
            {
                "missing" => contents.Replace(lockLine, string.Empty, StringComparison.Ordinal),
                "null" => contents.Replace(lockLine, "  \"lockSha256\": null,\n", StringComparison.Ordinal),
                "empty" => contents.Replace(lockSha256, string.Empty, StringComparison.Ordinal),
                "short" => contents.Replace(lockSha256, new string('b', 63), StringComparison.Ordinal),
                "uppercase" => contents.Replace(lockSha256, new string('B', 64), StringComparison.Ordinal),
                "nonhex" => contents.Replace(lockSha256, new string('g', 64), StringComparison.Ordinal),
                "out-of-order" => contents.Replace(lockLine, string.Empty, StringComparison.Ordinal)
                    .Replace(packageLine, packageLine + lockLine, StringComparison.Ordinal),
                _ => throw new InvalidOperationException($"Unknown mutation '{mutation}'."),
            };
            var path = Path.Combine(root, SunderRpcAuthoringDocuments.PackageExportFileName);
            File.WriteAllText(path, contents);

            var exception = Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ReadPackageProjectExport(
                path, "1.0.0", "ImplementationFirstRpc", "Debug", "net10.0", "example.generated", "7.6.5"));
            Assert.Contains(expectedError, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_ImplementationRejectsExportLockMismatchBeforePublication(bool hasPreviousOutput)
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "Program.cs");
            File.WriteAllText(sourcePath, ImplementationFirstSource);
            var references = CompilationReferences();
            var candidate = new SunderRpcCSharpSourceCompiler().Compile(new(
                string.Empty,
                "7.6.5",
                [new(sourcePath, ImplementationFirstSource)],
                references));
            Assert.True(candidate.Success, string.Join(Environment.NewLine, candidate.Diagnostics));
            WriteSourceAuthoringState(root, Assert.Single(candidate.Contracts).Descriptor);
            var exportTask = CreateSourceTask(
                root, Path.Combine(root, "obj", "Debug", "net10.0"), sourcePath, references);
            exportTask.GenerationMode = "ProjectExport";
            Assert.True(exportTask.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)exportTask.BuildEngine).Errors));
            var exportItem = Assert.Single(exportTask.PackageProjectExports);

            var intermediate = Path.Combine(root, "obj", "Debug", "net10.0", "linux-x64");
            var generated = Path.Combine(intermediate, GenerateSunderRpcBindingsTask.GeneratedDirectoryName);
            string[]? before = null;
            string[]? timestamps = null;
            if (hasPreviousOutput)
            {
                var previous = CreateSourceTask(root, intermediate, sourcePath, references);
                previous.GenerationMode = "Implementation";
                previous.ExpectedPackageProjectExports = [exportItem];
                Assert.True(previous.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)previous.BuildEngine).Errors));
                before = ReadOwnedSet(generated);
                timestamps = ReadOwnedTimestamps(generated);
            }

            using var produced = JsonDocument.Parse(File.ReadAllBytes(exportItem.ItemSpec));
            var producerVersion = produced.RootElement.GetProperty("producer").GetProperty("version").GetString()!;
            var lockSha256 = produced.RootElement.GetProperty("lockSha256").GetString()!;
            var tamperedBytes = SunderRpcAuthoringDocuments.WritePackageExport(
                producerVersion,
                exportTask.ProjectIdentity,
                exportTask.Configuration,
                exportTask.TargetFramework,
                produced.RootElement.GetProperty("inputFingerprint").GetString()!,
                new string('f', 64),
                "example.generated",
                "7.6.5",
                candidate.Contracts);
            File.WriteAllBytes(exportItem.ItemSpec, tamperedBytes);
            // Keep file identity current so only the exact-RID lock comparison can reject this export.
            exportItem.SetMetadata("Sha256", Convert.ToHexString(SHA256.HashData(tamperedBytes)).ToLowerInvariant());
            var exception = Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ValidatePackageProjectExport(
                exportItem.ItemSpec,
                producerVersion,
                exportTask.ProjectIdentity,
                exportTask.Configuration,
                exportTask.TargetFramework,
                "example.generated",
                "7.6.5",
                lockSha256,
                candidate.Contracts));
            Assert.Contains("exact-RID contract lock", exception.Message, StringComparison.Ordinal);

            var failed = CreateSourceTask(root, intermediate, sourcePath, references);
            failed.GenerationMode = "Implementation";
            failed.ExpectedPackageProjectExports = [exportItem];
            Assert.False(failed.Execute());
            Assert.Contains(((TestBuildEngine)failed.BuildEngine).Errors, error =>
                error.Contains("exact-RID contract lock", StringComparison.Ordinal));
            Assert.Empty(failed.CompileItems);
            Assert.Empty(failed.FileWrites);
            Assert.Empty(failed.BindingSnapshots);
            Assert.Empty(failed.ContractBundles);
            Assert.Empty(failed.RpcProviders);
            Assert.Empty(failed.RpcUses);
            Assert.Empty(failed.PackageProjectExports);
            Assert.Empty(failed.AuthoringReceipts);
            if (hasPreviousOutput)
            {
                Assert.Equal(before, ReadOwnedSet(generated));
                Assert.Equal(timestamps, ReadOwnedTimestamps(generated));
            }
            else
            {
                Assert.False(Directory.Exists(generated));
                Assert.False(File.Exists(GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(generated)));
            }
            Assert.Equal(tamperedBytes, File.ReadAllBytes(exportItem.ItemSpec));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Execute_DescriptorFirstHandlerNameDoesNotActivateSourceAuthoring()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "Handler.cs");
            File.WriteAllText(
                sourcePath,
                "namespace Example; public sealed class Handler : ISunderRpcServiceHandler { }");
            var task = CreateTask(
                root,
                Path.Combine(root, "obj"),
                Binding(WriteDescriptor(root, "test.binding"), "Test.Binding.Generated"));
            task.SourceFiles = [new TaskItem(sourcePath)];

            Assert.True(task.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)task.BuildEngine).Errors));
            Assert.Single(task.CompileItems);
            Assert.Empty(task.PackageProjectExports);
            Assert.Empty(task.AuthoringReceipts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Execute_GeneratesDeterministicSourceAndDoesNotRewriteUnchangedFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var descriptorPath = WriteDescriptor(root, "test.binding");
            var intermediateOutputPath = Path.Combine(root, "obj", "Debug", "net10.0");
            var binding = Binding(descriptorPath, "Test.Binding.Generated");
            var first = CreateTask(root, intermediateOutputPath, binding);

            Assert.True(first.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)first.BuildEngine).Errors));
            var compileItem = Assert.Single(first.CompileItems);
            var outputPath = compileItem.ItemSpec;
            Assert.Contains(first.FileWrites, item => PathsEqual(item.ItemSpec, outputPath));
            var bindingSnapshot = Assert.Single(first.BindingSnapshots);
            Assert.Contains(first.FileWrites, item => PathsEqual(item.ItemSpec, bindingSnapshot.ItemSpec));
            Assert.Equal(Path.GetFullPath(descriptorPath), bindingSnapshot.GetMetadata("SourceDescriptorPath"));
            Assert.Equal("1", bindingSnapshot.GetMetadata("DescriptorVersion"));
            Assert.Equal(64, bindingSnapshot.GetMetadata("DescriptorSha256").Length);
            Assert.Equal(64, bindingSnapshot.GetMetadata("AbiSha256").Length);
            Assert.StartsWith(
                Path.GetFullPath(intermediateOutputPath) + Path.DirectorySeparatorChar,
                Path.GetFullPath(outputPath),
                PathComparison);
            var firstSource = File.ReadAllText(outputPath);
            Assert.StartsWith("// <auto-generated />\n", firstSource, StringComparison.Ordinal);
            Assert.Contains("namespace Test.Binding.Generated;", firstSource, StringComparison.Ordinal);
            Assert.Contains("public sealed class Request", firstSource, StringComparison.Ordinal);
            var generatedDirectory = Path.GetDirectoryName(outputPath)!;
            var markerPath = GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(generatedDirectory);
            Assert.Equal(GenerateSunderRpcBindingsTask.MarkerContents, File.ReadAllText(markerPath));

            var oldTimestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(outputPath, oldTimestamp);
            var preservedTimestamp = File.GetLastWriteTimeUtc(outputPath);
            File.SetLastWriteTimeUtc(bindingSnapshot.ItemSpec, oldTimestamp);
            var preservedSnapshotTimestamp = File.GetLastWriteTimeUtc(bindingSnapshot.ItemSpec);
            File.SetLastWriteTimeUtc(markerPath, oldTimestamp);
            var preservedMarkerTimestamp = File.GetLastWriteTimeUtc(markerPath);
            Directory.SetLastWriteTimeUtc(generatedDirectory, oldTimestamp);
            var preservedDirectoryTimestamp = Directory.GetLastWriteTimeUtc(generatedDirectory);
            var second = CreateTask(root, intermediateOutputPath, binding);

            Assert.True(second.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)second.BuildEngine).Errors));
            Assert.Equal(outputPath, Assert.Single(second.CompileItems).ItemSpec);
            Assert.Equal(firstSource, File.ReadAllText(outputPath));
            Assert.Equal(preservedTimestamp, File.GetLastWriteTimeUtc(outputPath));
            Assert.Equal(preservedSnapshotTimestamp, File.GetLastWriteTimeUtc(bindingSnapshot.ItemSpec));
            Assert.Equal(preservedMarkerTimestamp, File.GetLastWriteTimeUtc(markerPath));
            Assert.Equal(preservedDirectoryTimestamp, Directory.GetLastWriteTimeUtc(generatedDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(" ", "public")]
    [InlineData("Public", "public")]
    [InlineData("Internal", "internal")]
    public void Execute_AppliesExplicitAccessibility(string accessibility, string expectedKeyword)
    {
        var root = CreateTempDirectory();
        try
        {
            var binding = Binding(WriteDescriptor(root, "test.binding"), "Test.Binding.Generated");
            binding.SetMetadata("Accessibility", accessibility);
            var task = CreateTask(root, Path.Combine(root, "obj"), binding);

            Assert.True(task.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)task.BuildEngine).Errors));
            var source = File.ReadAllText(Assert.Single(task.CompileItems).ItemSpec);
            Assert.Contains($"{expectedKeyword} sealed class Request", source, StringComparison.Ordinal);
            Assert.Contains($"{expectedKeyword} interface ISampleProvider", source, StringComparison.Ordinal);
            Assert.Contains($"{expectedKeyword} sealed class SampleClient", source, StringComparison.Ordinal);
            Assert.Contains($"{expectedKeyword} sealed class TestBindingRpcHandler", source, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("public")]
    [InlineData("Protected")]
    [InlineData(" Internal ")]
    public void Execute_RejectsInvalidAccessibilityBeforeWritingOutput(string accessibility)
    {
        var root = CreateTempDirectory();
        try
        {
            var intermediateOutputPath = Path.Combine(root, "obj");
            var binding = Binding(WriteDescriptor(root, "test.binding"), "Test.Binding.Generated");
            binding.SetMetadata("Accessibility", accessibility);
            var task = CreateTask(root, intermediateOutputPath, binding);

            Assert.False(task.Execute());
            Assert.Contains(((TestBuildEngine)task.BuildEngine).Errors, error =>
                error.Contains("Allowed values are 'Public' and 'Internal'", StringComparison.Ordinal));
            Assert.Empty(task.CompileItems);
            Assert.False(Directory.Exists(intermediateOutputPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Execute_UsesContractIdentityForDefaultOutputNames()
    {
        var root = CreateTempDirectory();
        try
        {
            var firstPath = WriteDescriptor(Path.Combine(root, "first"), "test.first");
            var secondPath = WriteDescriptor(Path.Combine(root, "second"), "test.second");
            var task = CreateTask(
                root,
                Path.Combine(root, "obj"),
                Binding(firstPath, "Test.Generated"),
                Binding(secondPath, "Test.Generated"));

            Assert.True(task.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)task.BuildEngine).Errors));
            Assert.Equal(2, task.CompileItems.Length);
            Assert.Equal(2, task.CompileItems.Select(static item => item.ItemSpec).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Execute_RejectsMissingNamespace()
    {
        var root = CreateTempDirectory();
        try
        {
            var binding = new TaskItem(WriteDescriptor(root, "test.binding"));
            var task = CreateTask(root, Path.Combine(root, "obj"), binding);

            Assert.False(task.Execute());
            Assert.Contains(((TestBuildEngine)task.BuildEngine).Errors, error =>
                error.Contains("requires 'Namespace'", StringComparison.Ordinal));
            Assert.Empty(task.CompileItems);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Execute_RejectsInvalidDescriptorBeforeWritingOutput()
    {
        var root = CreateTempDirectory();
        try
        {
            var descriptorPath = Path.Combine(root, "invalid.rpc.json");
            File.WriteAllText(descriptorPath, "{}");
            var task = CreateTask(root, Path.Combine(root, "obj"), Binding(descriptorPath, "Test.Generated"));

            Assert.False(task.Execute());
            Assert.Contains(((TestBuildEngine)task.BuildEngine).Errors, error =>
                error.Contains("descriptor", StringComparison.Ordinal)
                && error.Contains("invalid", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(root, "obj")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../outside.g.cs")]
    [InlineData("nested/../../outside.g.cs")]
    [InlineData("/outside.g.cs")]
    [InlineData("binding.txt")]
    public void Execute_RejectsUnsafeOutputFile(string outputFile)
    {
        var root = CreateTempDirectory();
        try
        {
            var binding = Binding(WriteDescriptor(root, "test.binding"), "Test.Generated");
            binding.SetMetadata("OutputFile", outputFile);
            var task = CreateTask(root, Path.Combine(root, "obj"), binding);

            Assert.False(task.Execute());
            Assert.Contains(((TestBuildEngine)task.BuildEngine).Errors, error =>
                error.Contains("OutputFile", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(root, "outside.g.cs")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Execute_RejectsDuplicateExplicitOutputFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var first = Binding(WriteDescriptor(Path.Combine(root, "first"), "test.first"), "Test.Generated");
            var second = Binding(WriteDescriptor(Path.Combine(root, "second"), "test.second"), "Test.Generated");
            first.SetMetadata("OutputFile", "Binding.g.cs");
            second.SetMetadata("OutputFile", "binding.g.cs");
            var task = CreateTask(root, Path.Combine(root, "obj"), first, second);

            Assert.False(task.Execute());
            Assert.Contains(((TestBuildEngine)task.BuildEngine).Errors, error =>
                error.Contains("same output", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(root, "obj")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Execute_RejectsDescriptorSymbolicLink()
    {
        var root = CreateTempDirectory();
        try
        {
            var descriptorPath = WriteDescriptor(root, "test.binding");
            var linkPath = Path.Combine(root, "linked.rpc.json");
            try
            {
                File.CreateSymbolicLink(linkPath, descriptorPath);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                return;
            }
            var task = CreateTask(root, Path.Combine(root, "obj"), Binding(linkPath, "Test.Generated"));

            Assert.False(task.Execute());
            Assert.Contains(((TestBuildEngine)task.BuildEngine).Errors, error =>
                error.Contains("symbolic link", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Execute_RejectsOutputSymbolicLink()
    {
        var root = CreateTempDirectory();
        try
        {
            var intermediateOutputPath = Path.Combine(root, "obj");
            var generatedDirectory = Path.Combine(intermediateOutputPath, "sunder-rpc");
            Directory.CreateDirectory(generatedDirectory);
            File.WriteAllText(
                GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(generatedDirectory),
                GenerateSunderRpcBindingsTask.MarkerContents);
            var outsidePath = Path.Combine(root, "outside.g.cs");
            File.WriteAllText(outsidePath, "outside");
            var linkPath = Path.Combine(generatedDirectory, "Binding.g.cs");
            try
            {
                File.CreateSymbolicLink(linkPath, outsidePath);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                return;
            }
            var binding = Binding(WriteDescriptor(root, "test.binding"), "Test.Generated");
            binding.SetMetadata("OutputFile", "Binding.g.cs");
            var task = CreateTask(root, intermediateOutputPath, binding);

            Assert.False(task.Execute());
            Assert.Contains(((TestBuildEngine)task.BuildEngine).Errors, error =>
                error.Contains("regular file or directory", StringComparison.Ordinal));
            Assert.Equal("outside", File.ReadAllText(outsidePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Execute_ReplacesTheCompleteOwnedSetAndRemovesStaleOutputs()
    {
        var root = CreateTempDirectory();
        try
        {
            var intermediateOutputPath = Path.Combine(root, "obj");
            var firstBinding = Binding(WriteDescriptor(Path.Combine(root, "first"), "test.first"), "Test.Generated");
            firstBinding.SetMetadata("OutputFile", "Old.g.cs");
            var first = CreateTask(root, intermediateOutputPath, firstBinding);
            Assert.True(first.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)first.BuildEngine).Errors));
            var staleSource = Assert.Single(first.CompileItems).ItemSpec;
            var staleSnapshot = Assert.Single(first.BindingSnapshots).ItemSpec;

            var secondBinding = Binding(WriteDescriptor(Path.Combine(root, "second"), "test.second"), "Test.Generated");
            secondBinding.SetMetadata("OutputFile", "nested/New.g.cs");
            var second = CreateTask(root, intermediateOutputPath, secondBinding);
            Assert.True(second.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)second.BuildEngine).Errors));

            Assert.False(File.Exists(staleSource));
            Assert.False(File.Exists(staleSnapshot));
            Assert.True(File.Exists(Assert.Single(second.CompileItems).ItemSpec));
            Assert.True(File.Exists(Assert.Single(second.BindingSnapshots).ItemSpec));
            Assert.Single(Directory.EnumerateFiles(
                Path.Combine(intermediateOutputPath, "sunder-rpc"),
                "*.rpc.json",
                SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Execute_PreservesThePreviousCompleteSetWhenGenerationFails()
    {
        var root = CreateTempDirectory();
        try
        {
            var descriptorPath = WriteDescriptor(root, "test.binding");
            var intermediateOutputPath = Path.Combine(root, "obj");
            var first = CreateTask(root, intermediateOutputPath, Binding(descriptorPath, "Test.Generated"));
            Assert.True(first.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)first.BuildEngine).Errors));
            var generatedDirectory = Path.Combine(intermediateOutputPath, "sunder-rpc");
            var before = ReadOwnedSet(generatedDirectory);

            File.WriteAllText(descriptorPath, "{}");
            var failed = CreateTask(root, intermediateOutputPath, Binding(descriptorPath, "Test.Generated"));

            Assert.False(failed.Execute());
            Assert.Equal(before, ReadOwnedSet(generatedDirectory));
            Assert.Empty(failed.CompileItems);
            Assert.Empty(failed.BindingSnapshots);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Execute_WithNoBindings_CleansAnOwnedSetAndOtherwiseDoesNotPublishOne()
    {
        var root = CreateTempDirectory();
        try
        {
            var intermediateOutputPath = Path.Combine(root, "obj");
            var first = CreateTask(
                root,
                intermediateOutputPath,
                Binding(WriteDescriptor(root, "test.binding"), "Test.Generated"));
            Assert.True(first.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)first.BuildEngine).Errors));
            var generatedDirectory = Path.Combine(intermediateOutputPath, "sunder-rpc");

            var cleanup = CreateTask(root, intermediateOutputPath);
            Assert.True(cleanup.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)cleanup.BuildEngine).Errors));
            Assert.True(Directory.Exists(generatedDirectory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(generatedDirectory));
            Assert.True(File.Exists(GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(generatedDirectory)));
            Assert.Empty(cleanup.CompileItems);
            Assert.Empty(cleanup.FileWrites);
            Assert.Empty(cleanup.BindingSnapshots);

            var unusedIntermediateOutputPath = Path.Combine(root, "unused-obj");
            var noOp = CreateTask(root, unusedIntermediateOutputPath);
            Assert.True(noOp.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)noOp.BuildEngine).Errors));
            Assert.False(Directory.Exists(Path.Combine(unusedIntermediateOutputPath, "sunder-rpc")));
            Assert.False(File.Exists(GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(
                Path.Combine(unusedIntermediateOutputPath, "sunder-rpc"))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CleanGeneratedOutput_DeletesOnlyACompleteMarkedLinkFreeSet()
    {
        var root = CreateTempDirectory();
        try
        {
            var intermediateOutputPath = Path.Combine(root, "obj");
            var generatedDirectory = Path.Combine(
                intermediateOutputPath,
                GenerateSunderRpcBindingsTask.GeneratedDirectoryName);
            var markerPath = GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(generatedDirectory);
            var generated = CreateTask(
                root,
                intermediateOutputPath,
                Binding(WriteDescriptor(root, "test.binding"), "Test.Generated"));
            Assert.True(generated.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)generated.BuildEngine).Errors));

            var cleanup = CreateCleanupTask(root, intermediateOutputPath);
            Assert.True(cleanup.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)cleanup.BuildEngine).Errors));
            Assert.False(Directory.Exists(generatedDirectory));
            Assert.False(File.Exists(markerPath));

            Directory.CreateDirectory(generatedDirectory);
            var sentinel = Path.Combine(generatedDirectory, "user.cs");
            File.WriteAllText(sentinel, "user-owned");
            var unmarked = CreateCleanupTask(root, intermediateOutputPath);
            Assert.False(unmarked.Execute());
            Assert.Equal("user-owned", File.ReadAllText(sentinel));

            File.WriteAllText(markerPath, GenerateSunderRpcBindingsTask.MarkerContents);
            var outside = Path.Combine(root, "outside.txt");
            File.WriteAllText(outside, "outside");
            var link = Path.Combine(generatedDirectory, "linked.txt");
            try
            {
                File.CreateSymbolicLink(link, outside);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                return;
            }
            var linked = CreateCleanupTask(root, intermediateOutputPath);
            Assert.False(linked.Execute());
            Assert.Equal("outside", File.ReadAllText(outside));
            Assert.True(File.Exists(markerPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GeneratedOutputLifecycle_RecoversMarkerOnlyInterruptedClean()
    {
        var root = CreateTempDirectory();
        try
        {
            var intermediateOutputPath = Path.Combine(root, "obj");
            var generatedDirectory = Path.Combine(
                intermediateOutputPath,
                GenerateSunderRpcBindingsTask.GeneratedDirectoryName);
            var markerPath = GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(generatedDirectory);
            var descriptor = Binding(WriteDescriptor(root, "test.binding"), "Test.Generated");
            var generated = CreateTask(root, intermediateOutputPath, descriptor);
            Assert.True(generated.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)generated.BuildEngine).Errors));

            Directory.Delete(generatedDirectory, recursive: true);
            var regenerated = CreateTask(root, intermediateOutputPath, descriptor);
            Assert.True(regenerated.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)regenerated.BuildEngine).Errors));
            Assert.True(Directory.Exists(generatedDirectory));
            Assert.True(GenerateSunderRpcBindingsTask.HasValidOwnershipMarker(markerPath));

            Directory.Delete(generatedDirectory, recursive: true);
            var cleanup = CreateCleanupTask(root, intermediateOutputPath);
            Assert.True(cleanup.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)cleanup.BuildEngine).Errors));
            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CleanGeneratedOutput_RecoversInterruptedPublicationBeforeDeletingOwnedSet()
    {
        var root = CreateTempDirectory();
        try
        {
            var intermediateOutputPath = Path.Combine(root, "obj");
            var generatedDirectory = Path.Combine(
                intermediateOutputPath,
                GenerateSunderRpcBindingsTask.GeneratedDirectoryName);
            var markerPath = GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(generatedDirectory);
            var generated = CreateTask(
                root,
                intermediateOutputPath,
                Binding(WriteDescriptor(root, "test.binding"), "Test.Generated"));
            Assert.True(generated.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)generated.BuildEngine).Errors));

            var token = Guid.NewGuid().ToString("D");
            var stagedDirectory = generatedDirectory + ".stage-" + Guid.NewGuid().ToString("N");
            var stagedMarkerPath = markerPath + ".stage-" + Guid.NewGuid().ToString("N");
            var backupDirectory = generatedDirectory + ".backup-" + token;
            var backupMarkerPath = markerPath + ".backup-" + token;
            Directory.CreateDirectory(stagedDirectory);
            File.WriteAllText(Path.Combine(stagedDirectory, "replacement.g.cs"), "replacement");
            File.WriteAllText(stagedMarkerPath, GenerateSunderRpcBindingsTask.MarkerContents);
            var journalPath = generatedDirectory + GeneratedOutputTransaction.TransactionSuffix;
            File.WriteAllText(journalPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                token,
                coordinatorPath = generatedDirectory,
                outputs = new[]
                {
                    new
                    {
                        finalPath = generatedDirectory,
                        stagedPath = stagedDirectory,
                        backupPath = backupDirectory,
                        existed = true,
                    },
                    new
                    {
                        finalPath = markerPath,
                        stagedPath = stagedMarkerPath,
                        backupPath = backupMarkerPath,
                        existed = true,
                    },
                },
            }));
            Directory.Move(generatedDirectory, backupDirectory);
            File.Move(markerPath, backupMarkerPath);

            var cleanup = CreateCleanupTask(root, intermediateOutputPath);
            Assert.True(cleanup.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)cleanup.BuildEngine).Errors));
            Assert.False(Directory.Exists(generatedDirectory));
            Assert.False(File.Exists(markerPath));
            Assert.False(Directory.Exists(stagedDirectory));
            Assert.False(File.Exists(stagedMarkerPath));
            Assert.False(Directory.Exists(backupDirectory));
            Assert.False(File.Exists(backupMarkerPath));
            Assert.False(File.Exists(journalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Execute_RefusesAnUnmarkedExistingGeneratedDirectory()
    {
        var root = CreateTempDirectory();
        try
        {
            var generatedDirectory = Path.Combine(root, "obj", "sunder-rpc");
            Directory.CreateDirectory(generatedDirectory);
            var existingPath = Path.Combine(generatedDirectory, "user.cs");
            File.WriteAllText(existingPath, "user-owned");
            var task = CreateTask(
                root,
                Path.Combine(root, "obj"),
                Binding(WriteDescriptor(root, "test.binding"), "Test.Generated"));

            Assert.False(task.Execute());
            Assert.Contains(((TestBuildEngine)task.BuildEngine).Errors, error =>
                error.Contains("without its valid generated-output marker", StringComparison.Ordinal));
            Assert.Equal("user-owned", File.ReadAllText(existingPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Execute_RejectsDescriptorWithUnsafeAncestorOutsideProjectDirectory()
    {
        var root = CreateTempDirectory();
        try
        {
            var projectDirectory = Path.Combine(root, "project");
            var realDirectory = Path.Combine(root, "outside-real");
            var linkDirectory = Path.Combine(root, "outside-link");
            Directory.CreateDirectory(projectDirectory);
            var descriptorPath = WriteDescriptor(realDirectory, "test.binding");
            try
            {
                Directory.CreateSymbolicLink(linkDirectory, realDirectory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                return;
            }

            var linkedDescriptorPath = Path.Combine(linkDirectory, Path.GetFileName(descriptorPath));
            var task = CreateTask(
                projectDirectory,
                Path.Combine(projectDirectory, "obj"),
                Binding(linkedDescriptorPath, "Test.Generated"));

            Assert.False(task.Execute());
            Assert.Contains(((TestBuildEngine)task.BuildEngine).Errors, error =>
                error.Contains("outside-link", StringComparison.Ordinal)
                && error.Contains("symbolic link or reparse point", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Execute_RejectsOutputWithUnsafeAncestorOutsideProjectDirectory()
    {
        var root = CreateTempDirectory();
        try
        {
            var projectDirectory = Path.Combine(root, "project");
            var realOutputDirectory = Path.Combine(root, "outside-real");
            var linkOutputDirectory = Path.Combine(root, "outside-link");
            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(realOutputDirectory);
            try
            {
                Directory.CreateSymbolicLink(linkOutputDirectory, realOutputDirectory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                return;
            }
            var task = CreateTask(
                projectDirectory,
                Path.Combine(linkOutputDirectory, "obj"),
                Binding(WriteDescriptor(projectDirectory, "test.binding"), "Test.Generated"));

            Assert.False(task.Execute());
            Assert.Contains(((TestBuildEngine)task.BuildEngine).Errors, error =>
                error.Contains("outside-link", StringComparison.Ordinal)
                && error.Contains("symbolic link or reparse point", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(realOutputDirectory, "obj", "sunder-rpc")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Execute_SerializesConcurrentCompleteSetPublication()
    {
        var root = CreateTempDirectory();
        try
        {
            var intermediateOutputPath = Path.Combine(root, "obj");
            var a = Binding(WriteDescriptor(Path.Combine(root, "a"), "test.a"), "Test.Generated");
            a.SetMetadata("OutputFile", "A.g.cs");
            var b1 = Binding(WriteDescriptor(Path.Combine(root, "b1"), "test.b1"), "Test.Generated");
            b1.SetMetadata("OutputFile", "B1.g.cs");
            var b2 = Binding(WriteDescriptor(Path.Combine(root, "b2"), "test.b2"), "Test.Generated");
            b2.SetMetadata("OutputFile", "B2.g.cs");
            var first = CreateTask(root, intermediateOutputPath, a);
            var second = CreateTask(root, intermediateOutputPath, b1, b2);

            var results = await System.Threading.Tasks.Task.WhenAll(
                System.Threading.Tasks.Task.Run(first.Execute),
                System.Threading.Tasks.Task.Run(second.Execute));

            Assert.All(results, Assert.True);
            var generatedDirectory = Path.Combine(intermediateOutputPath, "sunder-rpc");
            var sources = Directory.EnumerateFiles(generatedDirectory, "*.cs", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(generatedDirectory, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.True(
                sources.SequenceEqual(["A.g.cs"], StringComparer.Ordinal)
                || sources.SequenceEqual(["B1.g.cs", "B2.g.cs"], StringComparer.Ordinal),
                "Concurrent output contained a mixed set: " + string.Join(", ", sources));
            Assert.True(GenerateSunderRpcBindingsTask.HasValidOwnershipMarker(
                GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(generatedDirectory)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GenerateSunderRpcBindingsTask CreateTask(
        string projectDirectory,
        string intermediateOutputPath,
        params TaskItem[] bindings)
        => new()
        {
            BuildEngine = new TestBuildEngine(),
            ProjectDirectory = projectDirectory,
            IntermediateOutputPath = intermediateOutputPath,
            Bindings = bindings,
        };

    private static GenerateSunderRpcBindingsTask CreateSourceTask(
        string projectDirectory,
        string intermediateOutputPath,
        string sourcePath,
        IReadOnlyList<string> references)
        => new()
        {
            BuildEngine = new TestBuildEngine(),
            ProjectDirectory = projectDirectory,
            IntermediateOutputPath = intermediateOutputPath,
            SourceFiles = [new TaskItem(sourcePath)],
            ReferencePaths = references.Select(static path => (ITaskItem)new TaskItem(path)).ToArray(),
            PackageVersion = "7.6.5",
            ProjectIdentity = "ImplementationFirstRpc",
            Configuration = "Debug",
            TargetFramework = "net10.0",
            NullableEnabled = true,
        };

    private static CleanSunderRpcGeneratedOutputTask CreateCleanupTask(
        string projectDirectory,
        string intermediateOutputPath)
    {
        var generatedDirectory = new TaskItem(Path.Combine(
            intermediateOutputPath,
            GenerateSunderRpcBindingsTask.GeneratedDirectoryName));
        generatedDirectory.SetMetadata("DiscoveryRoot", intermediateOutputPath);
        return new()
        {
            BuildEngine = new TestBuildEngine(),
            ProjectDirectory = projectDirectory,
            GeneratedDirectories = [generatedDirectory],
        };
    }

    private static TaskItem Binding(string descriptorPath, string namespaceName)
    {
        var binding = new TaskItem(descriptorPath);
        binding.SetMetadata("Namespace", namespaceName);
        return binding;
    }

    private static string WriteDescriptor(string directory, string contractId)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sample.rpc.json");
        File.WriteAllText(path, Descriptor.Replace("CONTRACT_ID", contractId, StringComparison.Ordinal));
        return path;
    }

    private static string[] ReadOwnedSet(string generatedDirectory)
    {
        var files = Directory.EnumerateFiles(generatedDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(generatedDirectory, path).Replace(Path.DirectorySeparatorChar, '/')
                            + ":" + Convert.ToBase64String(File.ReadAllBytes(path)))
            .Append("<marker>:" + Convert.ToBase64String(File.ReadAllBytes(
                GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(generatedDirectory))))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return files;
    }

    private static string[] ReadOwnedTimestamps(string generatedDirectory)
        => Directory.EnumerateFiles(generatedDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(generatedDirectory, path).Replace(Path.DirectorySeparatorChar, '/')
                            + ":" + File.GetLastWriteTimeUtc(path).Ticks)
            .Append("<directory>:" + Directory.GetLastWriteTimeUtc(generatedDirectory).Ticks)
            .Append("<marker>:" + File.GetLastWriteTimeUtc(
                GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(generatedDirectory)).Ticks)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] CompilationReferences()
    {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
                                        ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable.");
        return trustedPlatformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(SunderServiceAttribute).Assembly.Location)
            .Append(typeof(SunderWorkerProviderRegistration).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void WriteSourceAuthoringState(string root, SunderRpcContractDescriptor descriptor)
    {
        var canonicalDescriptor = Encoding.UTF8.GetString(descriptor.GetCanonicalUtf8());
        var canonicalAbi = Encoding.UTF8.GetString(descriptor.GetCanonicalAbiProjectionUtf8());
        var baseline = "{\n"
                       + "  \"schemaVersion\": 1,\n"
                       + "  \"authorityKind\": \"package\",\n"
                       + "  \"authorityId\": \"example.generated\",\n"
                       + "  \"packageId\": \"example.generated\",\n"
                       + "  \"contracts\": {\n"
                       + "    \"greeting\": {\n"
                       + "      \"contractId\": \"example.generated.greeting\",\n"
                       + "      \"current\": {\n"
                       + "        \"state\": \"draft\",\n"
                       + "        \"exposure\": \"public\",\n"
                       + $"        \"contractVersion\": \"{descriptor.Version}\",\n"
                       + $"        \"descriptorBytes\": {descriptor.GetCanonicalUtf8().Length},\n"
                        + $"        \"canonicalDescriptor\": {JsonSerializer.Serialize(canonicalDescriptor, DeterministicJson)},\n"
                       + $"        \"descriptorSha256\": \"{descriptor.Sha256}\",\n"
                       + $"        \"abiProjectionBytes\": {descriptor.GetCanonicalAbiProjectionUtf8().Length},\n"
                        + $"        \"canonicalAbiProjection\": {JsonSerializer.Serialize(canonicalAbi, DeterministicJson)},\n"
                       + $"        \"abiSha256\": \"{descriptor.AbiSha256}\"\n"
                       + "      },\n"
                       + "      \"history\": []\n"
                       + "    }\n"
                       + "  }\n"
                       + "}\n";
        File.WriteAllText(Path.Combine(root, "sunder-contracts.owned.json"), baseline);
        File.WriteAllText(
            Path.Combine(root, "sunder-contracts.lock.json"),
            "{\n"
            + "  \"lockVersion\": 1,\n"
            + "  \"packageArtifacts\": {},\n"
            + "  \"packageImports\": {},\n"
            + "  \"providerImports\": {},\n"
            + "  \"platformImports\": {}\n"
            + "}\n");
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);

    private static string CreateTempDirectory()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (OperatingSystem.IsMacOS() && tempRoot.StartsWith("/var/", StringComparison.Ordinal))
        {
            tempRoot = "/private" + tempRoot;
        }
        var path = Path.Combine(tempRoot, "sunder-rpc-binding-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private const string Descriptor = """
        {
          "descriptorVersion": 1,
          "schemaProfile": "sunder.rpc.values.v1",
          "contractId": "CONTRACT_ID",
          "version": "1.0.0",
          "errors": {},
          "services": {
            "sample": {
              "methods": {
                "greet": {
                  "kind": "unary",
                  "idempotency": "none",
                  "errors": [],
                  "requestSchema": { "$ref": "#/$defs/Request" },
                  "responseSchema": { "$ref": "#/$defs/Response" }
                }
              }
            }
          },
          "$defs": {
            "String": {
              "type": "string",
              "minLength": 0,
              "maxLength": 4096
            },
            "Request": {
              "type": "object",
              "properties": { "name": { "$ref": "#/$defs/String" } },
              "required": ["name"],
              "additionalProperties": false
            },
            "Response": {
              "type": "object",
              "properties": { "message": { "$ref": "#/$defs/String" } },
              "required": ["message"],
              "additionalProperties": false
            }
          }
        }
        """;

    private const string ImplementationFirstSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using Sunder.Sdk.Packaging;
        using Sunder.Sdk.Rpc;
        using Sunder.Sdk.Services;
        using Sunder.Sdk.Worker;
        [assembly: SunderPackage(Id = "example.generated", Name = "Generated")]
        namespace Example.Generated;
        public static class Bootstrap
        {
            public static SunderWorkerProviderRegistration Create()
                => new GreetingService().CreateSunderRpcProviderRegistration();
        }
        [SunderService(SunderServiceExposure.Public)]
        [SunderAppUse(Required = true)]
        public sealed partial class GreetingService
        {
            [SunderMethod]
            public ValueTask<GreetingResult> GreetAsync(
                GreetingRequest request,
                SunderRpcInvocationContext context,
                CancellationToken cancellationToken) => ValueTask.FromResult(new GreetingResult { Message = request.Name });
        }
        public sealed record GreetingRequest
        {
            public required string Name { get; init; }
        }
        public sealed record GreetingResult
        {
            public required string Message { get; init; }
        }
        """;

}
