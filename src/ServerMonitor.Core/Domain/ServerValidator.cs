using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Domain;

public sealed class ServerValidator : IServerValidator
{
    public ServerValidationResult Validate(ServerInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var errors = new List<ServerValidationError>();

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            errors.Add(new(nameof(input.Name), ServerValidationErrorCode.NameRequired));
        }

        if (string.IsNullOrWhiteSpace(input.Host))
        {
            errors.Add(new(nameof(input.Host), ServerValidationErrorCode.HostRequired));
        }

        if (input.Port is < 1 or > 65535)
        {
            errors.Add(new(nameof(input.Port), ServerValidationErrorCode.PortOutOfRange));
        }

        if (string.IsNullOrWhiteSpace(input.Username))
        {
            errors.Add(new(nameof(input.Username), ServerValidationErrorCode.UsernameRequired));
        }

        return errors.Count == 0
            ? ServerValidationResult.Success
            : new ServerValidationResult(errors);
    }

    public ServerValidationResult Validate(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);

        return Validate(new ServerInput
        {
            Name = server.Name,
            Host = server.Host,
            Port = server.Port,
            Username = server.Username,
            OperatingSystem = server.OperatingSystem
        });
    }
}
