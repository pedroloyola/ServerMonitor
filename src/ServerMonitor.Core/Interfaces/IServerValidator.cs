using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Interfaces;

public interface IServerValidator
{
    ServerValidationResult Validate(ServerInput input);

    ServerValidationResult ValidateDraft(ServerInput input);

    ServerValidationResult Validate(Server server);
}
