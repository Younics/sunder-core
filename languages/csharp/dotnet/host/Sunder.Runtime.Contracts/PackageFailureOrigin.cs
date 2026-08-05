namespace Sunder.Runtime.Contracts;

public enum PackageFailureOrigin
{
    Unknown = 0,
    AppActivation = 1,
    AppHostedView = 2,
    AppUnhandledUi = 3,
    RuntimeActivation = 4,
    RuntimeConfiguration = 5,
    RuntimeAuthentication = 6,
    RuntimeBackgroundService = 7,
    RuntimeRpcProvider = 8,
    RuntimeProcess = 9,
}
