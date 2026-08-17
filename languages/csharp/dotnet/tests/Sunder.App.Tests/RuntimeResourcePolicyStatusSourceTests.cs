using System.Net;
using System.Net.Http.Json;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimeResourcePolicyStatusSourceTests
{
    [Fact]
    public async Task ReplacementProfileRefresh_OnlyClearsDesiredPendingWhenHashesMatch()
    {
        var current = Handshake(RuntimeResourcePolicies.Standard);
        var handler = new HandshakeHandler(() => current);
        using var transport = new RuntimeClientTransport(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);
        var source = new RuntimeResourcePolicyStatusSource(transport);
        var root = Path.Combine(Path.GetTempPath(), "sunder-resource-status-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new AppRuntimeResourcePolicySettingsService(Path.Combine(root, "policy.json"));
            settings.Save(new RuntimeResourcePolicySelection(
                RuntimeResourcePolicyPreset.Large,
                RuntimeResourcePolicies.Standard));
            var viewModel = new SettingsRuntimeResourcesViewModel(
                new SettingsRuntimeResourcesCoordinator(settings, source));
            viewModel.LoadSettings();

            await viewModel.RefreshCurrentEffectivePolicyAsync(CancellationToken.None);

            Assert.True(viewModel.ShowPendingNotice);
            Assert.Equal(
                RuntimeResourcePolicyContract.GetEffectiveProfileHash(RuntimeResourcePolicies.Standard),
                viewModel.CurrentProfileHash);

            current = Handshake(RuntimeResourcePolicies.Large);
            await viewModel.RefreshCurrentEffectivePolicyAsync(CancellationToken.None);

            Assert.False(viewModel.ShowPendingNotice);
            Assert.Equal(viewModel.DesiredProfileHash, viewModel.CurrentProfileHash);
            Assert.Equal(current.RuntimeInstanceId, transport.CurrentResourceProfile?.RuntimeInstanceId);
            Assert.Equal(2, handler.RequestCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeWithoutFeature_IsUnavailableAndKeepsStandardFailSafeLimits()
    {
        var handshake = new RuntimeHandshakeResponse(
            RuntimeProtocol.Identity,
            RuntimeProtocol.CurrentRevision,
            RuntimeProtocol.MinimumSupportedRevision,
            RuntimeProtocol.MaximumSupportedRevision,
            Guid.NewGuid(),
            [RuntimeProtocolFeatures.VersionedApiV1],
            new RuntimeProductVersionDiagnostics("Runtime", "1.0.0", "1.0.0"));
        using var transport = new RuntimeClientTransport(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            new HandshakeHandler(() => handshake));
        var source = new RuntimeResourcePolicyStatusSource(transport);

        var status = await source.GetCurrentEffectivePolicyAsync();

        Assert.False(status.IsAvailable);
        Assert.Null(transport.CurrentResourceProfile);
        Assert.Equal(
            RuntimeResourcePolicies.Standard.MaximumRpcValueBytes,
            transport.ResourceLimits.EffectivePolicy.MaximumRpcValueBytes);
    }

    private static RuntimeHandshakeResponse Handshake(RuntimeResourcePolicy policy)
        => new(
            RuntimeProtocol.Identity,
            RuntimeProtocol.CurrentRevision,
            RuntimeProtocol.MinimumSupportedRevision,
            RuntimeProtocol.MaximumSupportedRevision,
            Guid.NewGuid(),
            [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.RuntimeResourcePolicyV1],
            new RuntimeProductVersionDiagnostics("Runtime", "1.0.0", "1.0.0"),
            new RuntimeResourceProfileDescriptor(
                RuntimeResourcePolicyContract.GetEffectiveProfileHash(policy),
                policy.MaximumRpcValueBytes,
                policy.MaximumValueDepth,
                policy.MaximumValueNodes,
                policy.MaximumObjectMembers,
                policy.MaximumInlineDecodedBytes,
                policy.MaximumContentReferenceBytes));

    private sealed class HandshakeHandler(Func<RuntimeHandshakeResponse> getHandshake) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(request.RequestUri?.AbsolutePath == "/api/handshake"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(getHandshake()) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
