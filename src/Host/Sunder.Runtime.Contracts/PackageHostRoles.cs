namespace Sunder.Runtime.Contracts;

[Flags]
public enum PackageHostRoles
{
    ContractOnly = 0,
    App = 1,
    Runtime = 2,
}
