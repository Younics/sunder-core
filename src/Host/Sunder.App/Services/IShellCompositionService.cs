using Sunder.App.Models;
using Sunder.App.Features.Shell.State;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public interface IShellCompositionService
{
    ShellSnapshot Compose(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        ShellState state,
        SystemStatusResponse? systemStatus,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors,
        ShellNormalizationPolicy normalizationPolicy
    );
}
