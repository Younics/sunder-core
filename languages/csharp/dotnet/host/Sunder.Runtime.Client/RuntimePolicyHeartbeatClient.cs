using System.Net;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

// Only the App's dedicated policy thread uses this channel. There is deliberately
// no async fallback, shared negotiation gate, package RPC or unbounded response.
// Public management/package clients continue to use the ordinary async transport.
internal sealed class RuntimePolicyHeartbeatClient : IDisposable
{
    private readonly Func<RuntimeConnectionInfo?> _connection;
    private readonly RuntimeClientPolicyOptions _policy;
    private readonly HttpClient _client;
    private readonly RuntimeHttpResponseReader _reader;
    private RuntimeClientResourceLimits _limits = RuntimeClientResourceLimits.Standard;
    private RuntimeConnectionInfo? _negotiatedConnection;
    private long _exchangeStarted;
    private readonly RuntimePolicyHttpMessageHandler? _localHandler;
    internal string DiagnosticPhase => _localHandler?.DiagnosticPhase ?? "adapter";

    internal RuntimePolicyHeartbeatClient(Func<RuntimeConnectionInfo?> connection,
        HttpMessageHandler handler, RuntimeClientPolicyOptions policy)
    {
        _connection = connection;
        _policy = policy;
        _localHandler = handler as RuntimePolicyHttpMessageHandler;
        _client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _reader = new(policy, () => _limits);
    }

    internal RuntimeHandshakeResponse Handshake(CancellationToken token, long? exchangeStarted = null)
    {
        _exchangeStarted = exchangeStarted ?? Stopwatch.GetTimestamp();
        _negotiatedConnection = null;
        var connection = GetConnection();
        using var response = Send(connection, HttpMethod.Get, "api/handshake", null, token);
        var bytes = _reader.ReadRequiredPayload(response, _policy.MaxHandshakeResponseBytes, token);
        var handshake = JsonSerializer.Deserialize<RuntimeHandshakeResponse>(bytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 16 })
            ?? throw new RuntimeProtocolException("Runtime did not return a handshake.");
        if (RuntimeProtocolCompatibility.GetIncompatibility(handshake) is { } error)
            throw new RuntimeProtocolException(error);
        try { _limits = new(RuntimeClientResourceProfileParser.Parse(handshake, bytes)); }
        catch (InvalidDataException exception) { throw new RuntimeProtocolException(exception.Message); }
        _negotiatedConnection = connection;
        return handshake;
    }

    internal RuntimeAppPolicySessionStatus Heartbeat(RuntimeAppPolicySessionRequest session, CancellationToken token)
    {
        var connection = GetConnection();
        if (connection != _negotiatedConnection)
            throw new RuntimeProtocolException("The policy connection changed after its handshake.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(session, _limits.CreateJsonOptions());
        if (bytes.LongLength > Math.Min(_policy.MaxJsonRequestBytes, _limits.MaximumJsonEnvelopeBytes))
            throw new InvalidDataException("Runtime policy heartbeat request exceeds the JSON byte limit.");
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        using var response = Send(connection, HttpMethod.Post,
            $"api/v1/app-policy-sessions/{Uri.EscapeDataString(session.SessionId)}/heartbeat", content, token);
        return _reader.ReadRequiredJson<RuntimeAppPolicySessionStatus>(response, token);
    }

    private RuntimeConnectionInfo GetConnection() => _connection()
        ?? throw new InvalidOperationException("Authenticated Runtime connection information is not available.");

    private HttpResponseMessage Send(RuntimeConnectionInfo connection, HttpMethod method,
        string path, HttpContent? content, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, new Uri(RuntimeConnectionInfo.Normalize(connection.RuntimeUrl), path))
        {
            Content = content, Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        if (!connection.CanSendTo(request.RequestUri!))
            throw new InvalidOperationException("Refusing to send Runtime credentials to an untrusted origin.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.BearerToken);
        request.Options.Set(RuntimePolicyHttpMessageHandler.MaximumResponseBytes, path == "api/handshake"
            ? _policy.MaxHandshakeResponseBytes : Math.Min(_policy.MaxJsonResponseBytes, _limits.MaximumJsonEnvelopeBytes));
        request.Options.Set(RuntimePolicyHttpMessageHandler.MaximumErrorBytes, _policy.MaxErrorResponseBytes);
        // Gateway handshake, direct handshake and status share one absolute
        // deadline. CancelAfter alone is not a deadline when its timer callback
        // cannot run; a fresh HTTP connection must not restart the proof budget.
        request.Options.Set(RuntimePolicyHttpMessageHandler.ExchangeStarted, _exchangeStarted);
        var response = _client.Send(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.RequestMessage?.RequestUri is not { } origin || !connection.CanSendTo(origin))
        {
            response.Dispose();
            throw new InvalidDataException("Runtime response changed to an untrusted origin.");
        }
        return response;
    }

    public void Dispose() => _client.Dispose();
}
