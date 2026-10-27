using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed class PackageInformationViewModel
{
    private PackageInformationViewModel(string status) => Status = status;

    public string Status { get; } = string.Empty;
    public bool HasStatus => Status.Length > 0;
    public bool IsLoaded { get; private init; }
    public IReadOnlyList<PackageFeatureItemViewModel> Features { get; private init; } = [];
    public IReadOnlyList<PackageFeatureItemViewModel> TechnicalFeatures { get; private init; } = [];
    public IReadOnlyList<PackageDependencyItemViewModel> Dependencies { get; private init; } = [];
    public IReadOnlyList<PackageBindingItemViewModel> PublicBindings { get; private init; } = [];
    public IReadOnlyList<PackageServiceAccessItemViewModel> ServiceAccess { get; private init; } = [];
    public bool HasFeatures => Features.Count > 0;
    public bool HasTechnicalFeatures => TechnicalFeatures.Count > 0;
    public bool HasDependencies => Dependencies.Count > 0;
    public bool HasPublicBindings => PublicBindings.Count > 0;
    public bool HasServiceAccess => ServiceAccess.Count > 0;
    public bool HasNoDependencies => IsLoaded && !HasDependencies;
    public bool HasNoPublicBindings => IsLoaded && !HasPublicBindings;
    public IReadOnlyList<PackageFeatureItemViewModel> PrimaryFeatures => Features.Where(value => value.IsSensitive).ToArray();
    public bool HasSensitiveFeatures => PrimaryFeatures.Count > 0;
    public IReadOnlyList<PackageFeatureItemViewModel> AdditionalFeatures => Features.Where(value => !value.IsSensitive).ToArray();
    public bool HasAdditionalFeatures => AdditionalFeatures.Count > 0;
    public IReadOnlyList<PackageBindingItemViewModel> PrimaryBindings => PublicBindings.Take(6).ToArray();
    public IReadOnlyList<PackageBindingItemViewModel> AdditionalBindings => PublicBindings.Skip(6).ToArray();
    public bool HasAdditionalBindings => AdditionalBindings.Count > 0;
    public IReadOnlyList<PackageDependencyItemViewModel> PrimaryDependencies => Dependencies.Take(4).ToArray();
    public IReadOnlyList<PackageDependencyItemViewModel> AdditionalDependencies => Dependencies.Skip(4).ToArray();
    public bool HasAdditionalDependencies => AdditionalDependencies.Count > 0;
    public string FeatureSummary => $"{Features.Count} Sunder feature{(Features.Count == 1 ? string.Empty : "s")}";
    public string DependencySummary => $"{Dependencies.Count(value => value.IsRequired)} required · {Dependencies.Count(value => !value.IsRequired)} optional";
    public string BindingSummary => $"{PublicBindings.Count(value => value.Kind == "Provided service")} services · {PublicBindings.Count(value => value.Kind == "Extension point")} extension points";
    public string AccessSummary => $"{ServiceAccess.Count} public service declaration{(ServiceAccess.Count == 1 ? string.Empty : "s")}";

    public static PackageInformationViewModel Loading() => new("Loading package information…");
    public static PackageInformationViewModel Unavailable() => new("Package information is unavailable from this source. This does not mean that the package has no dependencies or access.");

    internal static PackageInformationViewModel Create(SunderPackageInformation facts, string rid,
        Func<string, string, PackageDependencyState>? resolveDependency = null, Action<string>? navigate = null)
    {
        var targets = facts.Features.Where(value => value.Rid == rid).ToArray();
        var roles = targets.Select(value => value.Role).ToHashSet(StringComparer.Ordinal);
        bool IncludesRole(string role) => roles.Contains(role);
        var features = PackageFeatureDescriptions.Project(targets);
        return new(string.Empty)
        {
            IsLoaded = true,
            Features = features.Where(value => !value.IsTechnical).ToArray(),
            TechnicalFeatures = features,
            Dependencies = facts.Dependencies.OrderBy(value => value.Presence == "required" ? 0 : 1).ThenBy(value => value.PackageId, StringComparer.Ordinal)
                .Select(value =>
                {
                    var state = resolveDependency?.Invoke(value.PackageId, value.VersionRange) ?? new(value.PackageId, "Availability not checked");
                    var bindings = value.Bindings.Where(binding => IncludesRole(binding.Role))
                        .Select(binding => new PackageBindingItemViewModel(PackageBindingNames.Title(binding.ContractId),
                            binding.ContractId, binding.Version, binding.Kind == "uses" ? "Uses API" : "Implements extension", RoleText(binding.Role)))
                        .Distinct().ToArray();
                    return new PackageDependencyItemViewModel(value.PackageId, state.Name, value.VersionRange, value.Presence == "required", state.Status,
                        bindings, navigate is null ? null : new RelayCommand(() => navigate(value.PackageId)));
                }).ToArray(),
            PublicBindings = facts.PublicBindings.Select(value =>
                (Binding: value, Providers: value.Providers.Where(provider => IncludesRole(provider.Role)).ToArray()))
                .Where(value => value.Binding.Kind == "extension" || value.Providers.Length > 0)
                .Select(value => new PackageBindingItemViewModel(PackageBindingNames.Title(value.Binding.ContractId), value.Binding.ContractId,
                    value.Binding.Version, value.Binding.Kind == "service" ? "Provided service" : "Extension point",
                    string.Join(" · ", value.Providers.Select(provider => RoleText(provider.Role)).Distinct())
                    + (value.Providers.Length > 1 ? $" · {value.Providers.Length} implementations" : string.Empty))).ToArray(),
            ServiceAccess = facts.PublicUses.Where(value => IncludesRole(value.Role)).Select(value => new PackageServiceAccessItemViewModel(
                PackageBindingNames.Title(value.ContractId), value.ContractId, value.Version, RoleText(value.Role),
                value.Required ? "Required for service readiness" : "Optional service",
                string.Join(" · ", value.Actions.Order(StringComparer.Ordinal).Select(action => action switch
                {
                    "discover" => "Find services", "invoke" => "Call services", "subscribe" => "Receive live updates", _ => action,
                })))).ToArray(),
        };
    }

    internal static SunderPackageInformation FromRuntime(PackageInformationDescriptor value) => new(
        value.Features.Select(item => new SunderPackageFeature(item.Role, item.Rid, item.Capability)).ToArray(),
        value.Dependencies.Select(item => new SunderPackageRelationship(item.PackageId, item.VersionRange, item.Presence,
            item.Bindings.Select(binding => new SunderPackageIntegration(binding.Role, binding.ContractId, binding.Version, binding.Kind)).ToArray())).ToArray(),
        value.PublicBindings.Select(item => new SunderPackagePublicBinding(item.ContractId, item.Version, item.Kind,
            item.Providers.Select(provider => new SunderPackagePublicProvider(provider.Role, provider.ProviderId)).ToArray())).ToArray(),
        value.PublicUses.Select(item => new SunderPackagePublicUse(item.Role, item.ContractId, item.Version, item.Required, item.Actions)).ToArray());

    internal static SunderPackageInformation FromRegistry(RegistryPackageInformation value) => new(
        value.Features.Select(item => new SunderPackageFeature(item.Role, item.Rid, item.Capability)).ToArray(),
        value.Dependencies.Select(item => new SunderPackageRelationship(item.PackageId, item.VersionRange, item.Presence,
            item.Bindings.Select(binding => new SunderPackageIntegration(binding.Role, binding.ContractId, binding.Version, binding.Kind)).ToArray())).ToArray(),
        value.PublicBindings.Select(item => new SunderPackagePublicBinding(item.ContractId, item.Version, item.Kind,
            item.Providers.Select(provider => new SunderPackagePublicProvider(provider.Role, provider.ProviderId)).ToArray())).ToArray(),
        value.PublicUses.Select(item => new SunderPackagePublicUse(item.Role, item.ContractId, item.Version, item.Required, item.Actions)).ToArray());

    internal static string RoleText(string role) => role == "app" ? "Desktop UI" : role == "runtime" ? "Background worker" : role;
}

