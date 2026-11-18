namespace ServerMonitor.Core.History;

/// <summary>
/// Something that keeps its own local history next to the built-in one. When the user clears or resets history, every
/// registered participant is asked to do the same, after the built-in history. Both calls return <c>false</c> when the
/// participant could not finish; an exception counts as <c>false</c>.
/// </summary>
public interface IHistoryParticipant
{
    /// <summary>Deletes the participant's history rows. Its storage stays in place.</summary>
    Task<bool> ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes the participant's history storage entirely, so it starts again from nothing.</summary>
    Task<bool> ResetAsync(CancellationToken cancellationToken = default);
}
