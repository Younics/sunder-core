using Sunder.Cli;
using Sunder.Host.Contracts;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli.Tests;

internal sealed class FakeHostClient : ICliHostClient
{
    public Func<CancellationToken, Task<HostRuntimeStatus>> Status { get; set; }
        = _ => Task.FromResult(new HostRuntimeStatus(
            HostRuntimeDesiredState.Running,
            HostRuntimeState.Ready,
            1,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            null,
            null,
            null));
    public Func<HostLifecycleRequest, CancellationToken, Task<HostLifecycleSubmission>> Start { get; set; }
        = (request, _) => Task.FromResult(Submission(request, HostOperationKinds.RuntimeStart));
    public Func<HostLifecycleRequest, CancellationToken, Task<HostLifecycleSubmission>> Stop { get; set; }
        = (request, _) => Task.FromResult(Submission(request, HostOperationKinds.RuntimeStop));
    public Func<HostLifecycleRequest, CancellationToken, Task<HostLifecycleSubmission>> Restart { get; set; }
        = (request, _) => Task.FromResult(Submission(request, HostOperationKinds.RuntimeRestart));
    public Func<HostOperationDescriptor, CancellationToken, Task<HostOperationDescriptor>> Wait { get; set; }
        = (operation, _) => Task.FromResult(operation with { State = HostOperationState.Succeeded, Message = "Completed." });

    public Task<HostRuntimeStatus> GetStatusAsync(CancellationToken token) => Status(token);
    public Task<HostLifecycleSubmission> SubmitStartRuntimeAsync(HostLifecycleRequest request, CancellationToken token) => Start(request, token);
    public Task<HostLifecycleSubmission> SubmitStopRuntimeAsync(HostLifecycleRequest request, CancellationToken token) => Stop(request, token);
    public Task<HostLifecycleSubmission> SubmitRestartRuntimeAsync(HostLifecycleRequest request, CancellationToken token) => Restart(request, token);
    public Task<HostOperationDescriptor> WaitForOperationAsync(HostOperationDescriptor operation, CancellationToken token) => Wait(operation, token);
    public void Dispose() { }

    private static HostLifecycleSubmission Submission(HostLifecycleRequest request, string kind)
        => new(new HostOperationDescriptor(
            "operation-1",
            request.MutationId,
            kind,
            request.ExpectedDeploymentGeneration,
            HostOperationState.Accepted,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            null));
}

