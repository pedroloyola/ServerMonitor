using System.Windows.Input;
using ServerMonitor.Core.Interfaces;
namespace ServerMonitor.App.Services;

/// <summary>Only the existing editor entry points; no creation or persistence authority.</summary>
public sealed record OnboardingActions(ICommand AddServerCommand, ICommand ImportFromSshCommand);

internal sealed class LoadedServerLoadStatusSource : IServerLoadStatusSource
{
    public Task<ServerLoadStatus> GetLoadStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(ServerLoadStatus.Loaded);
}
