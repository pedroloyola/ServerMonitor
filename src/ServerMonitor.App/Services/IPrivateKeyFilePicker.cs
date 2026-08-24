namespace ServerMonitor.App.Services;

public interface IPrivateKeyFilePicker
{
    Task<string?> PickAsync(CancellationToken cancellationToken = default);
}
