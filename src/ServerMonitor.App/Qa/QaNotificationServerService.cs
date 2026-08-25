using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Qa;

internal sealed class QaNotificationServerService : IServerService
{
    public static readonly Guid ServerId = Guid.Parse("c4abf07d-8a5c-469c-a7d2-62a06b35de58");

    private static readonly Server QaServer = new()
    {
        Id = ServerId,
        Name = "QA Notification Server",
        Host = "qa-notification.invalid",
        Port = 22,
        Username = "qa",
        OperatingSystem = ServerOperatingSystem.Linux,
        RefreshIntervalSeconds = 30,
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    public event EventHandler? ServersChanged { add { } remove { } }

    public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Server>>([QaServer]);

    public Task<ServerOperationResult> AddAsync(ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The QA notification harness is read-only.");

    public Task<ServerOperationResult> AddAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The QA notification harness is read-only.");

    public Task<ServerOperationResult> UpdateAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The QA notification harness is read-only.");

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> HideAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
