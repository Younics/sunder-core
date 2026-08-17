using Sunder.Runtime.LocalState;

namespace Sunder.Host.Supervisor;

internal sealed class RuntimeResourcePolicyLaunchResolver
{
    private readonly string? _policyFilePath;
    private readonly Action<string> _reportDiagnostic;

    public RuntimeResourcePolicyLaunchResolver(
        string? policyFilePath,
        Action<string>? reportDiagnostic = null)
    {
        _policyFilePath = string.IsNullOrWhiteSpace(policyFilePath)
            ? null
            : Path.GetFullPath(policyFilePath);
        _reportDiagnostic = reportDiagnostic ?? Console.Error.WriteLine;
    }

    public string ResolveLaunchPayload()
    {
        var effectivePolicy = LoadEffectivePolicy();
        return RuntimeResourcePolicyContract.SerializeLaunchPayload(effectivePolicy);
    }

    private RuntimeResourcePolicy LoadEffectivePolicy()
    {
        if (_policyFilePath is null || !File.Exists(_policyFilePath))
        {
            return RuntimeResourcePolicies.Standard;
        }

        try
        {
            var file = new FileInfo(_policyFilePath);
            if (file.Length <= 0 || file.Length > RuntimeResourcePolicyContract.MaximumSettingsFileBytes)
            {
                throw new InvalidDataException(
                    $"The file must be between 1 and {RuntimeResourcePolicyContract.MaximumSettingsFileBytes} bytes.");
            }

            var selection = RuntimeResourcePolicyContract.ParseSettings(File.ReadAllBytes(_policyFilePath));
            return selection.EffectivePolicy;
        }
        catch (Exception exception)
        {
            ReportDiagnostic(
                $"runtime.resource-policy.invalid: Runtime resource policy file '{_policyFilePath}' is invalid; "
                + $"using Standard for this worker start. {exception.Message}");
            return RuntimeResourcePolicies.Standard;
        }
    }

    private void ReportDiagnostic(string message)
    {
        try
        {
            _reportDiagnostic(message);
        }
        catch
        {
            // Diagnostics must not prevent the Standard fallback from starting Runtime.
        }
    }
}
