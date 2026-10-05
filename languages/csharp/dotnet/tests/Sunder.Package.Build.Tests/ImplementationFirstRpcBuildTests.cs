using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

[Collection(nameof(TaskShadowStagingTests))]
public sealed partial class ImplementationFirstRpcBuildTests
{
    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task CleanBuild_DifferentAppTfm_GeneratesCompilesProjectsAndPreservesOneAtomicSourceSet()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-implementation-first-rpc");
        var repositoryRoot = FindRepositoryRoot();
        var fixtureSource = Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "tests",
            "Sunder.Package.Build.TestFixtures",
            "ImplementationFirstRpc");
        var fixture = ResolvePhysicalTemporaryPath(scope.GetPath("fixture"));
        CopyDirectory(fixtureSource, fixture);
        var sourcePath = Path.Combine(fixture, "Program.cs");
        await File.WriteAllTextAsync(sourcePath, (await File.ReadAllTextAsync(sourcePath)).Replace("[SunderRpcService",
            "[SunderRpcRuntimeUse(Actions = new[] { \"discover\" })]\n[SunderRpcService", StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(fixture, "DeclarationProbe.cs"), """
            namespace Example.Generated;
            public static class DeclarationProbe
            {
                public static Sunder.Sdk.Worker.SunderWorkerProviderRegistration Declare(Sunder.Sdk.Rpc.ISunderRpcServiceHandler handler)
                    => GreetingService.DeclareSunderRpcProvider(handler);
            }
            public static partial class OrdinaryRegexProbe
            {
                [System.Text.RegularExpressions.GeneratedRegex("^ordinary+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
                private static partial System.Text.RegularExpressions.Regex Pattern();
                public static bool Matches(string value) => Pattern().IsMatch(value);
            }
            """);
        var project = Path.Combine(fixture, "ImplementationFirstRpc.csproj");
        var appProject = Path.Combine(fixture, "App", "ImplementationFirstRpc.App.csproj");
        var configuration = ResolveConfiguration();
        var taskAssembly = typeof(GenerateSunderRpcBindingsTask).Assembly.Location;
        var referenceDirectory = Path.GetDirectoryName(taskAssembly)! + Path.DirectorySeparatorChar;
        var targetsPath = Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.targets");
        var properties = new[]
        {
            $"-p:Configuration={configuration}",
            $"-p:SunderReferenceDirectory={referenceDirectory}",
            $"-p:SunderPackageBuildTaskAssembly={taskAssembly}",
            "-p:SunderPackageBuildTaskSourceIsImmutable=true",
            $"-p:SunderPackageBuildTargetsPath={targetsPath}",
        };

        AssertSuccess(await RunDotnetAsync(
            fixture,
            ["restore", project, "--disable-parallel", "--nologo", .. properties]));
        AssertSuccess(await RunDotnetAsync(
            fixture,
            ["restore", appProject, "--disable-parallel", "--nologo", .. properties]));
        AssertSuccess(await BuildAsync(fixture, project, properties));

        var intermediate = Path.Combine(
            fixture,
            "obj",
            configuration,
            "net10.0",
            RuntimeInformation.RuntimeIdentifier);
        var neutralIntermediate = Path.Combine(fixture, "obj", configuration, "net10.0");
        var generated = Path.Combine(intermediate, GenerateSunderRpcBindingsTask.GeneratedDirectoryName);
        var marker = GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(generated);
        var projectExportGenerated = Path.Combine(
            neutralIntermediate,
            GenerateSunderRpcBindingsTask.GeneratedDirectoryName);
        var projectExportMarker = GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(projectExportGenerated);
        var assembly = Path.Combine(
            fixture,
            "bin",
            configuration,
            "net10.0",
            RuntimeInformation.RuntimeIdentifier,
            "ImplementationFirstRpc.dll");
        var manifest = Path.Combine(intermediate, "sunder-package.json");
        var devOutput = Path.Combine(
            fixture,
            "bin",
            configuration,
            "net10.0",
            RuntimeInformation.RuntimeIdentifier,
            "sunder-dev");
        var exportPath = Path.Combine(projectExportGenerated, SunderRpcAuthoringDocuments.PackageExportFileName);
        var receiptPath = Path.Combine(generated, SunderRpcAuthoringDocuments.AuthoringReceiptFileName);
        var authoringPath = Path.Combine(generated, "authoring.g.cs");
        var importsPath = Path.Combine(generated, "imports.g.cs");
        var authoringSchemaRoot = Path.Combine(repositoryRoot, "specs", "rpc", "v1", "authoring");

        Assert.True(File.Exists(assembly));
        Assert.True(File.Exists(authoringPath));
        Assert.True(File.Exists(importsPath));
        Assert.True(File.Exists(exportPath));
        Assert.True(File.Exists(receiptPath));
        Assert.Equal(GenerateSunderRpcBindingsTask.MarkerContents, await File.ReadAllTextAsync(marker));
        Assert.Equal(
            GenerateSunderRpcBindingsTask.MarkerContents,
            await File.ReadAllTextAsync(projectExportMarker));
        Assert.False(File.Exists(Path.Combine(generated, SunderRpcAuthoringDocuments.PackageExportFileName)));
        Assert.False(File.Exists(Path.Combine(
            projectExportGenerated,
            SunderRpcAuthoringDocuments.AuthoringReceiptFileName)));
        Assert.Contains(
            "CreateSunderRpcProviderRegistration",
            await File.ReadAllTextAsync(authoringPath),
            StringComparison.Ordinal);

        var baseline = SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(
            Path.Combine(fixture, SunderRpcAuthoringDocuments.OwnedContractsFileName));
        var lockSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
            Path.Combine(fixture, SunderRpcAuthoringDocuments.ContractLockFileName)))).ToLowerInvariant();
        var accepted = baseline.Contracts["greeting"].Current!.Descriptor;
        using (var export = JsonDocument.Parse(await File.ReadAllBytesAsync(exportPath)))
        {
            AssertMatchesSchema(
                Path.Combine(authoringSchemaRoot, "package-project-export.schema.json"),
                export.RootElement);
            var contract = Assert.Single(export.RootElement.GetProperty("contracts").EnumerateArray());
            Assert.Equal("net10.0", export.RootElement.GetProperty("targetFramework").GetString());
            Assert.Equal(lockSha256, export.RootElement.GetProperty("lockSha256").GetString());
            Assert.Equal(accepted.Sha256, contract.GetProperty("descriptorSha256").GetString());
            Assert.Equal(accepted.AbiSha256, contract.GetProperty("abiSha256").GetString());
            Assert.False(contract.GetProperty("runtimeUse").GetProperty("required").GetBoolean());
            Assert.Equal(
                Encoding.UTF8.GetString(accepted.GetCanonicalUtf8()),
                contract.GetProperty("canonicalDescriptor").GetString());
        }
        using (var receipt = JsonDocument.Parse(await File.ReadAllBytesAsync(receiptPath)))
        {
            AssertMatchesSchema(
                Path.Combine(authoringSchemaRoot, "authoring-receipt.schema.json"),
                receipt.RootElement);
            Assert.Equal(lockSha256, receipt.RootElement.GetProperty("lockSha256").GetString());
            Assert.Single(receipt.RootElement.GetProperty("bundles").EnumerateArray());
            Assert.Single(receipt.RootElement.GetProperty("providers").EnumerateArray());
            Assert.Single(receipt.RootElement.GetProperty("generatedBindings").EnumerateArray());
        }
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(devOutput);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        var bundle = Assert.Single(validation.Manifest!.ContractBundles!);
        Assert.Equal("example.generated.greeting", bundle!.ContractId);
        Assert.Equal("1.0.0", bundle.Version);
        Assert.Equal(accepted.Sha256, bundle.DescriptorSha256);
        Assert.Equal(accepted.AbiSha256, bundle.AbiSha256);
        var provider = Assert.Single(validation.Manifest.Provides!);
        Assert.Equal("example.generated.greeting.provider", provider!.ProviderId);
        var runtimeUse = Assert.Single(validation.Manifest.UsesContracts!);
        Assert.Equal("runtime", runtimeUse!.Role);
        Assert.False(runtimeUse.Required);
        Assert.Equal(new[] { "discover" }, runtimeUse.Actions);
        var packagedDescriptor = Path.Combine(
            devOutput,
            (SunderPackageFormat.SharedPayloadRoot + bundle.DescriptorPath).Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(accepted.GetCanonicalUtf8(), await File.ReadAllBytesAsync(packagedDescriptor));

        var oldTimestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        SetTreeTimestamp(generated, marker, oldTimestamp);
        SetTreeTimestamp(projectExportGenerated, projectExportMarker, oldTimestamp);
        var generatedBytes = ReadTreeBytes(generated, marker);
        var generatedTimestamps = ReadTreeTimestamps(generated, marker);
        var projectExportBytes = ReadTreeBytes(projectExportGenerated, projectExportMarker);
        var projectExportTimestamps = ReadTreeTimestamps(projectExportGenerated, projectExportMarker);
        var assemblyTimestamp = File.GetLastWriteTimeUtc(assembly);

        AssertSuccess(await BuildAsync(fixture, project, properties));
        Assert.Equal(generatedBytes, ReadTreeBytes(generated, marker));
        Assert.Equal(generatedTimestamps, ReadTreeTimestamps(generated, marker));
        Assert.Equal(projectExportBytes, ReadTreeBytes(projectExportGenerated, projectExportMarker));
        Assert.Equal(projectExportTimestamps, ReadTreeTimestamps(projectExportGenerated, projectExportMarker));
        Assert.Equal(assemblyTimestamp, File.GetLastWriteTimeUtc(assembly));

        var exportTimestampBeforeAppBuild = File.GetLastWriteTimeUtc(exportPath);
        var exportBytesBeforeAppBuild = await File.ReadAllBytesAsync(exportPath);
        AssertSuccess(await BuildAsync(fixture, appProject, properties));
        Assert.Equal(exportTimestampBeforeAppBuild, File.GetLastWriteTimeUtc(exportPath));
        Assert.Equal(exportBytesBeforeAppBuild, await File.ReadAllBytesAsync(exportPath));

        var appIntermediate = Path.Combine(fixture, "App", "obj", configuration, "net10.0-windows");
        var appGenerated = Path.Combine(appIntermediate, GenerateSunderRpcBindingsTask.GeneratedDirectoryName);
        var appMarker = GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(appGenerated);
        var appAssembly = Path.Combine(
            fixture,
            "App",
            "bin",
            configuration,
            "net10.0-windows",
            "ImplementationFirstRpc.App.dll");
        var appReceiptPath = Path.Combine(appGenerated, SunderRpcAuthoringDocuments.AuthoringReceiptFileName);
        var appBindingPath = Path.Combine(appGenerated, "bindings", "greeting.g.cs");
        Assert.True(File.Exists(appAssembly));
        Assert.True(File.Exists(appReceiptPath));
        Assert.True(File.Exists(appBindingPath));
        Assert.False(File.Exists(Path.Combine(appGenerated, "authoring.g.cs")));
        Assert.False(File.Exists(Path.Combine(
            fixture,
            "App",
            "bin",
            configuration,
            "net10.0-windows",
            "Sunder.Sdk.Worker.dll")));
        Assert.Contains(
            "internal sealed class GreetingClient",
            await File.ReadAllTextAsync(appBindingPath),
            StringComparison.Ordinal);
        using (var appReceipt = JsonDocument.Parse(await File.ReadAllBytesAsync(appReceiptPath)))
        {
            AssertMatchesSchema(
                Path.Combine(authoringSchemaRoot, "authoring-receipt.schema.json"),
                appReceipt.RootElement);
            Assert.Equal(lockSha256, appReceipt.RootElement.GetProperty("lockSha256").GetString());
            Assert.Single(appReceipt.RootElement.GetProperty("bundles").EnumerateArray());
            Assert.Empty(appReceipt.RootElement.GetProperty("providers").EnumerateArray());
            Assert.False(appReceipt.RootElement.GetProperty("contracts")[0].TryGetProperty("runtimeUse", out _));
            var use = Assert.Single(appReceipt.RootElement.GetProperty("uses").EnumerateArray());
            Assert.Equal("app", use.GetProperty("role").GetString());
            Assert.True(use.GetProperty("required").GetBoolean());
            Assert.Equal(["discover", "invoke"], use.GetProperty("actions").EnumerateArray()
                .Select(static action => action.GetString()!)
                .ToArray());
        }
        Assert.False(Directory.Exists(Path.Combine(
            fixture,
            "obj",
            configuration,
            "net10.0-windows",
            GenerateSunderRpcBindingsTask.GeneratedDirectoryName)));
        var appDevOutput = Path.Combine(fixture, "App", "bin", configuration, "net10.0-windows", "sunder-dev");
        var appValidation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(appDevOutput);
        Assert.True(appValidation.Success, string.Join(Environment.NewLine, appValidation.Errors));
        Assert.Single(appValidation.Manifest!.ContractBundles!);
        Assert.Single(appValidation.Manifest.UsesContracts!);
        Assert.Empty(appValidation.Manifest.Provides!);

        SetTreeTimestamp(appGenerated, appMarker, oldTimestamp);
        var appGeneratedBytes = ReadTreeBytes(appGenerated, appMarker);
        var appGeneratedTimestamps = ReadTreeTimestamps(appGenerated, appMarker);
        var appAssemblyTimestamp = File.GetLastWriteTimeUtc(appAssembly);
        AssertSuccess(await BuildAsync(fixture, appProject, properties));
        Assert.Equal(appGeneratedBytes, ReadTreeBytes(appGenerated, appMarker));
        Assert.Equal(appGeneratedTimestamps, ReadTreeTimestamps(appGenerated, appMarker));
        Assert.Equal(appAssemblyTimestamp, File.GetLastWriteTimeUtc(appAssembly));

        AssertSuccess(await RunDotnetAsync(
            fixture,
            [
                "msbuild",
                project,
                "-t:_BuildSunderPackageApp",
                "-m:1",
                "-nologo",
                .. properties,
                "-p:SunderPackagePrimaryProject=true",
                $"-p:SunderPackageComposeRuntimeIdentifiers={RuntimeInformation.RuntimeIdentifier}",
            ]));
        Assert.Equal(appGeneratedBytes, ReadTreeBytes(appGenerated, appMarker));
        Assert.Equal(appGeneratedTimestamps, ReadTreeTimestamps(appGenerated, appMarker));
        Assert.Equal(appAssemblyTimestamp, File.GetLastWriteTimeUtc(appAssembly));
        Assert.Equal(projectExportBytes, ReadTreeBytes(projectExportGenerated, projectExportMarker));

        var manifestBytes = await File.ReadAllBytesAsync(manifest);
        var devOutputHash = HashTree(devOutput);
        await File.WriteAllTextAsync(
            Path.Combine(fixture, "Collision.cs"),
            "using Sunder.Sdk.Worker;\n"
            + "namespace Example.Generated;\n"
            + "public sealed partial class GreetingService\n"
            + "{\n"
            + "    public SunderWorkerProviderRegistration CreateSunderRpcProviderRegistration() => throw null!;\n"
            + "}\n");

        var failed = await BuildAsync(fixture, project, properties);
        Assert.NotEqual(0, failed.ExitCode);
        Assert.Contains(
            "rpc-authoring.generated-name-collision",
            failed.StandardOutput + failed.StandardError,
            StringComparison.Ordinal);
        Assert.Equal(generatedBytes, ReadTreeBytes(generated, marker));
        Assert.Equal(projectExportBytes, ReadTreeBytes(projectExportGenerated, projectExportMarker));
        Assert.Equal(assemblyTimestamp, File.GetLastWriteTimeUtc(assembly));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(manifest));
        Assert.Equal(devOutputHash, HashTree(devOutput));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(intermediate),
            path => Path.GetFileName(path).Contains(".stage-", StringComparison.Ordinal)
                    || Path.GetFileName(path).Contains(".backup-", StringComparison.Ordinal)
                    || Path.GetFileName(path).Contains(".sunder-output-transaction.json", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(neutralIntermediate),
            path => Path.GetFileName(path).Contains(".stage-", StringComparison.Ordinal)
                    || Path.GetFileName(path).Contains(".backup-", StringComparison.Ordinal)
                    || Path.GetFileName(path).Contains(".sunder-output-transaction.json", StringComparison.Ordinal));

        File.Delete(Path.Combine(fixture, "Collision.cs"));
        AssertSuccess(await RunDotnetAsync(
            fixture,
            ["clean", appProject, "--disable-build-servers", "-m:1", "--nologo", .. properties]));
        AssertSuccess(await RunDotnetAsync(
            fixture,
            ["clean", project, "--disable-build-servers", "-m:1", "--nologo", .. properties]));
        Assert.False(Directory.Exists(appGenerated));
        Assert.False(File.Exists(appMarker));
        Assert.False(Directory.Exists(generated));
        Assert.False(File.Exists(marker));
        Assert.False(Directory.Exists(projectExportGenerated));
        Assert.False(File.Exists(projectExportMarker));

        AssertSuccess(await BuildAsync(fixture, project, properties));
        AssertSuccess(await BuildAsync(fixture, appProject, properties));
        Assert.True(File.Exists(assembly));
        Assert.True(File.Exists(appAssembly));
        Assert.True(File.Exists(exportPath));
        Assert.True(File.Exists(appBindingPath));

        var buildReady = Path.Combine(fixture, "rpc-build-ready");
        var buildRelease = Path.Combine(fixture, "rpc-build-release");
        var manifestReady = Path.Combine(fixture, "rpc-manifest-ready");
        var manifestRelease = Path.Combine(fixture, "rpc-manifest-release");
        var cleanReady = Path.Combine(fixture, "rpc-clean-ready");
        var raceTargets = Path.Combine(fixture, "RpcRace.targets");
        await File.WriteAllTextAsync(raceTargets, """
            <Project>
              <UsingTask TaskName="_SunderWaitForRpcRaceRelease"
                         TaskFactory="RoslynCodeTaskFactory"
                         AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
                <ParameterGroup>
                  <ReadyFile ParameterType="System.String" Required="true" />
                  <ReleaseFile ParameterType="System.String" Required="true" />
                </ParameterGroup>
                <Task>
                  <Using Namespace="System" />
                  <Using Namespace="System.IO" />
                  <Using Namespace="System.Threading" />
                  <Code Type="Fragment" Language="cs"><![CDATA[
                    File.WriteAllText(ReadyFile, "ready");
                    var deadline = DateTime.UtcNow.AddMinutes(2);
                    while (!File.Exists(ReleaseFile) && DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(25);
                    }
                    if (!File.Exists(ReleaseFile))
                    {
                        Log.LogError("Timed out waiting to release the RPC build race.");
                        return false;
                    }
                  ]]></Code>
                </Task>
              </UsingTask>
              <Target Name="_PauseSunderRpcRaceBuild"
                      BeforeTargets="CoreCompile"
                      DependsOnTargets="GenerateSunderRpcImplementationBindings"
                      Condition="'$(SunderRpcRaceBuildReady)' != ''">
                <_SunderWaitForRpcRaceRelease ReadyFile="$(SunderRpcRaceBuildReady)"
                                               ReleaseFile="$(SunderRpcRaceBuildRelease)" />
              </Target>
              <Target Name="_PauseSunderRpcRaceManifest"
                      BeforeTargets="GenerateSunderPackageManifest"
                      Condition="'$(SunderRpcRaceManifestReady)' != ''">
                <_SunderWaitForRpcRaceRelease ReadyFile="$(SunderRpcRaceManifestReady)"
                                               ReleaseFile="$(SunderRpcRaceManifestRelease)" />
              </Target>
              <Target Name="_SignalSunderRpcRaceClean"
                      BeforeTargets="CleanSunderRpcGeneratedOutput"
                      Condition="'$(SunderRpcRaceCleanReady)' != ''">
                <WriteLinesToFile File="$(SunderRpcRaceCleanReady)" Lines="ready" Overwrite="true" />
              </Target>
            </Project>
            """);
        var projectContents = await File.ReadAllTextAsync(project);
        await File.WriteAllTextAsync(
            project,
            projectContents.Replace(
                "</Project>",
                $"  <Import Project=\"{raceTargets}\" />{Environment.NewLine}</Project>",
                StringComparison.Ordinal));

        var racingBuild = RunDotnetAsync(
            fixture,
            [
                "msbuild",
                project,
                "-t:Compile;GenerateSunderPackageManifest",
                "-m:1",
                "-nodeReuse:false",
                "-nologo",
                .. properties,
                $"-p:SunderRpcRaceBuildReady={buildReady}",
                $"-p:SunderRpcRaceBuildRelease={buildRelease}",
                $"-p:SunderRpcRaceManifestReady={manifestReady}",
                $"-p:SunderRpcRaceManifestRelease={manifestRelease}",
            ]);
        await WaitForFileAsync(buildReady, racingBuild);
        Assert.True(Directory.Exists(generated));
        using (GeneratedOutputLock.Acquire(
                   [GeneratedOutputLock.TargetLeafDiscoveryKey(neutralIntermediate, projectExportGenerated)],
                   [projectExportGenerated],
                   TimeSpan.FromSeconds(2)))
        {
            Assert.True(File.Exists(exportPath));
        }

        var racingClean = RunDotnetAsync(
            fixture,
            [
                "clean",
                project,
                "--disable-build-servers",
                "-m:1",
                "--nologo",
                .. properties,
                $"-p:SunderRpcRaceCleanReady={cleanReady}",
            ]);
        await WaitForFileAsync(cleanReady, racingClean);
        await Task.Delay(250);
        Assert.False(racingClean.IsCompleted);
        Assert.True(Directory.Exists(generated));
        await File.WriteAllTextAsync(buildRelease, "release");
        await WaitForFileAsync(manifestReady, racingBuild);
        Assert.False(racingClean.IsCompleted);
        Assert.True(Directory.Exists(generated));
        await File.WriteAllTextAsync(manifestRelease, "release");

        AssertSuccess(await racingBuild);
        AssertSuccess(await racingClean);
        Assert.False(Directory.Exists(generated));
        Assert.False(File.Exists(marker));
        Assert.False(Directory.Exists(projectExportGenerated));
        Assert.False(File.Exists(projectExportMarker));
    }

    private static Task<ProcessResult> BuildAsync(
        string workingDirectory,
        string project,
        IReadOnlyList<string> properties)
        => RunDotnetAsync(
            workingDirectory,
            [
                "build",
                project,
                "--no-restore",
                "--disable-build-servers",
                "-m:1",
                "--nologo",
                .. properties,
            ]);

    private static async Task<ProcessResult> RunDotnetAsync(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            return new(-1, await standardOutput, await standardError + "dotnet timed out.");
        }
        return new(process.ExitCode, await standardOutput, await standardError);
    }

    private static async Task WaitForFileAsync(string path, Task<ProcessResult> process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(path))
        {
            if (process.IsCompleted)
            {
                var result = await process;
                AssertSuccess(result);
                Assert.Fail($"Process completed without writing synchronization file '{path}'.");
            }
            await Task.Delay(25, timeout.Token);
        }
    }

    private static void AssertSuccess(ProcessResult result)
        => Assert.True(
            result.ExitCode == 0,
            $"dotnet exited with {result.ExitCode}.{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");

    private static void AssertMatchesSchema(string schemaPath, JsonElement instance)
    {
        using var schema = JsonDocument.Parse(File.ReadAllBytes(schemaPath));
        Assert.True(
            JsonSchemaTestValidator.IsValid(schema.RootElement, instance),
            $"Generated document does not match schema '{schemaPath}'.");
    }

    private static string[] ReadTreeBytes(string generated, string marker)
        => Directory.EnumerateFiles(generated, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(generated, path).Replace(Path.DirectorySeparatorChar, '/')
                            + ":" + Convert.ToBase64String(File.ReadAllBytes(path)))
            .Append("<marker>:" + Convert.ToBase64String(File.ReadAllBytes(marker)))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void SetTreeTimestamp(string generated, string marker, DateTime timestamp)
    {
        foreach (var path in Directory.EnumerateFiles(generated, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(path, timestamp);
        }
        File.SetLastWriteTimeUtc(marker, timestamp);
        Directory.SetLastWriteTimeUtc(generated, timestamp);
    }

    private static string[] ReadTreeTimestamps(string generated, string marker)
        => Directory.EnumerateFiles(generated, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(generated, path).Replace(Path.DirectorySeparatorChar, '/')
                            + ":" + File.GetLastWriteTimeUtc(path).Ticks)
            .Append("<directory>:" + Directory.GetLastWriteTimeUtc(generated).Ticks)
            .Append("<marker>:" + File.GetLastWriteTimeUtc(marker).Ticks)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string HashTree(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(path));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (name is "bin" or "obj") continue;
            CopyDirectory(directory, Path.Combine(destination, name));
        }
    }

    private static string ResolveConfiguration()
        => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent?.Name
           ?? "Debug";

    private static string ResolvePhysicalTemporaryPath(string path)
    {
        if (!OperatingSystem.IsMacOS()) return path;
        if (path.StartsWith("/var/", StringComparison.Ordinal)
            || path.StartsWith("/tmp/", StringComparison.Ordinal))
        {
            return "/private" + path;
        }
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Sunder Core repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
