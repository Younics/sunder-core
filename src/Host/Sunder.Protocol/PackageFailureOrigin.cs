namespace Sunder.Protocol;

public enum PackageFailureOrigin
{
    Unknown = 0,
    AppActivation = 1,
    AppHostedView = 2,
    AppUnhandledUi = 3,
    RuntimeActivation = 4,
    RuntimeConfiguration = 5,
    RuntimeAuthentication = 6,
}
