using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Package.Format;

namespace Sunder.App.ViewModels;

public sealed record PackageRpcAccessItemViewModel(
    string Role,
    string ContractId,
    string ContractVersion,
    bool Required,
    int ActionCount,
    string ActionsText)
{
    public string CallerRoleText => Role switch
    {
        SunderPackageFormat.AppHostRole => "App caller",
        SunderPackageFormat.RuntimeHostRole => "Runtime caller",
        _ => $"{Role} caller",
    };

    public string ContractVersionText => $"Version {ContractVersion}";

    public string RequirementText => Required ? "Required" : "Optional";
}

internal static class PackageRpcAccessProjection
{
    public static IReadOnlyList<PackageRpcAccessItemViewModel> FromRegistry(
        IEnumerable<RegistryPackageContractUse> uses)
        => Project(uses.Select(static use => new Source(
            use.Role,
            use.ContractId,
            use.ContractVersion,
            use.Required,
            use.Actions)));

    public static IReadOnlyList<PackageRpcAccessItemViewModel> FromRuntime(
        IEnumerable<PackageRpcContractUseDescriptor> uses)
        => Project(uses.Select(static use => new Source(
            use.Role,
            use.ContractId,
            use.ContractVersion,
            use.Required,
            use.Actions)));

    public static IReadOnlyList<PackageRpcAccessItemViewModel> FromManifest(
        IEnumerable<SunderPackageContractUseManifest?> uses)
        => Project(uses
            .Where(static use => use is not null)
            .Select(static use => new Source(
                use!.Role!,
                use.ContractId!,
                use.ContractVersion!,
                use.Required!.Value,
                (use.Actions ?? [])
                    .Where(static action => action is not null)
                    .Select(static action => action!)
                    .ToArray())));

    public static string Summary(IReadOnlyList<PackageRpcAccessItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return "No cross-package RPC access declared";
        }

        var actionCount = items.Sum(static item => item.ActionCount);
        return $"{items.Count} contract{(items.Count == 1 ? string.Empty : "s")} | "
               + $"{actionCount} action{(actionCount == 1 ? string.Empty : "s")}";
    }

    private static IReadOnlyList<PackageRpcAccessItemViewModel> Project(IEnumerable<Source> uses)
        => uses
            .OrderBy(static use => use.Role, StringComparer.Ordinal)
            .ThenBy(static use => use.ContractId, StringComparer.Ordinal)
            .ThenBy(static use => use.ContractVersion, StringComparer.Ordinal)
            .Select(static use =>
            {
                var actions = use.Actions
                    .Where(static action => !string.IsNullOrWhiteSpace(action))
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                return new PackageRpcAccessItemViewModel(
                    use.Role,
                    use.ContractId,
                    use.ContractVersion,
                    use.Required,
                    actions.Length,
                    string.Join(" | ", actions));
            })
            .ToArray();

    private sealed record Source(
        string Role,
        string ContractId,
        string ContractVersion,
        bool Required,
        IReadOnlyList<string> Actions);
}
