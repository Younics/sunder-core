namespace Sunder.App.Services;

internal sealed class SystemCliEnvironmentVariableStore : ICliEnvironmentVariableStore
{
    public static readonly SystemCliEnvironmentVariableStore Instance = new();

    public string? GetProcessEnvironmentVariable(string name)
        => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);

    public string? GetUserEnvironmentVariable(string name)
        => OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            : null;

    public void SetUserEnvironmentVariable(string name, string? value)
    {
        if (OperatingSystem.IsWindows())
        {
            Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        }
    }

    public void BroadcastEnvironmentChanged()
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsEnvironmentBroadcaster.BroadcastEnvironmentChanged();
        }
    }
}
