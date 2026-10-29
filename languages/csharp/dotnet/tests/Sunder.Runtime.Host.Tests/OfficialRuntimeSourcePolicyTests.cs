using System.Net;
using System.Text;
using System.Text.Json;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class OfficialRuntimeSourcePolicyTests
{
    [Theory]
    [InlineData("http://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.12/dotnet-runtime-10.0.12-linux-x64.tar.gz")]
    [InlineData("https://builds.dotnet.microsoft.com.evil.example/dotnet/Runtime/10.0.12/dotnet-runtime-10.0.12-linux-x64.tar.gz")]
    [InlineData("https://user:password@builds.dotnet.microsoft.com/dotnet/Runtime/10.0.12/dotnet-runtime-10.0.12-linux-x64.tar.gz")]
    [InlineData("https://builds.dotnet.microsoft.com:8443/dotnet/Runtime/10.0.12/dotnet-runtime-10.0.12-linux-x64.tar.gz")]
    [InlineData("https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.12/dotnet-runtime-10.0.12-linux-x64.tar.gz")]
    [InlineData("https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.12/dotnet-runtime-10.0.12-linux-arm64.tar.gz")]
    [InlineData("https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.12/dotnet-runtime-10.0.12-linux-x64.tar.gz?token=x")]
    [InlineData("https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.12/%64otnet-runtime-10.0.12-linux-x64.tar.gz")]
    public void OfficialUrlPolicy_DoesNotGrantOriginOnlyOrClientSuppliedAuthority(string url)
        => Assert.Equal(ExecutionRuntimeErrorCodes.SourceNotAllowed, Assert.Throws<RuntimeExecutionRuntimeException>(() =>
            OfficialRuntimeSourcePolicy.ValidateUri(new(url), ConfiguredOfficialSourceId.DotNet, "10.0.12", "linux-x64")).Code);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Redirects_CheckEveryHopBeforeSendingAndNeverSendCredentials(bool allowFirstCdn)
    {
        var requests = new List<Uri>();
        using var http = Create(request =>
        {
            requests.Add(request.RequestUri!);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            var location = allowFirstCdn && requests.Count == 1
                ? "https://dotnetcli.azureedge.net/dotnet/Runtime/10.0.12/dotnet-runtime-10.0.12-linux-x64.tar.gz"
                : "https://other.example/stolen-runtime.tar.gz";
            return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(location) } };
        });
        var error = await Assert.ThrowsAsync<RuntimeExecutionRuntimeException>(() => http.OpenAsync(
            new("https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.12/dotnet-runtime-10.0.12-linux-x64.tar.gz"),
            ConfiguredOfficialSourceId.DotNet, default, "10.0.12", "linux-x64"));
        Assert.Equal(ExecutionRuntimeErrorCodes.SourceNotAllowed, error.Code);
        Assert.Equal(allowFirstCdn ? 2 : 1, requests.Count);
        Assert.DoesNotContain(requests, uri => uri.Host == "other.example");
    }

    [Fact]
    public async Task Metadata_RejectsPartialAndUnknownLengthOversizedResponses()
    {
        using (var partial = Create(_ => new(HttpStatusCode.PartialContent) { Content = new StringContent("{}") }))
            await Assert.ThrowsAsync<InvalidDataException>(() => partial.ReadAsync(new(OfficialRuntimeSourcePolicy.DotNetMetadata),
                ConfiguredOfficialSourceId.DotNet, 32, default));
        using var oversized = Create(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new UnknownLengthStream(new byte[33])) });
        await Assert.ThrowsAsync<InvalidDataException>(() => oversized.ReadAsync(new(OfficialRuntimeSourcePolicy.DotNetMetadata),
            ConfiguredOfficialSourceId.DotNet, 32, default));
    }

    [Theory]
    [InlineData("win-x64", "zip")]
    [InlineData("win-arm64", "zip")]
    [InlineData("osx-x64", "tar.gz")]
    [InlineData("osx-arm64", "tar.gz")]
    [InlineData("linux-x64", "tar.gz")]
    [InlineData("linux-arm64", "tar.gz")]
    public async Task DotNet_SelectsStableRuntimeForExactRidAndKeepsUnknownSize(string rid, string extension)
    {
        var hash = new string('a', 128);
        var metadata = DotNetMetadata(rid, extension, hash);
        using var http = Create(_ => Json(metadata));
        var artifact = await new OfficialRuntimeResolver(http, new Clock()).ResolveAsync(
            new("sunder.runtime.dotnet", ">=10.0.0 <11.0.0", "sunder.dotnet-worker-module.v1"), rid, default);
        Assert.Equal("10.0.12", artifact.Identity.VendorVersion);
        Assert.Equal(RuntimeExecutionRuntimeChecksumAlgorithm.Sha512, artifact.Identity.VendorChecksumAlgorithm);
        Assert.Equal(hash, artifact.Identity.VendorChecksumDigest);
        Assert.Equal(rid, artifact.Identity.Rid);
        Assert.Null(artifact.Distribution.DownloadSize);
        Assert.Null(artifact.Distribution.ChecksumUrl);
        Assert.Null(artifact.Distribution.ChecksumDocumentSha256);
        Assert.DoesNotContain("sdk", artifact.Distribution.DownloadUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Node_RequiresLtsFloorAndOneExactChecksumEntry(bool duplicateChecksum)
    {
        const string archive = "node-v24.21.0-linux-x64.tar.gz";
        var hash = new string('b', 64);
        var requests = new List<string>();
        using var http = Create(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.AbsolutePath == "/dist/index.json") return Json("""
                [{"version":"v25.0.0","lts":false,"files":["linux-x64"]},
                 {"version":"v24.22.0-rc.1","lts":"Krypton","files":["linux-x64"]},
                 {"version":"v24.21.0","lts":"Krypton","files":["linux-x64"]},
                 {"version":"v24.18.0","lts":"Krypton","files":["linux-x64"]}]
                """);
            var line = $"{hash}  {archive}\n";
            return Json(line + (duplicateChecksum ? line : ""));
        });
        var resolve = () => new OfficialRuntimeResolver(http, new Clock()).ResolveAsync(
            new("sunder.runtime.node", ">=24.0.0 <25.0.0", "sunder.node-worker-module.v1"), "linux-x64", default);
        if (duplicateChecksum) await Assert.ThrowsAsync<InvalidDataException>(resolve);
        else
        {
            var selected = await resolve();
            Assert.Equal("24.21.0", selected.Identity.VendorVersion);
            Assert.Equal(hash, selected.Identity.VendorChecksumDigest);
            Assert.Equal(RuntimeExecutionRuntimeChecksumAlgorithm.Sha256, selected.Identity.VendorChecksumAlgorithm);
            Assert.NotNull(selected.Distribution.ChecksumDocumentSha256);
        }
        Assert.Equal([OfficialRuntimeSourcePolicy.NodeMetadata, "https://nodejs.org/dist/v24.21.0/SHASUMS256.txt"], requests);
    }

    [Fact]
    public async Task Metadata_DuplicatePropertiesCannotReplaceChecksumAuthority()
    {
        var metadata = DotNetMetadata("linux-x64", "tar.gz", new('a', 128)).Replace("\"hash\":", "\"hash\":\"bad\",\"hash\":", StringComparison.Ordinal);
        using var http = Create(_ => Json(metadata));
        await Assert.ThrowsAsync<InvalidDataException>(() => new OfficialRuntimeResolver(http, new Clock()).ResolveAsync(
            new("sunder.runtime.dotnet", ">=10.0.0 <11.0.0", "sunder.dotnet-worker-module.v1"), "linux-x64", default));
    }

    [Fact]
    public async Task DotNetSupportedMajorsCoexistAndResolveOnlyWithinThePackageMajor()
    {
        var metadata = DotNetMetadata("linux-x64", "tar.gz", new('a', 128));
        using var http = Create(request => Json(request.RequestUri!.AbsolutePath.Contains("/11.0/", StringComparison.Ordinal)
            ? metadata.Replace("10.0", "11.0", StringComparison.Ordinal).Replace("\"lts\"", "\"sts\"", StringComparison.Ordinal) : metadata));
        var resolver = new OfficialRuntimeResolver(http, new Clock(), dotnetMajors: [10, 11]);
        var catalog = await resolver.CatalogAsync(ConfiguredOfficialSourceId.DotNet, "linux-x64", 0, 10, default);
        Assert.Equal(new[] { "11.0.12", "10.0.12" }, catalog.Items.Select(item => item.Identity.VendorVersion));
        foreach (var major in new[] { 10, 11 })
        {
            var result = await resolver.ResolveAsync(new("sunder.runtime.dotnet", $">={major}.0.0 <{major + 1}.0.0", "sunder.dotnet-worker-module.v1"), "linux-x64", default);
            Assert.Equal($"{major}.0.12", result.Identity.VendorVersion);
            Assert.Equal($"dotnet-runtime-{major}.v1", result.Distribution.MaterializationProfile);
            Assert.Contains($"/{major}.0/", result.Distribution.MetadataUrl);
        }
        var unsupported = await Assert.ThrowsAsync<RuntimeExecutionRuntimeException>(() => new OfficialRuntimeResolver(http, new Clock())
            .ResolveAsync(new("sunder.runtime.dotnet", ">=11.0.0 <12.0.0", "sunder.dotnet-worker-module.v1"), "linux-x64", default));
        Assert.Equal(ExecutionRuntimeErrorCodes.Unsupported, unsupported.Code);
    }

    [Fact]
    public async Task NodeSupportedMajorsCoexistWithoutCrossMajorSelection()
    {
        using var http = Create(request => request.RequestUri!.AbsolutePath == "/dist/index.json" ? Json("""
            [{"version":"v26.1.0","lts":"test-next-line","files":["linux-x64"]},
             {"version":"v24.21.0","lts":"Krypton","files":["linux-x64"]}]
            """) : Json(new string('b', 64) + "  " + request.RequestUri.Segments[2].TrimEnd('/').Replace("v", "node-v", StringComparison.Ordinal) + "-linux-x64.tar.gz\n"));
        var resolver = new OfficialRuntimeResolver(http, new Clock(), nodeMajors: [24, 26], nodeEndOfLife: new Dictionary<int, DateTimeOffset>
        { [24] = DateTimeOffset.Parse("2028-04-30T00:00:00Z"), [26] = DateTimeOffset.Parse("2030-04-30T00:00:00Z") });
        foreach (var (major, version) in new[] { (24, "24.21.0"), (26, "26.1.0") })
        {
            var result = await resolver.ResolveAsync(new("sunder.runtime.node", $">={major}.0.0 <{major + 1}.0.0", "sunder.node-worker-module.v1"), "linux-x64", default);
            Assert.Equal(version, result.Identity.VendorVersion);
            Assert.Equal($"node-runtime-{major}.v1", result.Distribution.MaterializationProfile);
        }
    }

    [Fact]
    public void HistoricalEvidenceValidityIsNotCurrentSelectionEligibility()
    {
        var identity = new RuntimeExecutionRuntimeIdentity(ConfiguredOfficialSourceId.DotNet, "sunder.runtime.dotnet", "10.0.12", "linux-x64",
            "sunder.dotnet-worker-module.v1", RuntimeExecutionRuntimeChecksumAlgorithm.Sha512, new('a', 128));
        OfficialRuntimeSourcePolicy.ValidateIdentity(identity);
        Assert.False(OfficialRuntimeSourcePolicy.EligibleVersion(identity.SourceId, identity.VendorVersion, [11]));
        Assert.True(OfficialRuntimeSourcePolicy.EligibleVersion(identity.SourceId, identity.VendorVersion, [10, 11]));
        Assert.Equal("dotnet-runtime-10.v1", OfficialRuntimeSourcePolicy.Profile(identity.SourceId, identity.VendorVersion));
    }

    private static string DotNetMetadata(string rid, string extension, string hash) => $$$"""
        {"channel-version":"10.0","support-phase":"active","release-type":"lts","eol-date":"2028-11-14","releases":[
        {"runtime":{"version":"10.0.13-preview.1","files":[]}},
        {"sdk":{"version":"10.0.999"},"runtime":{"version":"10.0.12","files":[
        {"name":"dotnet-apphost-pack-{{{rid}}}.{{{extension}}}","rid":"{{{rid}}}","url":"https://untrusted.example/pack","hash":"{{{hash}}}"},
        {"name":"dotnet-runtime-{{{rid}}}.{{{extension}}}","rid":"{{{rid}}}","url":"https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.12/dotnet-runtime-10.0.12-{{{rid}}}.{{{extension}}}","hash":"{{{hash}}}"}]}}]}
        """;

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8) };
    private static OfficialRuntimeHttpClient Create(Func<HttpRequestMessage, HttpResponseMessage> send)
        => new(new Factory(new HttpClient(new Handler(send)) { Timeout = Timeout.InfiniteTimeSpan }));
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = send(request);
            result.RequestMessage = request;
            return Task.FromResult(result);
        }
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-07T12:00:00Z"); }
    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes) { public override bool CanSeek => false; }
}
