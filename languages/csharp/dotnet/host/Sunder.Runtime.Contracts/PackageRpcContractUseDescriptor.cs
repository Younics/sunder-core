namespace Sunder.Runtime.Contracts;

public sealed record PackageRpcContractUseDescriptor(
    string Role,
    string ContractId,
    string ContractVersion,
    string AbiSha256,
    bool Required,
    IReadOnlyList<string> Actions)
{
    private IReadOnlyList<string> _actions = RuntimeContractCollections.Freeze(Actions);

    public IReadOnlyList<string> Actions
    {
        get => _actions;
        init => _actions = RuntimeContractCollections.Freeze(value);
    }
}