internal sealed class FakeRuntimeClient : ICliRuntimeClient
{
    public Func<CancellationToken, Task<SystemStatusResponse>> SystemStatus { get; set; }
        = _ => Task.FromResult(new SystemStatusResponse("Sunder Runtime", "1.0.0", true, DateTimeOffset.UnixEpoch));
    public Func<CancellationToken, Task<RuntimeResetChallengeResponse>> ResetPrepare { get; set; }
        = _ => Task.FromResult(new RuntimeResetChallengeResponse("test-challenge", DateTimeOffset.UtcNow.AddMinutes(1)));
    public Func<string, CancellationToken, Task<RuntimeResetDrainResponse>> ResetDrain { get; set; }
        = (_, _) => Task.FromResult(new RuntimeResetDrainResponse([]));
    public Func<CancellationToken, Task<RuntimeV1ResetResult>> LocalReset { get; set; }
        = _ => Task.FromResult(new RuntimeV1ResetResult([
            new("package-catalog-and-payloads", "already-empty"),
            new("package-state-files-secrets-and-logs", "already-empty"),
            new("uploads-and-snapshots", "already-empty"),
            new("registry-credentials", "already-empty"),
            new("runtime-connection", "already-empty"),
            new("runtime-v1-root", "already-empty"),
        ]));
    public Func<CancellationToken, Task<IReadOnlyList<InstalledPackageDescriptor>>> Installed { get; set; }
        = _ => Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>([]);
    public Func<CancellationToken, Task<RuntimePackageSnapshot>> PackageSnapshot { get; set; }
        = _ => Task.FromResult(new RuntimePackageSnapshot(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            1,
            0,
            RuntimeBootstrapState.Ready,
            [],
            [],
            [],
            []));
    public Func<RuntimeRegistryAuthStartRequest, CancellationToken, Task<RuntimeRegistryAuthStartResponse>> AuthStart { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<string, CancellationToken, Task<RuntimeRegistryAuthSessionStatus?>> AuthSession { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<string, CancellationToken, Task<RuntimeRegistryAuthStatus>> AuthStatus { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<string, CancellationToken, Task<RuntimeRegistryAuthStatus>> Logout { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<RuntimeRegistryPackageRequest, CancellationToken, Task<RuntimeRegistryPackageChangeResult>> InstallRegistry { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<RuntimeRegistryUpdateRequest, CancellationToken, Task<RuntimeRegistryPackageChangeResult>> UpdateRegistry { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<RuntimeRegistrySourceAdoptionRequest, CancellationToken, Task<RuntimeRegistryPackageChangeResult>> AdoptRegistrySource { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<string, string, bool, bool, CancellationToken, Task<PackageOperationResult>> ApplyLocal { get; set; }
        = (_, _, _, _, _) => throw new NotSupportedException();
    public Func<string, bool, CancellationToken, Task<PackageOperationResult>> SetEnabled { get; set; }
        = (_, _, _) => throw new NotSupportedException();
    public Func<string, CancellationToken, Task<PackageUninstallPlan>> UninstallPlan { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<string, PackageUninstallRequest, CancellationToken, Task<PackageOperationResult>> Uninstall { get; set; }
        = (_, _, _) => throw new NotSupportedException();
    public Func<string, string, bool, CancellationToken, Task<RegistryPublishPackageResponse>> PublishPackage { get; set; }
        = (_, _, _, _) => throw new NotSupportedException();
    public Func<string, string, CancellationToken, Task<RegistryPublishStackResponse>> PublishStack { get; set; }
        = (_, _, _) => throw new NotSupportedException();
    public Func<RuntimeRegistryYankRequest, CancellationToken, Task<RegistryPackageManagementOperationResponse>> Yank { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<RuntimeRegistryDeprecateRequest, CancellationToken, Task<RegistryPackageManagementOperationResponse>> Deprecate { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<RuntimeRegistryDistTagRequest, CancellationToken, Task<RegistryPackageManagementOperationResponse>> DistTag { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<RuntimeRegistryDeleteStackRequest, CancellationToken, Task<RegistryStackManagementOperationResponse>> DeleteStack { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<CancellationToken, Task<IReadOnlyList<PackageSettingsSchemaDescriptor>>> SettingsSchemas { get; set; }
        = _ => Task.FromResult<IReadOnlyList<PackageSettingsSchemaDescriptor>>([]);
    public Func<string, CancellationToken, Task<PackageSettingsValuesResponse?>> SettingsValues { get; set; }
        = (_, _) => Task.FromResult<PackageSettingsValuesResponse?>(null);
    public Func<string, string, CancellationToken, Task<PackageSettingValueResponse>> SettingValue { get; set; }
        = (_, _, _) => throw new NotSupportedException();
    public Func<string, string, string, CancellationToken, Task> SetSetting { get; set; }
        = (_, _, _, _) => throw new NotSupportedException();
    public Func<string, string, CancellationToken, Task> DeleteSetting { get; set; }
        = (_, _, _) => throw new NotSupportedException();
    public Func<string, CancellationToken, Task<PackageAuthStatusResponse>> PackageAuthStatus { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<string, CancellationToken, Task<PackageAuthSessionStartResponse>> PackageAuthStart { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<string, string, CancellationToken, Task<PackageAuthSessionStatusResponse>> PackageAuthSession { get; set; }
        = (_, _, _) => throw new NotSupportedException();
    public Func<string, string, CancellationToken, Task<bool>> PackageAuthCancel { get; set; }
        = (_, _, _) => Task.FromResult(true);
    public Func<string, CancellationToken, Task<PackageAuthStatusResponse>> PackageAuthDisconnect { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<string, CancellationToken, Task<ContentUploadDescriptor>> StackUpload { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<RuntimeStackImportPreviewRequest, CancellationToken, Task<RuntimeStackImportPreviewResponse>> StackPreview { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<string, CancellationToken, Task> StackDiscard { get; set; }
        = (_, _) => Task.CompletedTask;
    public Func<CancellationToken, Task<RuntimeStackExportDiscoveryResponse>> StackExportItems { get; set; }
        = _ => throw new NotSupportedException();
    public Func<RuntimeStackExportRequest, CancellationToken, Task<RuntimeStackExportResponse>> StackExport { get; set; }
        = (_, _) => throw new NotSupportedException();
    public Func<ContentDownloadDescriptor, string, CancellationToken, Task> ContentDownload { get; set; }
        = (_, _, _) => throw new NotSupportedException();

    public Task<SystemStatusResponse> GetSystemStatusAsync(CancellationToken token) => SystemStatus(token);
    public Task<RuntimeResetChallengeResponse> PrepareResetAsync(CancellationToken token) => ResetPrepare(token);
    public Task<RuntimeResetDrainResponse> DrainForResetAsync(string challenge, CancellationToken token) => ResetDrain(challenge, token);
    public Task<RuntimeV1ResetResult> ResetLocalStateAsync(CancellationToken token) => LocalReset(token);
    public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken token) => Installed(token);
    public Task<RuntimePackageSnapshot> GetPackageSnapshotAsync(CancellationToken token) => PackageSnapshot(token);
    public Task<RuntimeRegistryAuthStartResponse> StartRegistryAuthAsync(RuntimeRegistryAuthStartRequest request, CancellationToken token) => AuthStart(request, token);
    public Task<RuntimeRegistryAuthSessionStatus?> GetRegistryAuthSessionAsync(string sessionId, CancellationToken token) => AuthSession(sessionId, token);
    public Task<RuntimeRegistryAuthStatus> GetRegistryAuthStatusAsync(string origin, CancellationToken token) => AuthStatus(origin, token);
    public Task<RuntimeRegistryAuthStatus> LogoutRegistryAsync(string origin, CancellationToken token) => Logout(origin, token);
    public Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken token) => InstallRegistry(request, token);
    public Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken token) => UpdateRegistry(request, token);
    public Task<RuntimeRegistryPackageChangeResult> AdoptRegistryPackageSourceAsync(RuntimeRegistrySourceAdoptionRequest request, CancellationToken token) => AdoptRegistrySource(request, token);
    public Task<PackageOperationResult> ApplyLocalPackageAsync(string path, string packageId, bool allowDowngrade, bool reinstall, CancellationToken token) => ApplyLocal(path, packageId, allowDowngrade, reinstall, token);
    public Task<PackageOperationResult> SetPackageEnabledAsync(string packageId, bool enabled, CancellationToken token) => SetEnabled(packageId, enabled, token);
    public Task<PackageUninstallPlan> GetPackageUninstallPlanAsync(string packageId, CancellationToken token) => UninstallPlan(packageId, token);
    public Task<PackageOperationResult> UninstallPackageAsync(string packageId, PackageUninstallRequest request, CancellationToken token) => Uninstall(packageId, request, token);
    public Task<RegistryPublishPackageResponse> PublishRegistryPackageAsync(string origin, string path, bool setLatest, CancellationToken token) => PublishPackage(origin, path, setLatest, token);
    public Task<RegistryPublishStackResponse> PublishRegistryStackAsync(string origin, string path, CancellationToken token) => PublishStack(origin, path, token);
    public Task<RegistryPackageManagementOperationResponse> SetYankAsync(RuntimeRegistryYankRequest request, CancellationToken token) => Yank(request, token);
    public Task<RegistryPackageManagementOperationResponse> SetDeprecationAsync(RuntimeRegistryDeprecateRequest request, CancellationToken token) => Deprecate(request, token);
    public Task<RegistryPackageManagementOperationResponse> SetDistTagAsync(RuntimeRegistryDistTagRequest request, CancellationToken token) => DistTag(request, token);
    public Task<RegistryStackManagementOperationResponse> DeleteRegistryStackAsync(RuntimeRegistryDeleteStackRequest request, CancellationToken token) => DeleteStack(request, token);
    public Task<IReadOnlyList<PackageSettingsSchemaDescriptor>> GetPackageSettingsSchemasAsync(CancellationToken token) => SettingsSchemas(token);
    public Task<PackageSettingsValuesResponse?> GetPackageSettingsValuesAsync(string packageId, CancellationToken token) => SettingsValues(packageId, token);
    public Task<PackageSettingValueResponse> GetPackageSettingValueAsync(string packageId, string key, CancellationToken token) => SettingValue(packageId, key, token);
    public Task SetPackageSettingValueAsync(string packageId, string key, string value, CancellationToken token) => SetSetting(packageId, key, value, token);
    public Task DeletePackageSettingValueAsync(string packageId, string key, CancellationToken token) => DeleteSetting(packageId, key, token);
    public Task<PackageAuthStatusResponse> GetPackageAuthStatusAsync(string packageId, CancellationToken token) => PackageAuthStatus(packageId, token);
    public Task<PackageAuthSessionStartResponse> StartPackageAuthAsync(string packageId, CancellationToken token) => PackageAuthStart(packageId, token);
    public Task<PackageAuthSessionStatusResponse> GetPackageAuthSessionStatusAsync(string packageId, string authSessionId, CancellationToken token) => PackageAuthSession(packageId, authSessionId, token);
    public Task<bool> CancelPackageAuthSessionAsync(string packageId, string authSessionId, CancellationToken token) => PackageAuthCancel(packageId, authSessionId, token);
    public Task<PackageAuthStatusResponse> DisconnectPackageAuthAsync(string packageId, CancellationToken token) => PackageAuthDisconnect(packageId, token);
    public Task<ContentUploadDescriptor> UploadStackAsync(string filePath, CancellationToken token) => StackUpload(filePath, token);
    public Task<RuntimeStackImportPreviewResponse> PreviewStackImportAsync(RuntimeStackImportPreviewRequest request, CancellationToken token) => StackPreview(request, token);
    public Task DiscardStackImportPlanAsync(string planId, CancellationToken token) => StackDiscard(planId, token);
    public Task<RuntimeStackExportDiscoveryResponse> ListStackExportItemsAsync(CancellationToken token) => StackExportItems(token);
    public Task<RuntimeStackExportResponse> ExportStackAsync(RuntimeStackExportRequest request, CancellationToken token) => StackExport(request, token);
    public Task DownloadContentAsync(ContentDownloadDescriptor download, string destinationPath, CancellationToken token) => ContentDownload(download, destinationPath, token);
    public void Dispose() { }
}

internal sealed class FakeRegistryClient : IRegistryClient
{
    public Uri RegistryApiUrl { get; } = new("https://registry.test/");
    public Func<string?, int, int, CancellationToken, Task<IReadOnlyList<RegistryPackageSummary>>> Search { get; set; }
        = (_, _, _, _) => Task.FromResult<IReadOnlyList<RegistryPackageSummary>>([]);
    public Func<string?, int, int, CancellationToken, Task<IReadOnlyList<RegistryStackSummary>>> SearchStacks { get; set; }
        = (_, _, _, _) => Task.FromResult<IReadOnlyList<RegistryStackSummary>>([]);
    public Func<string, CancellationToken, Task<RegistryStackDetails?>> GetStack { get; set; }
        = (_, _) => Task.FromResult<RegistryStackDetails?>(null);
    public Func<RegistryStackArtifact, string, string, bool, CancellationToken, Task> Download { get; set; }
        = (_, _, _, _, _) => Task.CompletedTask;
    public Func<string, CancellationToken, Task<RegistryPackageDistTagsResponse?>> DistTags { get; set; }
        = (_, _) => Task.FromResult<RegistryPackageDistTagsResponse?>(null);
    public Func<string, bool, RegistryPublishCredential, CancellationToken, Task<RegistryPublishPackageResponse>> PublishPackage { get; set; }
        = (_, _, _, _) => Task.FromResult(new RegistryPublishPackageResponse(false, null, null, "Publishing is not configured for this test.", [], ["Publishing is not configured for this test."]));
    public Func<string, RegistryPublishCredential, CancellationToken, Task<RegistryPublishStackResponse>> PublishStack { get; set; }
        = (_, _, _) => Task.FromResult(new RegistryPublishStackResponse(false, null, "Publishing is not configured for this test.", [], ["Publishing is not configured for this test."]));
    public Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, CancellationToken token) => Search(query, skip, take, token);
    public Task<IReadOnlyList<RegistryStackSummary>> SearchStacksAsync(string? query, int skip, int take, CancellationToken token) => SearchStacks(query, skip, take, token);
    public Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken token) => Task.FromResult<RegistryPackageDetails?>(null);
    public Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken token) => Task.FromResult<RegistryPackageVersionDetails?>(null);
    public Task<RegistryStackDetails?> GetStackAsync(string stackId, CancellationToken token) => GetStack(stackId, token);
    public Task<RegistryPackageDistTagsResponse?> GetDistTagsAsync(string packageId, CancellationToken token) => DistTags(packageId, token);
    public Task<RegistryPublishPackageResponse> PublishLocalPackageAsync(string packagePath, bool setLatest, CancellationToken token)
        => Task.FromResult(new RegistryPublishPackageResponse(false, null, null, "Publishing is not configured for this test.", [], ["Publishing is not configured for this test."]));
    public Task<RegistryPublishStackResponse> PublishLocalStackAsync(string stackPath, CancellationToken token)
        => Task.FromResult(new RegistryPublishStackResponse(false, null, "Publishing is not configured for this test.", [], ["Publishing is not configured for this test."]));
    public Task<RegistryPublishPackageResponse> PublishPackageAsync(string packagePath, bool setLatest, RegistryPublishCredential credential, CancellationToken token)
        => PublishPackage(packagePath, setLatest, credential, token);
    public Task<RegistryPublishStackResponse> PublishStackAsync(string stackPath, RegistryPublishCredential credential, CancellationToken token)
        => PublishStack(stackPath, credential, token);
    public Task DownloadStackAsync(RegistryStackArtifact artifact, string stackId, string destinationPath, bool force, CancellationToken token) => Download(artifact, stackId, destinationPath, force, token);
    public void Dispose() { }
}

internal sealed class FakeBrowserLauncher : IBrowserLauncher
{
    public Uri? Opened { get; private set; }
    public bool TryOpen(Uri uri) { Opened = uri; return true; }
}

internal sealed class FakePackageSecretValueReader : IPackageSecretValueReader
{
    public Func<PackageSecretSource, string?, CancellationToken, ValueTask<PackageSecretValue>> Read { get; set; }
        = (_, _, _) => ValueTask.FromException<PackageSecretValue>(new NotSupportedException());

    public ValueTask<PackageSecretValue> ReadAsync(
        PackageSecretSource source,
        string? environmentVariable,
        CancellationToken cancellationToken)
        => Read(source, environmentVariable, cancellationToken);
}

internal static class CliTestHost
{
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string[] args,
        FakeRuntimeClient? runtime = null,
        FakeRegistryClient? registry = null,
        FakeHostClient? host = null,
        IBrowserLauncher? browser = null,
        CancellationToken token = default,
        string runtimeUrl = "http://127.0.0.1:5275/",
        Func<CliOptions, ICliHostClient>? hostFactory = null,
        Func<CliOptions, ICliRuntimeClient>? runtimeFactory = null,
        Func<CliOptions, IRegistryClient>? registryFactory = null,
        Func<CliOptionOverrides, CliConfigurationRequirements, CliOptions>? optionsFactory = null,
        IRegistryPublishCredentialReader? publishCredentials = null,
        IPackageSecretValueReader? packageSecrets = null)
    {
        host ??= new();
        runtime ??= new();
        registry ??= new();
        var stdout = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var stderr = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var app = new CliApplication(
            stdout,
            stderr,
            hostFactory ?? (_ => host),
            runtimeFactory ?? (_ => runtime),
            registryFactory ?? (_ => registry),
            browser ?? new FakeBrowserLauncher(),
            publishCredentials ?? new RegistryPublishCredentialReader(
                TextReader.Null,
                () => false,
                _ => null,
                (_, _) => { }),
            packageSecrets ?? new PackageSecretValueReader(TextReader.Null, () => false, _ => null),
            optionsFactory);
        var globals = new List<string>();
        AddDefault(globals, args, "--registry-api-url", "https://registry.test/");
        AddDefault(globals, args, "--registry-web-url", "https://registry.test/");
        AddDefault(globals, args, "--runtime-url", runtimeUrl);
        var exit = await app.RunAsync([.. globals, .. args], token);
        return (exit, stdout.ToString().Replace("\r\n", "\n"), stderr.ToString().Replace("\r\n", "\n"));
    }

    private static void AddDefault(List<string> globals, IReadOnlyList<string> args, string name, string value)
    {
        var delimiter = args.ToList().IndexOf("--");
        var optionTokens = delimiter < 0 ? args : args.Take(delimiter);
        if (optionTokens.Any(token => token == name
                                      || token.StartsWith(name + "=", StringComparison.Ordinal)
                                      || token.StartsWith(name + ":", StringComparison.Ordinal))) return;
        globals.Add(name);
        globals.Add(value);
    }
}
