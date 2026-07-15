namespace Sunder.Runtime.Contracts;

public enum PackageCallbackSessionState
{
    Pending = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3,
    Expired = 4,
}

public sealed record PackageCallbackSessionStartRequest(
    IReadOnlyDictionary<string, string>? Parameters = null)
{
    private IReadOnlyDictionary<string, string>? _parameters
        = RuntimeContractCollections.FreezeDictionaryNullable(Parameters);

    public IReadOnlyDictionary<string, string>? Parameters
    {
        get => _parameters;
        init => _parameters = RuntimeContractCollections.FreezeDictionaryNullable(value);
    }
}

public sealed record PackageCallbackSessionResponse(
    string PackageId,
    string CallbackHandlerId,
    string CallbackSessionId,
    PackageCallbackSessionState State,
    string Message,
    string? LaunchUri,
    DateTimeOffset ExpiresAtUtc);
