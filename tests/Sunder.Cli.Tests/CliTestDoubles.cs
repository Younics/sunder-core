using Sunder.Cli;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli.Tests;

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
    public Func<string, string, bool, bool, CancellationToken, Task<PackageOperationResult>> ApplyLocal { get; set; }
        = (_, _, _, _, _) => throw new NotSupportedException();
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

    public Task<SystemStatusResponse> GetSystemStatusAsync(CancellationToken token) => SystemStatus(token);
    public Task<RuntimeResetChallengeResponse> PrepareResetAsync(CancellationToken token) => ResetPrepare(token);
    public Task<RuntimeResetDrainResponse> DrainForResetAsync(string challenge, CancellationToken token) => ResetDrain(challenge, token);
    public Task<RuntimeV1ResetResult> ResetLocalStateAsync(CancellationToken token) => LocalReset(token);
    public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken token) => Installed(token);
    public Task<RuntimeRegistryAuthStartResponse> StartRegistryAuthAsync(RuntimeRegistryAuthStartRequest request, CancellationToken token) => AuthStart(request, token);
    public Task<RuntimeRegistryAuthSessionStatus?> GetRegistryAuthSessionAsync(string sessionId, CancellationToken token) => AuthSession(sessionId, token);
    public Task<RuntimeRegistryAuthStatus> GetRegistryAuthStatusAsync(string origin, CancellationToken token) => AuthStatus(origin, token);
    public Task<RuntimeRegistryAuthStatus> LogoutRegistryAsync(string origin, CancellationToken token) => Logout(origin, token);
    public Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken token) => InstallRegistry(request, token);
    public Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken token) => UpdateRegistry(request, token);
    public Task<PackageOperationResult> ApplyLocalPackageAsync(string path, string packageId, bool allowDowngrade, bool reinstall, CancellationToken token) => ApplyLocal(path, packageId, allowDowngrade, reinstall, token);
    public Task<RegistryPublishPackageResponse> PublishRegistryPackageAsync(string origin, string path, bool setLatest, CancellationToken token) => PublishPackage(origin, path, setLatest, token);
    public Task<RegistryPublishStackResponse> PublishRegistryStackAsync(string origin, string path, CancellationToken token) => PublishStack(origin, path, token);
    public Task<RegistryPackageManagementOperationResponse> SetYankAsync(RuntimeRegistryYankRequest request, CancellationToken token) => Yank(request, token);
    public Task<RegistryPackageManagementOperationResponse> SetDeprecationAsync(RuntimeRegistryDeprecateRequest request, CancellationToken token) => Deprecate(request, token);
    public Task<RegistryPackageManagementOperationResponse> SetDistTagAsync(RuntimeRegistryDistTagRequest request, CancellationToken token) => DistTag(request, token);
    public Task<RegistryStackManagementOperationResponse> DeleteRegistryStackAsync(RuntimeRegistryDeleteStackRequest request, CancellationToken token) => DeleteStack(request, token);
    public void Dispose() { }
}

internal sealed class FakeRegistryClient : IRegistryClient
{
    public Uri RegistryUrl { get; } = new("https://registry.test/");
    public Func<string?, int, int, CancellationToken, Task<IReadOnlyList<RegistryPackageSummary>>> Search { get; set; }
        = (_, _, _, _) => Task.FromResult<IReadOnlyList<RegistryPackageSummary>>([]);
    public Func<RegistryStackArtifact, string, string, CancellationToken, Task> Download { get; set; }
        = (_, _, _, _) => Task.CompletedTask;
    public Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(string? query, int skip, int take, CancellationToken token) => Search(query, skip, take, token);
    public Task<IReadOnlyList<RegistryStackSummary>> SearchStacksAsync(string? query, int skip, int take, CancellationToken token) => Task.FromResult<IReadOnlyList<RegistryStackSummary>>([]);
    public Task<RegistryPackageDetails?> GetPackageAsync(string packageId, CancellationToken token) => Task.FromResult<RegistryPackageDetails?>(null);
    public Task<RegistryPackageVersionDetails?> GetVersionAsync(string packageId, string version, CancellationToken token) => Task.FromResult<RegistryPackageVersionDetails?>(null);
    public Task<RegistryStackDetails?> GetStackAsync(string stackId, CancellationToken token) => Task.FromResult<RegistryStackDetails?>(null);
    public Task<RegistryPackageDistTagsResponse?> GetDistTagsAsync(string packageId, CancellationToken token) => Task.FromResult<RegistryPackageDistTagsResponse?>(null);
    public Task<RegistryPublishPackageResponse> PublishLocalPackageAsync(string packagePath, bool setLatest, CancellationToken token)
        => Task.FromResult(new RegistryPublishPackageResponse(false, null, null, "Publishing is not configured for this test.", [], ["Publishing is not configured for this test."]));
    public Task<RegistryPublishStackResponse> PublishLocalStackAsync(string stackPath, CancellationToken token)
        => Task.FromResult(new RegistryPublishStackResponse(false, null, "Publishing is not configured for this test.", [], ["Publishing is not configured for this test."]));
    public Task DownloadStackAsync(RegistryStackArtifact artifact, string stackId, string destinationPath, CancellationToken token) => Download(artifact, stackId, destinationPath, token);
    public void Dispose() { }
}

internal sealed class FakeBrowserLauncher : IBrowserLauncher
{
    public Uri? Opened { get; private set; }
    public bool TryOpen(Uri uri) { Opened = uri; return true; }
}

internal static class CliTestHost
{
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string[] args,
        FakeRuntimeClient? runtime = null,
        FakeRegistryClient? registry = null,
        IBrowserLauncher? browser = null,
        CancellationToken token = default)
    {
        runtime ??= new();
        registry ??= new();
        var stdout = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var stderr = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var app = new CliApplication(stdout, stderr, _ => runtime, _ => registry, browser ?? new FakeBrowserLauncher());
        var globals = new[]
        {
            "--registry-api-url", "https://registry.test/",
            "--registry-web-url", "https://registry.test/",
            "--runtime-url", "http://runtime.test/",
        };
        var exit = await app.RunAsync([.. args, .. globals], token);
        return (exit, stdout.ToString().Replace("\r\n", "\n"), stderr.ToString().Replace("\r\n", "\n"));
    }
}