internal sealed record PackageDependencyState(string Name, string Status);
public sealed record PackageFeatureItemViewModel(string Title, string Description, string Scope, string Identifiers, bool IsSensitive, bool IsTechnical);
public sealed record PackageBindingItemViewModel(string Title, string ContractId, string Version, string Kind, string Scope)
{
    public string Identity => Title == ContractId ? $"Version {Version}" : $"{ContractId} · v{Version}";
    public bool HasScope => Scope.Length > 0;
}
public sealed record PackageServiceAccessItemViewModel(string Title, string ContractId, string Version, string Scope, string Requirement, string Actions)
{
    public string Identity => $"{ContractId} · v{Version}";
}
public sealed record PackageDependencyItemViewModel(string PackageId, string Name, string VersionRange, bool IsRequired, string Status,
    IReadOnlyList<PackageBindingItemViewModel> Bindings, ICommand? NavigateCommand)
{
    public string Requirement => IsRequired ? "Required" : "Optional";
    public bool HasDisplayName => Name != PackageId;
    public string Explanation => IsRequired ? "Required package dependency." : "Optional integration; absence is not a missing prerequisite.";
    public bool CanNavigate => NavigateCommand is not null;
    public bool HasBindings => Bindings.Count > 0;
    public bool HasNoBindings => !HasBindings;
    public string BindingSummary => $"{Bindings.Count} declared integration{(Bindings.Count == 1 ? string.Empty : "s")}";
}
