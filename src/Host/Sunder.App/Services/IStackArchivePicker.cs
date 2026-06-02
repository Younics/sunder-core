namespace Sunder.App.Services;

public interface IStackArchivePicker
{
    Task<string?> PickStackPathAsync(CancellationToken cancellationToken = default);

    Task<string?> PickStackSavePathAsync(string suggestedFileName, CancellationToken cancellationToken = default);
}
